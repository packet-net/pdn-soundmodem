namespace Packet.SoundModem.Ardop;

/// <summary>
/// ARDOP 4FSK transmitter: two-tone leader + sync, the 10-symbol 50 Bd frame type with
/// parity, template-driven data symbols and the 1500 Hz trailer, all run through
/// ardopcf's frequency-sampling TX filter. Ported from ardopcf (git a7c9228, MIT,
/// © 2014-2024 Rick Muething, John Wiseman, Peter LaRue): <c>GetTwoToneLeaderWithSync</c>
/// / <c>SendLeaderAndSYNC</c> Modulate.c:39-118, <c>Mod4FSKDataAndPlay</c> :121,
/// <c>Mod4FSK600BdDataAndPlay</c> :262, <c>AddTrailer</c> :595, and the
/// <c>initFilter</c>/<c>SampleSink</c> comb-resonator filter :676-905. Output is 12 kHz
/// 16-bit samples, sample-comparable with ardopcf's <c>--writetxwav</c> audio.
/// See PROVENANCE.md and docs/ardop-design.md §3.1-3.2.
/// </summary>
/// <remarks>
/// Sign conventions carried from the reference: leader symbols alternate polarity with
/// the final (sync) symbol repeating the polarity the <i>next</i> symbol would have
/// had; frame-type and 50/100 Bd data symbols flip polarity every symbol so there is
/// no phase discontinuity at symbol boundaries; 600 Bd symbols play their templates
/// unflipped. The filter drops its first 60 output samples (half the 120-tap comb),
/// exactly as <c>SampleSink</c> does.
/// </remarks>
public sealed class ArdopModulator
{
    /// <summary>ARDOP's native sample rate.</summary>
    public const int SampleRate = 12000;

    private readonly int _driveLevel;

    /// <summary>Creates a modulator. <paramref name="driveLevel"/> scales input samples
    /// by n/100 before the TX filter (ardopcf's DRIVELEVEL, default 100).</summary>
    public ArdopModulator(int driveLevel = 100)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(driveLevel, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(driveLevel, 100);
        _driveLevel = driveLevel;
    }

    /// <summary>
    /// Modulates an encoded frame (from <see cref="ArdopFrameCodec"/>) to 12 kHz
    /// samples. <paramref name="leaderLengthMs"/> is the two-tone leader length
    /// including the sync symbol (ardopcf default 240, negotiable 100-1000 in 20 ms
    /// steps); <paramref name="trailerLengthMs"/> adds trailer tone beyond the one
    /// mandatory symbol (default 20).
    /// </summary>
    public short[] Modulate(ReadOnlySpan<byte> encodedFrame, int leaderLengthMs = 240, int trailerLengthMs = 20)
    {
        if (encodedFrame.Length < 2)
        {
            throw new ArgumentException("encoded frame must be at least the 2 type bytes", nameof(encodedFrame));
        }

        var info = ArdopFrameInfo.Get(encodedFrame[0]);
        if (info.Modulation != ArdopModulation.Fsk4)
        {
            throw new NotSupportedException(
                $"{info.Name}: only the 4FSK modes are modulated in Phase A (PSK/QAM are Phase C)");
        }

        // Mod4FSKDataAndPlay: 50 Bd → 200 Hz filter; 100 Bd → 500 Hz;
        // Mod4FSK600BdDataAndPlay: 600 Bd → 2000 Hz. All centred on 1500 Hz.
        int filterWidth = info.Baud switch { 50 => 200, 100 => 500, _ => 2000 };
        var filter = new TxFilter(filterWidth, 1500, _driveLevel);

        // Leader + sync (GetTwoToneLeaderWithSync).
        int leaderSymbols = leaderLengthMs / 20;
        int sign = (leaderSymbols & 1) == 1 ? -1 : 1;
        for (int i = 0; i < leaderSymbols; i++)
        {
            int symbolSign = i == leaderSymbols - 1 ? -sign : sign;
            foreach (short sample in ArdopTxTemplates.Leader50Bd)
            {
                filter.Sink((short)(symbolSign * sample));
            }

            sign = -sign;
        }

        // Frame type: 2 bytes as 10 × 50 Bd symbols — 4 data dibits + 1 parity symbol
        // per byte, both parity symbols computed from the plain type byte
        // (SendLeaderAndSYNC, Modulate.c:90).
        for (int j = 0; j < 2; j++)
        {
            byte mask = 0xC0;
            for (int k = 0; k < 5; k++)
            {
                byte symbol = k < 4
                    ? (byte)((mask & encodedFrame[j]) >> (2 * (3 - k)))
                    : ArdopFrameType.TypeParity(encodedFrame[0]);
                short[] template = ArdopTxTemplates.Fsk50Bd[symbol];
                bool positive = ((5 * j + k) & 1) == 0;
                foreach (short sample in template)
                {
                    filter.Sink(positive ? sample : (short)-sample);
                }

                mask >>= 2;
            }
        }

        // Data symbols.
        short[][] templates = info.Baud switch
        {
            50 => ArdopTxTemplates.Fsk50Bd,
            100 => ArdopTxTemplates.Fsk100Bd,
            _ => ArdopTxTemplates.Fsk600Bd,
        };
        for (int m = 2; m < encodedFrame.Length; m++)
        {
            byte mask = 0xC0;
            for (int k = 0; k < 4; k++)
            {
                byte symbol = (byte)((mask & encodedFrame[m]) >> (2 * (3 - k)));
                short[] template = templates[symbol];
                if (info.Baud == 600)
                {
                    // 600 Bd symbols are played unflipped (Mod4FSK600BdDataAndPlay).
                    foreach (short sample in template)
                    {
                        filter.Sink(sample);
                    }
                }
                else
                {
                    bool positive = (k & 1) == 0;
                    foreach (short sample in template)
                    {
                        filter.Sink(positive ? sample : (short)-sample);
                    }
                }

                mask >>= 2;
            }
        }

        // Trailer: 1 + trailerMs/10 symbols of the 1500 Hz reference tone (AddTrailer).
        int trailerSymbols = 1 + trailerLengthMs / 10;
        for (int i = 0; i < trailerSymbols; i++)
        {
            foreach (short sample in ArdopTxTemplates.Trailer1500Hz)
            {
                filter.Sink(sample);
            }
        }

        return filter.Drain();
    }

    /// <summary>
    /// ardopcf's TX frequency-sampling filter (<c>initFilter</c>/<c>SampleSink</c>,
    /// Modulate.c:676-905): a 120-sample comb feeding a bank of 100 Hz resonators
    /// around 1500 Hz — 3 sections for the 200 Hz width, 7 for 500, 11 for 1000,
    /// 21 for 2000 — with the reference's exact transition coefficients, float
    /// arithmetic and int truncations preserved.
    /// </summary>
    private sealed class TxFilter
    {
        private const int N = 120;             // intN — comb length, 12000/100
        private const float R = 0.9995f;       // dblR — pole radius

        private readonly int _width;
        private readonly int _first;
        private readonly int _last;
        private readonly int _driveLevel;
        private readonly float _rn;
        private readonly float _r2;
        private readonly float[] _coef = new float[32];
        private readonly float[] _zout0 = new float[32];
        private readonly float[] _zout1 = new float[32];
        private readonly float[] _zout2 = new float[32];
        private readonly short[] _last120 = new short[121];
        private readonly List<short> _output = [];

        private float _zin1;
        private float _zin2;
        private int _sampleNo;
        private int _get;
        private int _put = 120;

        public TxFilter(int width, int centreHz, int driveLevel)
        {
            _width = width;
            _driveLevel = driveLevel;
            int centreSlot = centreHz / 100;
            (_first, _last) = width switch
            {
                200 => (centreSlot - 1, centreSlot + 1),
                500 => (centreSlot - 3, centreSlot + 3),
                1000 => (centreSlot - 5, centreSlot + 5),
                2000 => (centreSlot - 10, centreSlot + 10),
                _ => throw new ArgumentException($"unsupported filter width {width}", nameof(width)),
            };

            _rn = MathF.Pow(R, N);
            _r2 = MathF.Pow(R, 2);
            for (int i = _first; i <= _last; i++)
            {
                _coef[i] = 2 * R * MathF.Cos(2 * MathF.PI * i / N);
            }
        }

        public void Sink(short sample)
        {
            const int FilterDelay = N / 2;

            sample = (short)(sample * _driveLevel / 100);

            float zin = _sampleNo < N
                ? sample
                : sample - _rn * _last120[_get];
            if (++_get == 121)
            {
                _get = 0;
            }

            // The comb.
            float zcomb = zin - _zin2 * _r2;
            _zin2 = _zin1;
            _zin1 = zin;

            // The resonators. The (int) truncations on the inner sections of the wider
            // filters are ardopcf's — kept for sample-exactness, not taste.
            float filtered = 0;
            for (int j = _first; j <= _last; j++)
            {
                _zout0[j] = zcomb + _coef[j] * _zout1[j] - _r2 * _zout2[j];
                _zout2[j] = _zout1[j];
                _zout1[j] = _zout0[j];

                if (_sampleNo < FilterDelay)
                {
                    continue;
                }

                switch (_width)
                {
                    case 200:
                        if (j == _first || j == _last)
                        {
                            filtered += 0.7389f * _zout0[j];
                        }
                        else
                        {
                            filtered -= _zout0[j];
                        }

                        break;

                    case 500:
                        if (j == _first || j == _last)
                        {
                            filtered += 0.10601f * _zout0[j];
                        }
                        else if (j == _first + 1 || j == _last - 1)
                        {
                            filtered -= 0.59383f * _zout0[j];
                        }
                        else if ((j & 1) == 0)
                        {
                            filtered += (int)_zout0[j];
                        }
                        else
                        {
                            filtered -= (int)_zout0[j];
                        }

                        break;

                    case 1000:
                        if (j == _first || j == _last)
                        {
                            filtered += 0.377f * _zout0[j];
                        }
                        else if ((j & 1) == 0)
                        {
                            filtered += (int)_zout0[j];
                        }
                        else
                        {
                            filtered -= (int)_zout0[j];
                        }

                        break;

                    default: // 2000
                        if (j == _first || j == _last)
                        {
                            filtered += 0.371f * _zout0[j];
                        }
                        else if ((j & 1) == 0)
                        {
                            filtered += (int)_zout0[j];
                        }
                        else
                        {
                            filtered -= (int)_zout0[j];
                        }

                        break;
                }
            }

            if (_sampleNo >= FilterDelay)
            {
                filtered *= 0.00833333333f; // 1/120 — rescale for filter gain
                if (filtered > 32700)
                {
                    filtered = 32700;
                }
                else if (filtered < -32700)
                {
                    filtered = -32700;
                }

                _output.Add((short)filtered);
            }

            _last120[_put++] = sample;
            if (_put == 121)
            {
                _put = 0;
            }

            _sampleNo++;
        }

        public short[] Drain() => [.. _output];
    }
}
