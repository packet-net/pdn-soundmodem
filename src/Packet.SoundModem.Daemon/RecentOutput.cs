using System.Net;
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
/// <para><b>Bounded.</b> At most <see cref="MaxLines"/> lines, and at most
/// <see cref="MaxStoredChars"/> characters between them (a line is itself held to
/// <see cref="MaxStoredLine"/>), oldest dropped first. Worst case that is about 200 KB of
/// memory however long the station runs or however chatty it gets. What is served is cut shorter,
/// to <see cref="MaxLineChars"/> a line.</para>
/// <para><b>Secrets.</b> No log line is known to print the API key, the monitor token or the
/// UberSDR password, but one can reach a line by accident: the UberSDR connection puts the
/// password in the query string of its URL, and an exception message that names the URL carries
/// it. So there are nets, in this order. A line is scrubbed by shape as it is kept (a
/// <c>password=</c>, <c>token=</c>, <c>Bearer</c>, <c>Basic</c> or key header, credentials in a
/// URL). When the lines are read out they are scrubbed by value against the secrets in the
/// configuration as it is now, then network addresses are masked, and only then is a line cut
/// to its served length, so a secret is never split by the cut and left half showing. The one
/// cut made before that, of a line over <see cref="MaxStoredLine"/>, also drops the last
/// <see cref="CutGuard"/> characters, which is longer than any secret.</para>
/// <para><b>Addresses.</b> A report may end up in a public GitHub issue, so an IPv4 or IPv6
/// address in a line is masked to its last part (<c>x.x.x.123</c>). Loopback and the addresses
/// this station listens on stay readable.</para>
/// </remarks>
internal sealed partial class RecentOutput
{
    /// <summary>How many lines are kept.</summary>
    public const int MaxLines = 400;

    /// <summary>The longest a served line may be; the rest is cut and marked.</summary>
    public const int MaxLineChars = 400;

    /// <summary>The most characters of text kept in all.</summary>
    public const int MaxStoredChars = 100_000;

    /// <summary>The longest a line may be as it is kept, before it is cut for serving.</summary>
    public const int MaxStoredLine = 8_000;

    /// <summary>What is dropped from the end of a line cut at <see cref="MaxStoredLine"/>, so a
    /// secret that straddles that cut cannot leave its first half behind.</summary>
    public const int CutGuard = 256;

    /// <summary>What replaces a secret in a line.</summary>
    public const string Hidden = "(hidden)";

    /// <summary>A secret shorter than this is not scrubbed by value: it would be hiding ordinary
    /// text. Real keys, tokens and passwords are longer.</summary>
    private const int ShortestSecret = 4;

    private readonly TimeProvider _time;
    private readonly Queue<(string Stamp, string Text)> _lines = new();
    private readonly object _gate = new();
    private int _chars;

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
        string kept = Keep(line);
        if (kept.Length == 0)
        {
            return;
        }

        string stamp = $"{_time.GetUtcNow():HH:mm:ss} UTC";
        lock (_gate)
        {
            _lines.Enqueue((stamp, kept));
            _chars += kept.Length;
            while (_lines.Count > MaxLines || _chars > MaxStoredChars)
            {
                _chars -= _lines.Dequeue().Text.Length;
            }
        }
    }

    /// <summary>
    /// The lines kept, oldest first, each as served: every one of <paramref name="secrets"/>
    /// hidden wherever it appears, network addresses masked, then cut to
    /// <see cref="MaxLineChars"/>.
    /// </summary>
    /// <param name="secrets">The secrets in the configuration as it is now.</param>
    /// <param name="keepAddresses">Addresses that stay readable besides loopback: the ones this
    /// station listens on.</param>
    public IReadOnlyList<string> Lines(IEnumerable<string?> secrets, IEnumerable<string>? keepAddresses = null)
    {
        string[] known = [.. secrets.Where(s => s is { Length: >= ShortestSecret }).Select(s => s!)
            .Distinct(StringComparer.Ordinal).OrderByDescending(s => s.Length)];
        HashSet<IPAddress> keep = [];
        foreach (string a in keepAddresses ?? [])
        {
            if (IPAddress.TryParse(a, out IPAddress? parsed))
            {
                keep.Add(parsed);
            }
        }

        (string Stamp, string Text)[] copy;
        lock (_gate)
        {
            copy = [.. _lines];
        }

        var served = new List<string>(copy.Length);
        foreach ((string stamp, string text) in copy)
        {
            string line = text;
            foreach (string secret in known)
            {
                line = line.Replace(secret, Hidden, StringComparison.Ordinal);
            }

            line = MaskAddresses(line, keep);
            if (line.Length > MaxLineChars)
            {
                line = line[..MaxLineChars] + "...";
            }

            served.Add($"{stamp} {line}");
        }

        return served;
    }

    /// <summary>Hides what looks like a credential in <paramref name="line"/>, by its shape.</summary>
    /// <param name="line">One line of output.</param>
    internal static string Scrub(string line)
    {
        line = UrlCredentials().Replace(line, "$1" + Hidden + "@");
        line = QuotedValue().Replace(line, "$1$2" + Hidden + "$2");
        line = NamedValue().Replace(line, "$1" + Hidden);
        line = SchemeValue().Replace(line, "$1" + Hidden);
        line = BareBasic().Replace(line, "$1" + Hidden);
        return line;
    }

    /// <summary>
    /// <paramref name="line"/> with every IPv4 and IPv6 address masked down to its last part
    /// (<c>x.x.x.123</c>, <c>x:x:x:x:x:x:x:1a2b</c>), except loopback, the unspecified address and
    /// <paramref name="keep"/>.
    /// </summary>
    /// <param name="line">One line of output.</param>
    /// <param name="keep">Addresses to leave readable.</param>
    internal static string MaskAddresses(string line, ISet<IPAddress> keep)
    {
        line = Ipv4().Replace(line, m =>
        {
            if (!IPAddress.TryParse(m.Value, out IPAddress? ip) || m.Groups.Cast<Group>().Skip(1).Any(g => int.Parse(g.Value) > 255)
                || IPAddress.IsLoopback(ip) || ip.Equals(IPAddress.Any) || keep.Contains(ip))
            {
                return m.Value;
            }

            return "x.x.x." + m.Groups[4].Value;
        });

        return Ipv6().Replace(line, m =>
        {
            // A time of day (12:00:00) has the same shape as the start of an address, so an
            // address has to be written either compressed or in full.
            bool shaped = m.Value.Contains("::", StringComparison.Ordinal) || m.Value.Count(c => c == ':') == 7;
            if (!shaped || !IPAddress.TryParse(m.Value, out IPAddress? ip)
                || IPAddress.IsLoopback(ip) || ip.Equals(IPAddress.IPv6Any) || keep.Contains(ip))
            {
                return m.Value;
            }

            string last = m.Value[(m.Value.LastIndexOf(':') + 1)..];
            return "x:x:x:x:x:x:x:" + (last.Length > 0 ? last : "0");
        });
    }

    /// <summary>A line as it is kept: control characters out (a terminal's colours, a carriage
    /// return), scrubbed by shape, and cut at <see cref="MaxStoredLine"/> if it is longer than
    /// that.</summary>
    private static string Keep(string line)
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
        return scrubbed.Length > MaxStoredLine ? scrubbed[..(MaxStoredLine - CutGuard)] + "..." : scrubbed;
    }

    // user:password@host or token@host in a URL.
    [GeneratedRegex(@"(://)[^\s/@]+@")]
    private static partial Regex UrlCredentials();

    private const string Names =
        @"(?:[\w.-]*(?:password|passwd|passphrase|secret|token|key)|api[-_ ]key|x-password|authorization)";

    // "password": "two words", password='two words': a quoted value may hold spaces, and ends
    // only at the kind of quote it began with ("key": "it's value" is hidden whole).
    [GeneratedRegex(@"(\b" + Names + @"[""']?\s*[:=]\s*)([""'])(?:(?!\2)[^\r\n])*\2", RegexOptions.IgnoreCase)]
    private static partial Regex QuotedValue();

    // password=..., token=..., secret_key=..., X-Password: ..., Authorization: ... up to the next
    // separator. An Authorization scheme is left for SchemeValue to hide the credential of.
    [GeneratedRegex(@"(\b" + Names + @"""?\s*[:=]\s*)(?!(?:Bearer|Basic|Digest)\s)[^&\s"",;']+", RegexOptions.IgnoreCase)]
    private static partial Regex NamedValue();

    // Authorization: Basic xxx, and Bearer xxx anywhere (the scheme stays, the credential goes).
    [GeneratedRegex(@"((?:\bauthorization\s*[:=]\s*[""']?(?:Basic|Digest)|\bBearer)\s+)[^\s"",;']+", RegexOptions.IgnoreCase)]
    private static partial Regex SchemeValue();

    // A Basic credential with no header in front of it: base64 ends in = often, and the length
    // keeps this off the word "Basic" in prose.
    [GeneratedRegex(@"(\bBasic\s+)[A-Za-z0-9+/]{8,}={1,2}")]
    private static partial Regex BareBasic();

    [GeneratedRegex(@"(?<![\w.])(\d{1,3})\.(\d{1,3})\.(\d{1,3})\.(\d{1,3})(?!\w)(?!\.\d)")]
    private static partial Regex Ipv4();

    [GeneratedRegex(@"(?<![\w:.])(?:[0-9a-fA-F]{0,4}:){2,7}[0-9a-fA-F]{0,4}(?![\w:])")]
    private static partial Regex Ipv6();

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
                    else if (_pending.Length <= MaxStoredLine)
                    {
                        // One more than a kept line may be, so that a line cut here is seen to
                        // be too long and loses its last CutGuard characters in Keep above.
                        _pending.Append(c);
                    }
                }
            }
        }
    }
}
