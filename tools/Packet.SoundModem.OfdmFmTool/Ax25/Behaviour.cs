using System.Globalization;
using System.Text;

namespace Packet.SoundModem.OfdmFmTool.Ax25;

/// <summary>The printable test pattern the answering side sends: lines of 79 characters from <c>!</c> to <c>~</c>, each shifted one along from the one before and ended with a carriage return, exactly as many bytes as asked for.</summary>
internal static class TestPattern
{
    public static byte[] Make(int length, int offset = 0)
    {
        var bytes = new byte[length];
        for (int i = 0; i < length; i++)
        {
            bytes[i] = At(offset + i);
        }

        return bytes;
    }

    public static byte At(long index)
    {
        int column = (int)(index % 80);
        return column == 79 ? (byte)'\r' : (byte)('!' + ((column + (int)(index / 80)) % 94));
    }
}

/// <summary>Cuts a stream of bytes into lines at a carriage return or a line feed. A line end straight after another (CR LF) makes no empty line.</summary>
internal sealed class LineAssembler
{
    private readonly StringBuilder _line = new();
    private bool _lastWasReturn;

    public IEnumerable<string> Add(ReadOnlySpan<byte> data)
    {
        var lines = new List<string>();
        foreach (byte b in data)
        {
            if (b == '\n' && _lastWasReturn)
            {
                _lastWasReturn = false;
                continue;
            }

            _lastWasReturn = b == '\r';
            if (b is (byte)'\r' or (byte)'\n')
            {
                lines.Add(_line.ToString());
                _line.Clear();
            }
            else
            {
                _line.Append(b is >= 0x20 and <= 0x7E ? (char)b : '?');
            }
        }

        return lines;
    }
}

/// <summary>
/// What the answering side does with one connected link, apart from the stack: a one-line banner on connect, each line echoed, <c>SEND n</c> answered with n bytes of the test pattern, <c>STATS</c> answered with how many
/// I frames it sent and how many of them were repeats, and <c>BYE</c> ending the link. Lines end in a carriage return.
/// </summary>
internal sealed class AnswerBehaviour(string call, Action<byte[]> send, Action disconnect, Func<(long Sent, long Repeated)> stats)
{
    /// <summary>The most one <c>SEND</c> may ask for.</summary>
    public const int MostBytes = 1_000_000;

    private readonly LineAssembler _lines = new();

    public void Connected() => send(Encoding.ASCII.GetBytes($"pdn-ofdm {call}\r"));

    public void Data(ReadOnlySpan<byte> data)
    {
        foreach (string line in _lines.Add(data))
        {
            Line(line.Trim());
        }
    }

    private void Line(string line)
    {
        string[] words = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 1 && words[0].Equals("BYE", StringComparison.OrdinalIgnoreCase))
        {
            disconnect();
        }
        else if (words.Length == 1 && words[0].Equals("STATS", StringComparison.OrdinalIgnoreCase))
        {
            (long sent, long repeated) = stats();
            send(Encoding.ASCII.GetBytes($"STATS {sent.ToString(CultureInfo.InvariantCulture)} {repeated.ToString(CultureInfo.InvariantCulture)}\r"));
        }
        else if (words.Length == 2 && words[0].Equals("SEND", StringComparison.OrdinalIgnoreCase))
        {
            if (int.TryParse(words[1], NumberStyles.None, CultureInfo.InvariantCulture, out int count) && count is >= 1 and <= MostBytes)
            {
                send(TestPattern.Make(count));
            }
            else
            {
                send(Encoding.ASCII.GetBytes($"SEND needs a number from 1 to {MostBytes.ToString(CultureInfo.InvariantCulture)}\r"));
            }
        }
        else
        {
            send(Encoding.ASCII.GetBytes(line + "\r"));
        }
    }
}

/// <summary>
/// Counts the I frames one side sends and how many of them are repeats, from the frames it traces. A frame is new when its sequence number is the next one in turn (modulo 8 here), and a repeat otherwise:
/// after a loss the stack sends again from the first frame the other end did not get.
/// </summary>
internal sealed class RepeatCounter(int modulus = 8)
{
    private readonly Lock _gate = new();
    private int _next;
    private long _sent;
    private long _repeated;

    public (long Sent, long Repeated) Counts
    {
        get
        {
            lock (_gate)
            {
                return (_sent, _repeated);
            }
        }
    }

    /// <summary>An I frame was sent with this sequence number.</summary>
    public void Sent(int sequence)
    {
        lock (_gate)
        {
            _sent++;
            if (sequence == _next)
            {
                _next = (_next + 1) % modulus;
            }
            else
            {
                _repeated++;
            }
        }
    }
}

/// <summary>
/// The caller's <c>--get</c>: n bytes asked for, the time from the request to the last of them, and the throughput. The banner line the answering side sends first is skipped. The bytes are checked against the pattern; any other line that arrives after them is kept for the
/// answering side's <c>STATS</c>.
/// </summary>
internal sealed class GetMeasurement(int wanted, TimeProvider time)
{
    private readonly LineAssembler _after = new();
    private bool _bannerSeen;
    private long _received;
    private long _damaged;
    private long _started;
    private TimeSpan? _took;

    /// <summary>The request went out now.</summary>
    public void Requested() => _started = time.GetTimestamp();

    public bool GotAllBytes => _took is not null;

    public long Damaged => _damaged;

    public TimeSpan Took => _took ?? TimeSpan.Zero;

    /// <summary>Feeds what arrived. Returns the lines that came after the bytes.</summary>
    public IReadOnlyList<string> Data(ReadOnlySpan<byte> data)
    {
        // The answering side's banner comes first, whatever it was in reply to: one line, which is not part of the transfer.
        if (!_bannerSeen)
        {
            int end = data.IndexOfAny((byte)'\r', (byte)'\n');
            if (end < 0)
            {
                return [];
            }

            _bannerSeen = true;
            data = data[(end + 1)..];
        }

        int pattern = (int)Math.Min(data.Length, Math.Max(0, wanted - _received));
        for (int i = 0; i < pattern; i++)
        {
            if (data[i] != TestPattern.At(_received + i))
            {
                _damaged++;
            }
        }

        _received += pattern;
        if (_took is null && _received >= wanted)
        {
            _took = time.GetElapsedTime(_started);
        }

        return [.. _after.Add(data[pattern..])];
    }

    /// <summary>The line printed at the end, from the answering side's count of frames sent and repeated.</summary>
    public string Result(long framesSent, long framesRepeated)
    {
        double seconds = Math.Max(Took.TotalSeconds, 0.001);
        string rate = ((wanted * 8.0 / seconds) / 1000.0).ToString("0.00", CultureInfo.InvariantCulture);
        string line = $"Received {wanted.ToString(CultureInfo.InvariantCulture)} bytes in {Took.TotalSeconds.ToString("0.00", CultureInfo.InvariantCulture)} s, {rate} kbit/s, "
            + $"{framesRepeated.ToString(CultureInfo.InvariantCulture)} of {framesSent.ToString(CultureInfo.InvariantCulture)} frames sent again";
        return _damaged == 0 ? line : line + $", {_damaged.ToString(CultureInfo.InvariantCulture)} bytes damaged";
    }
}
