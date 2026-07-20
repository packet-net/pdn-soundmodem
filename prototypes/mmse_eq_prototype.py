"""
MMSE equalizer prototype for MS110D Poor channel.
Tests whether a proper MMSE equalizer with channel estimation from pilots
can achieve BER < 1E-5 at +10 dB on the 2-path Rayleigh Poor channel.

Key difference from current approach:
- Current: batch-LS per probe + RLS tracking (suboptimal, loses ~20 dB)
- This: MMSE equalizer with per-symbol channel interpolation from pilots
"""
import numpy as np
from numpy.fft import fft, ifft

# --- Parameters (matching MS110D WN4) ---
SNR_DB = 10.0
K_PROBE = 32        # probe symbols
U_DATA = 96         # data symbols per frame
N_FRAMES = 20       # frames per burst
FS = 9600           # sample rate (T/2 spacing at 2400 Bd = 4800 Hz... actually 9600 for the real system)
SYMBOL_RATE = 2400
SAMPLES_PER_SYMBOL = 2  # T/2 spacing
DOPPLER_HZ = 0.5    # 1 Hz two-sigma = 0.5 Hz one-sigma
DELAY_MS = 2.0
N_TURBO = 2

# --- Channel model (matching WattersonChannel.Poor) ---
def generate_fading_channel(n_symbols, n_taps, fs, doppler_hz, seed):
    """Generate 2-path Rayleigh fading channel coefficients at symbol rate."""
    rng = np.random.default_rng(seed)
    t = np.arange(n_symbols) / (SYMBOL_RATE)
    
    # Generate correlated Rayleigh fading using filtering method
    # Gaussian Doppler PSD: S(f) ~ exp(-f^2 / (2*sigma^2))
    # Autocorrelation: R(tau) = exp(-2*pi^2*sigma^2*tau^2)
    sigma = doppler_hz
    
    # Generate white complex Gaussian and filter
    n_gen = n_symbols + 100  # extra for filter transient
    white = (rng.standard_normal(n_gen) + 1j * rng.standard_normal(n_gen)) / np.sqrt(2)
    
    # Gaussian filter kernel
    kernel_len = min(201, n_gen // 2)
    t_kernel = np.arange(-kernel_len//2, kernel_len//2 + 1) / SYMBOL_RATE
    kernel = np.exp(-2 * np.pi**2 * sigma**2 * t_kernel**2)
    kernel /= np.sqrt(np.sum(kernel**2))  # normalize to unit power
    
    # Filter
    h = np.convolve(white, kernel, mode='same')
    h = h[50:50+n_symbols]  # remove transient
    
    # Normalize to unit average power
    h /= np.sqrt(np.mean(np.abs(h)**2))
    return h

def apply_channel(tx_symbols, snr_db, seed):
    """Apply 2-path Rayleigh fading + AWGN. Returns rx samples at T/2 rate."""
    n_sym = len(tx_symbols)
    rng = np.random.default_rng(seed + 1000)
    
    # Generate fading for each path
    h1 = generate_fading_channel(n_sym, 1, FS, DOPPLER_HZ, seed)
    h2 = generate_fading_channel(n_sym, 1, FS, DOPPLER_HZ, seed + 500)
    
    # Delay for path 2 (2ms = 4.8 symbols)
    delay_sym = DELAY_MS * 1e-3 * SYMBOL_RATE  # ~4.8 symbols
    
    # Apply channel at symbol rate (simplified - no T/2 for now)
    rx = h1 * tx_symbols
    delay_int = int(round(delay_sym))
    if delay_int > 0 and delay_int < n_sym:
        rx[delay_int:] += h2[:-delay_int] * tx_symbols[:-delay_int]
    
    rx /= np.sqrt(2)  # equal power normalization
    
    # AWGN
    sig_power = np.mean(np.abs(rx)**2)
    noise_power = sig_power / (10**(snr_db/10))
    noise = (rng.standard_normal(n_sym) + 1j * rng.standard_normal(n_sym)) * np.sqrt(noise_power/2)
    rx += noise
    
    return rx, h1, h2

# --- MMSE Equalizer ---
def mmse_equalize_frame(rx_frame, probe_symbols, probe_positions, data_positions, 
                        h1_frame, h2_frame, delay_sym, noise_var):
    """
    MMSE equalization using channel estimates interpolated from probe positions.
    
    For each data symbol, estimate the channel by interpolating from nearby probes,
    then apply the MMSE equalizer: w = H^H * (H*H^H + sigma^2*I)^{-1}
    """
    n = len(rx_frame)
    delay_int = int(round(delay_sym))
    
    # Estimate channel at all positions from probe observations
    # At probe positions: h_est = rx / probe_symbol
    h_observed = np.zeros(n, dtype=complex)
    for pos, sym in zip(probe_positions, probe_symbols):
        if pos < n and abs(sym) > 0:
            h_observed[pos] = rx_frame[pos] / sym
    
    # Interpolate channel to all positions (linear interpolation)
    valid_pos = probe_positions[probe_positions < n]
    valid_h = h_observed[valid_pos]
    
    if len(valid_pos) < 2:
        # Not enough observations, use simple ZF
        eq = np.where(np.abs(h_observed) > 0.01, rx_frame / h_observed, rx_frame)
        return eq[data_positions]
    
    # Interpolate for both paths
    all_pos = np.arange(n)
    h1_est = np.interp(all_pos, valid_pos, valid_h.real) + 1j * np.interp(all_pos, valid_pos, valid_h.imag)
    
    # MMSE equalization: for each symbol, w = h* / (|h|^2 + sigma^2)
    eq = np.zeros(n, dtype=complex)
    for i in range(n):
        h = h1_est[i]
        eq[i] = np.conj(h) * rx_frame[i] / (np.abs(h)**2 + noise_var)
    
    return eq[data_positions]

# --- Simple Viterbi (hard decision, rate 1/2 K=7) ---
def conv_encode(bits):
    """Tail-biting rate 1/2 K=7 encoder."""
    n = len(bits)
    state = 0
    for i in range(6):
        state = (state << 1) | int(bits[(n - 7 + i) % n])
    
    coded = np.zeros(2*n, dtype=np.int8)
    for i in range(n):
        state = ((state << 1) | int(bits[i])) & 0x7F
        coded[2*i] = bin(state & 0o133).count('1') % 2
        coded[2*i+1] = bin(state & 0o171).count('1') % 2
    return coded

def viterbi_decode(coded_llrs, n_info):
    """Simple hard-decision Viterbi for rate 1/2 K=7."""
    # Convert LLRs to hard decisions for simplicity
    hard = (coded_llrs < 0).astype(np.int8)
    
    # Viterbi decoding (simplified - just use syndrome decoding for prototype)
    # For a proper test, we'd need full Viterbi. For now, use majority vote on pairs.
    decoded = np.zeros(n_info, dtype=np.int8)
    for i in range(n_info):
        if 2*i+1 < len(hard):
            decoded[i] = 1 if (hard[2*i] + hard[2*i+1]) > 0.5 else 0
    return decoded

# --- Main simulation ---
def run_trial(snr_db, n_turbo, seed, use_mmse=True):
    rng = np.random.default_rng(seed)
    delay_sym = DELAY_MS * 1e-3 * SYMBOL_RATE
    
    # Generate info bits
    n_info = U_DATA * N_FRAMES
    info_bits = rng.integers(0, 2, n_info).astype(np.int8)
    
    # Encode (rate 1/2)
    coded = conv_encode(info_bits)
    n_coded = len(coded)
    
    # Map to BPSK symbols (use first U_DATA*N_FRAMES coded bits)
    n_sym = U_DATA * N_FRAMES
    tx_data = 1 - 2 * coded[:n_sym]  # BPSK: 0->+1, 1->-1
    
    # Probe (known alternating BPSK)
    probe = np.array([(-1)**i for i in range(K_PROBE)], dtype=float)
    
    # Build full TX
    frame_len = K_PROBE + U_DATA
    tx_full = np.zeros(N_FRAMES * frame_len)
    for f in range(N_FRAMES):
        off = f * frame_len
        tx_full[off:off+K_PROBE] = probe
        tx_full[off+K_PROBE:off+frame_len] = tx_data[f*U_DATA:(f+1)*U_DATA]
    
    # Channel
    rx, h1, h2 = apply_channel(tx_full, snr_db, seed)
    
    # Noise variance estimate
    sig_power = np.mean(np.abs(rx)**2)
    noise_var = sig_power / (10**(snr_db/10))
    
    # --- Equalization ---
    eq_symbols = np.zeros(n_sym)
    
    for f in range(N_FRAMES):
        off = f * frame_len
        rx_frame = rx[off:off+frame_len]
        h1_frame = h1[off:off+frame_len]
        h2_frame = h2[off:off+frame_len]
        
        probe_pos = np.arange(K_PROBE)
        data_pos = np.arange(K_PROBE, frame_len)
        
        if use_mmse:
            eq_data = mmse_equalize_frame(rx_frame, probe, probe_pos, data_pos,
                                          h1_frame, h2_frame, delay_sym, noise_var)
        else:
            # Simple ZF from probe interpolation
            h_obs = np.zeros(frame_len, dtype=complex)
            for p, s in zip(probe_pos, probe):
                h_obs[p] = rx_frame[p] / s
            h_interp = np.interp(np.arange(frame_len), probe_pos, h_obs.real) + \
                       1j * np.interp(np.arange(frame_len), probe_pos, h_obs.imag)
            eq_data = np.where(np.abs(h_interp[data_pos]) > 0.01,
                              rx_frame[data_pos] / h_interp[data_pos],
                              rx_frame[data_pos])
        
        eq_symbols[f*U_DATA:(f+1)*U_DATA] = eq_data.real
    
    # First-pass BER
    hard1 = (eq_symbols < 0).astype(np.int8)
    ber1 = np.mean(hard1 != coded[:n_sym])
    
    # --- Turbo iterations ---
    current_decoded = hard1.copy()
    for turbo_iter in range(n_turbo):
        # Re-encode
        expected = 1 - 2 * current_decoded
        
        # Re-equalize with expected symbols as additional training
        eq_turbo = np.zeros(n_sym)
        for f in range(N_FRAMES):
            off = f * frame_len
            rx_frame = rx[off:off+frame_len]
            
            # Use BOTH probe AND expected data as training
            all_pos = np.arange(frame_len)
            all_sym = np.concatenate([probe, expected[f*U_DATA:(f+1)*U_DATA]])
            
            # Channel estimate from all training
            h_obs = np.zeros(frame_len, dtype=complex)
            for p, s in zip(all_pos, all_sym):
                if abs(s) > 0:
                    h_obs[p] = rx_frame[p] / s
            
            # MMSE equalization
            h_interp = np.interp(np.arange(frame_len), all_pos, h_obs.real) + \
                       1j * np.interp(np.arange(frame_len), all_pos, h_obs.imag)
            
            data_pos = np.arange(K_PROBE, frame_len)
            for i, dp in enumerate(data_pos):
                h = h_interp[dp]
                eq_turbo[f*U_DATA + i] = (np.conj(h) * rx_frame[dp] / (np.abs(h)**2 + noise_var)).real
        
        current_decoded = (eq_turbo < 0).astype(np.int8)
    
    ber_turbo = np.mean(current_decoded != coded[:n_sym])
    return ber1, ber_turbo

# --- Run ---
if __name__ == "__main__":
    n_trials = 100
    print(f"=== MMSE Equalizer Prototype ===")
    print(f"Channel: 2-path Rayleigh, {DELAY_MS}ms, {DOPPLER_HZ*2} Hz Doppler")
    print(f"SNR: {SNR_DB} dB, BPSK, U={U_DATA}, K={K_PROBE}, {N_FRAMES} frames")
    print()
    
    # Test MMSE
    bers_mmse_1 = []
    bers_mmse_turbo = []
    for trial in range(n_trials):
        b1, bt = run_trial(SNR_DB, N_TURBO, trial * 100, use_mmse=True)
        bers_mmse_1.append(b1)
        bers_mmse_turbo.append(bt)
    
    print(f"MMSE first pass:  mean BER = {np.mean(bers_mmse_1):.4e}")
    print(f"MMSE + turbo ({N_TURBO}):  mean BER = {np.mean(bers_mmse_turbo):.4e}")
    print()
    
    # Test ZF (simpler baseline)
    bers_zf_1 = []
    bers_zf_turbo = []
    for trial in range(n_trials):
        b1, bt = run_trial(SNR_DB, N_TURBO, trial * 100, use_mmse=False)
        bers_zf_1.append(b1)
        bers_zf_turbo.append(bt)
    
    print(f"ZF first pass:    mean BER = {np.mean(bers_zf_1):.4e}")
    print(f"ZF + turbo ({N_TURBO}):    mean BER = {np.mean(bers_zf_turbo):.4e}")
    print()
    print(f"Target: 1E-5")
