using M0LTE.Dsp;
using M0LTE.Il2p;
using Packet.SoundModem.Modems;

namespace Packet.SoundModem.Ms110d;

/// <summary>
/// MIL-STD-188-110D Appendix D 3 kHz serial-tone waveform as an <see cref="IModem"/> -
/// autobaud HF single-carrier carrying IL2P+CRC-framed AX.25. Each <see cref="Modulate"/>
/// call emits one App D burst (preamble, data frames, EOM, EOT), and with
/// <see cref="Packing"/> set, <see cref="ModulateFrames"/> puts several frames in one; receive is
/// fully autobaud, so one modem instance decodes any Phase A waveform number regardless of its
/// own transmit setting.
/// </summary>
/// <remarks>
/// <para><b>Payload framing.</b> App D carries an unframed bitstream, so (exactly as
/// <see cref="FreeDvDatacModem"/> does for the FreeDV raw-data layer) the family-standard
/// pdn convention applies: <see cref="Il2pCodec"/> IL2P+CRC behind the 24-bit IL2P sync
/// word, no training preamble (<c>preambleBits: 0</c> - the App D preamble already
/// delimits), the EOM terminating the burst. By default that is one KISS transmission per
/// burst; with <see cref="Packing"/> set, frames queued together go out as IL2P+CRC frames back
/// to back in one burst's bit stream, behind one preamble and ahead of one EOM. Decoded block
/// bits stream through an <see cref="Il2pDeframer"/>, giving frames spanning block boundaries
/// and per-frame <see cref="FrameQuality"/>, so the receiver needs nothing to read a packed
/// burst.</para>
/// <para><b>Rate bridge.</b> Native 9600 Hz (design §4.3: 4 samples/symbol at 2400 Bd);
/// 48000 = 5 × 9600 bridges integer both ways through <see cref="Decimator"/> /
/// <see cref="Upsampler"/>. The 12 kHz path is rejected (12000/9600 is not an integer).</para>
/// <para><b>Interop status.</b> Spec-faithful + mask-passing, not interop-proven: no open
/// App D implementation or off-air recording exists to test against (design §1.2; Q2 -
/// pdn↔pdn only).</para>
/// </remarks>
public sealed class Ms110dModem : IModem, IHardwareControllable, IFramePackingModem
{
    /// <summary>Native DSP rate (4 samples/symbol at 2400 Bd).</summary>
    private const int NativeRate = Ms110dModulator.NativeRate;

    private readonly Action<byte[]> _frameReceived;

    /// <summary>The transmitter, swapped whole under <see cref="SetTxWaveform"/>: a modulator
    /// is immutable once built, so a waveform change builds a fresh one and publishes it with
    /// a volatile write. <see cref="Modulate"/> reads the reference once per burst, so a
    /// change lands between bursts, never inside one. Receive is untouched - it is autobaud,
    /// decoding every Phase A waveform whatever this is set to.</summary>
    private Ms110dModulator _tx;

    private Ms110dTxSettings _txSettings;
    private FramePacking? _packing;
    private readonly Ms110dDemodulator _rx;
    private readonly Il2pReceiver _deframer;
    private readonly EnergyBusyDetector _energyBusy;
    private readonly FirFilter _busyBandpass;
    private readonly Decimator? _decimator;
    private readonly Upsampler? _upsampler;
    private readonly float[] _decimated = new float[4096];
    private readonly int _sampleRate;

    /// <summary>Creates the modem.</summary>
    /// <param name="sampleRate">Channel DSP rate; must be an integer multiple of 9600
    /// (48000 on the daemon's 48 kHz path; 9600 runs natively).</param>
    /// <param name="frameReceived">Receives each decoded AX.25 frame.</param>
    /// <param name="tx">Transmit configuration (waveform number, interleaver, K, M, TLC,
    /// EOM/EOT). Default: WN 6, Short, K7, M 3.</param>
    /// <param name="rx">Receiver options.</param>
    /// <param name="acceptPlainIl2p">Pass frames that arrive as plain IL2P, with no trailing CRC,
    /// to <paramref name="frameReceived"/> as well as reporting them (off by default). They are
    /// read either way - see <see cref="Modems.Il2pReceiver"/> for what that buys and what it
    /// costs.</param>
    public Ms110dModem(
        int sampleRate,
        Action<byte[]> frameReceived,
        Ms110dTxSettings? tx = null,
        Ms110dDemodOptions? rx = null,
        bool acceptPlainIl2p = false)
    {
        ArgumentNullException.ThrowIfNull(frameReceived);
        if (sampleRate < NativeRate || sampleRate % NativeRate != 0)
        {
            throw new ArgumentException(
                $"sample rate must be an integer multiple of {NativeRate}; use the 48 kHz " +
                "DSP path - 12 kHz has no integer ratio to 9600",
                nameof(sampleRate));
        }

        _frameReceived = frameReceived;
        _sampleRate = sampleRate;
        _txSettings = tx ?? new Ms110dTxSettings();
        _tx = new Ms110dModulator(_txSettings);
        _rx = new Ms110dDemodulator(rx);
        _deframer = new Il2pReceiver(
            (frame, info, delivery) =>
            {
                if (!delivery.MonitorOnly)
                {
                    _frameReceived(frame);
                }

                FrameDecoded?.Invoke(frame, new FrameQuality(
                    Mode, frame.Length, info.CorrectedSymbols, info.CrcValid,
                    FrequencyOffsetHz: _rx.Lock?.CfoHz,
                    PlainIl2p: delivery.PlainIl2p,
                    TrailerNearBits: delivery.TrailerNearBits,
                    MonitorOnly: delivery.MonitorOnly));
            },
            crcMode: true, acceptPlainIl2p: acceptPlainIl2p);
        _rx.BlockDecoded += block =>
        {
            foreach (byte bit in block.Bits)
            {
                _deframer.PushBit(bit);
            }
        };
        _rx.BurstCompleted += burst =>
        {
            _deframer.Reset();
            if (burst.Reason == Ms110dBurstEndReason.SignalAbsent)
            {
                LocksReleased++;
                string wn = burst.Lock is { } locked ? $"wn{locked.WaveformNumber}" : "burst";
                LockReleased?.Invoke(
                    $"ms110d: let go of a {wn} lock with no signal left on it " +
                    $"({Ms110dDemodulator.PresenceWindowSeconds:0} s of noise-only mini-probes, {burst.Blocks} blocks read); listening afresh");
            }
        };
        _energyBusy = new EnergyBusyDetector(NativeRate);
        _busyBandpass = new FirFilter(FilterDesign.BandPass(180, 3420, NativeRate, 96));
        if (sampleRate != NativeRate)
        {
            int factor = sampleRate / NativeRate;
            _decimator = new Decimator(sampleRate, factor);
            _upsampler = new Upsampler(sampleRate, factor);
        }
    }

    /// <inheritdoc />
    public event Action<byte[], FrameQuality>? FrameDecoded;

    /// <summary>The receiver, for instruments and tests.</summary>
    internal Ms110dDemodulator Receiver => _rx;

    /// <inheritdoc />
    public string Mode => $"ms110d-wn{Volatile.Read(ref _tx).Mode.Wn}";

    /// <summary>
    /// The waveform number the receiver has locked to, read from the preamble of the burst it is
    /// decoding now, or null while it is searching or still reading that preamble.
    /// </summary>
    /// <remarks>
    /// This is the receive side's autobaud result, and it is not <see cref="Mode"/>, which names
    /// the transmit waveform (as each frame's <see cref="FrameQuality.Mode"/> does). A receiver
    /// that wants the speed it is hearing reads this. It is updated on the thread calling
    /// <see cref="Process"/> and read through a volatile read, so another thread sees the latest
    /// lock, at most one block behind.
    /// </remarks>
    public int? LockedWaveformNumber => _rx.Lock is { WaveformNumber: >= 0 and var wn } ? wn : null;

    /// <summary>
    /// Switches the TRANSMIT waveform at runtime - the typed API the KISS SETHW path calls
    /// after parsing its bytes, and the one an in-process host (the pdn node's
    /// <c>kind: soundmodem</c> transport) should call directly. Applies from the next burst;
    /// a burst already rendering keeps the modulator it started with. Receive needs nothing:
    /// it is autobaud, so this modem goes on decoding every Phase A waveform regardless.
    /// </summary>
    /// <param name="waveformNumber">Phase A waveform number: 0-8 or 13.</param>
    /// <param name="interleaver">New interleaver, or null to keep the current one.</param>
    /// <exception cref="ArgumentException">The waveform number or interleaver combination is
    /// not one MIL-STD-188-110D Phase A defines (thrown before anything changes).</exception>
    public void SetTxWaveform(int waveformNumber, Ms110dInterleaverKind? interleaver = null)
    {
        Ms110dTxSettings next = _txSettings with
        {
            WaveformNumber = waveformNumber,
            Interleaver = interleaver ?? _txSettings.Interleaver,
        };

        // Built before anything is published: an invalid combination throws here and the
        // modem keeps transmitting exactly what it was.
        var modulator = new Ms110dModulator(next);
        _txSettings = next;
        Volatile.Write(ref _tx, modulator);
    }

    /// <inheritdoc />
    public bool TrySetHardware(ReadOnlySpan<byte> payload, out string outcome)
    {
        if (payload.Length is < 1 or > 2)
        {
            outcome = "SETHW payload must be 1-2 bytes: waveform number, optional interleaver";
            return false;
        }

        Ms110dInterleaverKind? interleaver = null;
        if (payload.Length == 2)
        {
            if (payload[1] > 1)
            {
                outcome = $"interleaver byte {payload[1]} is not 0 (short) or 1 (long)";
                return false;
            }

            interleaver = payload[1] == 0 ? Ms110dInterleaverKind.Short : Ms110dInterleaverKind.Long;
        }

        try
        {
            SetTxWaveform(payload[0], interleaver);
        }
        catch (ArgumentException refused)
        {
            outcome = refused.Message;
            return false;
        }

        outcome = $"{Mode}, {_txSettings.Interleaver.ToString().ToLowerInvariant()} interleaver";
        if (PoorStatusNote(_txSettings.WaveformNumber) is { } note)
        {
            outcome += $" ({note})";
        }

        return true;
    }

    /// <summary>
    /// What the validation ledger (docs/dev/mode-validation.md) says about a waveform's standing
    /// on the D.6.1 Poor channel, for the journal at configuration and SETHW time - so the
    /// product says what the record says (Poor-gate successor program G3). Null for the
    /// waveforms that are hard-gated on Poor and on-air proven. Plain ASCII, one line.
    /// </summary>
    public static string? PoorStatusNote(int waveformNumber) => waveformNumber switch
    {
        7 => "wn7 Poor-channel standing: sim hard-gated since 2026-08-20 (the 8PSK ensemble); "
            + "no hardware confirmation exists at its +19 dB mask point",
        8 => "wn8 Poor-channel standing: measured-only, 2.9E-4 canonical / 1.8E-2 disjoint against "
            + "the 1E-5 mask; on 2026-08-03 16QAM did not carry over a 48 W NVIS path",
        _ => null,
    };

    /// <summary>
    /// Fires once each time the receiver lets go of a lock because its mini-probes have shown
    /// no signal for a whole window (issue #553): a burst too weak to read was acquired and
    /// has ended, or its signal faded out for good. One plain-ASCII line for the host's
    /// journal. Raised on the thread calling <see cref="Process"/>, after
    /// <see cref="CarrierDetect"/> has dropped.
    /// </summary>
    public event Action<string>? LockReleased;

    /// <summary>The MS110D modem behind <paramref name="modem"/>: the modem itself, or the one a
    /// <see cref="FrequencyShiftedModem"/> moved; null for any other modem.</summary>
    public static Ms110dModem? Unwrap(IModem modem) => modem switch
    {
        Ms110dModem ms110d => ms110d,
        FrequencyShiftedModem { Inner: Ms110dModem moved } => moved,
        _ => null,
    };

    /// <summary>How many times <see cref="LockReleased"/> has fired since construction.
    /// <see cref="ResetCarrierState"/> does not clear it.</summary>
    public int LocksReleased { get; private set; }

    /// <inheritdoc />
    public bool CarrierDetect => _rx.CarrierDetect;

    /// <inheritdoc />
    public bool ChannelBusy => CarrierDetect || _energyBusy.Busy;

    /// <inheritdoc />
    public void Process(ReadOnlySpan<float> samples)
    {
        if (_decimator is null)
        {
            FeedNative(samples);
            return;
        }

        for (int offset = 0; offset < samples.Length;)
        {
            int chunk = Math.Min(samples.Length - offset, (_decimated.Length - 1) * (_sampleRate / NativeRate));
            int produced = _decimator.Process(samples.Slice(offset, chunk), _decimated);
            FeedNative(_decimated.AsSpan(0, produced));
            offset += chunk;
        }
    }

    /// <inheritdoc />
    public float[] Modulate(ReadOnlySpan<byte> ax25Frame, int txDelayMilliseconds)
    {
        // One read per burst: a concurrent SetTxWaveform applies to the next burst whole.
        Ms110dModulator tx = Volatile.Read(ref _tx);
        byte[] bits = FrameBits(ax25Frame);
        return Render(tx.Modulate(bits), txDelayMilliseconds);
    }

    /// <inheritdoc />
    /// <remarks>
    /// <see cref="FramePacking.MaxBurst"/> must be positive. Read once per burst by the channel,
    /// so a change applies from the next one.
    /// </remarks>
    public FramePacking? Packing
    {
        get => Volatile.Read(ref _packing);
        set
        {
            if (value is { } packing && (packing.MaxBurst <= TimeSpan.Zero || packing.Gather < TimeSpan.Zero))
            {
                throw new ArgumentOutOfRangeException(
                    nameof(value), "a packed burst needs a positive length and a gather of zero or more");
            }

            Volatile.Write(ref _packing, value);
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// Sized with the modulator's own <see cref="Ms110dModulator.BurstSeconds"/>, so the count is
    /// exactly what <see cref="ModulateFrames"/> will render: one preamble, every frame's IL2P+CRC
    /// bits back to back, and the EOM, rounded up to whole interleaver blocks.
    /// </remarks>
    public int FramesPerBurst(IReadOnlyList<byte[]> frames)
    {
        ArgumentNullException.ThrowIfNull(frames);
        if (frames.Count == 0)
        {
            return 0;
        }

        if (Packing is not { } packing)
        {
            return 1;
        }

        Ms110dModulator tx = Volatile.Read(ref _tx);
        double limit = packing.MaxBurst.TotalSeconds;
        int bits = 0;
        int fit = 0;
        foreach (byte[] frame in frames)
        {
            int frameBits;
            try
            {
                frameBits = FrameBits(frame).Length;
            }
            catch (ArgumentException)
            {
                // A frame IL2P will not carry. It is sent (and refused) on its own, never inside
                // somebody else's burst.
                break;
            }

            if (fit > 0 && tx.BurstSeconds(bits + frameBits) > limit)
            {
                break;
            }

            bits += frameBits;
            fit++;
        }

        return Math.Max(1, fit);
    }

    /// <inheritdoc />
    public float[] ModulateFrames(IReadOnlyList<byte[]> frames, int txDelayMilliseconds)
    {
        ArgumentNullException.ThrowIfNull(frames);
        if (frames.Count == 0)
        {
            throw new ArgumentException("a burst needs at least one frame", nameof(frames));
        }

        Ms110dModulator tx = Volatile.Read(ref _tx);
        var encoded = new byte[frames.Count][];
        int total = 0;
        for (int i = 0; i < frames.Count; i++)
        {
            encoded[i] = FrameBits(frames[i]);
            total += encoded[i].Length;
        }

        // Back to back, with nothing between them: each frame's IL2P sync word follows the last
        // bit of the frame before, which is what the receiver's deframer hunts for anyway.
        var bits = new byte[total];
        int at = 0;
        foreach (byte[] frameBits in encoded)
        {
            frameBits.CopyTo(bits, at);
            at += frameBits.Length;
        }

        return Render(tx.Modulate(bits), txDelayMilliseconds);
    }

    /// <summary>One frame's IL2P+CRC wire bits behind the 24-bit sync word, no training
    /// preamble (the App D preamble already delimits).</summary>
    private static byte[] FrameBits(ReadOnlySpan<byte> ax25Frame) =>
        Il2pFramer.FrameBits(Il2pCodec.Encode(ax25Frame, appendCrc: true), preambleBits: 0);

    /// <summary>A native-rate burst with TXDELAY of silence ahead of it, at the channel rate.</summary>
    private float[] Render(float[] native, int txDelayMilliseconds)
    {
        int delayNative = NativeRate * Math.Max(0, txDelayMilliseconds) / 1000;
        var burst = new float[delayNative + native.Length];
        native.CopyTo(burst, delayNative);

        if (_upsampler is null)
        {
            return burst;
        }

        var upsampled = new float[_upsampler.OutputLength(burst.Length)];
        _upsampler.Process(burst, upsampled);
        return upsampled;
    }

    /// <inheritdoc />
    public void ResetCarrierState()
    {
        _rx.Reset();
        _deframer.Reset();
        _energyBusy.Reset();
    }

    private void FeedNative(ReadOnlySpan<float> samples)
    {
        foreach (float sample in samples)
        {
            _energyBusy.Process(_busyBandpass.Next(sample));
        }

        _rx.Process(samples);
    }
}
