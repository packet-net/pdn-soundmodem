using M0LTE.Fec;

namespace Packet.SoundModem.Modems.OfdmFm;

/// <summary>What one received burst came to.</summary>
/// <param name="Payload">The recovered bytes, or null if nothing decoded.</param>
/// <param name="Constellation">The constellation its header announced.</param>
/// <param name="StartSample">Where in the fed audio the burst was found.</param>
/// <param name="Coding">What the header said the payload is coded with, which need not be what
/// this receiver transmits. Reported so that a station can see what its correspondent has chosen,
/// which is the difference between "the far end moved up a rate" and "the link went bad".</param>
/// <param name="Recommendation">What the sender would like to hear back from us, or null if it
/// had no opinion. Advice about the OTHER direction, since a station can only measure what it
/// receives; see <see cref="OfdmFmHeader.Recommendation"/>.</param>
/// <param name="Geometry">The table entry the payload was carried on; see
/// <see cref="OfdmFmHeader.Geometry"/>.</param>
/// <param name="RecommendedGeometry">The geometry the sender would like to hear back on, or null
/// if it had no opinion or named one this station does not hold.</param>
/// <param name="CarrierSnrDb">Signal to noise per occupied carrier of the payload layout, pilots
/// included, in dB, from the error vector between each equalised point and the point the decoded
/// bits say was sent. Only present when the payload decoded, because only then is the reference
/// known. <para>The measurement that says where a band runs out: it sees every impairment the
/// carrier actually suffered - noise, distortion, estimate error, residual clock tilt - rather
/// than the noise alone, so it is what a bit-loading decision should be taken from.</para></param>
/// <param name="CarrierGainDb">The channel's power gain per occupied carrier of the payload
/// layout, relative to the mean over the layout, in dB. The passband shape as this burst's
/// estimate saw it.</param>
/// <param name="PreFecBitErrorRate">How much of the code's correcting power this burst spent:
/// the fraction of coded bits the demodulator got wrong before the decoder fixed them.
/// <para><b>The margin measure a rate decision needs.</b> Whether the CRC passed is one bit and it
/// arrives too late - by the time frames fail the link is already over the cliff. This rises
/// smoothly as the signal falls, on every burst that decodes, so it says how much room is left
/// rather than that there is none.</para>
/// <para>Null when there is nothing to measure: an uncoded burst has no decoder to disagree with,
/// so re-encoding it just reproduces the demodulator's own decisions and the answer would be a
/// constant zero rather than a measurement.</para>
/// <para><b>Only trustworthy where <paramref name="Payload"/> is not null.</b> The figure comes from
/// re-encoding what the decoder produced, so if the decode was wrong the reference is wrong too. A
/// failed burst still reports one and it will be large, which is honest as a direction and useless
/// as a number.</para></param>
/// <param name="FollowOn">Whether this was a follow-on burst, read on the timing and channel
/// inherited from the burst before it rather than acquired afresh: what tells the rate controller
/// that a failure is about the burst form rather than the rate.</param>
/// <param name="AskedFullBursts">Whether the far end asked, beside its rate recommendation, to be
/// sent full bursts rather than follow-on ones.</param>
public sealed record OfdmFmBurst(
    byte[]? Payload,
    OfdmFmConstellation Constellation,
    int StartSample,
    OfdmFmCoding Coding,
    double? PreFecBitErrorRate = null,
    OfdmFmRate? Recommendation = null,
    int Geometry = OfdmFmGeometryTable.AcquisitionId,
    int? RecommendedGeometry = null,
    IReadOnlyList<double>? CarrierSnrDb = null,
    IReadOnlyList<double>? CarrierGainDb = null,
    bool FollowOn = false,
    bool AskedFullBursts = false);

/// <summary>What a burst's header announced about the payload behind it.</summary>
/// <param name="Geometry">Which entry of the station's <see cref="OfdmFmGeometryTable"/> the
/// payload symbols occupy.
/// <para>The header itself is always read on entry 0, the acquisition geometry, which is what
/// makes this signallable at all: a receiver has to demodulate the header before it can learn
/// what layout the payload uses. When the payload's geometry is not the acquisition geometry, one
/// channel-estimate symbol on the payload's layout follows the header, because a narrow preamble
/// cannot estimate carriers it never occupied.</para>
/// <para>Four bits, indexing a table that never appears on the wire. A receiver holding no entry
/// for the id refuses the burst rather than guessing, exactly as it does for a coding it does not
/// know.</para></param>
/// <param name="Constellation">Bits per subcarrier the payload symbols carry.</param>
/// <param name="PayloadLength">Payload bytes, before the CRC-16 trailer. Twelve bits on the wire,
/// so 4095 at most, which is twice what the streaming receiver will assemble and what made room
/// for the geometry to be recommended as well as announced without the header growing past the
/// one symbol it fits on even the narrowest layout this repository ships
/// (<see cref="OfdmFmPresets.Narrow"/>).</param>
/// <param name="Coding">How the payload is coded.
/// <para>Signalled rather than agreed in advance, which is what lets one end change rate without
/// the other being reconfigured. The constellation was always signalled; the coding was not, so a
/// receiver could follow a sender from BPSK to QAM-256 but not from rate 1/2 to uncoded.</para>
/// </param>
/// <param name="Recommendation">What this station would like to HEAR from its correspondent, or
/// null for no opinion.
/// <para>The half of adaptation that cannot be worked out locally. A transmitter cannot measure the
/// path its own signal takes; only the far end knows what it can hear. So the receiver measures and
/// recommends, the transmitter decides, and the recommendation rides on the next burst going the
/// other way - which for a request and response protocol is exactly when it is needed.</para>
/// <para>Carried as a constellation and a coding rather than as an index into a ladder, so that
/// nothing has to agree on a table. A station that does not recognise a recommendation ignores it,
/// which is the same thing it does with no recommendation at all.</para>
/// <para><b>The two directions are rated separately and that is not an accident.</b> A link is not
/// reciprocal: different noise floor, different antenna, different site at each end. Each station
/// recommends for the other's transmitter and neither infers anything about its own.</para>
/// </param>
/// <param name="RecommendedGeometry">The geometry this station would like to HEAR on, alongside
/// the rate, or null when there is no recommendation at all or the id named is not in this
/// station's table. Bandwidth is the axis worth more than constellation or code on a wide audio
/// path, and it is the one a rate recommendation alone cannot reach.</param>
/// <param name="AskedFullBursts">Whether the sender asked, beside its recommendation, to be sent
/// full bursts rather than follow-on ones. Carried in the recommended constellation's nibble as
/// the constellation plus eight, so a reader from before the flag existed sees no recommendation
/// and nothing worse. False whenever there is no recommendation.</param>
public readonly record struct OfdmFmHeader(
    OfdmFmConstellation Constellation,
    int PayloadLength,
    OfdmFmCoding Coding,
    OfdmFmRate? Recommendation = null,
    int Geometry = OfdmFmGeometryTable.AcquisitionId,
    int? RecommendedGeometry = null,
    bool AskedFullBursts = false);

/// <summary>
/// An audio-band OFDM modem: a real-valued transform's subcarriers, each carrying a QAM symbol,
/// inside the audio passband of an ordinary FM radio.
/// </summary>
/// <remarks>
/// <para><b>OFDM-FM is our own waveform and every part of it is still provisional.</b> What is
/// implemented here is the machinery such a waveform needs - real-FFT symbols with a cyclic
/// prefix, a correlated preamble for timing, a channel estimate taken from it, pilot-tracked
/// phase, Gray-coded QAM from one to eight bits per carrier, and a CRC-checked frame - built so
/// that the parts a carrier layout fixes are parameters rather than assumptions. See
/// <see cref="OfdmFmParameters"/> for why the geometry lives outside the source.</para>
/// <para><b>Burst structure</b>, which is ours and provisional: one sync symbol and one preamble
/// symbol carrying a known pseudo-random BPSK pattern on every occupied carrier; one header symbol,
/// BPSK on the data carriers, giving the payload geometry, constellation, coding and length, a
/// recommendation for the other direction, and a CRC over all of it; then, only when the payload
/// geometry is not the acquisition geometry, one channel-estimate symbol carrying the preamble
/// pattern on the payload's carriers; then the payload symbols at that constellation, scrambled,
/// with a CRC-16 trailer.</para>
/// <para><b>Sync, preamble and header are on a fixed layout, the acquisition geometry</b>, which is
/// entry 0 of the station's <see cref="OfdmFmGeometryTable"/>, whatever the payload uses. That is
/// the only way the header can name the payload's layout: a receiver must demodulate the header
/// before it knows anything, so what the header is read against has to be agreed in advance, and
/// the table makes that agreement as small as it can be - a sample rate, a transform size, a cyclic
/// prefix and one narrow carrier span.</para>
/// <para><b>Not modelled</b>: carrier frequency offset, because an FM audio path has none - a
/// discriminator hands back baseband audio, and what remains is a sample-clock difference between
/// the two soundcards. Its common rotation is taken out per symbol by the pilots, and the tilt it
/// paints across the band is fitted over the whole burst and removed - see
/// <c>RemoveSampleClockTilt</c>, which records what it cost before it existed. A future version
/// wanting to work over SSB would need real frequency recovery.</para>
/// </remarks>
public sealed class OfdmFmBurstCodec
{
    /// <summary>How many clip-and-restore passes the peak reducer makes. Three is where the
    /// returns flatten: the first pass does most of the work and the spectrum restoration undoes
    /// part of it, so a couple more recover most of that without adding much distortion.</summary>
    private const int PeakReductionPasses = 3;

    /// <summary>The most payload the header's length field can describe: twelve bits.</summary>
    /// <remarks>
    /// It was sixteen. The four bits went to recommending a geometry, which is the axis a wide
    /// audio path pays most for and the one the rate recommendation could not reach, and the
    /// length field was the only place four bits could come from without the header growing past
    /// the single symbol it fits on even <see cref="OfdmFmPresets.Narrow"/>. The streaming modem
    /// carries everything this can name (<see cref="OfdmFmModem.MaxPayloadBytes"/>), and the daemon's
    /// KISS layer passes frames to 8192 bytes since pdn-soundmodem 0.72.0.
    /// </remarks>
    public const int MaxPayloadBytes = 4095;

    /// <summary>
    /// The payload codings a header can name, by index.
    /// </summary>
    /// <remarks>
    /// <para>Four bits, so up to sixteen; ten are used and 10 to 15 are held for the further
    /// LDPC families this waveform is likely to want. <b>The order is a wire format.</b> Appending is
    /// safe and reordering is not: a receiver on older firmware would decode a renumbered burst
    /// with the wrong code and fail its CRC, which looks exactly like a bad link.</para>
    /// <para>Interleaving is on for every coded entry. The switch exists to measure its worth, not
    /// to be turned off on air, and signalling it would spend a bit on something nobody should
    /// choose.</para>
    /// </remarks>
    private static readonly OfdmFmCoding[] _codingById =
    [
        new(OfdmFmFec.None),
        new(OfdmFmFec.Convolutional, 7, 1, 2, true),
        new(OfdmFmFec.Convolutional, 7, 2, 3, true),
        new(OfdmFmFec.Convolutional, 7, 3, 4, true),
        new(OfdmFmFec.Convolutional, 9, 1, 2, true),
        new(OfdmFmFec.Convolutional, 9, 2, 3, true),
        new(OfdmFmFec.Convolutional, 9, 3, 4, true),

        // Appended so the convolutional and LDPC families can be run head to head on air rather
        // than only on the bench; appending is the safe direction. One id for the whole rate-1/2
        // LDPC family - which mother codes a payload lands on is derived from its length
        // identically at both ends, so nothing more needs signalling.
        new(OfdmFmFec.Ldpc),

        // Appended for the punctured rates above 3/4 on the K=7 code: the 802.11n 5/6 and
        // DVB-S 7/8 patterns, ids 8 and 9. K=7 only, because that is the code the measured 8 kHz
        // preset runs and the ids are not free to spend twice: 10 to 15 stay held for the further
        // LDPC families.
        new(OfdmFmFec.Convolutional, 7, 5, 6, true),
        new(OfdmFmFec.Convolutional, 7, 7, 8, true),
    ];

    /// <summary>The index a header carries for a coding.</summary>
    /// <exception cref="ArgumentException">The coding has no wire representation, so a receiver
    /// could not be told about it. Better to refuse than to transmit a burst nobody can name.
    /// </exception>
    internal static int CodingId(OfdmFmCoding coding)
    {
        // A code that is not there has no constraint length and no rate, and an LDPC has no
        // constraint length and only one rate - but those fields still sit in the record and
        // still count towards its equality. Canonicalise them, or a profile that says "no
        // coding" while leaving a rate at something other than the default would be refused over
        // two numbers nothing reads.
        OfdmFmCoding named = coding.Scheme is OfdmFmFec.None or OfdmFmFec.Ldpc
            ? new OfdmFmCoding(coding.Scheme, Interleave: coding.Interleave)
            : coding;

        int id = Array.IndexOf(_codingById, named);
        if (id >= 0)
        {
            return id;
        }

        throw new ArgumentException(
            coding.Interleave
                ? $"coding {coding} cannot be signalled in a header; the wire format names "
                    + $"{_codingById.Length} codings and this is not one of them"
                : "a header names only interleaved codings, so this profile could transmit a burst "
                    + "no receiver can name. Interleaving off is a measurement configuration and "
                    + "not an on-air one: build an OfdmFmCodec directly to measure it",
            nameof(coding));
    }

    /// <summary>The index a header carries for a coding, without throwing when there is none.
    /// </summary>
    private static bool TryCodingId(OfdmFmCoding coding, out int id)
    {
        OfdmFmCoding named = coding.Scheme is OfdmFmFec.None or OfdmFmFec.Ldpc
            ? new OfdmFmCoding(coding.Scheme, Interleave: coding.Interleave)
            : coding;
        id = Array.IndexOf(_codingById, named);
        return id >= 0;
    }

    /// <summary>
    /// A received recommendation, given the ladder's own figures where the rate is on it.
    /// </summary>
    /// <remarks>
    /// A correspondent may recommend a rate this station does not carry on its ladder, and that is
    /// not an error: it is a perfectly transmittable rate and the far end is entitled to want it.
    /// The measured cost and speed are then unknown here, which is what the NaN says. A policy that
    /// needs those numbers should check the ladder rather than trusting these.
    /// </remarks>
    private static OfdmFmRate Recommended(OfdmFmConstellation constellation, OfdmFmCoding coding)
    {
        int rung = OfdmFmRateLadder.IndexOf(constellation, coding);
        return rung >= 0
            ? OfdmFmRateLadder.Rungs[rung]
            : new OfdmFmRate(constellation, coding, double.NaN, double.NaN);
    }

    /// <summary>The coding an index names, or null if it names none.</summary>
    private static OfdmFmCoding? CodingFor(int id) =>
        id >= 0 && id < _codingById.Length ? _codingById[id] : null;

    /// <summary>A decoder for a signalled coding, built once per coding and kept.</summary>
    /// <remarks>
    /// A burst names its own coding now, so the receiver cannot use the profile's. Codecs are
    /// cached because building one lays out puncture tables, and a receiver following a sender that
    /// is changing rate would otherwise rebuild on nearly every burst.
    /// </remarks>
    private OfdmFmCodec DecoderFor(OfdmFmCoding coding)
    {
        if (!_decoders.TryGetValue(coding, out OfdmFmCodec? codec))
        {
            codec = new OfdmFmCodec(coding);
            _decoders[coding] = codec;
        }

        return codec;
    }

    // 4 geometry, 4 constellation, 4 coding, 12 length, 4 + 4 + 4 recommendation (geometry,
    // constellation, coding), 16 CRC. The CRC covers the 36 bits before it, taken as five bytes
    // with the last nibble zero.
    //
    // 52 bits is 104 coded, and a usable acquisition layout carries more data carriers than that
    // - OfdmFmPresets.Narrow is sized against exactly this bound - so the header still fits the
    // one symbol it always used and costs no air time. The geometry field is the four bits that
    // room allowed; the recommended geometry came out of the length field, which was sixteen bits
    // for a payload the receiver then capped at 2048. Eight more bits would not have fit.
    private const int HeaderBits = 52;

    // Constellation 0, which is not a constellation - valid values run 1 to 8. Says "I am not
    // recommending anything", which is what a station transmits before it has heard the other end,
    // or when what it heard was not a rate it can place on the ladder.
    private const int NoRecommendation = 0;
    private const ushort ScramblerSeed = 0x1FF;

    /// <summary>
    /// Everything this codec keeps per carrier layout: the reference pattern, the pilot map, the
    /// drive, and the estimate denoiser's tables and scratch.
    /// </summary>
    /// <remarks>
    /// Built once per geometry and kept, because the denoiser runs on every header read and a
    /// receive path does not get to allocate. One for the acquisition geometry always; one for
    /// this station's own payload geometry; and one for any other entry in the table the first time
    /// a burst names it.
    /// </remarks>
    private sealed class Layout
    {
        public Layout(int id, OfdmFmGeometry geometry)
        {
            Id = id;
            Geometry = geometry;
            PilotMap = geometry.PilotMap();

            // A known pattern on every occupied carrier: it measures the channel, which is why it
            // covers pilots and data alike, and its values on the pilot carriers are what every
            // payload symbol's pilots carry.
            var reference = new (double Re, double Im)[geometry.TotalCarriers];
            var rng = new Random(20260808);
            for (int c = 0; c < reference.Length; c++)
            {
                reference[c] = (rng.Next(2) == 0 ? -1 : 1, 0);
            }

            Reference = reference;

            int carriers = reference.Length;
            DelayRe = new double[carriers];
            DelayIm = new double[carriers];
            TwiddleCos = new double[carriers];
            TwiddleSin = new double[carriers];
            for (int m = 0; m < carriers; m++)
            {
                double angle = 2 * Math.PI * m / carriers;
                TwiddleCos[m] = Math.Cos(angle);
                TwiddleSin[m] = Math.Sin(angle);
            }
        }

        public int Id { get; }
        public OfdmFmGeometry Geometry { get; }
        public bool[] PilotMap { get; }
        public (double Re, double Im)[] Reference { get; }
        public double[] DelayRe { get; }
        public double[] DelayIm { get; }
        public double[] TwiddleCos { get; }
        public double[] TwiddleSin { get; }

        /// <summary>Scale applied to every symbol on this layout. Set once from the reference
        /// pattern's unclipped peak, so every layout's symbols come out at the same RMS however
        /// many carriers they spread it over, and the deviation budget is spent the same way by
        /// a narrow header and a wide payload.</summary>
        public double Drive { get; set; } = 1.0;

        public int FirstCarrier => Geometry.FirstCarrier;
        public int TotalCarriers => Geometry.TotalCarriers;
        public int DataCarriers => Geometry.DataCarriers;
    }

    private readonly OfdmFmParameters _parameters;
    private readonly OfdmFmGeometryTable _table;
    private readonly Layout _acquisition;
    private readonly Layout _own;
    private readonly Layout?[] _layouts = new Layout?[OfdmFmGeometryTable.MaxEntries];
    private readonly int _headerSymbols;
    private readonly (double Re, double Im)[] _syncCarriers;
    private readonly OfdmFmCodec _codec;

    // The header's own code, independent of whatever codes the payload. The payload's coding is a
    // configuration choice and may be None; the header's is not optional, because the header is
    // what a receiver must read before it knows anything at all.
    private readonly int _searchDelay;
    private readonly Dictionary<OfdmFmCoding, OfdmFmCodec> _decoders = [];
    private readonly OfdmFmCodec _headerCodec;
    private readonly int _headerCodedBits;
    private readonly int _headerStride;

    /// <summary>Creates a modem for one bandwidth profile, running alone: its own layout is the
    /// acquisition geometry and the only one its bursts name.</summary>
    public OfdmFmBurstCodec(OfdmFmParameters parameters)
        : this(parameters, null)
    {
    }

    /// <summary>Creates a modem for one bandwidth profile within a station's geometry table.</summary>
    /// <param name="parameters">The profile. Its <see cref="OfdmFmParameters.GeometryId"/> names
    /// which table entry its payload symbols use; with a table it must be set, and the entry must
    /// have the profile's own layout.</param>
    /// <param name="table">The table every burst's geometry id indexes, or null to run the profile
    /// alone.</param>
    public OfdmFmBurstCodec(OfdmFmParameters parameters, OfdmFmGeometryTable? table)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        parameters.Validate();
        _parameters = parameters;

        int ownId;
        if (table is null)
        {
            _table = OfdmFmGeometryTable.Single(parameters.Geometry);
            ownId = OfdmFmGeometryTable.AcquisitionId;
        }
        else
        {
            table.Validate(parameters.FftSize);
            if (parameters.GeometryId is not int id)
            {
                throw new ArgumentException(
                    "a profile in a geometry table must say which entry it is", nameof(parameters));
            }

            if (table[id] is not OfdmFmGeometry entry)
            {
                throw new ArgumentException(
                    $"geometry {id} is not in the table", nameof(parameters));
            }

            if (!entry.SameLayoutAs(parameters.Geometry))
            {
                throw new ArgumentException(
                    $"the profile's layout is not the table's geometry {id}", nameof(parameters));
            }

            _table = table;
            ownId = id;
        }

        _acquisition = LayoutFor(OfdmFmGeometryTable.AcquisitionId)!;
        _own = LayoutFor(ownId)!;

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
        //
        // On the acquisition layout, always: it is the one symbol a receiver looks for before it
        // knows anything about the burst.
        var syncCarriers = new (double Re, double Im)[_acquisition.TotalCarriers];
        for (int c = 0; c < syncCarriers.Length; c++)
        {
            if (((_acquisition.FirstCarrier + c) & 1) == 0)
            {
                syncCarriers[c] = (_acquisition.Reference[c].Re * Math.Sqrt(2), 0);
            }
        }

        _syncCarriers = syncCarriers;

        // Through the cache rather than a fresh one, so the profile's own coding is the same
        // instance the receiver reaches for when a burst names it, which is the common case.
        // Whether it CAN be named was settled by Validate above, before any of this ran.
        _codec = DecoderFor(parameters.Codes);

        // A header may not fit one symbol: a narrow profile has few data carriers, and BPSK gives
        // one bit each. Span as many symbols as it takes rather than assuming.
        // Constraint length 9 rather than the payload's 7. The header is 52 bits, so the decode
        // costs nothing worth counting, and the longer constraint length is free accuracy.
        _searchDelay = new SearchBandLimit(parameters, _acquisition.Geometry).Delay;
        _headerCodec = new OfdmFmCodec(
            new OfdmFmCoding(OfdmFmFec.Convolutional, 9, 1, 2, true));
        _headerCodedBits = _headerCodec.CodedBits(HeaderBits);

        // How many symbols the header spans: enough to hold the coded bits on the ACQUISITION
        // layout's data carriers, times however many repeats the profile asks for.
        //
        // The coding is free and is not in question. Fifty-two header bits become 104 coded ones,
        // and every acquisition layout worth running has more data carriers than that: it is the
        // bound that decides how narrow a layout can usefully go, and OfdmFmPresets.Narrow is
        // sized against it. So a coded header fits in the same single symbol the uncoded one used
        // and costs no air time.
        //
        // The REPEAT is what costs, a whole symbol of it, and it is deliberately not the default.
        // The header was measured failing on fading, not on noise: with the flutter off it read
        // 64 of 64 at every carrier-to-noise ratio where the burst could be acquired at all, and
        // with 20 Hz of Doppler it read 18 of 40 at +20 dB. A flat fade takes every carrier down
        // together, so a header inside one symbol has no diversity to exploit and a second copy a
        // symbol later is the only thing that helps - adjacent symbols are 44 ms apart on the
        // shipped presets and the fade correlation between them is 0.13 at 20 Hz. But that is a
        // MOVING station's problem.
        // A fixed one on a fixed antenna has no such fade, its header was never failing, and
        // charging it 8 % of its air time to fix a fault it does not have is the wrong trade.
        _headerSymbols =
            ((_headerCodedBits + _acquisition.DataCarriers) - 1) / _acquisition.DataCarriers
            * Math.Max(1, parameters.HeaderRepeats);
        _headerStride = Coprime(_headerCodedBits);
    }

    /// <summary>The layout for a table entry, built the first time it is asked for and kept;
    /// null for an id the table does not fill.</summary>
    private Layout? LayoutFor(int id)
    {
        if (id < 0 || id >= OfdmFmGeometryTable.MaxEntries)
        {
            return null;
        }

        if (_layouts[id] is Layout built)
        {
            return built;
        }

        if (_table[id] is not OfdmFmGeometry geometry)
        {
            return null;
        }

        var layout = new Layout(id, geometry);

        // One drive level per layout, set once from its reference pattern. Normalising each
        // symbol to its own peak would be tidier to look at and quietly fatal: it would rescale
        // every symbol differently, and a QAM constellation carries information in amplitude.
        double[] raw = RenderSymbol(layout, layout.Reference, 1.0, NoPeakLimit);
        double peak = 0;
        foreach (double sample in raw)
        {
            peak = Math.Max(peak, Math.Abs(sample));
        }

        layout.Drive = peak > 0 ? 0.7 / peak : 1.0;
        _layouts[id] = layout;
        return layout;
    }

    /// <summary>The largest stride below half the count that shares no factor with it, so that
    /// stepping by it visits every position exactly once.</summary>
    private static int Coprime(int count)
    {
        for (int stride = Math.Max(1, count / 2) - 1; stride > 1; stride--)
        {
            int a = stride;
            int b = count;
            while (b != 0)
            {
                (a, b) = (b, a % b);
            }

            if (a == 1)
            {
                return stride;
            }
        }

        return 1;
    }

    /// <summary>The profile this modem runs.</summary>
    public OfdmFmParameters Parameters => _parameters;

    /// <summary>The table every burst's geometry id indexes.</summary>
    public OfdmFmGeometryTable Table => _table;

    /// <summary>The table entry this profile's own payload symbols use, and what its bursts
    /// announce unless told otherwise.</summary>
    public int GeometryId => _own.Id;

    /// <summary>The layout every burst's sync, preamble and header are on: table entry 0.</summary>
    public OfdmFmGeometry Acquisition => _acquisition.Geometry;

    /// <summary>Renders one burst carrying <paramref name="payload"/>.</summary>
    /// <param name="payload">Bytes to carry; a CRC-16 is appended.</param>
    /// <param name="constellation">Bits per subcarrier for the payload symbols.</param>
    /// <param name="leadInSymbols">Silent symbols before the preamble, so a receiver's acquisition
    /// has somewhere to settle.</param>
    /// <param name="recommendation">What this station would like to hear back, from what it has
    /// been hearing. Null for no opinion, which is where a link with no history starts.</param>
    /// <param name="coding">Code this burst rather than the profile's. Null uses the profile's.
    /// <para>Here because a station that adapts has to change code rate burst by burst, and building
    /// a whole codec per rate would rebuild the sync and preamble carriers seven times over for a
    /// geometry that never changes. The codec is cached per coding either way.</para></param>
    /// <param name="geometry">Carry the payload on this table entry rather than the profile's own.
    /// Null uses the profile's. Same reason as <paramref name="coding"/>: a station that follows
    /// its correspondent's recommendation changes geometry burst by burst.</param>
    /// <param name="recommendedGeometry">The geometry this station would like to hear back on,
    /// sent alongside <paramref name="recommendation"/> and meaningless without it. Null means the
    /// profile's own, which is "send me what I send you".</param>
    /// <param name="askFullBursts">Ask the far end, alongside <paramref name="recommendation"/>
    /// and meaningless without it, to send full bursts rather than follow-on ones. See
    /// <see cref="OfdmFmRateController.WantFullBursts"/>.</param>
    public float[] Modulate(
        ReadOnlySpan<byte> payload,
        OfdmFmConstellation constellation,
        int leadInSymbols = 1,
        OfdmFmRate? recommendation = null,
        OfdmFmCoding? coding = null,
        int? geometry = null,
        int? recommendedGeometry = null,
        bool askFullBursts = false) =>
        Render(payload, constellation, leadInSymbols, recommendation, coding, geometry,
            recommendedGeometry, followOn: false, askFullBursts);

    /// <summary>
    /// Renders a follow-on burst: the header and the payload only, for a frame that follows
    /// another in the same keyup on a geometry the receiver has already decoded a burst on. No
    /// lead-in, no sync symbol, no preamble, no estimate symbol; see
    /// <see cref="OfdmFmParameters.FollowOnFrames"/> for what that costs and buys.
    /// </summary>
    public float[] ModulateFollowOn(
        ReadOnlySpan<byte> payload,
        OfdmFmConstellation constellation,
        OfdmFmRate? recommendation = null,
        OfdmFmCoding? coding = null,
        int? geometry = null,
        int? recommendedGeometry = null,
        bool askFullBursts = false) =>
        Render(payload, constellation, 0, recommendation, coding, geometry, recommendedGeometry,
            followOn: true, askFullBursts);

    private float[] Render(
        ReadOnlySpan<byte> payload,
        OfdmFmConstellation constellation,
        int leadInSymbols,
        OfdmFmRate? recommendation,
        OfdmFmCoding? coding,
        int? geometry,
        int? recommendedGeometry,
        bool followOn,
        bool askFullBursts)
    {
        // The header's length field is 12 bits. A longer payload would have its length written
        // modulo 4096 and the burst would be sized wrongly at the far end, which shows up as a
        // payload CRC failure and reads like a bad link.
        if (payload.Length > MaxPayloadBytes)
        {
            throw new ArgumentOutOfRangeException(
                nameof(payload),
                payload.Length,
                $"a burst carries at most {MaxPayloadBytes} payload bytes");
        }

        // Refused rather than defaulted: a burst announcing a geometry this station does not hold
        // could never be heard back, and a station asked to transmit on one has been asked for
        // something it cannot do.
        Layout carrying = LayoutFor(geometry ?? _own.Id)
            ?? throw new ArgumentException(
                $"geometry {geometry} is not in this station's table", nameof(geometry));

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

        // Rendered per burst rather than cached, because how hard they may be clipped depends on
        // the constellation this burst carries: the preamble is the channel estimate's reference,
        // so its distortion reaches every payload carrier and a dense constellation cannot take as
        // much of it. Two extra transforms per burst, on the transmit side, which is free.
        //
        // That the references are clipped AT ALL was measured, not assumed: excluded, the sync
        // symbol is the burst's loudest instant, so it alone sets the drive and clipping the
        // payload buys almost nothing - about 2 dB thrown away. Neither reference is harmed: the
        // sync symbol's halves are clipped identically so the self-correlation is as strong, and
        // the estimate divides by the KNOWN preamble, absorbing its clipping as though it were
        // channel. See docs/dev/ofdm-fm/receiver-findings.md, "Peak reduction".
        OfdmFmCoding sending = coding ?? _parameters.Codes;
        double referenceLimit = PeakLimit(constellation);
        if (!followOn)
        {
            AppendSymbol(audio, RenderSymbol(_acquisition, _syncCarriers, _acquisition.Drive, referenceLimit));
            AppendSymbol(audio, RenderSymbol(_acquisition, _acquisition.Reference, _acquisition.Drive, referenceLimit));
        }

        // Header carriers, pilots included, are all BPSK, so one cap covers every position - see
        // BoundCarrierErrors.
        double[] headerErrorCap = UniformErrorCap(
            _acquisition.TotalCarriers, OfdmFmConstellation.Bpsk);
        foreach ((double Re, double Im)[] headerSymbol in HeaderSymbols(
            carrying.Id, constellation, payload.Length, recommendation, sending,
            recommendedGeometry ?? _own.Id, askFullBursts))
        {
            AppendSymbol(audio, RenderSymbol(
                _acquisition, headerSymbol, _acquisition.Drive, PeakLimit(OfdmFmConstellation.Bpsk),
                headerErrorCap));
        }

        // A payload on a layout the preamble did not cover gets its own estimate symbol: the
        // preamble pattern on the payload's carriers, at the payload's drive, so the channel the
        // receiver measures from it is exactly the one the payload symbols then travel. Skipped
        // when the payload is on the acquisition layout, which the preamble already measured,
        // and on a follow-on burst, whose receiver refreshed its estimate from the frame before.
        if (!followOn && carrying.Id != OfdmFmGeometryTable.AcquisitionId)
        {
            AppendSymbol(audio, RenderSymbol(carrying, carrying.Reference, carrying.Drive, referenceLimit));
        }

        int[] carrierBits = carrying.Geometry.BitsPerDataCarrier(constellation);
        byte[] coded = DecoderFor(sending).Encode(Unpack(framed));
        var points = new Dictionary<int, (float I, float Q)[]>();
        foreach (int bits in carrierBits.Distinct())
        {
            points[bits] = OfdmFmMapper.Points((OfdmFmConstellation)bits);
        }

        int perSymbol = carrierBits.Sum();
        int symbols = ((coded.Length + perSymbol) - 1) / perSymbol;

        // With bit loading a symbol can carry several constellations at once, so the densest
        // carrier sets how hard the whole symbol may be clipped.
        double payloadPeakLimit = PeakLimit((OfdmFmConstellation)carrierBits.Max());

        // Per carrier, not per symbol: a bit-loaded layout mixes constellations across the band,
        // so a dense carrier and a sparse one either side of it have different decision margins
        // and each needs its own cap - see BoundCarrierErrors. Built once, since neither the
        // layout nor the bit loading changes symbol to symbol.
        double[] payloadErrorCap = PayloadErrorCap(carrying, carrierBits);
        int read = 0;
        for (int s = 0; s < symbols; s++)
        {
            var carriers = new (double Re, double Im)[carrying.TotalCarriers];
            int data = 0;
            for (int c = 0; c < carriers.Length; c++)
            {
                if (carrying.PilotMap[c])
                {
                    carriers[c] = (carrying.Reference[c].Re, 0);
                    continue;
                }

                int bits = carrierBits[data++];
                int value = 0;
                for (int b = 0; b < bits; b++)
                {
                    // Past the end of the coded bits, pad with a pseudo-random bit rather than a
                    // zero. The receiver never looks at these - it reads exactly as many soft bits
                    // as the code produced - so their value is free, and zero is the one value
                    // that costs something. A run of identical carrier values is a near-impulse in
                    // the time domain: measured, the zero-padded last symbol peaked at 2.238 while
                    // every other symbol in the burst peaked between 0.70 and 0.89. An FM
                    // transmitter is peak deviation limited, so that one symbol was setting the
                    // drive for the whole burst and holding every other symbol roughly 8 dB below
                    // the deviation the radio was set up for.
                    value = (value << 1)
                        | (read < coded.Length ? coded[read++] : PadBit(read++));
                }

                (float I, float Q) point = points[bits][value];
                carriers[c] = (point.I, point.Q);
            }

            AppendSymbol(audio, RenderSymbol(
                carrying, carriers, carrying.Drive, payloadPeakLimit, payloadErrorCap));
        }

        return [.. audio];
    }

    /// <summary>Symbols the header spans on the acquisition layout: BPSK on its data carriers, so
    /// a narrow one needs more than one.</summary>
    public int HeaderSymbolCount => _headerSymbols;

    /// <summary>
    /// Samples from a burst's sync position to the end of its header - what a receiver has to hold
    /// before it can find out how long the whole burst is.
    /// </summary>
    public int HeaderEndOffset => _parameters.SymbolSamples * (2 + _headerSymbols);

    /// <summary>
    /// Samples from a burst's sync position to its first payload symbol, for a payload on the
    /// given table entry: the header, plus the channel-estimate symbol a payload off the
    /// acquisition layout carries.
    /// </summary>
    public int PayloadOffset(int geometry) =>
        HeaderEndOffset
        + (geometry == OfdmFmGeometryTable.AcquisitionId ? 0 : _parameters.SymbolSamples);

    /// <summary>Samples a follow-on burst's header occupies, from where the previous burst ended:
    /// what a receiver must hold before it can read the header and size the rest.</summary>
    public int FollowOnHeaderSamples => _parameters.SymbolSamples * _headerSymbols;

    /// <summary>
    /// Symbols of silence after where a follow-on burst is expected within which it is still looked
    /// for, when it is not exactly there.
    /// </summary>
    /// <remarks>
    /// Measured on air, 2026-09-19: pdn-soundmodem leaves 30 to 35 ms of silence between the frames
    /// of one keyup, carrier up, whatever the modem renders, and a receiver that read the follow-on
    /// header exactly where the previous burst ended lost every follow-on frame to it. On synthetic
    /// audio the bursts abut to the sample, which is why nothing caught it. Two symbols is three
    /// times that gap.
    /// </remarks>
    public const int FollowOnGapSymbols = 2;

    /// <summary>
    /// The prefix self-similarity, over the first two symbols of a follow-on burst, below which the
    /// search does not believe it has a symbol start. Well below what a burst produces at any
    /// signal-to-noise ratio its payload could be read at.
    /// </summary>
    public const double SymbolStartThreshold = 0.5;

    /// <summary>Samples either side of the search's answer at which the header is also tried,
    /// because the payload channel the keyup carries is only good to the sample.</summary>
    private const int FollowOnFineTries = 3;

    /// <summary>Symbols whose prefixes the fine search correlates: the header and the symbol after
    /// it, which every follow-on burst has.</summary>
    private const int FollowOnFineSymbols = 2;

    /// <summary>How many symbol starts the search offers the header's CRC, best first.</summary>
    private const int FollowOnCandidates = 6;

    /// <summary>
    /// Samples past where a follow-on burst is expected that a receiver must hold before reading its
    /// header: enough to read it exactly there, or to find it anywhere in the gap allowance and
    /// read it where it is found.
    /// </summary>
    public int FollowOnSearchSamples =>
        (FollowOnGapSymbols * _parameters.SymbolSamples) + (3 * _parameters.CyclicPrefix)
        + FollowOnFineTries
        + Math.Max(FollowOnFineSymbols * _parameters.SymbolSamples, FollowOnHeaderSamples);

    /// <summary>
    /// Samples before where a follow-on burst is expected that the search reads: a prefix, for a
    /// burst arriving early, and the band limit's run-in before that.
    /// </summary>
    public int FollowOnLookBack => _parameters.CyclicPrefix + (2 * _searchDelay) + 1;

    /// <summary>Total samples a follow-on burst occupies, for a header already read: its header
    /// symbols and its payload symbols, nothing else.</summary>
    public int FollowOnBurstSamples(OfdmFmHeader header)
    {
        Layout layout = LayoutFor(header.Geometry)
            ?? throw new ArgumentException(
                $"geometry {header.Geometry} is not in this station's table", nameof(header));
        int[] carrierBits = layout.Geometry.BitsPerDataCarrier(header.Constellation);
        int codedBits = DecoderFor(header.Coding).CodedBits((header.PayloadLength + 2) * 8);
        int perSymbol = carrierBits.Sum();
        int symbols = ((codedBits + perSymbol) - 1) / perSymbol;
        return FollowOnHeaderSamples + (symbols * _parameters.SymbolSamples);
    }

    /// <summary>
    /// The correlation coefficient at which the sync search calls it a burst. High enough that it
    /// does not happen by accident, with the header's CRC as the backstop for anything that slips
    /// through.
    /// </summary>
    public const double SyncThreshold = 0.8;

    /// <summary>
    /// Finds and decodes the first burst in <paramref name="audio"/>, or returns null if there is
    /// none.
    /// </summary>
    /// <remarks>
    /// Whole-buffer, and the shape a test wants: hand it a burst and get the payload. A streaming
    /// receiver runs its own incremental sync search and then calls
    /// <see cref="DecodeAt(ReadOnlySpan{float}, int)"/>, which
    /// is the same decode this uses - so the two paths cannot drift apart into producing different
    /// answers for the same audio.
    /// </remarks>
    public OfdmFmBurst? Demodulate(ReadOnlySpan<float> audio)
    {
        int sync = FindSync(audio);
        return sync < 0 ? null : DecodeAt(audio, sync);
    }

    /// <summary>
    /// Reads a burst's header at a known sync position: what constellation it says its payload is
    /// at, and how long that payload is. Null if the audio runs out or the header's own CRC fails.
    /// </summary>
    /// <remarks>
    /// For a streaming receiver, which needs the length before it can size the rest of the burst.
    /// The header is coded - convolutional K=9 rate 1/2, soft-decision Viterbi, BPSK on the data
    /// carriers - which costs no air time because the coded bits fit in the symbol the uncoded
    /// header already used.
    /// </remarks>
    public OfdmFmHeader? ReadHeader(ReadOnlySpan<float> audio, int sync)
    {
        double noise = NoiseVariance(_acquisition, audio, sync);
        (double Re, double Im)[]? channel =
            ChannelEstimate(_acquisition, audio, sync + _parameters.SymbolSamples, noise);
        return channel is null
            ? null
            : ReadHeaderAt(audio, sync + (2 * _parameters.SymbolSamples), channel, noise, out _);
    }

    /// <summary>
    /// Reads a follow-on burst's header at <paramref name="start"/>, where the previous burst
    /// ended, against the acquisition channel the keyup state holds. Null if there is no readable
    /// header there, which is how a receiver learns the keyup is over, or that it has lost it.
    /// </summary>
    /// <remarks>
    /// On success the state's acquisition channel is refreshed from this header's own symbols,
    /// whose every carrier is a known value once the CRC has passed, so the next header in the
    /// keyup is read against an estimate one frame old rather than one that ages with the keyup.
    /// </remarks>
    public OfdmFmHeader? ReadFollowOnHeader(
        ReadOnlySpan<float> audio, int start, OfdmFmKeyupState keyup)
    {
        ArgumentNullException.ThrowIfNull(keyup);
        if (ReadHeaderAt(audio, start, keyup.AcquisitionChannel, keyup.AcquisitionNoise,
            out byte[] headerBits) is not OfdmFmHeader header)
        {
            return null;
        }

        keyup.AcquisitionChannel =
            AcquisitionFromHeader(audio, start, headerBits, keyup.AcquisitionNoise);
        return header;
    }

    /// <summary>
    /// Reads a follow-on burst's header where the previous burst ended or, when there is no
    /// readable header exactly there, at the first symbol start within the gap allowance after it.
    /// Null if neither finds one. <paramref name="start"/> is where the header was read.
    /// </summary>
    /// <remarks>
    /// <para>Where the previous burst ended is exact when the transmitter's audio is continuous,
    /// which the modem's own output is, and a host that plays one burst after another may leave a
    /// short silence between them (see <see cref="FollowOnGapSymbols"/>). A follow-on burst has no
    /// sync symbol to be found by; it is found by its symbols' cyclic prefixes (see
    /// <see cref="FollowOnStartCandidates"/>).</para>
    /// <para>The search's answer is good to a few samples and the keyup's payload channel is good
    /// to one, so the header is tried at the answer and at a few samples either side, the CRC
    /// arbitrating; the acquisition channel is then refreshed from the header at the position that
    /// read, and the keyup goes on from there aligned to the sample again.</para>
    /// </remarks>
    public OfdmFmHeader? ReadFollowOnHeader(
        ReadOnlySpan<float> audio, int expected, OfdmFmKeyupState keyup, out int start)
    {
        ArgumentNullException.ThrowIfNull(keyup);
        start = expected;
        if (ReadFollowOnHeader(audio, expected, keyup) is OfdmFmHeader exact)
        {
            return exact;
        }

        foreach (int boundary in FollowOnStartCandidates(audio, expected))
        {
            // The prefixes place the boundary itself; the keyup's estimates were made a little
            // off it, and the header must be read where they were.
            int aligned = boundary + keyup.Alignment;
            for (int i = 0; i <= 2 * FollowOnFineTries; i++)
            {
                // 0, -1, +1, -2, +2, ...: nearest first.
                int at = aligned + ((i + 1) / 2 * ((i & 1) == 0 ? 1 : -1));
                if (at == expected || at < 0 || at + FollowOnHeaderSamples > audio.Length)
                {
                    continue;
                }

                if (ReadFollowOnHeader(audio, at, keyup) is OfdmFmHeader header)
                {
                    // A header with a sync symbol two symbols before it is a full burst's, not a
                    // follow-on one's: its payload sits an estimate symbol further on, and the
                    // sync hunt, resumed from where this burst was expected, reads it properly.
                    // Reading it here as a follow-on would fail the payload and carry the
                    // expectation on past it, and the whole keyup with it.
                    if (SyncSymbolBefore(audio, at))
                    {
                        return null;
                    }

                    start = at;
                    return header;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Samples from <paramref name="start"/> to the symbol boundary nearest it as the prefixes of
    /// the <paramref name="symbols"/> symbols from there place it, within a prefix either way.
    /// </summary>
    private int BoundaryOffset(ReadOnlySpan<float> audio, int start, int symbols)
    {
        int cp = _parameters.CyclicPrefix;
        int span = symbols * _parameters.SymbolSamples;
        int low = Math.Max(-cp, -start);
        int high = Math.Min(cp, audio.Length - start - span);
        int best = 0;
        double bestSimilarity = double.NegativeInfinity;
        for (int offset = low; offset <= high; offset++)
        {
            double similarity = PrefixSimilarity(
                audio, start + offset, cp, _parameters.FftSize, symbols);
            if (similarity > bestSimilarity)
            {
                bestSimilarity = similarity;
                best = offset;
            }
        }

        return best;
    }

    /// <summary>Whether a sync symbol sits two symbols before <paramref name="headerAt"/>, by
    /// the sync symbol's own mark, its two identical halves, on the band-limited audio the sync
    /// search uses.</summary>
    private bool SyncSymbolBefore(ReadOnlySpan<float> audio, int headerAt)
    {
        int position = headerAt - (2 * _parameters.SymbolSamples);
        if (position < 0)
        {
            return false;
        }

        int runIn = (2 * _searchDelay) + 1;
        int sliceStart = Math.Max(0, position - runIn);
        int sliceEnd = Math.Min(audio.Length, position + _parameters.SymbolSamples + _searchDelay + 1);
        float[] limited = SearchBandLimit.Once(
            _parameters, _acquisition.Geometry, audio[sliceStart..sliceEnd]);
        int at = position - sliceStart + _searchDelay;
        int cp = _parameters.CyclicPrefix;
        int half = _parameters.FftSize / 2;
        if (at + cp + _parameters.FftSize > limited.Length)
        {
            return false;
        }

        double dot = 0;
        double energyA = 0;
        double energyB = 0;
        for (int n = 0; n < half; n++)
        {
            double a = limited[at + cp + n];
            double b = limited[at + cp + half + n];
            dot += a * b;
            energyA += a * a;
            energyB += b * b;
        }

        double denominator = Math.Sqrt(energyA * energyB);
        return denominator >= 1e-12 && dot / denominator >= SymbolStartThreshold;
    }

    /// <summary>
    /// Where a follow-on burst expected at <paramref name="expected"/> may actually start, when it
    /// is not exactly there: the strongest symbol starts within the gap allowance, best first,
    /// each to the sample. Empty when nothing there looks like a symbol start.
    /// </summary>
    /// <remarks>
    /// <para>A follow-on burst has no sync symbol to be found by, but every symbol it has carries
    /// a cyclic prefix, and a prefix is the one place in an OFDM symbol where the audio repeats
    /// itself a transform later: the self-similarity that finds the sync symbol finds a symbol
    /// start too, over the prefixes of the burst's first two symbols, which every follow-on burst
    /// has. Coarsely on band-limited audio, for the same reason the sync search is: discriminator
    /// noise above the acquisition layout would otherwise be correlated along with the signal. The
    /// filter is longer than the prefix, so there the peak is broad; each candidate is then
    /// sharpened on the raw audio, whose prefixes are exact, within half a prefix.</para>
    /// <para>Candidates rather than an answer, because two prefixes of an oversampled signal are
    /// a short correlation and the strongest peak in the range is not always the header's: it can
    /// be the payload symbol after it, or nothing at all. The header's CRC is the arbiter that the
    /// caller applies to each in turn, so a wrong first candidate costs a header read and not the
    /// keyup.</para>
    /// </remarks>
    internal IReadOnlyList<int> FollowOnStartCandidates(ReadOnlySpan<float> audio, int expected)
    {
        int cp = _parameters.CyclicPrefix;
        int fft = _parameters.FftSize;
        int symbol = _parameters.SymbolSamples;
        int reach = FollowOnFineSymbols * symbol;
        int from = Math.Max(0, expected - cp);
        int to = Math.Min(expected + (FollowOnGapSymbols * symbol) + cp, audio.Length - reach - (cp / 2));
        if (cp == 0 || to < from)
        {
            return [];
        }

        int runIn = (2 * _searchDelay) + 1;
        int sliceStart = Math.Max(0, from - runIn);
        int sliceEnd = Math.Min(audio.Length, to + reach + _searchDelay + 1);
        float[] limited = SearchBandLimit.Once(
            _parameters, _acquisition.Geometry, audio[sliceStart..sliceEnd]);

        var peaks = new List<(int At, double Similarity)>();
        for (int s = from; s <= to; s++)
        {
            int at = s - sliceStart + _searchDelay;
            if (at + reach > limited.Length)
            {
                break;
            }

            double similarity = PrefixSimilarity(limited, at, cp, fft, FollowOnFineSymbols);
            if (similarity >= SymbolStartThreshold)
            {
                peaks.Add((s, similarity));
            }
        }

        // The strongest, then the strongest at least a prefix away from any already taken, and so
        // on: one candidate per peak, not the whole top of one.
        var candidates = new List<int>();
        foreach ((int coarse, _) in peaks.OrderByDescending(p => p.Similarity))
        {
            if (candidates.Count >= FollowOnCandidates)
            {
                break;
            }

            if (candidates.Any(c => Math.Abs(c - coarse) < cp))
            {
                continue;
            }

            int fine = coarse;
            double best = double.NegativeInfinity;
            int last = Math.Min(coarse + (cp / 2), audio.Length - reach);
            for (int s = Math.Max(0, coarse - (cp / 2)); s <= last; s++)
            {
                double similarity = PrefixSimilarity(audio, s, cp, fft, FollowOnFineSymbols);
                if (similarity > best)
                {
                    best = similarity;
                    fine = s;
                }
            }

            candidates.Add(fine);
        }

        return candidates;
    }

    /// <summary>Correlation coefficient between the prefix-length run of audio at the start of
    /// each of <paramref name="symbols"/> consecutive symbols from <paramref name="start"/> and
    /// the run one transform later in the same symbol: near one at a symbol start, low anywhere
    /// else. Zero when the two runs differ in energy by 20 dB or more, because a coefficient is
    /// scale-free and the ring of a filter against a burst can correlate as well as anything
    /// while being no symbol start at all.</summary>
    private static double PrefixSimilarity(
        ReadOnlySpan<float> audio, int start, int cp, int fft, int symbols)
    {
        double dot = 0;
        double energyA = 0;
        double energyB = 0;
        for (int s = 0; s < symbols; s++)
        {
            int at = start + (s * (fft + cp));
            for (int k = 0; k < cp; k++)
            {
                double a = audio[at + k];
                double b = audio[at + fft + k];
                dot += a * b;
                energyA += a * a;
                energyB += b * b;
            }
        }

        double denominator = Math.Sqrt(energyA * energyB);
        if (denominator < 1e-12 || energyA < 0.01 * energyB || energyB < 0.01 * energyA)
        {
            return 0;
        }

        return dot / denominator;
    }

    /// <summary>
    /// Decodes a follow-on burst at <paramref name="start"/>, where the previous burst ended,
    /// with the timing and channel the keyup state carries. Null if the header does not read;
    /// a burst with a null payload if it read but the payload's CRC did not, or if no burst on
    /// its geometry has been decoded in this keyup, so that there is no channel to read it
    /// against. On success the state's payload channel for that geometry is refreshed from the
    /// decoded symbols.
    /// </summary>
    public OfdmFmBurst? DecodeFollowOnAt(
        ReadOnlySpan<float> audio, int start, OfdmFmKeyupState keyup)
    {
        ArgumentNullException.ThrowIfNull(keyup);
        if (ReadHeaderAt(audio, start, keyup.AcquisitionChannel, keyup.AcquisitionNoise,
            out byte[] headerBits) is not OfdmFmHeader header)
        {
            return null;
        }

        Layout layout = LayoutFor(header.Geometry)!;
        if (!keyup.PayloadChannels.TryGetValue(header.Geometry, out (double Re, double Im)[]? channel)
            || !keyup.PayloadNoise.TryGetValue(header.Geometry, out double noise))
        {
            return new OfdmFmBurst(
                null, header.Constellation, start, header.Coding, null, header.Recommendation,
                header.Geometry, header.RecommendedGeometry,
                FollowOn: true, AskedFullBursts: header.AskedFullBursts);
        }

        // The payload's reference epoch is the previous frame's last payload symbol, which sits
        // one header's worth before this payload; the burst-wide fit counts elapsed symbols from
        // there. The header symbols stay out of that fit: their own epoch is the previous
        // header, and mixing the two would put a frame's length of disagreement into one
        // regression.
        int payloadStart = start + FollowOnHeaderSamples;
        return DecodeBody(
            audio, start, header, headerBits, start, layout, channel, noise,
            keyup.AcquisitionChannel, payloadStart,
            firstPayloadSymbol: _headerSymbols,
            headerInFit: false, keyup);
    }

    /// <summary>
    /// Total samples one burst occupies from its sync position, for a header already read. What
    /// lets a streaming receiver hold exactly the burst and release the buffer afterwards, rather
    /// than accumulating audio and hoping.
    /// </summary>
    public int BurstSamples(OfdmFmHeader header)
    {
        // The signalled geometry and the signalled coding, not this receiver's profile: getting
        // either from the profile is what made both ends have to agree in advance, and a receiver
        // that sized a burst with the wrong one would stop collecting part way through it. A header
        // naming a geometry this station does not hold never gets here: ReadHeader refuses it.
        Layout layout = LayoutFor(header.Geometry)
            ?? throw new ArgumentException(
                $"geometry {header.Geometry} is not in this station's table", nameof(header));
        int[] carrierBits = layout.Geometry.BitsPerDataCarrier(header.Constellation);
        int codedBits = DecoderFor(header.Coding).CodedBits((header.PayloadLength + 2) * 8);
        int perSymbol = carrierBits.Sum();
        int symbols = ((codedBits + perSymbol) - 1) / perSymbol;
        return PayloadOffset(header.Geometry) + (symbols * _parameters.SymbolSamples);
    }

    /// <summary>
    /// Decodes the burst at a known sync position. Returns null if there is no readable header
    /// there or the audio runs out; returns a burst with a null payload if the header read but the
    /// payload's CRC did not, which is a real burst this receiver could not copy rather than
    /// nothing at all.
    /// </summary>
    public OfdmFmBurst? DecodeAt(ReadOnlySpan<float> audio, int sync) => DecodeAt(audio, sync, out _);

    /// <summary>
    /// As above, and hands back the state a receiver needs to read follow-on bursts after this
    /// one: null when no header was read, otherwise the acquisition channel and the payload
    /// layout's channel as this burst measured them, refreshed from the decoded payload when
    /// there was one.
    /// </summary>
    public OfdmFmBurst? DecodeAt(ReadOnlySpan<float> audio, int sync, out OfdmFmKeyupState? keyup)
    {
        keyup = null;
        double acquisitionNoise = NoiseVariance(_acquisition, audio, sync);
        (double Re, double Im)[]? acquisitionChannel = ChannelEstimate(
            _acquisition, audio, sync + _parameters.SymbolSamples, acquisitionNoise);
        if (acquisitionChannel is null)
        {
            return null;
        }

        int headerStart = sync + (2 * _parameters.SymbolSamples);
        if (ReadHeaderAt(audio, headerStart, acquisitionChannel, acquisitionNoise, out byte[] headerBits)
            is not OfdmFmHeader header)
        {
            return null;
        }

        // The layout the payload is on, which the header just named. ReadHeader refused any id
        // this station does not hold, so this cannot be null.
        Layout layout = LayoutFor(header.Geometry)!;

        // The channel the payload travels, and the noise on its carriers. On the acquisition
        // layout both were measured already, from the preamble and the sync symbol. On any other
        // layout the preamble never occupied the carriers, so the estimate comes from the symbol
        // the transmitter sent for the purpose, and the noise from the bins of the sync symbol
        // that the sync symbol leaves empty - which, over a wider span than the sync symbol's, is
        // most of them.
        double noise;
        (double Re, double Im)[]? channel;
        if (header.Geometry == OfdmFmGeometryTable.AcquisitionId)
        {
            noise = acquisitionNoise;
            channel = acquisitionChannel;
        }
        else
        {
            noise = NoiseVariance(layout, audio, sync);
            channel = ChannelEstimate(layout, audio, sync + HeaderEndOffset, noise);
            if (channel is null)
            {
                return null;
            }
        }

        // The state a follow-on frame would be read with, as this burst measured it. The
        // payload channel registered now is the estimate symbol's (or the preamble's); a decoded
        // payload replaces it with one refreshed from the payload's own symbols.
        keyup = new OfdmFmKeyupState(acquisitionChannel, acquisitionNoise);
        keyup.PayloadChannels[header.Geometry] = channel;
        keyup.PayloadNoise[header.Geometry] = noise;
        keyup.Geometry = header.Geometry;

        return DecodeBody(
            audio, sync, header, headerBits, headerStart, layout, channel, noise,
            acquisitionChannel, sync + PayloadOffset(header.Geometry),
            firstPayloadSymbol: header.Geometry == OfdmFmGeometryTable.AcquisitionId ? _headerSymbols : 0,
            headerInFit: true, keyup);
    }

    /// <summary>Payload symbols at the end of a decoded frame that refresh the keyup's channel
    /// estimate for the next follow-on frame. Four: enough to average the noise down, and each
    /// rotated to the last one's timing by the correction that decoded it, so what the clock did
    /// under them does not smear the average.</summary>
    private const int DecisionDirectedSymbols = 4;

    /// <summary>
    /// The part of a decode that is the same for a full burst and a follow-on one: equalise the
    /// payload symbols against a channel, correct the clock tilt, decode, and on success refresh
    /// the keyup state from what was decoded.
    /// </summary>
    /// <param name="audio">The audio the burst is in.</param>
    /// <param name="startSample">Where the burst starts in the audio, for the record.</param>
    /// <param name="header">The burst's header, already read.</param>
    /// <param name="headerBits">The 52 decoded header bits, for re-encoding as references.</param>
    /// <param name="headerStart">Where its header symbols start.</param>
    /// <param name="layout">The payload's layout, as the header named it.</param>
    /// <param name="channel">The channel estimate the payload is equalised against.</param>
    /// <param name="noise">Noise power per bin on the payload's carriers.</param>
    /// <param name="acquisitionChannel">The channel estimate the header was read against.</param>
    /// <param name="payloadStart">Where its payload symbols start.</param>
    /// <param name="firstPayloadSymbol">Symbols between the payload channel's reference epoch
    /// and the first payload symbol, for the clock-tilt fit's elapsed count.</param>
    /// <param name="headerInFit">Whether the header symbols, equalised against the acquisition
    /// channel, join the tilt fit as known references. True for a full burst, whose header and
    /// preamble share an epoch; false for a follow-on, whose header's epoch is another frame's.
    /// </param>
    /// <param name="keyup">The keyup state to refresh from the decoded payload.</param>
    private OfdmFmBurst? DecodeBody(
        ReadOnlySpan<float> audio,
        int startSample,
        OfdmFmHeader header,
        byte[] headerBits,
        int headerStart,
        Layout layout,
        (double Re, double Im)[] channel,
        double noise,
        (double Re, double Im)[] acquisitionChannel,
        int payloadStart,
        int firstPayloadSymbol,
        bool headerInFit,
        OfdmFmKeyupState keyup)
    {
        int sync = startSample;
        OfdmFmConstellation constellation = header.Constellation;
        int framedLength = header.PayloadLength + 2;

        int[] carrierBits = layout.Geometry.BitsPerDataCarrier(constellation);
        var tables = new Dictionary<int, (float I, float Q)[]>();
        foreach (int b in carrierBits.Distinct())
        {
            tables[b] = OfdmFmMapper.Points((OfdmFmConstellation)b);
        }

        int payloadBits = framedLength * 8;
        OfdmFmCodec payloadCodec = DecoderFor(header.Coding);
        int codedBits = payloadCodec.CodedBits(payloadBits);
        int perSymbol = carrierBits.Sum();
        int symbols = ((codedBits + perSymbol) - 1) / perSymbol;

        // How much each carrier's soft bits are worth. After equalisation a carrier's noise is
        // the channel's noise divided by that carrier's gain, so its log-likelihood ratios should
        // carry weight in proportion to that gain squared. A flat scale says a carrier 20 dB down
        // - and the edge carriers of a voice passband ARE that far down - is as trustworthy as a
        // clean one, and hands the Viterbi decoder noise wearing the same confidence as data.
        //
        // The noise AT that carrier is deliberately not the other half of this ratio, though on
        // the deployed un-de-emphasised tap it tilts 18 dB across the band and the theory says it
        // belongs here. It was built and it measured WORSE at every practical rate's cliff - even
        // handed the link model's ground-truth noise per carrier, per seed, rather than its own
        // estimate. See "Per-carrier noise weighting" in docs/dev/ofdm-fm/receiver-findings.md before
        // rebuilding it.
        var weight = new double[layout.TotalCarriers];
        double meanPower = 0;
        for (int c = 0; c < channel.Length; c++)
        {
            weight[c] = (channel[c].Re * channel[c].Re) + (channel[c].Im * channel[c].Im);
            meanPower += weight[c];
        }

        meanPower /= Math.Max(1, channel.Length);
        for (int c = 0; c < weight.Length; c++)
        {
            weight[c] = meanPower > 0 ? weight[c] / meanPower : 1.0;
        }

        // The header's carriers became known values the moment its CRC passed: re-encode the
        // decoded bits and every header carrier is a reference the sample-clock fit can read,
        // from a part of the burst that has already proved itself. Equalised afresh here - the
        // header read did the same work and threw it away, and two transforms per burst is
        // nothing against what the extra measurements buy the fit. On the acquisition layout and
        // against the acquisition channel, which is what the header was sent on.
        byte[] headerCoded = _headerCodec.Encode(headerBits.AsSpan(0, HeaderBits));
        var headerEqualised = new (double Re, double Im)[headerInFit ? _headerSymbols : 0][];
        for (int h = 0; h < headerEqualised.Length; h++)
        {
            int offset = headerStart + (h * _parameters.SymbolSamples);
            (double Re, double Im)[]? symbol =
                Equalised(_acquisition, audio, offset, acquisitionChannel);
            if (symbol is null)
            {
                return null;
            }

            headerEqualised[h] = symbol;
        }

        // Every payload symbol is equalised before any is demapped, because the sample-clock
        // correction below needs the whole burst's pilots in view at once.
        var equalised = new (double Re, double Im)[symbols][];
        for (int s = 0; s < symbols; s++)
        {
            int offset = payloadStart + (s * _parameters.SymbolSamples);
            (double Re, double Im)[]? symbol = Equalised(layout, audio, offset, channel);
            if (symbol is null)
            {
                return null;
            }

            equalised[s] = symbol;
        }

        (byte[]? Payload, double? PreFec, byte[] Sent) Decoded((double Re, double Im)[][] source)
        {
            var llrs = new float[symbols * perSymbol];
            int written = 0;
            for (int s = 0; s < symbols; s++)
            {
                (double Re, double Im)[] symbol = source[s];
                int data = 0;
                for (int c = 0; c < symbol.Length; c++)
                {
                    if (layout.PilotMap[c])
                    {
                        continue;
                    }

                    int bits = carrierBits[data++];
                    OfdmFmMapper.SoftBits(
                        tables[bits], bits, (float)symbol[c].Re, (float)symbol[c].Im,
                        SoftScale * (float)weight[c],
                        llrs.AsSpan(written, bits));
                    written += bits;
                }
            }

            // What turns the demapper's distances into TRUE log-likelihood ratios, for the one
            // decoder that cares about absolute scale. The metrics carry SoftScale times channel
            // power over mean power; true likelihood is distance times channel power over noise;
            // so the factor is mean power over noise, with the demapper's own scaling folded out.
            // The noise is the sync symbol's free measurement, and zero means "unmeasured", which
            // only a noiseless bench produces.
            double llrTrueScale = noise > 0 ? meanPower / (noise * SoftScale) : 0;
            byte[] payloadBitArray =
                payloadCodec.Decode(llrs.AsSpan(0, codedBits), payloadBits, llrTrueScale);

            // What the carriers would have carried if the decoder is right: the decoded bits
            // re-encoded. Through Encode even when there is no code, because the interleaver is
            // applied either way and the carriers hold the bits in interleaved order.
            byte[] sent = payloadCodec.Encode(payloadBitArray);
            double? preFec = PreFecErrorRate(header.Coding, sent, llrs, codedBits);
            var writer = new BitWriter();
            foreach (byte bit in payloadBitArray)
            {
                writer.Write(bit, 1);
            }

            byte[] framed = writer.ToArray();
            if (framed.Length < framedLength)
            {
                return (null, preFec, sent);
            }

            Array.Resize(ref framed, framedLength);
            Scramble(framed);
            ushort crc = (ushort)(framed[^2] | (framed[^1] << 8));
            byte[] payload = framed[..header.PayloadLength];
            return Crc16X25.Compute(payload) == crc ? (payload, preFec, sent) : (null, preFec, sent);
        }

        // When the pilots believe a sample-clock tilt, the corrected read is tried first and the
        // payload CRC arbitrates: a correction that proves itself is kept - and its spending
        // figure with it, so a clock difference does not masquerade as link margin - and one that
        // does not is discarded for the plain read. The fallback is what makes the fit safe to
        // act on at all: even shrunk by its own variance it is sometimes noise, and a burst the
        // plain read would have copied must never be lost to a correction. Costs one extra decode
        // only on the bursts where the fit fired, which a clock difference makes rare or worth it.
        // Three readings at most, tried best-informed first, the payload CRC arbitrating each:
        // the burst-wide tilt fit refined symbol by symbol from each symbol's own pilots, the
        // burst-wide fit alone, and the plain equalised symbols. A reading that proves itself is
        // kept, with its spending figure; one that does not is discarded for the next. The extra
        // decodes cost only on bursts where a correction fired and failed, which a clock
        // difference makes rare or worth it.
        (byte[]? Payload, double? PreFec, byte[] Sent) outcome = default;
        (double Re, double Im)[][] read = equalised;
        // A follow-on burst, whose header is not in the fit, may have been found a sample or
        // two off the grid its channel estimate was made on: for it the fit's static tilt is
        // that error and is applied. For a full burst it is estimate noise and is not.
        double[]? burstRamp = null;
        double[]? refinedRamp = null;
        (double Re, double Im)[][]? corrected = ClockTiltCorrection
            ? SampleClockCorrected(
                layout, equalised, channel, headerEqualised, headerCoded, acquisitionChannel,
                firstPayloadSymbol, correctStatic: !headerInFit, out burstRamp)
            : null;
        (double Re, double Im)[][]? refined = ClockTiltCorrection
            ? PerSymbolTiltRefined(layout, corrected ?? equalised, channel, out refinedRamp)
            : null;
        bool decided = false;
        foreach ((double Re, double Im)[][]? candidate in new[] { refined, corrected, equalised })
        {
            if (candidate is null)
            {
                continue;
            }

            (byte[]? Payload, double? PreFec, byte[] Sent) attempt = Decoded(candidate);
            if (!decided || attempt.Payload is not null)
            {
                outcome = attempt;
                read = candidate;
                decided = true;
            }

            if (attempt.Payload is not null)
            {
                break;
            }
        }

        // Per-carrier signal to noise from the error vectors, only once the CRC has made the
        // reference trustworthy: against a wrong reference the figure would be a measurement of
        // the decoder's mistakes rather than of the channel.
        // The plain equalised symbols are always the last candidate, so a reading was always
        // taken and Sent is set whenever Payload is; the compiler cannot see that through the loop.
        double[]? carrierSnr = null;
        if (outcome.Payload is not null)
        {
            (double Re, double Im)[][] points =
                KnownPoints(layout, symbols, carrierBits, tables, outcome.Sent!, codedBits);
            carrierSnr = CarrierSnr(layout, read, points);

            // Every carrier of every payload symbol is a known value now, so the last few
            // symbols are a channel estimate as good as an estimate symbol and one symbol old:
            // what the next follow-on frame in this keyup is read against. Each symbol was
            // rotated to the estimate's epoch by the corrections that decoded it, and that
            // rotation is what aligns the last symbols coherently and puts the new estimate at
            // the last one's timing, which is where the next burst begins.
            var ramp = new double[symbols];
            for (int s = 0; s < symbols; s++)
            {
                if (ReferenceEquals(read, refined))
                {
                    ramp[s] = (burstRamp?[s] ?? 0) + refinedRamp![s];
                }
                else if (ReferenceEquals(read, corrected))
                {
                    ramp[s] = burstRamp![s];
                }
            }

            keyup.PayloadChannels[header.Geometry] = DecisionDirectedEstimate(
                layout, read, channel, points, ramp, PilotCentre(layout, channel), noise);
            keyup.PayloadNoise[header.Geometry] = noise;
            keyup.Geometry = header.Geometry;

            // Measured over the last symbols, which is where the next burst of the keyup takes
            // its alignment from: over a long burst the clocks drift, and the boundary at the end
            // is not quite the boundary at the start.
            int measured = Math.Min(symbols, DecisionDirectedSymbols);
            keyup.Alignment = -BoundaryOffset(
                audio, payloadStart + ((symbols - measured) * _parameters.SymbolSamples), measured);
        }

        var carrierGain = new double[weight.Length];
        for (int c = 0; c < weight.Length; c++)
        {
            carrierGain[c] = weight[c] > 0 ? 10 * Math.Log10(weight[c]) : double.NegativeInfinity;
        }

        return new OfdmFmBurst(
            outcome.Payload, constellation, sync, header.Coding, outcome.PreFec,
            header.Recommendation, header.Geometry, header.RecommendedGeometry,
            carrierSnr, carrierGain,
            FollowOn: !headerInFit, AskedFullBursts: header.AskedFullBursts);
    }

    /// <summary>
    /// Signal to noise per carrier, from the distance between what was received on each carrier
    /// and what the decoded bits say was sent there, averaged over the payload symbols.
    /// </summary>
    /// <remarks>
    /// The walk over symbols and carriers is the transmitter's own, padding bits included, so the
    /// reference point for every carrier of every symbol is exactly what the modulator put there.
    /// Pilots are measured too, against the reference pattern, which is what makes the figure
    /// available on every occupied carrier rather than only the data ones. Signal power is taken
    /// from the reference points themselves rather than assumed to be one, so a bit-loaded layout
    /// mixing constellations reads correctly.
    /// </remarks>
    private static double[] CarrierSnr(
        Layout layout,
        (double Re, double Im)[][] symbols,
        (double Re, double Im)[][] points)
    {
        var error = new double[layout.TotalCarriers];
        var signal = new double[layout.TotalCarriers];
        for (int s = 0; s < symbols.Length; s++)
        {
            for (int c = 0; c < layout.TotalCarriers; c++)
            {
                (double pRe, double pIm) = points[s][c];
                double dRe = symbols[s][c].Re - pRe;
                double dIm = symbols[s][c].Im - pIm;
                error[c] += (dRe * dRe) + (dIm * dIm);
                signal[c] += (pRe * pRe) + (pIm * pIm);
            }
        }

        var snr = new double[layout.TotalCarriers];
        for (int c = 0; c < snr.Length; c++)
        {
            snr[c] = error[c] > 0
                ? 10 * Math.Log10(signal[c] / error[c])
                : double.PositiveInfinity;
        }

        return snr;
    }

    /// <summary>
    /// The constellation point every carrier of every payload symbol carried, from the decoded
    /// bits re-encoded: the transmitter's own walk over symbols and carriers, padding bits
    /// included, so the reference for each carrier is exactly what the modulator put there.
    /// Pilots carry the reference pattern.
    /// </summary>
    private static (double Re, double Im)[][] KnownPoints(
        Layout layout,
        int symbols,
        int[] carrierBits,
        Dictionary<int, (float I, float Q)[]> tables,
        byte[] sent,
        int codedBits)
    {
        var points = new (double Re, double Im)[symbols][];
        int read = 0;
        for (int s = 0; s < symbols; s++)
        {
            points[s] = new (double Re, double Im)[layout.TotalCarriers];
            int data = 0;
            for (int c = 0; c < layout.TotalCarriers; c++)
            {
                if (layout.PilotMap[c])
                {
                    points[s][c] = (layout.Reference[c].Re, 0);
                    continue;
                }

                int bits = carrierBits[data++];
                int value = 0;
                for (int b = 0; b < bits; b++)
                {
                    value = (value << 1)
                        | (read < codedBits && read < sent.Length ? sent[read++] : PadBit(read++));
                }

                (float I, float Q) point = tables[bits][value];
                points[s][c] = (point.I, point.Q);
            }
        }

        return points;
    }

    /// <summary>
    /// A channel estimate for a payload layout from the last few symbols of a decoded frame,
    /// every carrier of which is a known value, at the timing of the last of them.
    /// </summary>
    /// <remarks>
    /// <para>Taken from the symbols as they were read: equalised against the channel the frame
    /// was decoded with and rotated by the corrections that decoded it. The read symbol against
    /// the point sent, per carrier, is then the residual of the estimate the frame was read
    /// with, coherent across the symbols because each was rotated to the same epoch before it
    /// was demapped; put back through that estimate it is a fresh one, weighted by each point's
    /// own power so a dense constellation's inner points do not pull it toward their noise.
    /// The estimate is then moved to the last symbol's timing by that symbol's own rotation,
    /// because that is where the next burst of the keyup begins, and denoised exactly as a
    /// preamble's estimate is.</para>
    /// <para>Averaging the raw bins instead, which is what this did first, smeared the estimate
    /// across whatever the clocks did during those symbols. On air on 2026-09-19 the relative
    /// clock wandered by up to 0.4 samples inside a burst on the radio1 to radio2 path, and a
    /// follow-on burst read against a smeared estimate came out one to two samples off its grid
    /// and 4 to 8 dB down, on every other burst.</para>
    /// </remarks>
    private (double Re, double Im)[] DecisionDirectedEstimate(
        Layout layout,
        (double Re, double Im)[][] read,
        (double Re, double Im)[] channel,
        (double Re, double Im)[][] points,
        double[] ramp,
        double centre,
        double noise)
    {
        int symbols = read.Length;
        int from = Math.Max(0, symbols - DecisionDirectedSymbols);
        var numerator = new (double Re, double Im)[layout.TotalCarriers];
        var denominator = new double[layout.TotalCarriers];
        for (int s = from; s < symbols; s++)
        {
            for (int c = 0; c < layout.TotalCarriers; c++)
            {
                (double yRe, double yIm) = read[s][c];
                (double xRe, double xIm) = points[s][c];
                // Y times the conjugate of X, summed; divided by the summed power of X below.
                numerator[c] = (
                    numerator[c].Re + (yRe * xRe) + (yIm * xIm),
                    numerator[c].Im + (yIm * xRe) - (yRe * xIm));
                denominator[c] += (xRe * xRe) + (xIm * xIm);
            }
        }

        double last = ramp[symbols - 1];
        var estimate = new (double Re, double Im)[layout.TotalCarriers];
        for (int c = 0; c < estimate.Length; c++)
        {
            (double gRe, double gIm) = denominator[c] > 1e-12
                ? (numerator[c].Re / denominator[c], numerator[c].Im / denominator[c])
                : (1, 0);
            (double hRe, double hIm) = channel[c];
            double re = (hRe * gRe) - (hIm * gIm);
            double im = (hRe * gIm) + (hIm * gRe);

            // The corrections rotated the last symbol by minus this; the channel at its timing
            // is the epoch's rotated by plus it.
            double angle = last * (layout.FirstCarrier + c - centre);
            double cos = Math.Cos(angle);
            double sin = Math.Sin(angle);
            estimate[c] = ((re * cos) - (im * sin), (re * sin) + (im * cos));
        }

        return Denoise(layout, estimate, noise);
    }

    /// <summary>The pilots' weighted mean position: the point the tilt corrections rotate
    /// about, computed the way both of them compute it.</summary>
    private static double PilotCentre(Layout layout, (double Re, double Im)[] channel)
    {
        double weightSum = 0;
        double centre = 0;
        for (int c = 0; c < layout.PilotMap.Length; c++)
        {
            if (!layout.PilotMap[c])
            {
                continue;
            }

            double w = (channel[c].Re * channel[c].Re) + (channel[c].Im * channel[c].Im);
            weightSum += w;
            centre += w * (layout.FirstCarrier + c);
        }

        return weightSum > 0 ? centre / weightSum : layout.FirstCarrier + (layout.TotalCarriers / 2.0);
    }

    /// <summary>
    /// The acquisition layout's channel from a header whose CRC has passed: every carrier of
    /// every header symbol is a known BPSK value once the bits are re-encoded, so the header is
    /// a preamble one frame old. What keeps a keyup's follow-on headers readable however long
    /// the keyup runs.
    /// </summary>
    private (double Re, double Im)[] AcquisitionFromHeader(
        ReadOnlySpan<float> audio, int headerStart, byte[] headerBits, double noise)
    {
        byte[] coded = _headerCodec.Encode(headerBits.AsSpan(0, HeaderBits));
        var channel = new (double Re, double Im)[_acquisition.TotalCarriers];
        for (int h = 0; h < _headerSymbols; h++)
        {
            (double[] re, double[] im) = SymbolBins(audio, headerStart + (h * _parameters.SymbolSamples));
            int data = 0;
            for (int c = 0; c < channel.Length; c++)
            {
                int bin = _acquisition.FirstCarrier + c;
                double known = _acquisition.PilotMap[c]
                    ? _acquisition.Reference[c].Re
                    : (coded[HeaderBitAt(h, data++)] == 1 ? 1 : -1);
                channel[c] = (channel[c].Re + (re[bin] * known), channel[c].Im + (im[bin] * known));
            }
        }

        for (int c = 0; c < channel.Length; c++)
        {
            channel[c] = (channel[c].Re / _headerSymbols, channel[c].Im / _headerSymbols);
        }

        return Denoise(_acquisition, channel, noise);
    }

    /// <summary>
    /// How many of the coded bits the demodulator got wrong, as a fraction, by comparing what the
    /// decoder decided, re-encoded, against what came off the subcarriers.
    /// </summary>
    /// <remarks>
    /// <para>The classic way to read a coded link's margin, and it costs one encode: a pass of a
    /// shift register over a few thousand bits, against a Viterbi decode that has just walked the
    /// same length with 64 or 256 states. Nothing measurable.</para>
    /// <para>The comparison is in the interleaved domain, which is where the log-likelihood ratios
    /// already are. <c>Encode</c> interleaves on its way out, so its result lines up with them bit
    /// for bit and neither side has to be reordered.</para>
    /// <para>The sign of a log-likelihood ratio is the demodulator's hard decision, positive
    /// meaning zero. Exactly zero is counted as a zero rather than as half an error; it happens
    /// only where a carrier was punctured out or the metric underflowed, and both are rare enough
    /// that inventing a convention for them would be more precision than the measure has.</para>
    /// </remarks>
    private static double? PreFecErrorRate(
        OfdmFmCoding coding,
        byte[] sent,
        float[] llrs,
        int codedBits)
    {
        if (coding.Scheme == OfdmFmFec.None)
        {
            return null;
        }

        int compared = Math.Min(Math.Min(sent.Length, codedBits), llrs.Length);
        if (compared == 0)
        {
            return null;
        }

        int wrong = 0;
        for (int i = 0; i < compared; i++)
        {
            if ((llrs[i] < 0 ? 1 : 0) != sent[i])
            {
                wrong++;
            }
        }

        return (double)wrong / compared;
    }

    // The channel estimate comes from a reference symbol: every occupied carrier of the layout
    // carries a known value, so dividing through gives the channel's response bin by bin. For the
    // acquisition layout that symbol is the preamble, one symbol after the sync; for any other it
    // is the estimate symbol the transmitter sends after the header.
    private (double Re, double Im)[]? ChannelEstimate(
        Layout layout, ReadOnlySpan<float> audio, int start, double noise)
    {
        if (start < 0 || start + _parameters.SymbolSamples > audio.Length)
        {
            return null;
        }

        (double[] re, double[] im) = SymbolBins(audio, start);
        var channel = new (double Re, double Im)[layout.TotalCarriers];
        for (int c = 0; c < channel.Length; c++)
        {
            int bin = layout.FirstCarrier + c;
            double refRe = layout.Reference[c].Re;
            channel[c] = refRe >= 0 ? (re[bin], im[bin]) : (-re[bin], -im[bin]);
        }

        return Denoise(layout, channel, noise);
    }

    /// <summary>
    /// Throws away the part of a channel estimate that cannot be channel.
    /// </summary>
    /// <remarks>
    /// <para>The estimate is one noisy division per carrier and it is the dominant impairment in
    /// this receiver: equalisation divides by it, so a carrier 20 dB down - and this waveform sits
    /// on both edges of a voice passband, so it always has some - multiplies its own estimate noise
    /// tenfold.</para>
    /// <para>The channel is a physical audio path, so its response is a handful of filters and its
    /// energy sits in a short run of delays. Noise does not: it is spread evenly over every delay.
    /// So transform the estimate to a delay profile, shrink each tap by how much of it is signal
    /// rather than noise, and transform back. The taps that were only ever noise collapse to
    /// nothing and the ones carrying the channel are left alone.</para>
    /// <para><b>Over the occupied band only, which is the whole trick.</b> An earlier attempt put
    /// the occupied bins into a full-transform spectrum, invented values for the unoccupied ones,
    /// and truncated in time. That rings - the invented edge is a discontinuity - and it rang hard
    /// enough to stop QAM-256 round tripping on a noiseless loopback while QPSK never noticed.
    /// Transforming only the bins that carry something invents nothing.</para>
    /// <para>The shrinkage needs no threshold to be chosen because it is derived from the noise
    /// measurement: a tap's signal power is its own power less the noise power per tap, floored at
    /// zero, and the gain is the usual ratio of that to the total. On a clean channel the noise
    /// measures near zero, every gain is one, and this does nothing at all - which is what lets it
    /// sit in front of a dense constellation safely.</para>
    /// </remarks>
    /// <summary>
    /// Diagnostic switch for offline tools: whether the channel estimate is denoised at all.
    /// Process-wide and not for a station. It exists because the first wide-span bursts read
    /// 6 to 8 dB worse than x6 across the whole band, and the denoiser was the suspect: a steep
    /// roll-off inside the occupied span is a long tail in the delay domain, which the shrinkage
    /// attacks as though it were noise. A tool that can run the same capture both ways settles it.
    /// </summary>
    public static bool EstimateDenoising { get; set; } = true;

    /// <summary>
    /// Diagnostic switch for offline tools: whether the sample-clock tilt correction is tried at
    /// all. Process-wide and not for a station. Same reason as <see cref="EstimateDenoising"/>:
    /// a residual tilt on a long, wide burst is one of the few things that can cost every carrier
    /// at once, and the only way to see its share is to read a capture with and without it.
    /// </summary>
    public static bool ClockTiltCorrection { get; set; } = true;

    /// <summary>
    /// Diagnostic switch for offline tools: whether <see cref="BoundCarrierErrors"/> runs at all,
    /// on the transmit side. Process-wide and not for a station. Same reason as
    /// <see cref="EstimateDenoising"/>: it is what lets a measurement compare the crest-factor
    /// distribution with and without the bound, on the same payloads, to show the bound is not
    /// spending drive on symbols that never needed it.
    /// </summary>
    public static bool CarrierErrorBound { get; set; } = true;

    private static (double Re, double Im)[] Denoise(
        Layout layout, (double Re, double Im)[] channel, double noise)
    {
        int n = channel.Length;
        if (n < 4 || noise <= 0 || !EstimateDenoising)
        {
            return channel;
        }

        double[] twiddleCos = layout.TwiddleCos;
        double[] twiddleSin = layout.TwiddleSin;
        double[] delayRe = layout.DelayRe;
        double[] delayIm = layout.DelayIm;
        if (twiddleCos.Length != n)
        {
            return channel;
        }

        // Take the bulk delay out first, and put it back at the end. This matters more than it
        // looks: the burst is not sampled at the instant the channel's response peaks, so across
        // the band the estimate carries a phase ramp of some non-integer slope, and a non-integer
        // ramp does not transform to a spike. It transforms to a sinc smeared over every tap - at
        // which point "the channel is a few taps and noise is everywhere" is false, the shrinkage
        // below attacks the sinc's tails as though they were noise, and the estimate comes back
        // distorted. Measured: without this the uncoded row lost ground at high carrier-to-noise
        // ratios, where there is nothing to denoise and only the distortion showed.
        double rampRe = 0;
        double rampIm = 0;
        for (int c = 1; c < n; c++)
        {
            // Average the carrier-to-carrier phase step, each weighted by how much signal it has,
            // so the weak band edges do not set the slope for the strong middle.
            rampRe += (channel[c].Re * channel[c - 1].Re) + (channel[c].Im * channel[c - 1].Im);
            rampIm += (channel[c].Im * channel[c - 1].Re) - (channel[c].Re * channel[c - 1].Im);
        }

        double slope = Math.Atan2(rampIm, rampRe);
        for (int c = 0; c < n; c++)
        {
            double angle = -slope * c;
            double cos = Math.Cos(angle);
            double sin = Math.Sin(angle);
            channel[c] = (
                (channel[c].Re * cos) - (channel[c].Im * sin),
                (channel[c].Re * sin) + (channel[c].Im * cos));
        }

        // Delay profile of the occupied band. Not a power of two in general, so a plain transform;
        // at a hundred-odd carriers that is a few thousand multiplies once per header read, with
        // the trigonometry read out of a table rather than recomputed.
        for (int k = 0; k < n; k++)
        {
            double sumRe = 0;
            double sumIm = 0;
            int step = 0;
            for (int c = 0; c < n; c++)
            {
                double cos = twiddleCos[step];
                double sin = twiddleSin[step];
                sumRe += (channel[c].Re * cos) - (channel[c].Im * sin);
                sumIm += (channel[c].Re * sin) + (channel[c].Im * cos);
                step += k;
                if (step >= n)
                {
                    step -= n;
                }
            }

            delayRe[k] = sumRe / n;
            delayIm[k] = sumIm / n;
        }

        // Noise on each carrier is independent, so spreading it over n taps divides its power by n.
        // A tap's signal power is its own power less that, floored at zero, and the gain is the
        // usual ratio - no threshold to pick, and on a clean channel every gain comes out at one.
        double perTap = noise / n;
        for (int k = 0; k < n; k++)
        {
            double power = (delayRe[k] * delayRe[k]) + (delayIm[k] * delayIm[k]);
            double signal = Math.Max(power - perTap, 0);
            double gain = signal / (signal + perTap);
            delayRe[k] *= gain;
            delayIm[k] *= gain;
        }

        for (int c = 0; c < n; c++)
        {
            double sumRe = 0;
            double sumIm = 0;
            int step = 0;
            for (int k = 0; k < n; k++)
            {
                double cos = twiddleCos[step];
                double sin = -twiddleSin[step];
                sumRe += (delayRe[k] * cos) - (delayIm[k] * sin);
                sumIm += (delayRe[k] * sin) + (delayIm[k] * cos);
                step += c;
                if (step >= n)
                {
                    step -= n;
                }
            }

            channel[c] = (sumRe, sumIm);
        }

        for (int c = 0; c < n; c++)
        {
            double angle = slope * c;
            double cos = Math.Cos(angle);
            double sin = Math.Sin(angle);
            channel[c] = (
                (channel[c].Re * cos) - (channel[c].Im * sin),
                (channel[c].Re * sin) + (channel[c].Im * cos));
        }

        return channel;
    }

    /// <summary>
    /// Noise power per bin, measured where the burst itself guarantees there is nothing else.
    /// </summary>
    /// <remarks>
    /// The sync symbol modulates only EVEN absolute bins of the acquisition layout - that is what
    /// makes its useful part two identical halves, and what the timing search relies on. So its
    /// ODD occupied bins carry no signal at all, and neither does any bin outside the acquisition
    /// span: whatever is in them is noise, measured in exactly the band a payload occupies, at
    /// exactly the instant it arrives, for nothing. No sounding, no training sequence, no airtime.
    /// For a payload layout wider than the acquisition one that is most of its bins, which is
    /// what makes the measurement honest about the discriminator noise that rises across the top
    /// of the band, where the sync symbol never goes.
    /// </remarks>
    internal double NoiseVarianceAt(ReadOnlySpan<float> audio, int sync) =>
        NoiseVariance(_own, audio, sync);

    private double NoiseVariance(Layout layout, ReadOnlySpan<float> audio, int sync)
    {
        if (sync < 0 || sync + _parameters.SymbolSamples > audio.Length)
        {
            return 0;
        }

        (double[] re, double[] im) = SymbolBins(audio, sync);
        double total = 0;
        int counted = 0;
        int syncFirst = _acquisition.FirstCarrier;
        int syncEnd = _acquisition.Geometry.EndCarrier;
        for (int c = 0; c < layout.TotalCarriers; c++)
        {
            int bin = layout.FirstCarrier + c;
            if ((bin & 1) == 0 && bin >= syncFirst && bin < syncEnd)
            {
                continue;
            }

            total += (re[bin] * re[bin]) + (im[bin] * im[bin]);
            counted++;
        }

        return counted > 0 ? total / counted : 0;
    }

    /// <summary>
    /// As above, and also hands back the 52 decoded header bits themselves, because a caller that
    /// wants to re-encode what was ACTUALLY transmitted must start from these. Rebuilding the bits
    /// from the parsed <see cref="OfdmFmHeader"/> is almost the same thing and the gap bites: a
    /// recommendation that did not parse comes back null, re-encodes as "no recommendation", and
    /// the reconstruction silently disagrees with the wire on the very carriers a caller wanted as
    /// known references.
    /// </summary>
    private OfdmFmHeader? ReadHeaderAt(
        ReadOnlySpan<float> audio,
        int headerStart,
        (double Re, double Im)[] channel,
        double noise,
        out byte[] decodedBits)
    {
        decodedBits = [];

        // Soft metrics for the coded header bits, accumulated over every copy of every bit in
        // every header symbol. On the acquisition layout, which is the only one the header is
        // ever sent on.
        var soft = new double[_headerCodedBits];
        for (int s = 0; s < _headerSymbols; s++)
        {
            (double Re, double Im)[]? headerSymbol = Equalised(
                _acquisition,
                audio,
                headerStart + (s * _parameters.SymbolSamples),
                channel,
                noise);
            if (headerSymbol is null)
            {
                return null;
            }

            // Weight each carrier by the channel's power there: after equalisation a carrier's
            // noise is the channel's divided by its gain, so a carrier 20 dB down - and the corner
            // of an FM voice passband always is - must not outvote a clean one.
            //
            // There was a per-symbol weight here too, each symbol scaled by its own pilot-measured
            // amplitude, on the reasoning that two header symbols are two different fades and the
            // faded one deserves less say. It was removed for two reasons. It measured as nothing:
            // A/B'd at 64 seeds over 768 bursts, 616 frames recovered with it against 617 without,
            // p = 1.0. And it broke a supported configuration outright - a profile with no pilot
            // carriers got an amplitude of zero, which multiplied every soft metric to zero, so the
            // header never decoded and the modem silently carried no traffic at all.
            int data = 0;
            for (int c = 0; c < headerSymbol.Length; c++)
            {
                if (_acquisition.PilotMap[c])
                {
                    continue;
                }

                double gain = (channel[c].Re * channel[c].Re) + (channel[c].Im * channel[c].Im);
                soft[HeaderBitAt(s, data++)] += gain * headerSymbol[c].Re;
            }
        }

        // A positive metric means the carrier landed at +1, which is a one; the decoder's
        // convention is that a positive log-likelihood ratio means a zero, so the sign flips. The
        // scale is normalised only to keep the numbers in a sensible range - a max-log Viterbi is
        // scale invariant, so this changes no decision.
        double mean = 0;
        foreach (double value in soft)
        {
            mean += Math.Abs(value);
        }

        mean /= Math.Max(1, soft.Length);
        double scale = mean > 1e-30 ? SoftScale / mean : 1;
        var llrs = new float[_headerCodedBits];
        for (int i = 0; i < llrs.Length; i++)
        {
            llrs[i] = (float)(-soft[i] * scale);
        }

        byte[] headerBitArray = _headerCodec.Decode(llrs, HeaderBits);
        decodedBits = headerBitArray;
        var headerBits = new BitWriter();
        foreach (byte bit in headerBitArray)
        {
            headerBits.Write(bit, 1);
        }

        byte[] headerBytes = headerBits.ToArray();
        if (headerBytes.Length < 7)
        {
            return null;
        }

        // The 52 bits, as laid out by HeaderSymbols: four nibbles and a twelve-bit length, then the
        // recommendation's three nibbles, then the CRC starting on a nibble boundary.
        int geometryId = headerBytes[0] >> 4;
        int constellationValue = headerBytes[0] & 0x0F;
        int codingId = headerBytes[1] >> 4;
        int length = ((headerBytes[1] & 0x0F) << 8) | headerBytes[2];
        int recGeometry = headerBytes[3] >> 4;
        int recConstellation = headerBytes[3] & 0x0F;
        int recCodingId = headerBytes[4] >> 4;
        ushort headerCrc = (ushort)(
            ((headerBytes[4] & 0x0F) << 12) | (headerBytes[5] << 4) | (headerBytes[6] >> 4));
        headerBytes[4] &= 0xF0;
        if (Crc16X25.Compute(headerBytes.AsSpan(0, 5)) != headerCrc)
        {
            return null;
        }

        // A reserved coding index, or a geometry this station does not hold, is refused rather
        // than defaulted. A receiver that guessed would decode with the wrong code or on the wrong
        // carriers and fail the payload CRC, which reads as a bad link and not as "that sender is
        // using something I do not know about".
        if (constellationValue is < 1 or > 8
            || CodingFor(codingId) is not OfdmFmCoding coding
            || _table[geometryId] is null)
        {
            return null;
        }

        // A recommendation that does not parse is dropped, not fatal. It is advice from the far
        // end about a direction this station is not even decoding here, so a burst that carries an
        // unreadable one is still a perfectly good burst. The recommended geometry rides with the
        // rate and is dropped with it, and separately if it names an entry this station does not
        // hold: the rate advice is still good, the geometry advice is simply one this station
        // cannot take.
        OfdmFmRate? recommendation = null;
        int? recommendedGeometry = null;
        bool askedFullBursts = false;

        // Values 9 to 15 are a constellation plus eight with the full-bursts flag set; see the
        // writer. Zero is no recommendation and 1 to 8 a constellation without the flag.
        int recConstellationValue = recConstellation > 8 ? recConstellation - 8 : recConstellation;
        if (recConstellationValue is >= 1 and <= 8
            && CodingFor(recCodingId) is OfdmFmCoding recCoding)
        {
            recommendation = Recommended((OfdmFmConstellation)recConstellationValue, recCoding);
            recommendedGeometry = _table[recGeometry] is null ? null : recGeometry;
            askedFullBursts = recConstellation > 8;
        }

        return new OfdmFmHeader(
            (OfdmFmConstellation)constellationValue, length, coding, recommendation,
            geometryId, recommendedGeometry, askedFullBursts);
    }

    /// <summary>Where the sync search thinks the burst starts, for a measurement that needs to
    /// separate acquisition from what happens after it. A channel delays the burst by its own group
    /// delay, so a measurement cannot assume the transmitter's own offset survives the trip.
    /// </summary>
    internal int SearchDelaySamples => _searchDelay;

    internal int FindSyncIn(ReadOnlySpan<float> audio) => FindSync(audio);

    /// <summary>One equalised symbol, for a measurement that wants to look at the constellation
    /// rather than at the bits. The channel estimate comes from the burst at
    /// <paramref name="sync"/>, exactly as a decode would take it.</summary>
    internal (double Re, double Im)[]? EqualisedAt(ReadOnlySpan<float> audio, int offset, int sync)
    {
        (double Re, double Im)[]? channel = EstimateAt(audio, sync);
        return channel is null ? null : Equalised(_own, audio, offset, channel);
    }

    /// <summary>The channel estimate this burst would use for a payload on this station's own
    /// layout, exposed so a measurement can compare one taken in noise against one taken from a
    /// clean copy of the same signal. On a station running alone that is the preamble's estimate;
    /// in a table it is the estimate symbol's.</summary>
    internal (double Re, double Im)[]? EstimateAt(ReadOnlySpan<float> audio, int sync)
    {
        double noise = NoiseVariance(_own, audio, sync);
        int start = _own.Id == OfdmFmGeometryTable.AcquisitionId
            ? sync + _parameters.SymbolSamples
            : sync + HeaderEndOffset;
        return ChannelEstimate(_own, audio, start, noise);
    }

    /// <summary>One equalised symbol against a channel estimate supplied from elsewhere - the
    /// experiment that separates "the constellation is noisy" from "the estimate we divide it by
    /// is noisy", which look identical downstream and want completely different fixes.</summary>
    internal (double Re, double Im)[]? EqualisedWith(
        ReadOnlySpan<float> audio, int offset, (double Re, double Im)[] channel) =>
        Equalised(_own, audio, offset, channel);

    // Equalises one symbol against the channel estimate, then takes out whatever common phase the
    // pilots say has crept in since - a sample-clock difference between the two ends shows up as a
    // slow rotation, and the pilots are there to measure it.
    private (double Re, double Im)[]? Equalised(
        Layout layout,
        ReadOnlySpan<float> audio,
        int offset,
        (double Re, double Im)[] channel,
        double noise = 0)
    {
        if (offset + _parameters.SymbolSamples > audio.Length)
        {
            return null;
        }

        (double[] re, double[] im) = SymbolBins(audio, offset);
        var carriers = new (double Re, double Im)[layout.TotalCarriers];
        for (int c = 0; c < carriers.Length; c++)
        {
            int bin = layout.FirstCarrier + c;
            (double hRe, double hIm) = channel[c];
            double power = (hRe * hRe) + (hIm * hIm);

            // Zero forcing, deliberately, and the reasoning is worth keeping because the
            // alternative looks obviously better and is not.
            //
            // A minimum-mean-square-error equaliser divides by |H|^2 + sigma^2 instead, which on a
            // waveform that sits on both edges of a voice passband - and so always has carriers
            // 20 dB down - looks like exactly the right medicine: where the channel vanishes it
            // rolls the carrier off instead of amplifying its noise without limit. It was built,
            // with a noise measurement taken free from the sync symbol's unmodulated bins
            // (NoiseVariance), and it measured WORSE: near threshold, 24 seeds a rung, the summed
            // score went from 93 to 92 at the profile's own rate and from 86 to 80 rescaled.
            //
            // The reason is not the noise estimate, which was checked against the model's own
            // ground truth and is accurate to within a tenth of a decibel. It is that the MMSE
            // output is BIASED: its expectation is the transmitted symbol shrunk by
            // |H|^2/(|H|^2 + sigma^2), so a weak carrier's constellation collapses toward the
            // origin while the demapper below is still measuring distance to full-sized reference
            // points. Remove that bias correctly - divide the result by the same factor - and the
            // algebra reduces exactly to this line. For a per-subcarrier OFDM equaliser, unbiased
            // MMSE IS zero forcing.
            //
            // What MMSE really buys in a coded system is the knowledge of which carriers to trust,
            // and that is already taken: DecodeAt weights each carrier's soft bits by |H|^2, which
            // is the same information applied where it actually helps.
            double denominator = power;
            if (denominator < 1e-20)
            {
                carriers[c] = (0, 0);
                continue;
            }

            carriers[c] = (((re[bin] * hRe) + (im[bin] * hIm)) / denominator,
                ((im[bin] * hRe) - (re[bin] * hIm)) / denominator);
        }

        double pilotRe = 0;
        double pilotIm = 0;
        for (int c = 0; c < carriers.Length; c++)
        {
            if (!layout.PilotMap[c])
            {
                continue;
            }

            // Pilots carry the reference pattern's own value, so the residual is the rotation.
            double sign = layout.Reference[c].Re >= 0 ? 1 : -1;
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

    /// <summary>
    /// Takes out the phase tilt a sample-clock difference between the two ends paints across the
    /// band, which the per-symbol pilot correction cannot see.
    /// </summary>
    /// <remarks>
    /// <para>An FM audio path has no carrier to offset, so the clocks in question are the two
    /// soundcards', and a difference slides the receiver's transform window slowly through the
    /// burst. A window slid by tau puts a phase ramp of slope proportional to tau ACROSS the
    /// carriers - not a common rotation, which the pilots already correct per symbol, but a tilt.
    /// The channel estimate absorbed the tilt as it stood at the preamble; what accrues after that
    /// lands on the payload, worst at the band edges, last symbols first, dense constellations
    /// first.</para>
    /// <para>Measured before this existed - noiseless loopback, the transmitted burst resampled by
    /// the offset, a narrow profile, 256 bytes: at 100 ppm QAM-256 2/3 spent 77 % of its code's
    /// correcting power on nothing but the tilt, and at 200 ppm it failed outright, with QAM-64
    /// 2/3 and QAM-16 3/4 past their own cliffs too. Two consumer soundcards 100 ppm apart is an
    /// ordinary thing to own. Nothing in the FM link model moves the clocks, so no ladder ever
    /// showed this; it would have arrived on air first.</para>
    /// <para>The model is one number that GROWS: a constant clock difference tilts the band by B
    /// more per symbol, so every pilot of every payload symbol contributes to one pooled weighted
    /// regression. A per-symbol fit is the obvious shape and it fails on arithmetic before it is
    /// ever built: on a layout with only a handful of pilots a single symbol's slope is mostly
    /// pilot noise, and rotating the band edges by noise costs more than the tilt does.</para>
    /// <para><b>Three guards, each paid for by a measurement.</b> First, the drift is not the only
    /// regressor: the channel estimate's own noise at a pilot is a STATIC angle offset, the same
    /// in every symbol, and through the regression a static tilt projects onto the drift term.
    /// Fitted alone, the drift soaked it up and over-rotated the late symbols - measured on a
    /// paired ladder at the practical rates' cliffs, 32 seeds, doing nothing recovered 113 frames
    /// and the drift-only fit 84. So the static tilt is fitted alongside as a nuisance and thrown
    /// away. Second, the fitted drift is shrunk by its own standard error - the same arithmetic
    /// the estimate denoiser uses on its taps - which took the same ladder to 94: better, still
    /// worse than doing nothing, because a short burst gives the variance estimate almost no
    /// degrees of freedom and it is sometimes badly wrong. Which is why, third, the caller lets
    /// the payload CRC arbitrate: the corrected read must PROVE itself, and a burst the plain
    /// read would have copied is never lost to a correction. Returns the corrected symbols, or
    /// null when no drift is believed; never touches the originals, which are the fallback.</para>
    /// <para><b>The header is the fit's pilot field.</b> The moment its CRC passes, re-encoding
    /// its decoded bits makes every header carrier a known reference - a symbol as dense as the
    /// preamble, one and more periods after it - which multiplies the fit's measurements by an
    /// order of magnitude for no air time, from a part of the burst that has already proved
    /// itself. It also pins down the static-tilt nuisance almost by itself, which is what starves
    /// the drift term of its collinearity. The header symbols feed the fit and are not corrected
    /// by it: they are already decoded, and nothing downstream reads them again.</para>
    /// </remarks>
    /// <para><b>Positions are absolute bins</b>, not indices into a layout, because the header is
    /// on the acquisition layout and the payload may be on another with a different first carrier.
    /// A tilt is so many radians per bin per symbol whatever layout a carrier belongs to, and the
    /// regression demeans each group's positions anyway, so absolute bins cost nothing and make
    /// the two sets of measurements commensurable.</para>
    /// <para><b>The static tilt is applied on a follow-on burst</b>, and only there
    /// (<paramref name="correctStatic"/>). On a full burst it is the channel estimate's own noise,
    /// measured above. On a follow-on burst found by the search rather than read where the
    /// previous one ended, it is the search's error: the header's CRC passes a sample or three
    /// off the grid the keyup's payload estimate was made on, and one sample is 60 degrees at the
    /// top carrier of the 8 kHz span, which QAM-64 does not survive and which no fit that
    /// discards the static term can put back. Measured offline on the 2026-09-19 capture: the
    /// follow-on bursts read 6 dB under the first burst of their keyup with the term discarded.
    /// Shrunk by its own variance exactly as the drift is, so a fit that believes neither still
    /// returns null and the plain read stands.</para>
    private (double Re, double Im)[][]? SampleClockCorrected(
        Layout layout,
        (double Re, double Im)[][] symbols,
        (double Re, double Im)[] channel,
        (double Re, double Im)[][] headerSymbols,
        byte[] headerCoded,
        (double Re, double Im)[] acquisitionChannel,
        int firstPayloadSymbol,
        bool correctStatic,
        out double[]? ramp)
    {
        ramp = null;
        // The centre the correction is applied about: the pilots' weighted mean position, so the
        // applied tilt is zero-mean over the same pilots that set each symbol's common rotation.
        // A pilot in a notch weighs nearly nothing, which is exactly what its angle is worth.
        bool[] pilotMap = layout.PilotMap;
        int pilots = 0;
        double weightSum = 0;
        double centre = 0;
        for (int c = 0; c < pilotMap.Length; c++)
        {
            if (!pilotMap[c])
            {
                continue;
            }

            double w = (channel[c].Re * channel[c].Re) + (channel[c].Im * channel[c].Im);
            pilots++;
            weightSum += w;
            centre += w * (layout.FirstCarrier + c);
        }

        // One pilot has no tilt to read and no common rotation was corrected either; both are
        // supported profiles and both simply keep the per-symbol behaviour they already had.
        if (pilots < 2 || weightSum <= 0)
        {
            return null;
        }

        centre /= weightSum;

        // Two regressors per measurement: a static tilt across the band, and a tilt that grows
        // with the symbol index. Only the second is a clock difference; the first is mostly the
        // channel estimate's own noise and exists to be discarded. Measurements arrive in GROUPS,
        // one per symbol, each with its own absorbed mean - so both the angle and the position
        // are demeaned within the group, against the group's own weights, before accumulating.
        double uu = 0;
        double uv = 0;
        double vv = 0;
        double uy = 0;
        double vy = 0;
        double yy = 0;
        int observations = 0;
        int groups = 0;
        int widest = Math.Max(layout.TotalCarriers, _acquisition.TotalCarriers);
        var position = new double[widest];
        var weight = new double[widest];
        var theta = new double[widest];

        void Accumulate(int elapsed, int count)
        {
            if (count < 2)
            {
                return;
            }

            double wSum = 0;
            double xBar = 0;
            double yBar = 0;
            for (int i = 0; i < count; i++)
            {
                wSum += weight[i];
                xBar += weight[i] * position[i];
                yBar += weight[i] * theta[i];
            }

            if (wSum <= 0)
            {
                return;
            }

            xBar /= wSum;
            yBar /= wSum;
            for (int i = 0; i < count; i++)
            {
                double u = position[i] - xBar;
                double v = elapsed * u;
                double y = theta[i] - yBar;
                uu += weight[i] * u * u;
                uv += weight[i] * u * v;
                vv += weight[i] * v * v;
                uy += weight[i] * u * y;
                vy += weight[i] * v * y;
                yy += weight[i] * y * y;
            }

            observations += count;
            groups++;
        }

        // The header symbols: every carrier is a known value now that the CRC has passed - the
        // pilots carry the preamble's pattern and the data carriers carry the re-encoded header
        // bits - so each header symbol is a reference symbol as dense as the preamble itself,
        // one and more symbol periods after it. A carrier whose sign disagrees with what was
        // sent is skipped rather than read: its angle is near half a turn of pure noise, which
        // is an outlier to a least-squares fit, not a tilt measurement.
        bool[] acquisitionPilots = _acquisition.PilotMap;
        for (int h = 0; h < headerSymbols.Length; h++)
        {
            int count = 0;
            int data = 0;
            for (int c = 0; c < acquisitionPilots.Length; c++)
            {
                double sign;
                if (acquisitionPilots[c])
                {
                    sign = _acquisition.Reference[c].Re >= 0 ? 1 : -1;
                }
                else
                {
                    sign = headerCoded[HeaderBitAt(h, data++)] == 1 ? 1 : -1;
                }

                double re = headerSymbols[h][c].Re * sign;
                double im = headerSymbols[h][c].Im * sign;
                if (re <= 0)
                {
                    continue;
                }

                position[count] = _acquisition.FirstCarrier + c;
                weight[count] = (acquisitionChannel[c].Re * acquisitionChannel[c].Re)
                    + (acquisitionChannel[c].Im * acquisitionChannel[c].Im);
                theta[count] = Math.Atan2(im, re);
                count++;
            }

            Accumulate(1 + h, count);
        }

        // The payload symbols: only the pilots are known. Elapsed symbols are counted from the
        // symbol the payload's channel estimate was taken at, because that is the epoch its
        // angles are relative to: the preamble when the payload is on the acquisition layout,
        // the estimate symbol one past the header when it is on its own, the previous frame's
        // last symbols on a follow-on burst. The caller says which. The header symbols are
        // relative to the preamble either way. One drift, two epochs, and each group is counted
        // from its own.
        for (int s = 0; s < symbols.Length; s++)
        {
            int count = 0;
            for (int c = 0; c < pilotMap.Length; c++)
            {
                if (!pilotMap[c])
                {
                    continue;
                }

                double sign = layout.Reference[c].Re >= 0 ? 1 : -1;
                position[count] = layout.FirstCarrier + c;
                weight[count] = (channel[c].Re * channel[c].Re) + (channel[c].Im * channel[c].Im);
                theta[count] = Math.Atan2(symbols[s][c].Im * sign, symbols[s][c].Re * sign);
                count++;
            }

            Accumulate(s + 1 + firstPayloadSymbol, count);
        }

        double det = (uu * vv) - (uv * uv);
        if (det <= 1e-9 * uu * vv)
        {
            return null;
        }

        double staticTilt = ((vv * uy) - (uv * vy)) / det;
        double drift = ((uu * vy) - (uv * uy)) / det;

        // How much of the fitted drift to believe: its power less its own variance, over the sum,
        // exactly as the estimate denoiser treats a tap. The variance comes from the fit's own
        // residuals, so no noise figure has to be supplied and no threshold has to be chosen. The
        // degrees of freedom pay for the per-group demeaning as well as the two regressors.
        int degreesOfFreedom = observations - groups - 2;
        if (degreesOfFreedom < 1)
        {
            return null;
        }

        double residual = Math.Max(yy - (staticTilt * uy) - (drift * vy), 0);
        double sigma = residual / degreesOfFreedom;
        double driftVariance = sigma * uu / det;
        double power = drift * drift;
        double believed = Math.Max(power - driftVariance, 0);
        double slope = believed > 0 ? drift * (believed / (believed + driftVariance)) : 0;

        double offset = 0;
        if (correctStatic)
        {
            double staticVariance = sigma * vv / det;
            double staticPower = staticTilt * staticTilt;
            double staticBelieved = Math.Max(staticPower - staticVariance, 0);
            offset = staticBelieved > 0
                ? staticTilt * (staticBelieved / (staticBelieved + staticVariance))
                : 0;
        }

        if (slope == 0 && offset == 0)
        {
            return null;
        }

        // The rotation applied to each symbol, radians per bin about the centre, for the caller
        // that needs to know where each symbol was read: the estimate the next burst is read with.
        ramp = new double[symbols.Length];
        var corrected = new (double Re, double Im)[symbols.Length][];
        for (int s = 0; s < symbols.Length; s++)
        {
            double perCarrier = offset + (slope * (s + 1 + firstPayloadSymbol));
            ramp[s] = perCarrier;
            corrected[s] = new (double Re, double Im)[symbols[s].Length];
            for (int c = 0; c < symbols[s].Length; c++)
            {
                double angle = -perCarrier * (layout.FirstCarrier + c - centre);
                double cos = Math.Cos(angle);
                double sin = Math.Sin(angle);
                (double re, double im) = symbols[s][c];
                corrected[s][c] = ((re * cos) - (im * sin), (re * sin) + (im * cos));
            }
        }

        return corrected;
    }

    /// <summary>Pilots a layout needs before a single symbol's tilt is worth reading from them.
    /// Two pilots give one difference and that is mostly pilot noise, which is why the burst-wide
    /// fit exists; the wide layouts carry eight to twelve, spread across the band, and from those
    /// a symbol's own tilt reads to about a degree at the band edge.</summary>
    private const int PilotsForPerSymbolTilt = 6;

    /// <summary>Symbols either side of a symbol whose pilot slopes are averaged with its own.
    /// The clocks wander on a scale of a few symbols, so a window of five follows the wander and
    /// still divides the per-symbol estimate's noise by five.</summary>
    private const int PerSymbolTiltHalfWindow = 2;

    /// <summary>
    /// Refines the burst-wide tilt correction symbol by symbol, from each payload symbol's own
    /// pilots, for the part of the clock difference that is not constant across the burst.
    /// </summary>
    /// <remarks>
    /// <para>The burst-wide fit in <see cref="SampleClockCorrected"/> models one drift for the
    /// whole burst, and on air the two soundcards' clocks wander on top of it: measured 1 to 2 ppm
    /// between runs a minute apart, and tens of ppm on a 50 ms scale in the tone captures. On a
    /// nine-symbol burst that is nothing. On a thirty-symbol burst it is what stopped half the
    /// 4000-byte frames decoding on 2026-09-19 while the 1024-byte ones on the same span all did:
    /// the bursts that survived read 3 to 5 dB below the short ones, and turning the burst-wide
    /// fit off made them worse, so the residual was a tilt the one-line model could not follow.
    /// </para>
    /// <para>The model here is the same regression the burst-wide fit uses, run over one
    /// symbol's pilots at a time: the pilots' angle against their absolute bin, weighted by
    /// channel power, gives that symbol's residual slope, and the slope is smoothed over a short
    /// window of neighbouring symbols and shrunk by its own variance exactly as the burst-wide
    /// drift is. Everything the burst-wide fit protected against still holds: the payload CRC
    /// arbitrates, and a burst the coarser readings would have copied is never lost to this one.
    /// Null when the layout has too few pilots to read a symbol's slope, or when no symbol's
    /// slope is believed, in which case the caller has nothing new to try.</para>
    /// </remarks>
    private static (double Re, double Im)[][]? PerSymbolTiltRefined(
        Layout layout,
        (double Re, double Im)[][] symbols,
        (double Re, double Im)[] channel,
        out double[]? ramp)
    {
        ramp = null;
        bool[] pilotMap = layout.PilotMap;
        int pilots = 0;
        double weightSum = 0;
        double centre = 0;
        for (int c = 0; c < pilotMap.Length; c++)
        {
            if (!pilotMap[c])
            {
                continue;
            }

            double w = (channel[c].Re * channel[c].Re) + (channel[c].Im * channel[c].Im);
            pilots++;
            weightSum += w;
            centre += w * (layout.FirstCarrier + c);
        }

        if (pilots < PilotsForPerSymbolTilt || weightSum <= 0 || symbols.Length == 0)
        {
            return null;
        }

        centre /= weightSum;
        double sxx = 0;
        for (int c = 0; c < pilotMap.Length; c++)
        {
            if (pilotMap[c])
            {
                double w = (channel[c].Re * channel[c].Re) + (channel[c].Im * channel[c].Im);
                double u = layout.FirstCarrier + c - centre;
                sxx += w * u * u;
            }
        }

        if (sxx <= 0)
        {
            return null;
        }

        // Each symbol's own slope and the variance of that slope, from its pilots alone.
        var slope = new double[symbols.Length];
        var variance = new double[symbols.Length];
        for (int s = 0; s < symbols.Length; s++)
        {
            double thetaBar = 0;
            for (int c = 0; c < pilotMap.Length; c++)
            {
                if (!pilotMap[c])
                {
                    continue;
                }

                double w = (channel[c].Re * channel[c].Re) + (channel[c].Im * channel[c].Im);
                double sign = layout.Reference[c].Re >= 0 ? 1 : -1;
                thetaBar += w * Math.Atan2(symbols[s][c].Im * sign, symbols[s][c].Re * sign);
            }

            thetaBar /= weightSum;
            double sxy = 0;
            for (int c = 0; c < pilotMap.Length; c++)
            {
                if (!pilotMap[c])
                {
                    continue;
                }

                double w = (channel[c].Re * channel[c].Re) + (channel[c].Im * channel[c].Im);
                double sign = layout.Reference[c].Re >= 0 ? 1 : -1;
                double theta = Math.Atan2(symbols[s][c].Im * sign, symbols[s][c].Re * sign);
                sxy += w * (layout.FirstCarrier + c - centre) * (theta - thetaBar);
            }

            slope[s] = sxy / sxx;
            double residual = 0;
            for (int c = 0; c < pilotMap.Length; c++)
            {
                if (!pilotMap[c])
                {
                    continue;
                }

                double w = (channel[c].Re * channel[c].Re) + (channel[c].Im * channel[c].Im);
                double sign = layout.Reference[c].Re >= 0 ? 1 : -1;
                double theta = Math.Atan2(symbols[s][c].Im * sign, symbols[s][c].Re * sign);
                double e = theta - thetaBar - (slope[s] * (layout.FirstCarrier + c - centre));
                residual += w * e * e;
            }

            // The per-observation variance, with the weights normalised to a mean of one so the
            // figure is on the angles' own scale, over the degrees of freedom two regressors
            // leave; then the slope's variance is that over the positions' spread.
            double perObservation = residual / (weightSum / pilots) / Math.Max(1, pilots - 2);
            variance[s] = perObservation / sxx;
        }

        // Smoothed over the window and shrunk by what is left of its own variance after the
        // averaging, the same arithmetic the burst-wide drift and the estimate denoiser use: no
        // threshold to choose, and a slope that is mostly noise goes to nothing.
        var applied = new double[symbols.Length];
        bool any = false;
        for (int s = 0; s < symbols.Length; s++)
        {
            int from = Math.Max(0, s - PerSymbolTiltHalfWindow);
            int to = Math.Min(symbols.Length - 1, s + PerSymbolTiltHalfWindow);
            double sum = 0;
            double varianceSum = 0;
            int count = 0;
            for (int k = from; k <= to; k++)
            {
                sum += slope[k];
                varianceSum += variance[k];
                count++;
            }

            double mean = sum / count;
            double meanVariance = varianceSum / (count * (double)count);
            double power = mean * mean;
            double believed = Math.Max(power - meanVariance, 0);
            if (believed <= 0)
            {
                continue;
            }

            applied[s] = mean * (believed / (believed + meanVariance));
            any = true;
        }

        if (!any)
        {
            return null;
        }

        ramp = applied;
        var refined = new (double Re, double Im)[symbols.Length][];
        for (int s = 0; s < symbols.Length; s++)
        {
            refined[s] = new (double Re, double Im)[symbols[s].Length];
            for (int c = 0; c < symbols[s].Length; c++)
            {
                double angle = -applied[s] * (layout.FirstCarrier + c - centre);
                double cos = Math.Cos(angle);
                double sin = Math.Sin(angle);
                (double re, double im) = symbols[s][c];
                refined[s][c] = ((re * cos) - (im * sin), (re * sin) + (im * cos));
            }
        }

        return refined;
    }

    // Timing by self-correlation: the sync symbol's useful part is two identical halves, so the
    // signal correlated against itself half a symbol later peaks exactly where the symbol starts.
    // Both halves pass through the same channel, so a tilt or an echo scales them together and the
    // coefficient stays high - which is the whole reason this beats matching a clean reference.
    private int FindSync(ReadOnlySpan<float> audio)
    {
        // The search looks at band-limited audio; everything else looks at the audio as it arrived.
        // See SearchBandLimit for why that split is not optional. Limited to the acquisition
        // layout's span, because that is where the sync symbol is.
        float[] limited = SearchBandLimit.Once(_parameters, _acquisition.Geometry, audio);
        int found = FindSyncIn(limited.AsSpan(), out int delay);
        return found < 0 ? -1 : Math.Max(0, found - delay);
    }

    private int FindSyncIn(ReadOnlySpan<float> audio, out int delay)
    {
        delay = _searchDelay;
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
        return bestScore >= SyncThreshold ? best : -1;
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

    // The header, spread over as many BPSK symbols as the acquisition layout's data carriers need.
    private List<(double Re, double Im)[]> HeaderSymbols(
        int geometry,
        OfdmFmConstellation constellation,
        int payloadLength,
        OfdmFmRate? recommendation,
        OfdmFmCoding coding,
        int recommendedGeometry,
        bool askFullBursts)
    {
        var writer = new BitWriter();
        writer.Write(geometry, 4);
        writer.Write((int)constellation, 4);
        writer.Write(CodingId(coding), 4);
        writer.Write(payloadLength, 12);

        // A recommendation this station cannot name on the wire is sent as no recommendation at
        // all. Refusing the burst would be worse: the recommendation is advice, and losing the
        // advice must never cost the payload it was riding on. The geometry rides with the rate,
        // and is zero - which is a perfectly good geometry id but not one anybody reads without a
        // rate beside it - when there is no rate to ride with.
        int recGeometry = 0;
        int recConstellation = NoRecommendation;
        int recCoding = 0;
        if (recommendation is OfdmFmRate wanted && TryCodingId(wanted.Coding, out int id))
        {
            recGeometry = recommendedGeometry & 0x0F;
            recConstellation = (int)wanted.Constellation;
            recCoding = id;

            // "Send me full bursts" rides in the same nibble as the constellation plus eight,
            // values 9 to 15, which no constellation uses. A reader from before the flag existed
            // sees a value it does not know and drops the recommendation, which is the safe
            // failure: advice lost, nothing else. QAM-256 is the one constellation the flag
            // cannot accompany, because eight plus eight does not fit the nibble; it is not a rate
            // any FM link has reached, and the ask then goes without the flag rather than not at
            // all.
            if (askFullBursts && recConstellation <= 7)
            {
                recConstellation += 8;
            }
        }

        writer.Write(recGeometry, 4);
        writer.Write(recConstellation, 4);
        writer.Write(recCoding, 4);

        // 36 bits so far: the CRC covers them as five bytes, the last nibble of which is zero
        // because nothing has been written there yet. The receiver zeroes the same nibble before
        // checking.
        byte[] head = writer.ToArray();
        ushort crc = Crc16X25.Compute(head.AsSpan(0, 5));
        writer.Write(crc, 16);
        byte[] bytes = writer.ToArray();

        // Coded, then laid out so that every coded bit appears in every header symbol.
        byte[] coded = _headerCodec.Encode(Unpack(bytes).AsSpan(0, HeaderBits));
        var symbols = new List<(double Re, double Im)[]>(_headerSymbols);
        for (int s = 0; s < _headerSymbols; s++)
        {
            var carriers = new (double Re, double Im)[_acquisition.TotalCarriers];
            int data = 0;
            for (int c = 0; c < carriers.Length; c++)
            {
                if (_acquisition.PilotMap[c])
                {
                    carriers[c] = (_acquisition.Reference[c].Re, 0);
                    continue;
                }

                carriers[c] = (coded[HeaderBitAt(s, data++)] == 1 ? 1 : -1, 0);
            }

            symbols.Add(carriers);
        }

        return symbols;
    }

    /// <summary>
    /// Which coded header bit a given data carrier of a given header symbol carries.
    /// </summary>
    /// <remarks>
    /// <para>The header symbols are treated as one run of slots and the coded bits are walked
    /// across it by a stride coprime to their number. That guarantees coverage - every coded bit is
    /// reached before any is reached twice - which a per-symbol rule does not: a narrow profile
    /// needs more header symbols than a wide one, and a mapping that assumed exactly two left most
    /// of the code untransmitted on anything small enough to need three.</para>
    /// <para>The scatter matters twice over. Across the band, because a run of consecutive bits
    /// would sit on the lowest carriers, which is the weakest part of an FM voice path. Across
    /// symbols, because copies of one bit land a whole codeword apart in slot order, which on the
    /// profiles with carriers to spare puts them in different symbols and so in different fades.
    /// </para>
    /// </remarks>
    private int HeaderBitAt(int symbol, int dataCarrier)
    {
        long slot = ((long)symbol * _acquisition.DataCarriers) + dataCarrier;
        return (int)(slot * _headerStride % _headerCodedBits);
    }

    /// <param name="layout">The carrier layout this symbol occupies.</param>
    /// <param name="carriers">The intended point for every occupied carrier, pilots included: what
    /// <see cref="BoundCarrierErrors"/> measures a clipped carrier's error against.</param>
    /// <param name="scale">The layout's drive, applied after clipping and the error bound.</param>
    /// <param name="peakLimitDb">The crest-factor target <see cref="ReducePeaks"/> clips to.</param>
    /// <param name="errorCap">Per carrier, the most a clipped point may end up drifting from
    /// <paramref name="carriers"/>'s own value before <see cref="BoundCarrierErrors"/> pulls it
    /// back; null for a symbol nothing decodes carrier by carrier (the sync symbol, a preamble or
    /// estimate reference, and the unclipped drive-calibration render), where the crest-factor
    /// pass above is the only thing applied.</param>
    private double[] RenderSymbol(
        Layout layout, (double Re, double Im)[] carriers, double scale, double peakLimitDb,
        double[]? errorCap = null)
    {
        int fft = _parameters.FftSize;
        var binRe = new double[fft];
        var binIm = new double[fft];
        for (int c = 0; c < carriers.Length; c++)
        {
            int bin = layout.FirstCarrier + c;
            binRe[bin] = carriers[c].Re;
            binIm[bin] = carriers[c].Im;
        }

        double[] useful = RealFft.ToTimeDomain(binRe, binIm, fft);
        ReducePeaks(layout, useful, peakLimitDb);
        if (errorCap is not null && CarrierErrorBound)
        {
            BoundCarrierErrors(layout, carriers, useful, errorCap);
        }

        for (int n = 0; n < fft; n++)
        {
            useful[n] *= scale;
        }

        return useful;
    }

    /// <summary>
    /// What a carrier's error may be pulled to, as a fraction of half the alphabet's own
    /// nearest-neighbour spacing: a carrier at the cap still sits well inside its own decision
    /// region, never at the boundary itself.
    /// </summary>
    /// <remarks>
    /// Not a round number picked for looking reasonable - measured. A third was the first guess
    /// and it fixed every payload in the sweep, but it also fired on carriers nowhere near their
    /// boundary: enough of them that the crest-factor distribution's 95th percentile moved by
    /// 0.8 to 1.0 dB at BPSK and QPSK, spending drive protecting symbols that were never at risk.
    /// 0.6 is the loosest fraction that still cleared 6000 payloads (64, 90 and 128 bytes, seeds 0
    /// to 1999) with zero CRC failures; above it, at 0.9, the bound stopped firing on the carriers
    /// that actually needed it and failures came back. At 0.6 the crest-factor distribution's
    /// median and 95th percentile move by no more than 0.05 dB at any of BPSK, QPSK or QAM-64 -
    /// see the sweep in the PR that added this and
    /// <c>OfdmFmBurstCodecTests.Every_Seeded_Uncoded_Payload_Survives_The_Clean_Whole_Buffer_Path</c>.
    /// </remarks>
    private const double DecisionBoundaryFraction = 0.6;

    private static double ErrorCap(double minimumDistance) =>
        minimumDistance * DecisionBoundaryFraction / 2.0;

    /// <summary>The cap for a symbol whose occupied carriers, pilots included, are all drawn from
    /// one alphabet - the sync, preamble and header symbols.</summary>
    private static double[] UniformErrorCap(int totalCarriers, OfdmFmConstellation constellation)
    {
        var cap = new double[totalCarriers];
        Array.Fill(cap, ErrorCap(OfdmFmMapper.MinimumDistance(constellation)));
        return cap;
    }

    /// <summary>The cap per carrier of a payload symbol: a pilot's own BPSK spacing at a pilot
    /// position, and whatever constellation bit loading gave that data carrier everywhere else -
    /// a bit-loaded layout mixes constellations across the band, so a dense carrier's margin is
    /// tighter than a sparse one's either side of it, and each needs its own figure.</summary>
    private static double[] PayloadErrorCap(Layout layout, int[] carrierBits)
    {
        double pilotCap = ErrorCap(OfdmFmMapper.MinimumDistance(OfdmFmConstellation.Bpsk));
        var byBits = new Dictionary<int, double>();
        var cap = new double[layout.TotalCarriers];
        int data = 0;
        for (int c = 0; c < cap.Length; c++)
        {
            if (layout.PilotMap[c])
            {
                cap[c] = pilotCap;
                continue;
            }

            int bits = carrierBits[data++];
            if (!byBits.TryGetValue(bits, out double dataCap))
            {
                dataCap = ErrorCap(OfdmFmMapper.MinimumDistance((OfdmFmConstellation)bits));
                byBits[bits] = dataCap;
            }

            cap[c] = dataCap;
        }

        return cap;
    }

    /// <summary>Peak reduction disabled. Used only for the unscaled reference render, which exists
    /// to measure the waveform rather than to be transmitted.</summary>
    private const double NoPeakLimit = double.PositiveInfinity;

    /// <summary>The limit actually applied: the profile's override if it set one, otherwise the
    /// measured floor for this constellation.</summary>
    private double PeakLimit(OfdmFmConstellation constellation) =>
        _parameters.PeakToAverageLimitDb ?? PeakLimitFor(constellation);

    /// <summary>
    /// How hard a symbol carrying this constellation may be clipped, in dB of crest factor.
    /// </summary>
    /// <remarks>
    /// <para>Measured, not assumed. For each constellation, the lowest limit at which a noiseless
    /// loopback still round trips, UNCODED, because uncoded is the case with no code to absorb the
    /// distortion and therefore the one that sets the floor: BPSK and QPSK 4.0 dB, 8PSK 6.0, QAM-16
    /// 7.0, QAM-32 7.5, QAM-64 and denser 9.0. Coded, every one of them tolerates 1 to 4 dB more.
    /// </para>
    /// <para>A decibel of margin is added to each, and the table is kept monotonic in density even
    /// where the measurement was not, since QAM-256 measuring 0.5 dB below QAM-128 is noise rather
    /// than a constellation that tolerates more.</para>
    /// <para>The floor alone used to be a promise this method did not keep: for a few payloads in a
    /// hundred at BPSK/QPSK, the clip-and-restore cycle's own fixed point put one carrier's error
    /// past its decision boundary, and running more passes made that carrier WORSE rather than
    /// better - proof it was not a matter of insufficient iteration. <see cref="ReducePeaks"/> now
    /// caps each carrier's own error against its own alphabet after the last pass, which is what
    /// actually keeps that promise; see its remarks for the bound and
    /// <c>OfdmFmBurstCodecTests.Every_Seeded_Uncoded_Payload_Survives_The_Clean_Whole_Buffer_Path</c>
    /// for the sweep that checks it.</para>
    /// </remarks>
    private static double PeakLimitFor(OfdmFmConstellation constellation) =>
        constellation.BitsPerCarrier() switch
        {
            <= 2 => 5.0,
            3 => 7.0,
            4 => 8.0,
            5 => 8.5,
            _ => 10.0,
        };

    /// <summary>
    /// Pulls the symbol's peaks down without letting it grow outside its own band.
    /// </summary>
    /// <remarks>
    /// <para><b>Why this is worth doing at all.</b> This waveform is injected past the radio's
    /// limiter, so the operator sets the drive once against the burst's loudest instant, and
    /// everything else rides however far below that its peak-to-average ratio dictates. An FM
    /// discriminator's output noise is fixed by the IF while the recovered signal is proportional
    /// to deviation, so post-detection signal to noise goes as deviation SQUARED. Every decibel
    /// taken off the crest factor is therefore a decibel of link, for no bandwidth and no air
    /// time.</para>
    /// <para>Measured on this waveform: raising peak deviation from 1500 Hz to 2500 Hz, which is
    /// nothing but driving harder into the same channel, moved the working point by 3 to 4 dB. The
    /// crest factor was 11.9 dB, so most of the deviation budget was being spent on peaks that
    /// carry almost no energy.</para>
    /// <para><b>Clip, then put the spectrum back.</b> Clipping alone would spray energy outside the
    /// occupied carriers and into the adjacent channel, which is not ours to use. So each pass
    /// clips in the time domain, transforms, zeros every bin the waveform does not own, and
    /// transforms back - which restores the spectrum and undoes part of the clipping, hence the
    /// iteration. The cost is in-band distortion on our own carriers, which is why the threshold is
    /// a parameter and not a constant: a dense constellation has less room for it than QPSK.</para>
    /// </remarks>
    private void ReducePeaks(Layout layout, double[] useful, double limitDb)
    {
        if (double.IsInfinity(limitDb))
        {
            return;
        }

        int fft = _parameters.FftSize;
        int first = layout.FirstCarrier;
        int last = first + layout.TotalCarriers - 1;
        double target = Math.Pow(10, limitDb / 20.0);

        for (int pass = 0; pass < PeakReductionPasses; pass++)
        {
            double sum = 0;
            for (int n = 0; n < fft; n++)
            {
                sum += useful[n] * useful[n];
            }

            double rms = Math.Sqrt(sum / fft);
            if (rms <= 1e-12)
            {
                return;
            }

            double ceiling = rms * target;
            bool clipped = false;
            for (int n = 0; n < fft; n++)
            {
                if (Math.Abs(useful[n]) > ceiling)
                {
                    useful[n] = Math.Sign(useful[n]) * ceiling;
                    clipped = true;
                }
            }

            if (!clipped)
            {
                return;
            }

            // Put the spectrum back inside the carriers this waveform owns. Everything the clipping
            // threw outside them is somebody else's channel.
            (double[] re, double[] im) = RealFft.ToBins(useful, fft);
            for (int bin = 0; bin < re.Length; bin++)
            {
                if (bin < first || bin > last)
                {
                    re[bin] = 0;
                    im[bin] = 0;
                }
            }

            double[] rebuilt = RealFft.ToTimeDomain(re, im, fft);
            Array.Copy(rebuilt, useful, fft);
        }
    }

    /// <summary>
    /// The guard <see cref="ReducePeaks"/> does not provide on its own: after the last clip pass,
    /// pulls any carrier whose point drifted more than its own <paramref name="errorCap"/> from
    /// what was actually meant back to exactly that distance, leaving every carrier that never
    /// left its margin untouched.
    /// </summary>
    /// <remarks>
    /// <para>The crest-factor target in <see cref="ReducePeaks"/> is a property of the WHOLE
    /// symbol - its own peak over its own average - and nothing in it promises anything about any
    /// ONE carrier. Most of the time that does not matter: the distortion a clip-and-restore-to-
    /// band cycle leaves behind spreads over every occupied carrier by roughly the same amount,
    /// comfortably inside a BPSK or QPSK carrier's own margin. For some payloads it is not: the
    /// iteration's own fixed point, for that particular data, leaves one carrier disproportionately
    /// worse than the rest - measured, occasionally past its own decision boundary, and not fixable
    /// by iterating longer, because more passes moved that carrier further from its intended point
    /// rather than back toward it. The fixed point itself is wrong for that carrier, not merely
    /// unreached.</para>
    /// <para>So the fix is not a looser crest-factor target, which would spend a decibel of drive
    /// on every symbol to protect the rare one: it is a promise made at the level a receiver
    /// actually decides at, one carrier at a time, applied only where the crest-factor pass left
    /// a carrier needing it. A symbol with nothing over its cap - most of them - comes out exactly
    /// as the crest-factor pass produced it. A symbol with a carrier over its cap has that carrier
    /// pulled back along its own error vector to sit exactly on the cap, which may regrow the
    /// symbol's peak a little above the crest-factor target: a decodable symbol slightly over
    /// budget beats an undecodable one exactly on it.</para>
    /// </remarks>
    private void BoundCarrierErrors(
        Layout layout, (double Re, double Im)[] carriers, double[] useful, double[] errorCap)
    {
        int fft = _parameters.FftSize;
        int first = layout.FirstCarrier;
        int last = first + layout.TotalCarriers - 1;
        (double[] re, double[] im) = RealFft.ToBins(useful, fft);
        bool adjusted = false;
        for (int c = 0; c < carriers.Length; c++)
        {
            int bin = first + c;
            double errorRe = re[bin] - carriers[c].Re;
            double errorIm = im[bin] - carriers[c].Im;
            double cap = errorCap[c];
            double errorMagnitude = Math.Sqrt((errorRe * errorRe) + (errorIm * errorIm));
            if (errorMagnitude <= cap)
            {
                continue;
            }

            double pull = cap / errorMagnitude;
            re[bin] = carriers[c].Re + (errorRe * pull);
            im[bin] = carriers[c].Im + (errorIm * pull);
            adjusted = true;
        }

        if (!adjusted)
        {
            return;
        }

        // Every other bin is already the clipping's own out-of-band silence, but the pull above
        // only touched the occupied ones - nothing to do here except rebuild from what is now, at
        // most, a handful of corrected carriers among the untouched rest.
        for (int bin = 0; bin < re.Length; bin++)
        {
            if (bin < first || bin > last)
            {
                re[bin] = 0;
                im[bin] = 0;
            }
        }

        double[] rebuilt = RealFft.ToTimeDomain(re, im, fft);
        Array.Copy(rebuilt, useful, fft);
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

    /// <summary>
    /// A deterministic pseudo-random filler bit for the tail of the last symbol.
    /// </summary>
    /// <remarks>
    /// Any bit sequence without long runs would do, since nothing decodes these. This is a cheap
    /// integer hash of the position rather than a scrambler so that it cannot accidentally line up
    /// in phase with the payload scrambler and reintroduce the very structure it exists to break.
    /// </remarks>
    private static int PadBit(int position)
    {
        uint x = (uint)position * 2654435761u;
        x ^= x >> 15;
        x *= 2246822519u;
        x ^= x >> 13;
        return (int)(x & 1);
    }

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
