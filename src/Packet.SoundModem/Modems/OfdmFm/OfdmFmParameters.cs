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
/// <param name="BitLoading">Bits per data carrier as runs across the band, so a channel whose
/// noise rises with frequency can carry more where it is quiet. Absent means uniform.</param>
public sealed record OfdmFmParameters(
    int SampleRate,
    int FftSize,
    int CyclicPrefix,
    int FirstCarrier,
    int DataCarriers,
    int PilotCarriers,
    OfdmFmCoding? Coding = null,
    IReadOnlyList<OfdmFmBitLoadingTier>? BitLoading = null)
{
    /// <summary>The coding this profile uses; none if the profile does not say.</summary>
    public OfdmFmCoding Codes => Coding ?? new OfdmFmCoding();

    /// <summary>
    /// Bits each data carrier carries: the profile's bit-loading tiers if it has them, otherwise
    /// the requested constellation uniformly across the band.
    /// </summary>
    public int[] BitsPerDataCarrier(OfdmFmConstellation uniform)
    {
        var bits = new int[DataCarriers];
        if (BitLoading is null || BitLoading.Count == 0)
        {
            Array.Fill(bits, uniform.BitsPerCarrier());
            return bits;
        }

        int at = 0;
        foreach (OfdmFmBitLoadingTier tier in BitLoading)
        {
            if (tier.Bits is < 1 or > 8)
            {
                throw new InvalidOperationException(
                    $"bit-loading tier of {tier.Bits} bits is not a constellation we have");
            }

            for (int c = 0; c < tier.Carriers && at < bits.Length; c++)
            {
                bits[at++] = tier.Bits;
            }
        }

        if (at != DataCarriers)
        {
            throw new InvalidOperationException(
                $"bit-loading tiers cover {at} carriers, profile has {DataCarriers}");
        }

        return bits;
    }

    /// <summary>
    /// The parameters this repository commits to, and they are <b>deliberately not the real ones</b>.
    /// </summary>
    /// <remarks>
    /// <para>OFDM-FM is our own waveform, but the profiles we actually run were sized against what
    /// we learned about IP400's OFDM-AB while researching it, and those numbers are not ours to
    /// publish: the specification is neither public nor, as of 2026-08-08, final, and what we know
    /// came unofficially from the mode's author, who is staying quiet publicly so the organisation
    /// funding the project can be its information source. Printing his numbers here would put him
    /// in an awkward position and cost us the relationship, so this file carries a small synthetic
    /// profile instead: enough to exercise every code path, nowhere near anything real.</para>
    /// <para>The working profiles live in a local, untracked JSON file - see <see cref="LoadLocal"/>.
    /// Keeping them out of the source has a second benefit worth having on its own: it forces
    /// every part of the implementation to be geometry-generic, which is exactly what you want
    /// while a geometry is still moving.</para>
    /// </remarks>
    public static OfdmFmParameters Synthetic { get; } = new(
        SampleRate: 8000, FftSize: 128, CyclicPrefix: 8, FirstCarrier: 6,
        DataCarriers: 20, PilotCarriers: 4);

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
    /// Reads a profile set from an untracked local file, or returns null if there is none.
    /// </summary>
    /// <param name="path">File to read; defaults to <c>ofdm-fm.local.json</c> beside the
    /// executable, then the same name at the repository root.</param>
    /// <remarks>
    /// The file is a JSON object of named profiles, for example
    /// <c>{ "nb": { "sampleRate": 24000, "fftSize": 1024, ... } }</c>. It is listed in
    /// .gitignore and must stay there: see <see cref="Synthetic"/> for why.
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
    public void Validate()
    {
        if (FftSize < 8 || (FftSize & (FftSize - 1)) != 0)
        {
            throw new InvalidOperationException($"FftSize must be a power of two, was {FftSize}");
        }

        if (FirstCarrier < 1 || FirstCarrier + TotalCarriers > (FftSize / 2))
        {
            throw new InvalidOperationException(
                $"carriers {FirstCarrier}..{FirstCarrier + TotalCarriers - 1} do not fit the "
                + $"{FftSize / 2} usable bins of a {FftSize}-point real transform (DC and Nyquist "
                + "are never occupied)");
        }

        if (DataCarriers < 1 || PilotCarriers < 0)
        {
            throw new InvalidOperationException("a profile needs at least one data carrier");
        }

        if (CyclicPrefix < 0 || CyclicPrefix >= FftSize)
        {
            throw new InvalidOperationException($"CyclicPrefix {CyclicPrefix} is not a guard");
        }
    }

    /// <summary>Which occupied positions carry pilots: spread as evenly as the count allows, so a
    /// channel estimate has references across the whole block rather than at one end.</summary>
    public bool[] PilotMap()
    {
        var map = new bool[TotalCarriers];
        if (PilotCarriers == 0)
        {
            return map;
        }

        double step = TotalCarriers / (double)PilotCarriers;
        for (int p = 0; p < PilotCarriers; p++)
        {
            int position = (int)Math.Round((p + 0.5) * step);
            map[Math.Clamp(position, 0, TotalCarriers - 1)] = true;
        }

        return map;
    }

    private static string? FindLocalFile()
    {
        const string Name = "ofdm-fm.local.json";
        string beside = Path.Combine(AppContext.BaseDirectory, Name);
        if (File.Exists(beside))
        {
            return beside;
        }

        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            string candidate = Path.Combine(dir.FullName, Name);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            dir = dir.Parent;
        }

        return null;
    }
}
