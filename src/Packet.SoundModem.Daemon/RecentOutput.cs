using System.Text;
using System.Text.RegularExpressions;

namespace Packet.SoundModem.Daemon;

/// <summary>
/// The last few hundred lines the daemon wrote to its console, kept in memory so that "Report a
/// problem" can attach them (<c>GET /api/report</c>).
/// </summary>
/// <remarks>
/// <para><b>Why not the journal.</b> The service user may not be able to read journalctl, and a
/// station run from a terminal or a container has no journal at all. The daemon already says
/// everything worth saying on stdout and stderr, so the page gets a copy of that: console output
/// is teed at start-up (<see cref="Install"/>), and nothing else in the daemon changes.</para>
/// <para><b>Bounded.</b> At most <see cref="MaxLines"/> lines of at most <see cref="MaxLineChars"/>
/// characters each, oldest dropped first, so it is a fixed few tens of kilobytes however long the
/// station runs or however chatty it gets.</para>
/// <para><b>Secrets.</b> No log line is known to print the API key, the monitor token or the
/// UberSDR password, but one can reach a line by accident: the UberSDR connection puts the
/// password in the query string of its URL, and an exception message that names the URL carries
/// it. So two nets, both cheap. A line is scrubbed by shape as it is captured (a
/// <c>password=</c>, <c>token=</c>, <c>Bearer</c> or key header, or credentials in a URL), and the
/// lines are scrubbed by value as they are read out, against the secrets in the configuration
/// as it is now (<see cref="Lines"/>). A caller gets lines that have been through both.</para>
/// </remarks>
internal sealed partial class RecentOutput
{
    /// <summary>How many lines are kept.</summary>
    public const int MaxLines = 400;

    /// <summary>The longest a kept line may be; the rest is cut and marked.</summary>
    public const int MaxLineChars = 400;

    /// <summary>What replaces a secret in a line.</summary>
    public const string Hidden = "(hidden)";

    /// <summary>A secret shorter than this is not scrubbed by value: it would be hiding ordinary
    /// text. Real keys, tokens and passwords are longer.</summary>
    private const int ShortestSecret = 4;

    private readonly TimeProvider _time;
    private readonly Queue<string> _lines = new();
    private readonly object _gate = new();

    /// <param name="time">The clock for the time on each line; the system's if null.</param>
    public RecentOutput(TimeProvider? time = null) => _time = time ?? TimeProvider.System;

    /// <summary>
    /// Starts keeping a copy of the console's output and error, and returns what keeps it. The
    /// real console still gets everything, written as before.
    /// </summary>
    public static RecentOutput Install()
    {
        var recent = new RecentOutput();
        Console.SetOut(recent.Tee(Console.Out));
        Console.SetError(recent.Tee(Console.Error));
        return recent;
    }

    /// <summary>A writer that passes everything to <paramref name="inner"/> and keeps a copy of
    /// each line.</summary>
    /// <param name="inner">Where the output really goes.</param>
    public TextWriter Tee(TextWriter inner) => new TeeWriter(this, inner);

    /// <summary>Adds one line, as if the daemon had written it.</summary>
    /// <param name="line">The line, without its newline.</param>
    public void Add(string line)
    {
        string kept = Shape(line);
        if (kept.Length == 0)
        {
            return;
        }

        string stamped = $"{_time.GetUtcNow():HH:mm:ss} {kept}";
        lock (_gate)
        {
            _lines.Enqueue(stamped);
            while (_lines.Count > MaxLines)
            {
                _lines.Dequeue();
            }
        }
    }

    /// <summary>The lines kept, oldest first, with every one of <paramref name="secrets"/> hidden
    /// wherever it appears.</summary>
    /// <param name="secrets">The secrets in the configuration as it is now.</param>
    public IReadOnlyList<string> Lines(IEnumerable<string?> secrets)
    {
        string[] known = [.. secrets.Where(s => s is { Length: >= ShortestSecret }).Select(s => s!)
            .Distinct(StringComparer.Ordinal).OrderByDescending(s => s.Length)];
        string[] copy;
        lock (_gate)
        {
            copy = [.. _lines];
        }

        foreach (ref string line in copy.AsSpan())
        {
            foreach (string secret in known)
            {
                line = line.Replace(secret, Hidden, StringComparison.Ordinal);
            }
        }

        return copy;
    }

    /// <summary>Hides what looks like a credential in <paramref name="line"/>, by its shape.</summary>
    /// <param name="line">One line of output.</param>
    internal static string Scrub(string line)
    {
        line = UrlCredentials().Replace(line, "$1" + Hidden + "@");
        line = NamedValue().Replace(line, "$1" + Hidden);
        line = BearerValue().Replace(line, "$1" + Hidden);
        return line;
    }

    /// <summary>A line as it is kept: control characters out (a terminal's colours, a carriage
    /// return), scrubbed, and cut to <see cref="MaxLineChars"/>.</summary>
    private static string Shape(string line)
    {
        var plain = new StringBuilder(line.Length);
        foreach (char c in line)
        {
            if (c == '\t')
            {
                plain.Append(' ');
            }
            else if (!char.IsControl(c))
            {
                plain.Append(c);
            }
        }

        string scrubbed = Scrub(plain.ToString()).TrimEnd();
        return scrubbed.Length > MaxLineChars ? scrubbed[..MaxLineChars] + "..." : scrubbed;
    }

    // user:password@host in a URL.
    [GeneratedRegex(@"(://)[^\s/@:]+:[^\s/@]+@")]
    private static partial Regex UrlCredentials();

    // password=..., token=..., api-key: ..., X-Password: ..., Authorization: ... up to the next
    // separator.
    [GeneratedRegex(@"((?:password|passwd|token|secret|api[-_ ]?key|x-password|authorization)""?\s*[:=]\s*""?)(?!Bearer\s)[^&\s"",;]+", RegexOptions.IgnoreCase)]
    private static partial Regex NamedValue();

    [GeneratedRegex(@"(Bearer\s+)\S+", RegexOptions.IgnoreCase)]
    private static partial Regex BearerValue();

    /// <summary>Passes everything on and keeps each completed line.</summary>
    private sealed class TeeWriter(RecentOutput owner, TextWriter inner) : TextWriter
    {
        private readonly StringBuilder _pending = new();
        private readonly object _gate = new();

        public override Encoding Encoding => inner.Encoding;

        public override IFormatProvider FormatProvider => inner.FormatProvider;

        public override void Write(char value)
        {
            inner.Write(value);
            Keep(value.ToString());
        }

        public override void Write(string? value)
        {
            inner.Write(value);
            Keep(value);
        }

        public override void Write(char[] buffer, int index, int count)
        {
            inner.Write(buffer, index, count);
            Keep(new string(buffer, index, count));
        }

        public override void Write(ReadOnlySpan<char> buffer)
        {
            inner.Write(buffer);
            Keep(new string(buffer));
        }

        public override void WriteLine()
        {
            inner.WriteLine();
            Keep("\n");
        }

        public override void WriteLine(string? value)
        {
            // One call to the real writer, so two threads' lines still do not interleave in it.
            inner.WriteLine(value);
            Keep(value + "\n");
        }

        public override void WriteLine(ReadOnlySpan<char> buffer)
        {
            inner.WriteLine(buffer);
            Keep(new string(buffer) + "\n");
        }

        public override void Flush() => inner.Flush();

        private void Keep(string? text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return;
            }

            lock (_gate)
            {
                foreach (char c in text)
                {
                    if (c == '\n')
                    {
                        owner.Add(_pending.ToString());
                        _pending.Clear();
                    }
                    else if (_pending.Length < MaxLineChars * 4)
                    {
                        _pending.Append(c);
                    }
                }
            }
        }
    }
}
