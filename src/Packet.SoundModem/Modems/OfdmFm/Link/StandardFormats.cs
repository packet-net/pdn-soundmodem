namespace Packet.SoundModem.Modems.OfdmFm.Link;

/// <summary>How a burst of a standard format opens (the prototype's layouts full, n3x and n1x).</summary>
public enum FormatOpening
{
    /// <summary>On the full band (all 333 carriers), with one training symbol. Formats whose band tops out above 3 kHz.</summary>
    Full = 0,

    /// <summary>On the carriers up to 3000 Hz only, with a longer opening: three training symbols against one. The 3 kHz and 2 kHz bands.</summary>
    UpTo3000Hz = 1,

    /// <summary>On the carriers up to 1500 Hz only, with a longer opening: six training symbols against one. The 1.5 kHz and 1 kHz bands.</summary>
    UpTo1500Hz = 2,
}

/// <summary>What a standard format number means (the prototype's <c>formats.spec</c>).</summary>
public readonly struct FormatSpec
{
    internal FormatSpec(int number, int band, int rung, double topHz, int carriers, double infoBitsPerCarrier, int constellationBits, double codeRate, double driveDb, FormatOpening opening, double kbitPerSecond)
    {
        Number = number;
        Band = band;
        Rung = rung;
        TopHz = topHz;
        Carriers = carriers;
        InfoBitsPerCarrier = infoBitsPerCarrier;
        ConstellationBits = constellationBits;
        CodeRate = codeRate;
        DriveDb = driveDb;
        Opening = opening;
        KbitPerSecond = kbitPerSecond;
    }

    /// <summary>The format number, 32 x band + rung.</summary>
    public int Number { get; }

    /// <summary>The band, 0 (widest) to 7.</summary>
    public int Band { get; }

    /// <summary>The rung, 0 (fewest bits) to 31.</summary>
    public int Rung { get; }

    /// <summary>The top of the payload's band, Hz.</summary>
    public double TopHz { get; }

    /// <summary>The carriers the payload uses: those from the first up to <see cref="TopHz"/>.</summary>
    public int Carriers { get; }

    /// <summary>Information bits per carrier per symbol.</summary>
    public double InfoBitsPerCarrier { get; }

    /// <summary>Bits per carrier of the constellation: 1 (BPSK), 2, 4, 6, 8 or 10 (1024-QAM).</summary>
    public int ConstellationBits { get; }

    /// <summary>Information bits per coded bit.</summary>
    public double CodeRate { get; }

    /// <summary>Decibels over full drive.</summary>
    public double DriveDb { get; }

    /// <summary>How the burst opens.</summary>
    public FormatOpening Opening { get; }

    /// <summary>The payload's information rate, kbit/s.</summary>
    public double KbitPerSecond { get; }
}

/// <summary>
/// The first list of standard formats (revision 0): a number is a band (top three bits) and a rung (low five), and
/// everything else follows. Pinned to the prototype's <c>formats.py</c>, <c>station.slower</c> and <c>station.rate</c>;
/// see <c>docs/dev/ofdm-fm/adaptation-protocol.md</c>.
/// </summary>
public static class StandardFormats
{
    /// <summary>Formats in the list: every 8-bit number.</summary>
    public const int Count = 256;

    /// <summary>Rungs in a band.</summary>
    public const int Rungs = 32;

    /// <summary>The number of payload carriers on the full band.</summary>
    public const int FullBandCarriers = 333;

    /// <summary>The longest burst, in payload symbols.</summary>
    public const int MaxSymbols = 252;

    /// <summary>The most robust format: the bottom rung of the 1.5 kHz band.</summary>
    public const int MostRobust = 6 * Rungs;

    /// <summary>The format of a first contact: the most robust one.</summary>
    public const int FirstContact = MostRobust;

    private const double SymbolSeconds = 2112.0 / 48000.0;

    // The 32 rung values, 0.2 * (9.3 / 0.2) ** (k / 31), as the prototype's repr() prints them (17 significant
    // digits at most): the same doubles, not a recomputation.
    private static readonly double[] RungEta =
    {
        0.2, 0.2263699633822193, 0.25621680160833654, 0.28999893998994236,
        0.32823524713202834, 0.3715130043701547, 0.420496925976451, 0.4759393686781249,
        0.5386918872991185, 0.6097183140110003, 0.6901095620806931, 0.7811003814896295,
        0.8840883237782245, 1.0006552074016217, 1.1325914132886605, 1.2819233837658506,
        1.450944747209431, 1.6422515464771115, 1.8587821122020838, 2.1038621933735495,
        2.3812560383760295, 2.6952242110543514, 3.050589029816223, 3.4528086348684903,
        3.9080608212049537, 4.423337924958255, 5.006554220499911, 5.666667477828302,
        6.413816547276027, 7.259477084735732, 8.21663780922844, 9.3,
    };

    private static readonly double[] BandTopHz = { 8000.0, 6500.0, 5000.0, 4000.0, 3000.0, 2000.0, 1500.0, 1000.0 };

    private static readonly int[] ConstellationBitCounts = { 2, 4, 6, 8, 10 };

    private static readonly FormatSpec[] Table = BuildTable();

    /// <summary>What a format number means. Throws for a number outside 0 to 255.</summary>
    public static FormatSpec Describe(int number)
    {
        if ((uint)number >= Count)
        {
            throw new ArgumentOutOfRangeException(nameof(number));
        }

        return Table[number];
    }

    /// <summary>The information bits a burst of this format carries in this many payload symbols (the check included): the whole of 8 bits that fit.</summary>
    public static int InfoBits(int number, int payloadSymbols)
    {
        FormatSpec s = Describe(number);
        return (int)(s.InfoBitsPerCarrier * s.Carriers * payloadSymbols) / 8 * 8;
    }

    /// <summary>The format <paramref name="rungs"/> steps slower in the same band; when the band runs out, the most robust format unless this one is already slower than that, in which case its own band's bottom rung.</summary>
    public static int Slower(int number, int rungs)
    {
        if (rungs < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(rungs));
        }

        FormatSpec s = Describe(number);
        if (s.Rung - rungs >= 0)
        {
            return (s.Band * Rungs) + s.Rung - rungs;
        }

        return Rate(MostRobust) <= s.KbitPerSecond ? MostRobust : s.Band * Rungs;
    }

    /// <summary>The payload's information rate in kbit/s.</summary>
    public static double Rate(int number) => Describe(number).KbitPerSecond;

    private static FormatSpec[] BuildTable()
    {
        var table = new FormatSpec[Count];
        for (int n = 0; n < Count; n++)
        {
            int band = n / Rungs;
            int rung = n % Rungs;
            double top = BandTopHz[band];
            double eta = RungEta[rung];
            int carriers = 0;
            for (int k = 0; k < FullBandCarriers; k++)
            {
                if ((9 + k) * 48000.0 / 2048 <= top)
                {
                    carriers++;
                }
            }

            int bits = ConstellationFor(eta);
            double drive = eta <= 2.5 ? 6.0 : eta <= 5.0 ? 3.0 : 0.0;
            FormatOpening opening = top <= 1500.0 ? FormatOpening.UpTo1500Hz : top <= 3000.0 ? FormatOpening.UpTo3000Hz : FormatOpening.Full;
            table[n] = new FormatSpec(n, band, rung, top, carriers, eta, bits, eta / bits, drive, opening, eta * carriers / SymbolSeconds / 1000);
        }

        return table;
    }

    private static int ConstellationFor(double eta)
    {
        if (eta < 0.4)
        {
            return 1;
        }

        foreach (int m in ConstellationBitCounts)
        {
            if (eta / m <= 0.8)
            {
                return m;
            }
        }

        return 10;
    }
}
