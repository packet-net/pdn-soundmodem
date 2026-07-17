using Packet.SoundModem.Dsp;
using M0LTE.Il2p;
using Packet.SoundModem.Modems;

namespace Packet.SoundModem.Ms110d;

/// <summary>
/// MIL-STD-188-110D Appendix D 3 kHz serial-tone waveform as an <see cref="IModem"/> —
/// autobaud HF single-carrier carrying IL2P+CRC-framed AX.25. Each <see cref="Modulate"/>
/// call emits one App D burst (preamble → data frames → EOM → EOT); receive is fully
/// autobaud, so one modem instance decodes any Phase A waveform number regardless of its
/// own transmit setting.
/// </summary>
/// <remarks>
/// <para><b>Payload framing.</b> App D carries an unframed bitstream, so (exactly as
/// <see cref="FreeDvDatacModem"/> does for the FreeDV raw-data layer) the family-standard
/// pdn convention applies: <see cref="Il2pCodec"/> IL2P+CRC behind the 24-bit IL2P sync
/// word, no training preamble (<c>preambleBits: 0</c> — the App D preamble already
/// delimits), one KISS transmission per burst, the EOM terminating it. Decoded block bits
/// stream through an <see cref="Il2pDeframer"/>, giving frames spanning block boundaries
/// and per-frame <see cref="FrameQuality"/>.</para>
/// <para><b>Rate bridge.</b> Native 9600 Hz (design §4.3: 4 samples/symbol at 2400 Bd);
/// 48000 = 5 × 9600 bridges integer both ways through <see cref="Decimator"/> /
/// <see cref="Upsampler"/>. The 12 kHz path is rejected (12000/9600 is not an integer).</para>
/// <para><b>Interop status.</b> Spec-faithful + mask-passing, not interop-proven: no open
/// App D implementation or off-air recording exists to test against (design §1.2; Q2 —
/// pdn↔pdn only).</para>
/// </remarks>
public sealed class Ms110dModem : IModem
{
    /// <summary>Native DSP rate (4 samples/symbol at 2400 Bd).</summary>
    private const int NativeRate = Ms110dModulator.NativeRate;

    private readonly Action<byte[]> _frameReceived;
    private readonly Ms110dModulator _tx;
    private readonly Ms110dDemodulator _rx;
    private readonly Il2pDeframer _deframer;
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
    public Ms110dModem(
        int sampleRate,
        Action<byte[]> frameReceived,
        Ms110dTxSettings? tx = null,
        Ms110dDemodOptions? rx = null)
    {
        ArgumentNullException.ThrowIfNull(frameReceived);
        if (sampleRate < NativeRate || sampleRate % NativeRate != 0)
        {
            throw new ArgumentException(
                $"sample rate must be an integer multiple of {NativeRate}; use the 48 kHz " +
                "DSP path — 12 kHz has no integer ratio to 9600",
                nameof(sampleRate));
        }

        _frameReceived = frameReceived;
        _sampleRate = sampleRate;
        _tx = new Ms110dModulator(tx ?? new Ms110dTxSettings());
        _rx = new Ms110dDemodulator(rx);
        _deframer = new Il2pDeframer(
            (frame, info) =>
            {
                _frameReceived(frame);
                FrameDecoded?.Invoke(frame, new FrameQuality(
                    Mode, frame.Length, info.CorrectedSymbols, info.CrcValid,
                    FrequencyOffsetHz: _rx.Lock?.CfoHz));
            },
            crcMode: true);
        _rx.BlockDecoded += block =>
        {
            foreach (byte bit in block.Bits)
            {
                _deframer.PushBit(bit);
            }
        };
        _rx.BurstCompleted += _ => _deframer.Reset();
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

    /// <inheritdoc />
    public string Mode => $"ms110d-wn{_tx.Mode.Wn}";

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
        byte[] il2pWire = Il2pCodec.Encode(ax25Frame, appendCrc: true);
        byte[] bits = Il2pFramer.FrameBits(il2pWire, preambleBits: 0);
        float[] native = _tx.Modulate(bits);

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
