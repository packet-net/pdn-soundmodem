using System.Text.Json;

namespace Packet.SoundModem.Modems.OfdmFm;

/// <summary>
/// The geometry of one OFDM-FM bandwidth profile: how many subcarriers, where they sit, and on
/// what transform.
/// </summary>
/// <param name="SampleRate">Audio rate the transform runs at.</param>
/// <param name="FftSize">Points in the real-valued transform. The spectrum a real signal occupies
/// is <c>FftSize/2 + 1</c> bins from DC to Nyquist, spaced <see cref="SubcarrierSpacingHz"/>
/// apart.</param>
/// <param name="CyclicPrefix">Samples of guard prepended to each symbol.</param>
/// <param name="FirstCarrier">Lowest occupied bin. Sets where in the audio passband the signal
/// starts, which is what makes a profile fit a given radio's audio path.</param>
/// <param name="DataCarriers">Occupied bins carrying payload.</param>
/// <param name="PilotCarriers">Occupied bins carrying a known reference, spread evenly through
/// the block. They cost throughput and buy per-symbol phase tracking.</param>
/// <param name="Coding">Forward error correction for the payload; none if absent.</param>
/// <param name="PeakToAverageLimitDb">Override for the crest factor each symbol is pulled down to,
/// in dB. <b>Null does NOT mean off</b>: it means the limit follows the constellation, which is the
/// measured floor for that constellation plus a decibel of margin, and is what you want. <para>Worth having because this waveform is injected past the radio's
/// limiter: the drive is set once against the burst's loudest instant, and post-detection signal to
/// noise goes as deviation squared, so a decibel off the crest factor is a decibel of link for no
/// bandwidth and no air time. Untreated this waveform measures 11.9 dB.</para>
/// <para>The cost is distortion on our own carriers, and how much of it a burst can absorb depends
/// on its constellation, which is why the automatic limit is a function of that rather than one
/// number. Measured floors, uncoded, being the case with no code to absorb the distortion: BPSK and
/// QPSK 4.0 dB, 8PSK 6.0, QAM-16 7.0, QAM-32 7.5, QAM-64 and denser 9.0. The table adds a decibel
/// to each. See <see cref="OfdmFmBurstCodec"/>'s <c>ReducePeaks</c> for the per-carrier decision
/// bound that keeps a burst inside this floor decodable, which the crest-factor target on its own
/// did not guarantee.</para>
/// <para>Worth about 2.5 dB on the deployed configuration. The sync and preamble symbols are
/// clipped too, at the same limit as the payload - leaving them out was measured to cost 2 dB,
/// because an unclipped sync symbol becomes the burst's loudest instant and sets the drive on its
/// own. Neither reference is harmed by it; see docs/dev/ofdm-fm/receiver-findings.md,
/// "Peak reduction".</para>
/// </param>
/// <param name="HeaderRepeats">How many times over to send the header, in consecutive symbols.
/// <para><b>One, and the measurement says leave it there.</b> The header is coded either way and
/// the coded bits fit inside a single symbol on every real profile, so the coding costs no air
/// time at all. A second copy costs a whole symbol, about 8 % of a short burst.</para>
/// <para>The reasoning for a second copy is sound and the result still says no. A flat fade takes
/// every carrier down together, so a header inside one symbol has nothing to fall back on, and a
/// copy a symbol later usually misses the same dropout. It does exactly that: over 960 bursts at
/// 64 seeds a cell, header losses fall from 32 to 25. But total frames recovered goes 806 to 808,
/// which is nothing (p = 0.95), because the longer burst loses as many frames in the payload as
/// the second copy saves in the header - 8 % more air time is 8 % more of the burst exposed to the
/// next dropout. It trades a header failure for a payload failure, roughly one for one.</para>
/// <para>The setting is kept rather than deleted because that trade depends on payload length: at
/// 64 bytes the extra symbol is 8 % of the burst, and at 256 bytes it would be nearer 2 %, where
/// the same header saving might survive it. Nobody has measured that. Do not raise it on the
/// strength of the mechanism sounding right, which is exactly the mistake that put it here.</para>
/// </param>
/// <param name="BitLoading">Bits per data carrier as runs across the band, so a channel whose
/// noise rises with frequency can carry more where it is quiet. Absent means uniform.</param>
/// <param name="AdaptiveRate">Let the link choose its own rate.
/// <para><b>Off by default, and not for compatibility - nobody runs this waveform.</b> It is off
/// because two of the three situations it can find itself in are ones it makes worse. A beacon or a
/// one-way link never hears anything back, so it would sit at whatever it started on and never
/// learn; and on a channel with several stations their reports average into one nonsense
/// recommendation. It is right for a point-to-point link and that is what to turn it on for.</para>
/// <para>When on: Each end measures what it hears, works out which rung of
/// <see cref="OfdmFmRateLadder"/> would deliver most, and asks the other end for it; what a station
/// transmits at is then whatever its correspondent asked for, and <paramref name="Constellation"/>
/// and <paramref name="Coding"/> stop being transmit choices.</para>
/// <para><b>One controller per channel, which is right for a point-to-point link and wrong for a
/// shared one.</b> The payload here is an opaque AX.25 frame and this layer does not read
/// addresses, so it cannot tell two correspondents apart; on a channel with several stations on it
/// their reports would be averaged together into one nonsense recommendation. Turn this off there
/// until something carries a correspondent's identity down to this layer.</para>
/// <para>The ladder's absolute figures were measured on one geometry, so a different
/// profile is running on a calibration that was not taken for it. The controller reads the
/// DIFFERENCES between rungs rather than the absolute levels, which travel better, and it starts at
/// the most robust rate and climbs only on evidence - so a wrong calibration costs some throughput
/// rather than the link. Still worth re-measuring with <c>RateLadderGridProbe</c> before relying on
/// it elsewhere.</para></param>
/// <param name="RecommendationLifeSeconds">How long a correspondent's rate recommendation is
/// honoured after the last burst heard from them.
/// <para>A recommendation describes a path, and a path nobody has been heard on for two minutes may
/// not be that path any more. Going back to the most robust rate costs one slow burst; going on
/// transmitting at what a station asked for before it drove into a valley costs the frame, and then
/// costs it again.</para></param>
/// <param name="AdaptiveTopConstellation">The densest constellation an adaptive link on this
/// station will ever ASK its correspondent to send: a bound on what
/// <see cref="OfdmFmRateController"/> recommends, not on what it will accept. See
/// <paramref name="AdaptiveRate"/>.
/// <para><b>Why a station needs to bound its own ask.</b> The receive thread does one soft
/// demapping per carrier per symbol, costlier the denser the constellation, and one Viterbi walk
/// per burst; a burst whose payload will not decode still has to be walked before the CRC can say
/// so. On 2026-09-19 an adaptive link stepped up to QAM-256 rate 2/3 and one station decoded 9 of
/// 10 bursts while the other decoded nothing and logged an audio capture overrun: the rate the far
/// end had asked for cost that receiver more to decode than the burst spent on air. Nobody had
/// measured decode time against air time before that.</para>
/// <para>Measured with <c>tools/ofdm-fm-bench</c>: a 1900-byte burst on a roughly 300-carrier wide
/// layout, on a Raspberry Pi 4 Model B Rev 1.5, air time over mean decode wall time across 40
/// repetitions after a warm-up, and again with the payload zeroed after the header, standing in
/// for a burst that will not decode, since a receiver has to walk the whole thing to find that
/// out. QAM-256 rate 2/3 held 3.2x on a clean burst and 1.3x on a wrecked one, under the two times
/// this bound is set to hold: the failure mode this field exists to head off, reproduced on the
/// bench. Every QAM-64 rung held at least 3.8x clean; the four K=7-coded rungs held 3.5x to 3.6x
/// wrecked and the one K=9-coded rung, 2/3, held 2.0x wrecked, right at the line rather than clear
/// of it. QAM-16 and below held 2.6x or better wrecked, with more margin the sparser the
/// constellation. The bound is on constellation density, not on rung or code rate, so it cannot
/// separate that one K=9 rung from the four K=7 rungs sharing its constellation; the four with
/// comfortable margin decided it. Full table in docs/dev/ofdm-fm/receiver-findings.md.</para>
/// <para>Default <see cref="OfdmFmConstellation.Qam64"/>: the densest constellation that left this
/// station's own bursts at least twice as much air time as decode time on that Pi, including the
/// wrecked-payload case. A station configuring this denser is asking its correspondent for a rate
/// this receive thread was measured not to keep up with. The far end's own recommendation to THIS
/// station's transmitter, <see cref="OfdmFmRateController.Transmit"/>, is never bounded by
/// it - a station cannot measure a correspondent's receive thread, only its own, so it can only
/// bound its own ask.</para></param>
/// <param name="Constellation">Bits per subcarrier this profile transmits at. Receive adapts to
/// whatever a burst's header announces, so this is a transmit choice only, and QPSK is the default
/// because it is the constellation the coded FM ladder was measured at. Where
/// <paramref name="BitLoading"/> is present it decides the per-carrier bit counts and this only
/// rides in the header.</param>
/// <param name="ContiguousBursts">Render no lead-in at all when the host asks for less than one
/// symbol of it, so that frames inside one keyup follow each other with no silence between.
/// <para>The host asks for the full TXDELAY before the first frame of a keyup and about 30 ms
/// before each one after, and 30 ms used to be rounded UP to a whole silent symbol, 44 ms, on the
/// grounds that a partial symbol of silence is not a unit this waveform has. On the 8 kHz preset a
/// 1024-byte burst is twelve symbols, so that silent symbol was 8 % of every frame in a bulk
/// transfer, paid for a settling time the receiver does not need between bursts it has just
/// decoded: it resumes its search at the end of the burst it read, which is exactly where the
/// next sync symbol then starts. The first frame's lead-in is unchanged, because that one is the
/// radio's keying time and not the receiver's.</para>
/// <para>Off by default and on in the measured preset; measured, not assumed, see
/// <c>docs/dev/ofdm-fm/preset-design.md</c>.</para></param>
/// <param name="FollowOnFrames">Send a frame that follows another in the same keyup, on the same
/// geometry, as a follow-on burst: its header and payload only, with no sync symbol, preamble,
/// estimate symbol or lead-in, because the receiver still holds the timing and the channel from
/// the frame before.
/// <para>Four symbols, 176 ms, before any payload is what every frame paid, a third of a
/// 1024-byte burst on the 8 kHz preset; a follow-on frame pays one. The receiver reads the
/// header where the previous burst ended, against the acquisition channel it refreshed from the
/// previous header, and the payload against a channel refreshed from the previous frame's own
/// decoded payload symbols. A follow-on header that fails costs the rest of the keyup, because
/// there is no sync symbol to find the next frame by; that is the trade, and it is why this is a
/// choice.</para>
/// <para>A follow-on burst is read where the previous one ended and, when it is not exactly
/// there, looked for up to <see cref="OfdmFmBurstCodec.FollowOnGapSymbols"/> symbols later by the
/// self-similarity of its symbols' cyclic prefixes, so a host that leaves a short silence between
/// the frames of a keyup, as pdn-soundmodem does (30 ms, measured on air), does not lose the
/// keyup to it. A longer silence does lose it.</para>
/// <para>Receivers on this code read follow-on bursts whether or not they send them, so a
/// station turning this on needs only its correspondents to be current. The first frame of a
/// keyup, and the first after a change of geometry, is always a full burst. Implies
/// <paramref name="ContiguousBursts"/> for the frames it applies to.</para></param>
/// <param name="GeometryId">Which entry of the station's <see cref="OfdmFmGeometryTable"/> this
/// profile's carrier layout is, 0 to 15, and therefore what its bursts announce in their header.
/// <para><b>Null means this profile runs alone.</b> Its own layout is then both the acquisition
/// geometry and the payload geometry, every burst says "geometry 0", and the far end must be
/// configured for the same layout in advance - which is the arrangement this waveform had before
/// geometry was signalled, and is still what a bench with one profile wants.</para>
/// <para>With an id, the profile is one entry in a table shared by every profile in the geometry
/// file, the sync, preamble and header go out on entry 0 whatever this profile's layout is, and a
/// receiver on any profile in the same table can hear it. Two profiles may share an id only if
/// their layouts are identical: rate variants of one span, for instance.</para></param>
public sealed record OfdmFmParameters(
    int SampleRate,
    int FftSize,
    int CyclicPrefix,
    int FirstCarrier,
    int DataCarriers,
    int PilotCarriers,
    OfdmFmCoding? Coding = null,
    IReadOnlyList<OfdmFmBitLoadingTier>? BitLoading = null,
    OfdmFmConstellation Constellation = OfdmFmConstellation.Qpsk,
    int HeaderRepeats = 1,
    double? PeakToAverageLimitDb = null,
    bool AdaptiveRate = false,
    double RecommendationLifeSeconds = 120,
    int? GeometryId = null,
    bool ContiguousBursts = false,
    bool FollowOnFrames = false,
    OfdmFmConstellation AdaptiveTopConstellation = OfdmFmConstellation.Qam64)
{
    /// <summary>The coding this profile uses; none if the profile does not say.</summary>
    public OfdmFmCoding Codes => Coding ?? new OfdmFmCoding();

    /// <summary>This profile's own carrier layout, as a geometry a table can hold.</summary>
    public OfdmFmGeometry Geometry => new(FirstCarrier, DataCarriers, PilotCarriers, BitLoading);

    /// <summary>
    /// Bits each data carrier carries: the profile's bit-loading tiers if it has them, otherwise
    /// the requested constellation uniformly across the band.
    /// </summary>
    public int[] BitsPerDataCarrier(OfdmFmConstellation uniform) =>
        Geometry.BitsPerDataCarrier(uniform);

    /// <summary>
    /// A small invented carrier layout: the toy, for tests and for a bench with no radio on it.
    /// </summary>
    /// <remarks>
    /// <para><b>Not a layout to key a transmitter with.</b> The ones this repository ships, and
    /// expects a station to run, are in <see cref="OfdmFmPresets"/> and have been on the air. This
    /// one is sized to exercise every code path at a fraction of the cost, and to give two
    /// processes something that interoperates without either of them touching a radio, which is
    /// what the tests and the probes run against.</para>
    /// <para>The 12 kHz is not a waveform choice: it is one of the two rates a pdn-soundmodem
    /// channel runs at, so even the toy is one a station could actually key. Everything else here
    /// is invented.</para>
    /// <para>An operator wanting a layout of their own supplies it rather than editing this: see
    /// <see cref="LoadLocal"/>. A working layout is a property of a particular radio and a
    /// particular audio path, so it belongs in configuration, and keeping that door open has a
    /// second benefit worth having on its own - it forces every part of the implementation to be
    /// geometry-generic rather than quietly assuming whatever the shipped presets happen to
    /// be.</para>
    /// </remarks>
    public static OfdmFmParameters Synthetic { get; } = new(
        SampleRate: 12000, FftSize: 128, CyclicPrefix: 8, FirstCarrier: 6,
        DataCarriers: 20, PilotCarriers: 4);

    /// <summary>
    /// The same waveform expressed on a finer sample grid: identical subcarrier spacing, identical
    /// occupied frequencies, identical symbol duration, just more samples describing them.
    /// </summary>
    /// <param name="sampleRate">The rate to express it at. Must be this profile's rate times a
    /// power of two, or the result would not have a power-of-two transform.</param>
    /// <returns>The rescaled profile, or null if <paramref name="sampleRate"/> is not reachable.</returns>
    /// <remarks>
    /// <para>Not resampling: no filter, no interpolation, no loss. <see cref="SubcarrierSpacingHz"/>
    /// is <c>SampleRate/FftSize</c>, so doubling both leaves the spacing alone; the bin indices are
    /// therefore the same bins at the same frequencies and <see cref="FirstCarrier"/> does
    /// <em>not</em> scale. <see cref="CyclicPrefix"/> does, because it is a duration. The transform
    /// costs more and the signal on air is the same signal, sampled faster.</para>
    /// <para>The sync symbol's even-absolute-bin rule survives for the same reason: the bin indices
    /// are untouched, so their parity is too.</para>
    /// <para>This exists because a host channel runs at 12000 or 48000 Hz and a waveform's natural
    /// rate is its own business. The alternative - decimating the channel's audio into the modem
    /// and interpolating its bursts back out - needs anti-alias filters at both ends and puts a
    /// filter's phase response inside an equaliser's job. This puts nothing in the path.</para>
    /// </remarks>
    public OfdmFmParameters? Rescaled(int sampleRate)
    {
        if (sampleRate == SampleRate)
        {
            return this;
        }

        // SampleRate guarded before it is a divisor: a geometry file is a text file somebody edits,
        // and "sampleRate": 0 would otherwise be a DivideByZeroException out of a property, thrown
        // during the plugin's constructor and taking every other profile down with it.
        if (sampleRate <= 0 || SampleRate <= 0 || sampleRate % SampleRate != 0)
        {
            return null;
        }

        int ratio = sampleRate / SampleRate;
        if ((ratio & (ratio - 1)) != 0)
        {
            return null;
        }

        return this with
        {
            SampleRate = sampleRate,
            FftSize = FftSize * ratio,
            CyclicPrefix = CyclicPrefix * ratio,
        };
    }

    /// <summary>
    /// The rate this modem runs at, always, or null if this profile cannot be expressed there.
    /// </summary>
    /// <remarks>
    /// <b>48 kHz, and nothing else.</b> It is what the CM108 in every cheap USB radio interface
    /// runs natively, so a channel at 48 kHz means the capture rate and the DSP rate are the same
    /// number: no decimation in the daemon, no driver resampler, no group delay and no anti-alias
    /// filter of somebody else's design between the radio and the demodulator. Measured, the rate
    /// costs the modem nothing either way (see HostRateTests), so there is no reason to run at
    /// anything else and every reason not to - this modem is not expected to share a radio with
    /// modes that want 12 kHz, and offering a choice would only mean two paths to test.
    /// </remarks>
    public int? HostSampleRate => Rescaled(HostRate) is not null ? HostRate : null;

    /// <summary>The one rate this modem runs at.</summary>
    public const int HostRate = 48000;

    /// <summary>Spacing between subcarriers. Orthogonality is over the useful part of the symbol,
    /// so this is the reciprocal of the useful symbol time, not of the whole symbol.</summary>
    public double SubcarrierSpacingHz => (double)SampleRate / FftSize;

    /// <summary>Symbols per second, cyclic prefix included.</summary>
    public double SymbolRate => (double)SampleRate / (FftSize + CyclicPrefix);

    /// <summary>Occupied bins, data and pilots together.</summary>
    public int TotalCarriers => DataCarriers + PilotCarriers;

    /// <summary>Lowest and highest occupied frequency.</summary>
    public (double LowHz, double HighHz) Occupancy =>
        (FirstCarrier * SubcarrierSpacingHz,
            (FirstCarrier + TotalCarriers - 1) * SubcarrierSpacingHz);

    /// <summary>Payload bits per second at a given constellation, before any FEC.</summary>
    public double BitRate(OfdmFmConstellation constellation) =>
        DataCarriers * SymbolRate * constellation.BitsPerCarrier();

    /// <summary>Samples in one symbol including its prefix.</summary>
    public int SymbolSamples => FftSize + CyclicPrefix;

    /// <summary>
    /// Reads a profile set from an untracked local file, or returns null if there is none: the
    /// override, for an operator running a layout of their own.
    /// </summary>
    /// <param name="path">File to read; defaults to <c>ofdm-fm.local.json</c> beside this assembly,
    /// then beside the host application, walking upward from each.</param>
    /// <remarks>
    /// <para>The file is a JSON object of named profiles, one property per constructor parameter:
    /// <c>{ "example": { "sampleRate": 48000, "fftSize": 2048, "cyclicPrefix": 64,
    /// "firstCarrier": 9, "dataCarriers": 197, "pilotCarriers": 8 } }</c>. Profiles are written
    /// natively at the host rate, so the numbers a file carries are the numbers the transform
    /// runs on and nothing rescales underneath them.</para>
    /// <para><b>This is the override, not the source of what ships.</b>
    /// <see cref="OfdmFmPresets"/> holds the measured layouts and the catalogue's modes are built
    /// from those; nothing here has to exist for a station to work. The file is for an operator
    /// experimenting with a layout of their own - a different span for a radio whose audio path is
    /// not like the ones the presets were measured on, a bit-loading tier set, a geometry table.
    /// The example above is one such layout and not a preset. Being station configuration rather
    /// than source, the file is listed in .gitignore and should stay there.</para>
    /// </remarks>
    public static IReadOnlyDictionary<string, OfdmFmParameters>? LoadLocal(string? path = null)
    {
        string? file = path ?? FindLocalFile();
        if (file is null || !File.Exists(file))
        {
            return null;
        }

        using FileStream stream = File.OpenRead(file);
        var profiles = JsonSerializer.Deserialize<Dictionary<string, OfdmFmParameters>>(
            stream, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        return profiles is null || profiles.Count == 0 ? null : profiles;
    }

    /// <summary>Validates a profile, throwing with the reason rather than failing obscurely deep
    /// in a transform.</summary>
    /// <remarks>
    /// <b>Everything a burst would throw on has to be checked here, not only the geometry.</b> The
    /// coding and the bit-loading tiers are as much a part of a profile as the carrier counts, and
    /// they are only touched when a burst is built - so a profile that passed a geometry-only check
    /// would be accepted, registered, offered as a mode, and then throw out of
    /// <c>IModem.Process</c> on the receive thread the first time anything arrived. A validator
    /// that lets that through is not doing its job.
    /// </remarks>
    public void Validate()
    {
        if (SampleRate <= 0)
        {
            throw new InvalidOperationException($"SampleRate must be positive, was {SampleRate}");
        }

        if (FftSize < 8 || (FftSize & (FftSize - 1)) != 0)
        {
            throw new InvalidOperationException($"FftSize must be a power of two, was {FftSize}");
        }

        // The carrier layout and the bit-loading tiers, by the geometry's own check. The tiers
        // are only reached when a burst is built - which on a bad profile would be on the receive
        // thread, long after anybody could act on it.
        Geometry.Validate(FftSize);

        if (CyclicPrefix < 0 || CyclicPrefix >= FftSize)
        {
            throw new InvalidOperationException($"CyclicPrefix {CyclicPrefix} is not a guard");
        }

        if (GeometryId is int id && (id < 0 || id >= OfdmFmGeometryTable.MaxEntries))
        {
            throw new InvalidOperationException(
                $"geometryId {id} is not a 4-bit id (0 to {OfdmFmGeometryTable.MaxEntries - 1})");
        }

        // The coding, by using it. It throws with its own reasons, and is otherwise only reached
        // when a burst is built.
        _ = new OfdmFmCodec(Codes);

        // A burst names its own coding, so a coding with no name would build, decode locally and
        // then be untransmittable. Caught here with the rest of the config rather than on the
        // transmit path, and restated as the exception every other config fault here throws so a
        // caller validating a profile has one thing to catch.
        try
        {
            _ = OfdmFmBurstCodec.CodingId(Codes);
        }
        catch (ArgumentException e)
        {
            throw new InvalidOperationException(e.Message, e);
        }
    }

    /// <summary>Which occupied positions carry pilots: spread as evenly as the count allows, so a
    /// channel estimate has references across the whole block rather than at one end.</summary>
    public bool[] PilotMap() => Geometry.PilotMap();

    /// <summary>
    /// The geometry table a set of profiles shares: one entry per distinct
    /// <see cref="GeometryId"/>, or null if no profile declares one.
    /// </summary>
    /// <param name="profiles">The profiles, at the rate they will run at.</param>
    /// <param name="rejected">Profiles that could not join the table, with the reason: an id
    /// already taken by a different layout, or a transform size or cyclic prefix that differs from
    /// the others', which a receiver cannot switch between burst by burst.</param>
    /// <exception cref="InvalidOperationException">Profiles declare ids but none declares id 0,
    /// so there is no acquisition geometry and nothing in the table could be heard.</exception>
    /// <remarks>
    /// Built from the profiles rather than from a table of its own in the file, so the layouts stay
    /// where they have always lived and adding an id to a profile is the whole of the change. A
    /// profile with no id is left out and runs alone (see <see cref="GeometryId"/>).
    /// </remarks>
    public static OfdmFmGeometryTable? TableOf(
        IReadOnlyDictionary<string, OfdmFmParameters> profiles,
        out IReadOnlyList<(string Name, string Reason)> rejected)
    {
        ArgumentNullException.ThrowIfNull(profiles);
        var entries = new OfdmFmGeometry?[OfdmFmGeometryTable.MaxEntries];
        var owner = new string?[OfdmFmGeometryTable.MaxEntries];
        var refused = new List<(string, string)>();
        (int Fft, int Prefix, int Rate, string Name)? shape = null;
        int filled = 0;

        foreach ((string name, OfdmFmParameters profile) in
            profiles.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            if (profile.GeometryId is not int id)
            {
                continue;
            }

            if (id < 0 || id >= OfdmFmGeometryTable.MaxEntries)
            {
                refused.Add((name, $"geometryId {id} is not a 4-bit id"));
                continue;
            }

            if (shape is null)
            {
                shape = (profile.FftSize, profile.CyclicPrefix, profile.SampleRate, name);
            }
            else if (shape.Value.Fft != profile.FftSize
                || shape.Value.Prefix != profile.CyclicPrefix
                || shape.Value.Rate != profile.SampleRate)
            {
                refused.Add((name,
                    $"transform {profile.FftSize}/{profile.CyclicPrefix} at {profile.SampleRate} Hz "
                    + $"differs from '{shape.Value.Name}', and a receiver cannot change those per "
                    + "burst"));
                continue;
            }

            OfdmFmGeometry layout = profile.Geometry;
            if (entries[id] is OfdmFmGeometry existing)
            {
                if (!existing.SameLayoutAs(layout))
                {
                    refused.Add((name,
                        $"geometryId {id} is already '{owner[id]}' with a different layout"));
                }

                continue;
            }

            entries[id] = layout;
            owner[id] = name;
            filled++;
        }

        rejected = refused;
        if (filled == 0)
        {
            return null;
        }

        if (entries[OfdmFmGeometryTable.AcquisitionId] is null)
        {
            throw new InvalidOperationException(
                "profiles declare geometry ids but none declares id 0, which is the acquisition "
                + "geometry every burst's header is read on");
        }

        return new OfdmFmGeometryTable(entries);
    }

    /// <summary>
    /// Beside this assembly first, then beside the host application, walking upward from each.
    /// </summary>
    /// <remarks>
    /// <b>This assembly's own directory comes first, and that is the case that matters.</b> Loaded
    /// as a plugin, <see cref="AppContext.BaseDirectory"/> is the daemon's install directory, which
    /// is nowhere near where an operator put the plugin and its geometry - so searching only from
    /// there finds nothing and the station silently comes up on the synthetic profile. An operator
    /// drops the DLL and the JSON in one directory and expects that to be the whole of it.
    /// </remarks>
    private static string? FindLocalFile()
    {
        const string Name = "ofdm-fm.local.json";
        string? beside = Path.GetDirectoryName(typeof(OfdmFmParameters).Assembly.Location);
        foreach (string? from in (ReadOnlySpan<string?>)[beside, AppContext.BaseDirectory])
        {
            if (string.IsNullOrEmpty(from))
            {
                continue;
            }

            var dir = new DirectoryInfo(from);
            while (dir is not null)
            {
                string candidate = Path.Combine(dir.FullName, Name);
                if (File.Exists(candidate))
                {
                    return candidate;
                }

                dir = dir.Parent;
            }
        }

        return null;
    }
}
