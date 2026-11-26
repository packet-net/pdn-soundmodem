namespace Packet.SoundModem.Modems.OfdmFm.Link;

/// <summary>
/// Turns a burst header and a payload into audio samples: the transmit side of the adaptive OFDM-FM modem, from the
/// header and payload bytes to 48 kHz floats. Pinned to the prototype (<c>modem.waveform</c> and <c>modem.report_wave</c>)
/// and to <c>docs/dev/ofdm-fm/adaptation-protocol.md</c>.
/// </summary>
/// <remarks>
/// <para>A burst is a lead-in of silence, an opening (training symbols on the opening's carriers, then the header's
/// symbols, both at the level gain of a data burst), the payload symbols, each symbol an inverse transform with a
/// 64-sample cyclic prefix; the body is then scaled to a root mean square level for the drive and clipped at the peak
/// limit. A data burst scales by <c>RmsFull * 10^(drive/20) / rms</c> in one multiply; a report scales by
/// <c>(RmsFull / rms) * 10^(drive/20)</c> in two (the prototype's order for each) and has no level gain.</para>
/// <para>One instance per transmitter; not thread-safe. Every buffer for the longest burst is allocated by the
/// constructor, so a render allocates nothing once the code plans for its shape are cached (the first burst of a
/// new shape builds them). Arithmetic is in double; the output is converted to float last.</para>
/// </remarks>
public sealed class BurstRenderer
{
    /// <summary>The sample rate of the audio, in hertz.</summary>
    public const int SampleRate = 48000;

    /// <summary>Samples in a symbol's inverse transform.</summary>
    public const int FftSize = 2048;

    /// <summary>Samples of cyclic prefix before each symbol.</summary>
    public const int CyclicPrefix = 64;

    /// <summary>Samples in a symbol with its prefix.</summary>
    public const int SymbolSamples = FftSize + CyclicPrefix;

    /// <summary>The bin of the first carrier.</summary>
    public const int FirstBin = 9;

    /// <summary>Carriers in a symbol (all of them on the full band).</summary>
    public const int Carriers = StandardFormats.FullBandCarriers;

    /// <summary>The prototype's lead-in: two symbols of silence.</summary>
    public const int DefaultLeadSamples = 2 * SymbolSamples;

    /// <summary>The root mean square level of a burst at full drive: -12.8 dB.</summary>
    public static readonly double RmsFull = Math.Pow(10, -12.8 / 20);

    /// <summary>Samples are held to plus or minus this.</summary>
    public const double PeakLimit = 0.95;

    private const int MaxOpeningSymbols = 9;

    private static readonly OpeningShape[] Shapes = BuildShapes();

    private readonly int _lead;
    private readonly double[] _gridRe;
    private readonly double[] _gridIm;
    private readonly double[] _fftRe = new double[FftSize];
    private readonly double[] _fftIm = new double[FftSize];
    private readonly double[] _body;
    private readonly byte[] _info;
    private readonly byte[] _coded;
    private readonly byte[] _tx;
    private readonly byte[] _loading = new byte[Carriers];
    private readonly byte[] _headerBits = new byte[BurstHeaderCodec.HeaderBits];
    private readonly byte[] _headerCoded = new byte[666];
    private byte[] _scratch = new byte[1024];
    private int _gridSymbols;

    /// <summary>Makes a renderer, with every buffer for the longest burst.</summary>
    /// <param name="leadSamples">Samples of silence before the burst; at least 0.</param>
    public BurstRenderer(int leadSamples = DefaultLeadSamples)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(leadSamples);
        _lead = leadSamples;
        int maxSymbols = MaxOpeningSymbols + StandardFormats.MaxSymbols;
        _gridRe = new double[maxSymbols * Carriers];
        _gridIm = new double[maxSymbols * Carriers];
        _body = new double[maxSymbols * SymbolSamples];
        int info = 0;
        int coded = 0;
        for (int n = 0; n < StandardFormats.Count; n++)
        {
            FormatSpec s = StandardFormats.Describe(n);
            info = Math.Max(info, StandardFormats.InfoBits(n, StandardFormats.MaxSymbols));
            coded = Math.Max(coded, s.Carriers * s.ConstellationBits * StandardFormats.MaxSymbols);
        }

        _info = new byte[info];
        _coded = new byte[coded];
        _tx = new byte[coded];
    }

    /// <summary>The samples of silence before every burst.</summary>
    public int LeadSamples => _lead;

    /// <summary>How many samples the peak limit touched in the last burst: those whose size was over the limit before it acted. Zero before the first burst.</summary>
    public int LimitedSamples { get; private set; }

    /// <summary>The samples a data burst of this format and length takes, lead-in included.</summary>
    public int SamplesFor(int format, int payloadSymbols)
    {
        OpeningShape shape = Shapes[(int)StandardFormats.Describe(format).Opening];
        return _lead + ((shape.Training + shape.HeaderSymbols + payloadSymbols) * SymbolSamples);
    }

    /// <summary>The samples a report of this opening takes, lead-in included.</summary>
    public int ReportSamples(FormatOpening opening)
    {
        OpeningShape shape = ShapeOf(opening);
        return _lead + ((shape.Training + shape.HeaderSymbols) * SymbolSamples);
    }

    /// <summary>The symbols of the last burst's grid (opening and payload), for tests.</summary>
    internal int GridSymbols => _gridSymbols;

    /// <summary>The last burst's carriers before the inverse transform, real parts, symbol by symbol (333 each), for tests.</summary>
    internal ReadOnlySpan<double> GridReal => _gridRe.AsSpan(0, _gridSymbols * Carriers);

    /// <summary>The last burst's carriers before the inverse transform, imaginary parts, for tests.</summary>
    internal ReadOnlySpan<double> GridImaginary => _gridIm.AsSpan(0, _gridSymbols * Carriers);

    /// <summary>
    /// Renders a data burst. The header names the format, the length in payload symbols, sender, destination and burst
    /// number; <paramref name="payload"/> is the packed payload (see <see cref="PayloadCodec.Pack"/>), at most
    /// <see cref="PayloadCodec.Capacity"/> bytes. The drive is the format's.
    /// </summary>
    /// <returns>The samples written: <see cref="SamplesFor"/>.</returns>
    public int RenderData(in BurstHeader header, ReadOnlySpan<byte> payload, Span<float> samples)
    {
        if (header.Kind != BurstKind.Data || header.Version != 0 || header.PayloadSymbols <= 0)
        {
            throw new ArgumentException("A data burst needs a version 0 data header with payload symbols.", nameof(header));
        }

        int symbols = header.PayloadSymbols;
        FormatSpec spec = StandardFormats.Describe(header.Format);
        int total = SamplesFor(header.Format, symbols);
        if (samples.Length < total)
        {
            throw new ArgumentException($"Need room for {total} samples.", nameof(samples));
        }

        int infoBits = StandardFormats.InfoBits(header.Format, symbols);
        if (payload.Length > PayloadCodec.Capacity(header.Format, symbols))
        {
            throw new ArgumentException("The payload does not fit the burst.", nameof(payload));
        }

        BurstHeaderCodec.Write(header, _headerBits);
        OpeningShape shape = Shapes[(int)spec.Opening];
        int known = shape.Training + shape.HeaderSymbols;
        double gain = Math.Sqrt((double)spec.Carriers / shape.BandCarriers);
        BuildOpening(spec.Opening, shape, gain);

        int perSymbol = spec.Carriers * spec.ConstellationBits;
        int codedBits = symbols * perSymbol;
        Span<byte> info = _info.AsSpan(0, infoBits);
        PayloadCodec.Encode(payload, infoBits, header.Sender, header.Destination, header.BurstNumber, info);
        PayloadCode code = PayloadCode.Get(infoBits, codedBits);
        if (_scratch.Length < code.ScratchLength)
        {
            _scratch = new byte[code.ScratchLength];
        }

        Span<byte> coded = _coded.AsSpan(0, codedBits);
        code.Encode(info, coded, _scratch);
        Span<byte> tx = _tx.AsSpan(0, codedBits);
        Interleaver.Interleave<byte>(coded, tx);

        _loading.AsSpan().Clear();
        _loading.AsSpan(0, spec.Carriers).Fill((byte)spec.ConstellationBits);
        for (int s = 0; s < symbols; s++)
        {
            int at = (known + s) * Carriers;
            Constellation.MapSymbol(tx.Slice(s * perSymbol, perSymbol), _loading, _gridRe.AsSpan(at, Carriers), _gridIm.AsSpan(at, Carriers));
        }

        _gridSymbols = known + symbols;
        Span<double> body = Transform();
        double rms = Math.Sqrt(SumOfSquares(body) / body.Length);
        double scale = RmsFull * Math.Pow(10, spec.DriveDb / 20) / rms;
        for (int i = 0; i < body.Length; i++)
        {
            body[i] *= scale;
        }

        return Finish(body, samples);
    }

    /// <summary>
    /// Renders a report: the opening's training and a header, no payload, at <paramref name="driveDb"/> over full drive.
    /// Unlike a data burst it has no level gain, and scales to the level in two steps.
    /// </summary>
    /// <returns>The samples written: <see cref="ReportSamples"/>.</returns>
    public int RenderReport(in BurstHeader report, FormatOpening opening, double driveDb, Span<float> samples)
    {
        if (report.Kind != BurstKind.Report || report.Version != 0)
        {
            throw new ArgumentException("A report needs a version 0 report header.", nameof(report));
        }

        int total = ReportSamples(opening);
        if (samples.Length < total)
        {
            throw new ArgumentException($"Need room for {total} samples.", nameof(samples));
        }

        BurstHeaderCodec.Write(report, _headerBits);
        BuildOpening(opening, ShapeOf(opening), 1.0);
        _gridSymbols = Shapes[(int)opening].Training + Shapes[(int)opening].HeaderSymbols;
        Span<double> body = Transform();
        double level = RmsFull / Math.Sqrt(SumOfSquares(body) / body.Length);
        double drive = Math.Pow(10, driveDb / 20);
        for (int i = 0; i < body.Length; i++)
        {
            body[i] = body[i] * level * drive;
        }

        return Finish(body, samples);
    }

    private static OpeningShape ShapeOf(FormatOpening opening)
    {
        if ((uint)opening >= (uint)Shapes.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(opening));
        }

        return Shapes[(int)opening];
    }

    private static double SumOfSquares(ReadOnlySpan<double> x)
    {
        double sum = 0;
        for (int i = 0; i < x.Length; i++)
        {
            sum += x[i] * x[i];
        }

        return sum;
    }

    // The opening's grid: the training rows on the band's carriers, then the header's symbols, all times gain.
    private void BuildOpening(FormatOpening opening, OpeningShape shape, double gain)
    {
        for (int r = 0; r < shape.Training; r++)
        {
            Span<double> re = _gridRe.AsSpan(r * Carriers, Carriers);
            Span<double> im = _gridIm.AsSpan(r * Carriers, Carriers);
            TrainingSymbols.Points(r, re, im);
            for (int c = 0; c < Carriers; c++)
            {
                if (c >= shape.BandCarriers)
                {
                    re[c] = 0;
                    im[c] = 0;
                }
                else
                {
                    re[c] *= gain;
                    im[c] *= gain;
                }
            }
        }

        HeaderCode.Encode(opening, _headerBits, _headerCoded.AsSpan(0, shape.HeaderSymbols * 2 * shape.BandCarriers));
        _loading.AsSpan().Clear();
        _loading.AsSpan(0, shape.BandCarriers).Fill(2);
        int perSymbol = 2 * shape.BandCarriers;
        for (int h = 0; h < shape.HeaderSymbols; h++)
        {
            int at = (shape.Training + h) * Carriers;
            Span<double> re = _gridRe.AsSpan(at, Carriers);
            Span<double> im = _gridIm.AsSpan(at, Carriers);
            Constellation.MapSymbol(_headerCoded.AsSpan(h * perSymbol, perSymbol), _loading, re, im);
            for (int c = 0; c < shape.BandCarriers; c++)
            {
                re[c] *= gain;
                im[c] *= gain;
            }
        }
    }

    // The grid's symbols as the body: each an inverse transform, behind its last 64 samples.
    private Span<double> Transform()
    {
        for (int s = 0; s < _gridSymbols; s++)
        {
            Array.Clear(_fftRe);
            Array.Clear(_fftIm);
            int at = s * Carriers;
            for (int c = 0; c < Carriers; c++)
            {
                int k = FirstBin + c;
                _fftRe[k] = _gridRe[at + c];
                _fftIm[k] = _gridIm[at + c];
                _fftRe[FftSize - k] = _gridRe[at + c];
                _fftIm[FftSize - k] = -_gridIm[at + c];
            }

            RealFft.Inverse(_fftRe, _fftIm);
            int o = s * SymbolSamples;
            _fftRe.AsSpan(FftSize - CyclicPrefix, CyclicPrefix).CopyTo(_body.AsSpan(o, CyclicPrefix));
            _fftRe.CopyTo(_body.AsSpan(o + CyclicPrefix, FftSize));
        }

        return _body.AsSpan(0, _gridSymbols * SymbolSamples);
    }

    // Counts what the limit touches, clips, and writes the lead-in and the body as floats.
    private int Finish(Span<double> body, Span<float> samples)
    {
        int limited = 0;
        samples[.._lead].Clear();
        Span<float> o = samples.Slice(_lead, body.Length);
        for (int i = 0; i < body.Length; i++)
        {
            double v = body[i];
            if (Math.Abs(v) > PeakLimit)
            {
                limited++;
                v = v > 0 ? PeakLimit : -PeakLimit;
            }

            o[i] = (float)v;
        }

        LimitedSamples = limited;
        return _lead + body.Length;
    }

    private static OpeningShape[] BuildShapes()
    {
        // (training symbols, header symbols, top of the band in Hz): full, up to 3000 Hz, up to 1500 Hz.
        (int Training, int Header, double Top)[] spec = [(1, 1, 8000.0), (3, 2, 3000.0), (6, 3, 1500.0)];
        var shapes = new OpeningShape[spec.Length];
        for (int i = 0; i < spec.Length; i++)
        {
            int band = 0;
            for (int k = 0; k < Carriers; k++)
            {
                if (((FirstBin + k) * (double)SampleRate / FftSize) <= spec[i].Top)
                {
                    band++;
                }
            }

            shapes[i] = new OpeningShape(spec[i].Training, spec[i].Header, band);
        }

        return shapes;
    }

    private readonly record struct OpeningShape(int Training, int HeaderSymbols, int BandCarriers);
}
