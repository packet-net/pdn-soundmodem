using System.Globalization;

namespace Packet.SoundModem.Modems.OfdmFm.Link;

/// <summary>
/// The code the burst header goes through: the prototype's <c>modem.layout_code(layout, 3)</c>, which is a single
/// block of the 5G NR LDPC base graph 2 at lifting size 9 with 39 filler bits, the first 18 systematic bits never
/// sent, rate-matched (read round the circular buffer from its start, skipping the fillers, repeating when it
/// wraps) to 666 coded bits for the full-band opening, 480 for the opening on the carriers up to 3 kHz and 336
/// for the opening up to 1.5 kHz. Bits are one per byte, 0 or 1.
/// </summary>
/// <remarks>
/// The table is base graph 2 of 3GPP TS 38.212 section 5.3.2, the entries for the lifting size set that contains
/// 9, embedded as <c>header-ldpc-bg2-set4.txt</c> (see the notice in that file and in LICENSING.md). Only these
/// three shapes are built; this is not a general 5G NR encoder. The core of this code is solved by the same peeled
/// path as the payload code's (a sum of the four core rows gives one parity block, the rest follow row by row), so
/// <see cref="LinkLdpcStructure"/> is reused; the prototype takes that path too.
/// </remarks>
public static class HeaderCode
{
    private const int Z = 9;
    private const int FillerBits = 39;
    private const int BaseGraphRows = 42;
    private const int BaseGraphColumns = 52;
    private const int InformationColumns = 10;
    private const int CoreRows = 4;
    private const int Punctured = 2;

    private static readonly Lazy<Plan[]> Plans = new(BuildPlans, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>Coded bits the opening's header takes: 666, 480 or 336.</summary>
    public static int CodedBits(FormatOpening opening) => PlanFor(opening).CodedBits;

    /// <summary>Length of the full codeword <see cref="EncodeCodeword"/> writes, in bits.</summary>
    public static int CodewordLength(FormatOpening opening) => PlanFor(opening).Structure.CodewordLength;

    /// <summary>
    /// Encodes the 51 header bits (<see cref="BurstHeaderCodec.HeaderBits"/>) into <paramref name="coded"/>
    /// (<see cref="CodedBits"/> bits). Allocates nothing once the plans exist.
    /// </summary>
    public static void Encode(FormatOpening opening, ReadOnlySpan<byte> headerBits, Span<byte> coded)
    {
        Plan plan = PlanFor(opening);
        if (coded.Length != plan.CodedBits)
        {
            throw new ArgumentException($"expected room for {plan.CodedBits} coded bits", nameof(coded));
        }

        Span<byte> x = stackalloc byte[plan.Structure.CodewordLength];
        Span<byte> work = stackalloc byte[plan.Structure.Rows * Z];
        Fill(plan, headerBits, x, work);
        for (int j = 0; j < plan.TransmitOrder.Length; j++)
        {
            coded[j] = x[plan.TransmitOrder[j]];
        }
    }

    /// <summary>
    /// Writes the full codeword (<see cref="CodewordLength"/> bits: the 51 header bits, the 39 filler zeros, then
    /// parity) into <paramref name="codeword"/>, for checking with <see cref="SyndromeIsZero"/>.
    /// </summary>
    public static void EncodeCodeword(FormatOpening opening, ReadOnlySpan<byte> headerBits, Span<byte> codeword)
    {
        Plan plan = PlanFor(opening);
        if (codeword.Length != plan.Structure.CodewordLength)
        {
            throw new ArgumentException($"expected room for {plan.Structure.CodewordLength} bits", nameof(codeword));
        }

        Span<byte> work = stackalloc byte[plan.Structure.Rows * Z];
        Fill(plan, headerBits, codeword, work);
    }

    /// <summary>True when every check row of the opening's kept code is satisfied by the full codeword.</summary>
    public static bool SyndromeIsZero(FormatOpening opening, ReadOnlySpan<byte> codeword) =>
        PlanFor(opening).Structure.SyndromeIsZero(codeword);

    /// <summary>The codeword position of each coded bit, in the order sent (for tests).</summary>
    internal static ReadOnlySpan<int> TransmitOrder(FormatOpening opening) => PlanFor(opening).TransmitOrder;

    /// <summary>Check rows the opening's code keeps (for tests).</summary>
    internal static int CheckRows(FormatOpening opening) => PlanFor(opening).Structure.Rows;

    /// <summary>How the core is solved (for tests).</summary>
    internal static string EncoderKind(FormatOpening opening) => PlanFor(opening).Structure.EncoderKind;

    private static void Fill(Plan plan, ReadOnlySpan<byte> headerBits, Span<byte> x, Span<byte> work)
    {
        if (headerBits.Length != BurstHeaderCodec.HeaderBits)
        {
            throw new ArgumentException($"expected {BurstHeaderCodec.HeaderBits} header bits", nameof(headerBits));
        }

        x.Clear();
        for (int i = 0; i < headerBits.Length; i++)
        {
            x[i] = (byte)(headerBits[i] & 1);
        }

        plan.Structure.Complete(x, work);
    }

    private static Plan PlanFor(FormatOpening opening)
    {
        // The plans are built in the order full band, up to 3 kHz, up to 1.5 kHz (666, 480 and 336 coded bits).
        return opening switch
        {
            FormatOpening.Full => Plans.Value[0],
            FormatOpening.UpTo3000Hz => Plans.Value[1],
            FormatOpening.UpTo1500Hz => Plans.Value[2],
            _ => throw new ArgumentOutOfRangeException(nameof(opening)),
        };
    }

    private static Plan[] BuildPlans()
    {
        var shifts = ReadTable();
        int k = BurstHeaderCodec.HeaderBits;
        int kFull = InformationColumns * Z;

        // The circular buffer: codeword bits 2Z onward, without the fillers (positions k..K-1).
        var available = new List<int>();
        for (int i = Punctured * Z; i < BaseGraphColumns * Z; i++)
        {
            if (i < k || i >= kFull)
            {
                available.Add(i);
            }
        }

        // Coded bits per opening: 2 bits a carrier over the opening's carriers and header symbols.
        int[] coded = [666, 480, 336];
        var plans = new Plan[3];
        for (int p = 0; p < 3; p++)
        {
            var order = new int[coded[p]];
            int max = 0;
            for (int j = 0; j < order.Length; j++)
            {
                order[j] = available[j % available.Count];
                max = Math.Max(max, order[j]);
            }

            int columns = (max + 1 + Z - 1) / Z;
            int rows = Math.Max(CoreRows, columns - InformationColumns);
            if (rows > BaseGraphRows)
            {
                rows = BaseGraphRows;
            }

            LinkLdpcStructure structure = LinkLdpcStructure.Create(Z, rows, InformationColumns, (r, c) => shifts[r * BaseGraphColumns + c]);
            plans[p] = new Plan(coded[p], order, structure);
        }

        return plans;
    }

    // The embedded table, as shift = V mod Z (or -1 where the base graph has no entry), row-major.
    private static int[] ReadTable()
    {
        var shifts = new int[BaseGraphRows * BaseGraphColumns];
        Array.Fill(shifts, -1);
        using Stream stream = typeof(HeaderCode).Assembly.GetManifestResourceStream("header-ldpc-bg2-set4.txt")
            ?? throw new InvalidDataException("embedded table header-ldpc-bg2-set4.txt is missing");
        using var reader = new StreamReader(stream);
        while (reader.ReadLine() is { } raw)
        {
            int hash = raw.IndexOf('#');
            string line = (hash >= 0 ? raw[..hash] : raw).Trim();
            if (line.Length == 0 || char.IsLetter(line[0]))
            {
                continue;
            }

            string[] v = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            int r = int.Parse(v[0], CultureInfo.InvariantCulture);
            int c = int.Parse(v[1], CultureInfo.InvariantCulture);
            shifts[r * BaseGraphColumns + c] = int.Parse(v[2], CultureInfo.InvariantCulture) % Z;
        }

        return shifts;
    }

    private sealed record Plan(int CodedBits, int[] TransmitOrder, LinkLdpcStructure Structure);
}
