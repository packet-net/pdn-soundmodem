using M0LTE.Dsp;
using M0LTE.Il2p;
using Packet.SoundModem.Modems;

namespace Packet.SoundModem.Ms110d;

/// <summary>
/// MIL-STD-188-110D Appendix D 3 kHz serial-tone waveform as an <see cref="IModem"/> -
/// autobaud HF single-carrier carrying IL2P+CRC-framed AX.25. Each <see cref="Modulate"/>
/// call emits one App D burst (preamble → data frames → EOM → EOT); receive is fully
/// autobaud, so one modem instance decodes any Phase A waveform number regardless of its
/// own transmit setting.
/// </summary>
/// <remarks>
/// <para><b>Payload framing.</b> App D carries an unframed bitstream, so (exactly as
/// <see cref="FreeDvDatacModem"/> does for the FreeDV raw-data layer) the family-standard
/// pdn convention applies: <see cref="Il2pCodec"/> IL2P+CRC behind the 24-bit IL2P sync
/// word, no training preamble (<c>preambleBits: 0</c> - the App D preamble already
/// delimits), one KISS transmission per burst, the EOM terminating it. Decoded block bits
/// stream through an <see cref="Il2pDeframer"/>, giving frames spanning block boundaries
/// and per-frame <see cref="FrameQuality"/>.</para>
/// <para><b>Rate bridge.</b> Native 9600 Hz (design §4.3: 4 samples/symbol at 2400 Bd);
/// 48000 = 5 × 9600 bridges integer both ways through <see cref="Decimator"/> /
/// <see cref="Upsampler"/>. The 12 kHz path is rejected (12000/9600 is not an integer).</para>
/// <para><b>Interop status.</b> Spec-faithful + mask-passing, not interop-proven: no open
/// App D implementation or off-air recording exists to test against (design §1.2; Q2 -
/// pdn↔pdn only).</para>
/// </remarks>
public sealed class Ms110dModem : IModem, IHardwareControllable
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
    public string Mode => $"ms110d-wn{Volatile.Read(ref _tx).Mode.Wn}";

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
        return true;
    }

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
        byte[] il2pWire = Il2pCodec.Encode(ax25Frame, appendCrc: true);
        byte[] bits = Il2pFramer.FrameBits(il2pWire, preambleBits: 0);
        float[] native = tx.Modulate(bits);

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
