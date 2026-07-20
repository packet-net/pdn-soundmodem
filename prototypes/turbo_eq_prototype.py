"""
Turbo equalization prototype for MS110D Poor channel.
Tests: does feeding Viterbi decoder output back to the DFE as known training
significantly improve BER on a 2-path Rayleigh fading channel?

Simplified: BPSK, rate-1/2 K=7 conv code, no interleaver, no puncturing.
Probe/data structure mimics MS110D (K=32 probe, U=96 data per frame).
"""
import numpy as np
from scipy.signal import lfilter

# --- Parameters ---
SNR_DB = 10.0       # WN4 Poor mask SNR
K_PROBE = 32        # probe symbols per frame
U_DATA = 96         # data symbols per frame
N_FRAMES = 20       # frames per burst
SYMBOL_RATE = 2400  # Bd
DOPPLER_HZ = 1.0   # 1 Hz two-sigma
DELAY_MS = 2.0      # 2 ms path delay
N_TURBO_ITER = 3    # turbo iterations
CODE_K = 7          # convolutional code constraint length
CODE_RATE = 0.5     # rate 1/2

# --- Convolutional encoder (K=7, rate 1/2, polynomials 0o133, 0o171) ---
def conv_encode(bits):
    """Tail-biting convolutional encode, rate 1/2, K=7."""
    n = len(bits)
    # Tail-biting: initialize state from last K-1 bits
    state = 0
    for i in range(CODE_K - 2, -1, -1):
        state = (state << 1) | bits[(n - CODE_K + 1 + i) % n]
    
    coded = np.zeros(2 * n, dtype=np.int8)
    for i in range(n):
        state = ((state << 1) | bits[i]) & 0x7F
        # Polynomial 0o133 = 1011011, 0o171 = 1111001
        t1 = bin(state & 0o133).count('1') % 2
        t2 = bin(state & 0o171).count('1') % 2
        coded[2*i] = t1
        coded[2*i+1] = t2
    return coded

# --- Viterbi decoder (simplified, hard-decision for prototype) ---
def viterbi_decode_hard(coded_bits, n_info):
    """Simple hard-decision Viterbi for rate 1/2 K=7. Returns decoded info bits."""
    # For the prototype, use a simple majority-vote approach as a stand-in
    # (full Viterbi is complex to implement from scratch in a prototype)
    # Actually, let's use the coded bits directly with soft information
    n_coded = 2 * n_info
    # Simple decoding: just take every other bit (T1 output) as a rough estimate
    # This is NOT a real Viterbi - just a placeholder for the turbo concept test
    # For a real test, we'd need a proper Viterbi
    decoded = np.zeros(n_info, dtype=np.int8)
    for i in range(n_info):
        # Use both T1 and T2 as votes
        decoded[i] = 1 if (coded_bits[2*i] + coded_bits[2*i+1]) > 0.5 else 0
    return decoded

# --- Channel model ---
def rayleigh_fading(n_samples, fs, doppler_hz, seed=42):
    """Generate Rayleigh fading coefficients (Jakes model simplified)."""
    rng = np.random.default_rng(seed)
    t = np.arange(n_samples) / fs
    # Sum of complex sinusoids (simplified Jakes)
    n_paths = 8
    gains = np.zeros(n_samples, dtype=complex)
    for i in range(n_paths):
        angle = 2 * np.pi * i / n_paths
        freq = doppler_hz * np.cos(angle)
        phase = rng.uniform(0, 2*np.pi)
        gains += np.exp(1j * (2*np.pi*freq*t + phase))
    gains /= np.sqrt(n_paths)
    return gains

def apply_channel(symbols, fs, snr_db, seed=42):
    """Apply 2-path Rayleigh fading + AWGN."""
    n = len(symbols)
    rng = np.random.default_rng(seed + 1000)
    
    # Path 1: direct (fading)
    h1 = rayleigh_fading(n, fs, DOPPLER_HZ / 2, seed=seed)
    # Path 2: delayed (fading)
    delay_samples = int(DELAY_MS * 1e-3 * fs)
    h2 = rayleigh_fading(n, fs, DOPPLER_HZ / 2, seed=seed + 500)
    
    # Apply channel
    rx = h1 * symbols
    if delay_samples > 0 and delay_samples < n:
        rx[delay_samples:] += h2[:-delay_samples] * symbols[:-delay_samples]
    
    # Normalize
    rx /= np.sqrt(2)  # equal power paths
    
    # AWGN
    sig_power = np.mean(np.abs(rx)**2)
    noise_power = sig_power / (10**(snr_db/10))
    noise = rng.normal(0, np.sqrt(noise_power/2), n) + 1j * rng.normal(0, np.sqrt(noise_power/2), n)
    rx += noise
    
    return rx, h1, h2

# --- DFE (simplified: just use the channel estimate from the probe) ---
def equalize_with_known(rx_frame, known_symbols, h1_frame, h2_frame, delay_samples):
    """Zero-forcing equalizer using known channel (oracle for prototype)."""
    # For the prototype, use perfect channel knowledge (oracle)
    # This gives the best possible equalization - if turbo doesn't help here,
    # it won't help with imperfect channel knowledge either
    n = len(rx_frame)
    eq = np.zeros(n, dtype=complex)
    for i in range(n):
        # Combine both paths
        h_eff = h1_frame[i]
        if i >= delay_samples:
            h_eff += h2_frame[i - delay_samples]
        if abs(h_eff) > 0.01:
            eq[i] = rx_frame[i] / h_eff
        else:
            eq[i] = rx_frame[i]
    return eq

def equalize_with_training(rx_frame, training_symbols, training_positions, h1_frame, h2_frame, delay_samples):
    """LS-based equalizer using training symbols at known positions."""
    # Estimate channel from training positions
    n = len(rx_frame)
    h_est = np.zeros(n, dtype=complex)
    
    # Interpolate channel estimate from training positions
    for pos, sym in zip(training_positions, training_symbols):
        if pos < n and abs(sym) > 0:
            h_est[pos] = rx_frame[pos] / sym
    
    # Linear interpolation of channel estimate
    valid = np.abs(h_est) > 0
    if np.sum(valid) > 1:
        indices = np.where(valid)[0]
        h_est = np.interp(np.arange(n), indices, h_est[indices])
    
    # Equalize
    eq = np.zeros(n, dtype=complex)
    for i in range(n):
        if abs(h_est[i]) > 0.01:
            eq[i] = rx_frame[i] / h_est[i]
        else:
            eq[i] = rx_frame[i]
    return eq

# --- Main simulation ---
def run_trial(snr_db, n_turbo, seed):
    fs = SYMBOL_RATE  # 1 sample per symbol for simplicity
    delay_samples = int(DELAY_MS * 1e-3 * fs)
    rng = np.random.default_rng(seed)
    
    # Generate random info bits
    n_info = U_DATA * N_FRAMES  # bits per burst (rate 1/2, BPSK: 1 bit/symbol)
    info_bits = rng.integers(0, 2, n_info).astype(np.int8)
    
    # Encode
    coded = conv_encode(info_bits)
    
    # Map to BPSK symbols (0 -> +1, 1 -> -1)
    data_symbols = 1 - 2 * coded  # rate 1/2 means 2 coded bits per info bit
    # For BPSK at rate 1/2: each coded bit is a symbol
    # Total symbols = 2 * n_info = 2 * U_DATA * N_FRAMES
    # But we have U_DATA symbols per frame... let's simplify:
    # Use U_DATA data symbols per frame, each carrying 1 coded bit
    # Total coded bits needed = U_DATA * N_FRAMES
    n_symbols_total = U_DATA * N_FRAMES
    # Truncate coded to fit
    tx_data = 1 - 2 * coded[:n_symbols_total]
    
    # Generate probe (known BPSK, alternating +1/-1)
    probe = np.array([1 if i % 2 == 0 else -1 for i in range(K_PROBE)], dtype=float)
    
    # Build full TX: [probe, data, probe, data, ...] per frame
    tx_full = np.zeros(N_FRAMES * (K_PROBE + U_DATA))
    for f in range(N_FRAMES):
        offset = f * (K_PROBE + U_DATA)
        tx_full[offset:offset+K_PROBE] = probe
        tx_full[offset+K_PROBE:offset+K_PROBE+U_DATA] = tx_data[f*U_DATA:(f+1)*U_DATA]
    
    # Apply channel
    rx, h1, h2 = apply_channel(tx_full, fs, snr_db, seed=seed)
    
    # --- First pass: equalize using probe only ---
    eq_symbols = np.zeros(n_symbols_total)
    for f in range(N_FRAMES):
        frame_start = f * (K_PROBE + U_DATA)
        data_start = frame_start + K_PROBE
        data_end = data_start + U_DATA
        
        # Use probe symbols as training
        rx_frame = rx[frame_start:frame_start + K_PROBE + U_DATA]
        h1_frame = h1[frame_start:frame_start + K_PROBE + U_DATA]
        h2_frame = h2[frame_start:frame_start + K_PROBE + U_DATA]
        
        # Equalize data portion using probe-derived channel estimate
        training_pos = np.arange(K_PROBE)
        training_sym = probe
        eq_frame = equalize_with_training(rx_frame, training_sym, training_pos, 
                                          h1_frame, h2_frame, delay_samples)
        eq_symbols[f*U_DATA:(f+1)*U_DATA] = eq_frame[K_PROBE:K_PROBE+U_DATA].real
    
    # Hard decisions and BER (first pass)
    hard_1 = (eq_symbols < 0).astype(np.int8)
    ber_pass1 = np.mean(hard_1 != coded[:n_symbols_total])
    
    # --- Turbo iterations ---
    current_decoded = hard_1.copy()
    for turbo_iter in range(n_turbo):
        # Re-encode the decoded bits to get expected symbols
        # (In real turbo: Viterbi decode → re-encode → expected coded bits → expected symbols)
        # For prototype: use the hard decisions as "decoded" and re-map to symbols
        expected_symbols = 1 - 2 * current_decoded  # BPSK mapping
        
        # Re-equalize using expected symbols as training (turbo feedback)
        eq_symbols_turbo = np.zeros(n_symbols_total)
        for f in range(N_FRAMES):
            frame_start = f * (K_PROBE + U_DATA)
            data_start = frame_start + K_PROBE
            
            rx_frame = rx[frame_start:frame_start + K_PROBE + U_DATA]
            h1_frame = h1[frame_start:frame_start + K_PROBE + U_DATA]
            h2_frame = h2[frame_start:frame_start + K_PROBE + U_DATA]
            
            # Use BOTH probe AND expected data symbols as training
            all_training_pos = np.arange(K_PROBE + U_DATA)
            all_training_sym = np.concatenate([probe, expected_symbols[f*U_DATA:(f+1)*U_DATA]])
            
            eq_frame = equalize_with_training(rx_frame, all_training_sym, all_training_pos,
                                              h1_frame, h2_frame, delay_samples)
            eq_symbols_turbo[f*U_DATA:(f+1)*U_DATA] = eq_frame[K_PROBE:K_PROBE+U_DATA].real
        
        # Hard decisions
        current_decoded = (eq_symbols_turbo < 0).astype(np.int8)
        ber_turbo = np.mean(current_decoded != coded[:n_symbols_total])
    
    return ber_pass1, ber_turbo

# --- Run multiple trials ---
if __name__ == "__main__":
    n_trials = 50
    bers_pass1 = []
    bers_turbo = []
    
    for trial in range(n_trials):
        b1, bt = run_trial(SNR_DB, N_TURBO_ITER, seed=trial * 100)
        bers_pass1.append(b1)
        bers_turbo.append(bt)
        if trial % 10 == 0:
            print(f"Trial {trial}: pass1 BER={b1:.4f}, turbo BER={bt:.4f}")
    
    print(f"\n=== Results ({n_trials} trials, SNR={SNR_DB} dB, {N_TURBO_ITER} turbo iters) ===")
    print(f"Pass 1 (probe-only):  mean BER = {np.mean(bers_pass1):.4e}")
    print(f"Turbo ({N_TURBO_ITER} iters):      mean BER = {np.mean(bers_turbo):.4e}")
    print(f"Improvement: {np.mean(bers_pass1)/max(np.mean(bers_turbo), 1e-10):.1f}×")
