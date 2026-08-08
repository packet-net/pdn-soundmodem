using M0LTE.Fec;

namespace Packet.SoundModem.Modems.OfdmAb;

/// <summary>What one received burst came to.</summary>
/// <param name="Payload">The recovered bytes, or null if nothing decoded.</param>
/// <param name="Constellation">The constellation its header announced.</param>
/// <param name="StartSample">Where in the fed audio the burst was found.</param>
public sealed record OfdmAbBurst(byte[]? Payload, OfdmAbConstellation Constellation, int StartSample);

/// <summary>
/// An audio-band OFDM modem: a real-valued transform's subcarriers, each carrying a QAM symbol,
/// inside the audio passband of an ordinary FM radio.
/// </summary>
/// <remarks>
/// <para><b>This is not OFDM-AB, and must not be called it.</b> OFDM-AB's waveform specification
/// is neither public nor final as of 2026-08-08. What is implemented here is the machinery such a
/// waveform needs - real-FFT symbols with a cyclic prefix, a correlated preamble for timing, a
/// channel estimate taken from it, pilot-tracked phase, Gray-coded QAM from one to eight bits per
/// carrier, and a CRC-checked frame - built so that the parts a specification actually fixes are
/// parameters rather than assumptions. See <see cref="OfdmAbParameters"/> for why the geometry
/// lives outside the source.</para>
/// <para><b>Burst structure</b>, which is ours and provisional: one preamble symbol carrying a
/// known pseudo-random BPSK pattern on every occupied carrier; one header symbol, BPSK on the data
/// carriers, giving the payload constellation, its length and a CRC over both; then the payload
/// symbols at that constellation, scrambled, with a CRC-16 trailer.</para>
/// <para><b>Not modelled</b>: carrier frequency offset, because an FM audio path has none - a
/// discriminator hands back baseband audio, and what remains is a sample-clock difference between
/// the two soundcards, which shows up as slow phase rotation that the pilots absorb. A future
/// version wanting to work over SSB would need real frequency recovery.</para>
/// </remarks>
public sealed class OfdmAbModem
{
    private const int HeaderBits = 40; // 4 constellation, 20 length, 16 CRC
    private const ushort ScramblerSeed = 0x1FF;

    private readonly OfdmAbParameters _parameters;
    private readonly bool[] _pilotMap;
    private readonly double[] _preambleSymbol;
    private readonly (double Re, double Im)[] _preambleBins;
    private readonly double _drive;
    private readonly int _headerSymbols;
    private readonly double[] _syncSymbol;
    private readonly OfdmAbCodec _codec;

    /// <summary>Creates a modem for one bandwidth profile.</summary>
    public OfdmAbModem(OfdmAbParameters parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        parameters.Validate();
        _parameters = parameters;
        _pilotMap = parameters.PilotMap();

        // A known pattern on every occupied carrier: it times the burst by correlation and then
        // measures the channel, which is why it covers pilots and data alike.
        var reference = new (double Re, double Im)[parameters.TotalCarriers];
        var rng = new Random(20260808);
        for (int c = 0; c < reference.Length; c++)
        {
            reference[c] = (rng.Next(2) == 0 ? -1 : 1, 0);
        }

        _preambleBins = reference;

        // One drive level for the whole burst, set once from the preamble. Normalising each
        // symbol to its own peak would be tidier to look at and quietly fatal: it would rescale
        // every symbol differently, and a QAM constellation carries information in amplitude.
        double[] raw = RenderSymbol(reference, scale: 1.0);
        double peak = 0;
        foreach (double sample in raw)
        {
            peak = Math.Max(peak, Math.Abs(sample));
        }

        _drive = peak > 0 ? 0.7 / peak : 1.0;
        _preambleSymbol = RenderSymbol(reference, _drive);

        // A sync symbol that modulates only every second carrier, so its useful part is two
        // identical halves. Timing then comes from correlating the received signal against ITSELF
        // half a symbol later, which no channel can spoil because both halves travel the same
        // path - where correlating against a clean reference fails the moment the path tilts.
        // EVEN ABSOLUTE BINS, not every second occupied carrier. A bin repeats over half a symbol
        // only if its index is even; an odd one anti-repeats, so with an odd first carrier the two
        // halves come out sign-flipped and the correlation peaks at -1 where the search looks for
        // +1. That is a signal which looks perfectly healthy and decodes to nothing, and a
        // synthetic profile with an even first carrier hides it completely - which is exactly what
        // happened here until the geometry changed underneath it.
        var syncCarriers = new (double Re, double Im)[parameters.TotalCarriers];
        for (int c = 0; c < syncCarriers.Length; c++)
        {
            if (((parameters.FirstCarrier + c) & 1) == 0)
            {
                syncCarriers[c] = (reference[c].Re * Math.Sqrt(2), 0);
            }
        }

        _syncSymbol = RenderSymbol(syncCarriers, _drive);
        _codec = new OfdmAbCodec(parameters.Codes);

        // A header may not fit one symbol: a narrow profile has few data carriers, and BPSK gives
        // one bit each. Span as many symbols as it takes rather than assuming.
        _headerSymbols = ((HeaderBits + parameters.DataCarriers) - 1) / parameters.DataCarriers;
    }

    /// <summary>The profile this modem runs.</summary>
    public OfdmAbParameters Parameters => _parameters;

    /// <summary>Renders one burst carrying <paramref name="payload"/>.</summary>
    /// <param name="payload">Bytes to carry; a CRC-16 is appended.</param>
    /// <param name="constellation">Bits per subcarrier for the payload symbols.</param>
    /// <param name="leadInSymbols">Silent symbols before the preamble, so a receiver's acquisition
    /// has somewhere to settle.</param>
    public float[] Modulate(
        ReadOnlySpan<byte> payload, OfdmAbConstellation constellation, int leadInSymbols = 1)
    {
        byte[] framed = new byte[payload.Length + 2];
        payload.CopyTo(framed);
        ushort crc = Crc16X25.Compute(payload);
        framed[^2] = (byte)(crc & 0xFF);
        framed[^1] = (byte)(crc >> 8);
        Scramble(framed);

        var audio = new List<float>(_parameters.SymbolSamples * 8);
        for (int s = 0; s < leadInSymbols * _parameters.SymbolSamples; s++)
        {
            audio.Add(0f);
        }

        AppendSymbol(audio, _syncSymbol);
        AppendSymbol(audio, _preambleSymbol);
        foreach ((double Re, double Im)[] headerSymbol in HeaderSymbols(constellation, payload.Length))
        {
            AppendSymbol(audio, RenderSymbol(headerSymbol, _drive));
        }

        int[] carrierBits = _parameters.BitsPerDataCarrier(constellation);
        byte[] coded = _codec.Encode(Unpack(framed));
        var points = new Dictionary<int, (float I, float Q)[]>();
        foreach (int bits in carrierBits.Distinct())
        {
            points[bits] = OfdmAbMapper.Points((OfdmAbConstellation)bits);
        }

        int perSymbol = carrierBits.Sum();
        int symbols = ((coded.Length + perSymbol) - 1) / perSymbol;
        int read = 0;
        for (int s = 0; s < symbols; s++)
        {
            var carriers = new (double Re, double Im)[_parameters.TotalCarriers];
            int data = 0;
            for (int c = 0; c < carriers.Length; c++)
            {
                if (_pilotMap[c])
                {
                    carriers[c] = (_preambleBins[c].Re, 0);
                    continue;
                }

                int bits = carrierBits[data++];
                int value = 0;
                for (int b = 0; b < bits; b++)
                {
                    value = (value << 1) | (read < coded.Length ? coded[read++] : 0);
                }

                (float I, float Q) point = points[bits][value];
                carriers[c] = (point.I, point.Q);
            }

            AppendSymbol(audio, RenderSymbol(carriers, _drive));
        }

        return [.. audio];
    }

    /// <summary>
    /// Finds and decodes the first burst in <paramref name="audio"/>, or returns null if there is
    /// none. Whole-buffer rather than streaming: a burst is short and cheap to hold, and the
    /// streaming surface can wrap this once the waveform stops moving.
    /// </summary>
    public OfdmAbBurst? Demodulate(ReadOnlySpan<float> audio)
    {
        int sync = FindSync(audio);
        if (sync < 0)
        {
            return null;
        }

        int start = sync + _parameters.SymbolSamples;
        if (start + _parameters.SymbolSamples > audio.Length)
        {
            return null;
        }

        // The channel estimate comes from the preamble itself: every occupied carrier carries a
        // known value, so dividing through gives the channel's response bin by bin.
        (double[] re, double[] im) = SymbolBins(audio, start);
        var channel = new (double Re, double Im)[_parameters.TotalCarriers];
        for (int c = 0; c < channel.Length; c++)
        {
            int bin = _parameters.FirstCarrier + c;
            double refRe = _preambleBins[c].Re;
            channel[c] = refRe >= 0 ? (re[bin], im[bin]) : (-re[bin], -im[bin]);
        }

        int headerStart = start + _parameters.SymbolSamples;
        var headerBits = new BitWriter();
        for (int s = 0; s < _headerSymbols; s++)
        {
            (double Re, double Im)[]? headerSymbol = Equalised(
                audio, headerStart + (s * _parameters.SymbolSamples), channel);
            if (headerSymbol is null)
            {
                return null;
            }

            for (int c = 0; c < headerSymbol.Length; c++)
            {
                if (!_pilotMap[c])
                {
                    headerBits.Write(headerSymbol[c].Re >= 0 ? 1 : 0, 1);
                }
            }
        }

        byte[] headerBytes = headerBits.ToArray();
        int constellationValue = headerBytes[0] >> 4;
        int length = ((headerBytes[0] & 0x0F) << 16) | (headerBytes[1] << 8) | headerBytes[2];
        ushort headerCrc = (ushort)((headerBytes[3] << 8) | headerBytes[4]);
        if (Crc16X25.Compute(headerBytes.AsSpan(0, 3)) != headerCrc)
        {
            return null;
        }

        if (constellationValue is < 1 or > 8 || length is < 0 or > 65535)
        {
            return null;
        }

        var constellation = (OfdmAbConstellation)constellationValue;
        int framedLength = length + 2;

        int[] carrierBits = _parameters.BitsPerDataCarrier(constellation);
        var tables = new Dictionary<int, (float I, float Q)[]>();
        foreach (int b in carrierBits.Distinct())
        {
            tables[b] = OfdmAbMapper.Points((OfdmAbConstellation)b);
        }

        int payloadBits = framedLength * 8;
        int codedBits = _codec.CodedBits(payloadBits);
        int perSymbol = carrierBits.Sum();
        int symbols = ((codedBits + perSymbol) - 1) / perSymbol;

        var llrs = new float[symbols * perSymbol];
        int written = 0;
        for (int s = 0; s < symbols; s++)
        {
            int offset = headerStart + ((_headerSymbols + s) * _parameters.SymbolSamples);
            (double Re, double Im)[]? symbol = Equalised(audio, offset, channel);
            if (symbol is null)
            {
                return null;
            }

            int data = 0;
            for (int c = 0; c < symbol.Length; c++)
            {
                if (_pilotMap[c])
                {
                    continue;
                }

                int bits = carrierBits[data++];
                OfdmAbMapper.SoftBits(
                    tables[bits], bits, (float)symbol[c].Re, (float)symbol[c].Im, SoftScale,
                    llrs.AsSpan(written, bits));
                written += bits;
            }
        }

        byte[] payloadBitArray = _codec.Decode(llrs.AsSpan(0, codedBits), payloadBits);
        var writer = new BitWriter();
        foreach (byte bit in payloadBitArray)
        {
            writer.Write(bit, 1);
        }

        byte[] framed = writer.ToArray();
        if (framed.Length < framedLength)
        {
            return null;
        }

        Array.Resize(ref framed, framedLength);
        Scramble(framed);
        ushort crc = (ushort)(framed[^2] | (framed[^1] << 8));
        byte[] payload = framed[..length];
        return Crc16X25.Compute(payload) == crc
            ? new OfdmAbBurst(payload, constellation, sync)
            : new OfdmAbBurst(null, constellation, sync);
    }

    // Equalises one symbol against the channel estimate, then takes out whatever common phase the
    // pilots say has crept in since - a sample-clock difference between the two ends shows up as a
    // slow rotation, and the pilots are there to measure it.
    private (double Re, double Im)[]? Equalised(
        ReadOnlySpan<float> audio, int offset, (double Re, double Im)[] channel)
    {
        if (offset + _parameters.SymbolSamples > audio.Length)
        {
            return null;
        }

        (double[] re, double[] im) = SymbolBins(audio, offset);
        var carriers = new (double Re, double Im)[_parameters.TotalCarriers];
        for (int c = 0; c < carriers.Length; c++)
        {
            int bin = _parameters.FirstCarrier + c;
            (double hRe, double hIm) = channel[c];
            double power = (hRe * hRe) + (hIm * hIm);
            if (power < 1e-20)
            {
                carriers[c] = (0, 0);
                continue;
            }

            carriers[c] = (((re[bin] * hRe) + (im[bin] * hIm)) / power,
                ((im[bin] * hRe) - (re[bin] * hIm)) / power);
        }

        double pilotRe = 0;
        double pilotIm = 0;
        for (int c = 0; c < carriers.Length; c++)
        {
            if (!_pilotMap[c])
            {
                continue;
            }

            // Pilots carry the preamble's own value, so the residual is the rotation.
            double sign = _preambleBins[c].Re >= 0 ? 1 : -1;
            pilotRe += carriers[c].Re * sign;
            pilotIm += carriers[c].Im * sign;
        }

        double magnitude = Math.Sqrt((pilotRe * pilotRe) + (pilotIm * pilotIm));
        if (magnitude > 1e-12)
        {
            double cos = pilotRe / magnitude;
            double sin = -pilotIm / magnitude;
            for (int c = 0; c < carriers.Length; c++)
            {
                (double cRe, double cIm) = carriers[c];
                carriers[c] = ((cRe * cos) - (cIm * sin), (cRe * sin) + (cIm * cos));
            }
        }

        return carriers;
    }

    // Timing by self-correlation: the sync symbol's useful part is two identical halves, so the
    // signal correlated against itself half a symbol later peaks exactly where the symbol starts.
    // Both halves pass through the same channel, so a tilt or an echo scales them together and the
    // coefficient stays high - which is the whole reason this beats matching a clean reference.
    private int FindSync(ReadOnlySpan<float> audio)
    {
        int fft = _parameters.FftSize;
        int cp = _parameters.CyclicPrefix;
        int half = fft / 2;
        if (audio.Length < _parameters.SymbolSamples * 3)
        {
            return -1;
        }

        double bestScore = 0;
        int best = -1;
        int limit = audio.Length - _parameters.SymbolSamples;
        for (int start = 0; start < limit; start++)
        {
            double dot = 0;
            double energyA = 0;
            double energyB = 0;
            for (int n = 0; n < half; n++)
            {
                double a = audio[start + cp + n];
                double b = audio[start + cp + half + n];
                dot += a * b;
                energyA += a * a;
                energyB += b * b;
            }

            double denominator = Math.Sqrt(energyA * energyB);
            if (denominator < 1e-12)
            {
                continue;
            }

            double score = dot / denominator;
            if (score > bestScore)
            {
                bestScore = score;
                best = start;
            }
        }

        // A correlation coefficient this high does not happen by accident, and the header's CRC
        // is the backstop for anything that slips through.
        return bestScore >= 0.8 ? best : -1;
    }

    private (double[] Re, double[] Im) SymbolBins(ReadOnlySpan<float> audio, int offset)
    {
        var symbol = new double[_parameters.FftSize];
        for (int n = 0; n < symbol.Length; n++)
        {
            symbol[n] = audio[offset + _parameters.CyclicPrefix + n];
        }

        return RealFft.ToBins(symbol, _parameters.FftSize);
    }

    // The header, spread over as many BPSK symbols as this profile's data carriers need.
    private List<(double Re, double Im)[]> HeaderSymbols(
        OfdmAbConstellation constellation, int payloadLength)
    {
        var writer = new BitWriter();
        writer.Write((int)constellation, 4);
        writer.Write(payloadLength, 20);
        byte[] head = writer.ToArray();
        ushort crc = Crc16X25.Compute(head.AsSpan(0, 3));
        writer.Write(crc >> 8, 8);
        writer.Write(crc & 0xFF, 8);
        byte[] bytes = writer.ToArray();

        var reader = new BitReader(bytes);
        var symbols = new List<(double Re, double Im)[]>(_headerSymbols);
        for (int s = 0; s < _headerSymbols; s++)
        {
            var carriers = new (double Re, double Im)[_parameters.TotalCarriers];
            for (int c = 0; c < carriers.Length; c++)
            {
                carriers[c] = _pilotMap[c]
                    ? (_preambleBins[c].Re, 0)
                    : (reader.Read(1) == 1 ? 1 : -1, 0);
            }

            symbols.Add(carriers);
        }

        return symbols;
    }

    private double[] RenderSymbol((double Re, double Im)[] carriers, double scale)
    {
        int fft = _parameters.FftSize;
        var binRe = new double[fft];
        var binIm = new double[fft];
        for (int c = 0; c < carriers.Length; c++)
        {
            int bin = _parameters.FirstCarrier + c;
            binRe[bin] = carriers[c].Re;
            binIm[bin] = carriers[c].Im;
        }

        double[] useful = RealFft.ToTimeDomain(binRe, binIm, fft);
        for (int n = 0; n < fft; n++)
        {
            useful[n] *= scale;
        }

        return useful;
    }

    private void AppendSymbol(List<float> audio, double[] useful)
    {
        int fft = _parameters.FftSize;
        int cp = _parameters.CyclicPrefix;
        for (int n = 0; n < cp; n++)
        {
            audio.Add((float)useful[fft - cp + n]);
        }

        for (int n = 0; n < fft; n++)
        {
            audio.Add((float)useful[n]);
        }
    }

    // Additive LFSR so a run of identical bytes does not become a run of identical symbols, which
    // would put all the burst's energy on a handful of subcarriers. Self-inverse: the same call
    // scrambles and descrambles.
    private static void Scramble(Span<byte> data)
    {
        ushort state = ScramblerSeed;
        for (int i = 0; i < data.Length; i++)
        {
            byte mask = 0;
            for (int b = 0; b < 8; b++)
            {
                int bit = ((state >> 8) ^ (state >> 4)) & 1;
                state = (ushort)(((state << 1) | bit) & 0x1FF);
                mask = (byte)((mask << 1) | bit);
            }

            data[i] ^= mask;
        }
    }

    /// <summary>Scale on the soft metrics. Max-log distances are on the constellation's own
    /// scale; the decoder only cares about their ratios, so this keeps them in a comfortable
    /// numeric range rather than encoding any noise estimate.</summary>
    private const float SoftScale = 4f;

    private static byte[] Unpack(ReadOnlySpan<byte> bytes)
    {
        var bits = new byte[bytes.Length * 8];
        for (int i = 0; i < bytes.Length; i++)
        {
            for (int b = 0; b < 8; b++)
            {
                bits[(i * 8) + b] = (byte)((bytes[i] >> (7 - b)) & 1);
            }
        }

        return bits;
    }

    private sealed class BitReader(byte[] data)
    {
        private int _position;

        public int Read(int bits)
        {
            int value = 0;
            for (int b = 0; b < bits; b++)
            {
                int index = _position >> 3;
                int bit = index < data.Length ? (data[index] >> (7 - (_position & 7))) & 1 : 0;
                value = (value << 1) | bit;
                _position++;
            }

            return value;
        }
    }

    private sealed class BitWriter
    {
        private readonly List<byte> _bytes = [];
        private int _bitCount;

        public void Write(int value, int bits)
        {
            for (int b = bits - 1; b >= 0; b--)
            {
                if ((_bitCount & 7) == 0)
                {
                    _bytes.Add(0);
                }

                if (((value >> b) & 1) != 0)
                {
                    _bytes[^1] |= (byte)(1 << (7 - (_bitCount & 7)));
                }

                _bitCount++;
            }
        }

        public byte[] ToArray() => [.. _bytes];
    }
}
