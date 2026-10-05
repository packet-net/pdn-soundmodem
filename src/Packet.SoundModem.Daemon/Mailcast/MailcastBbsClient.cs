using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using Bbs.Fbb;
using Mailcast.Core;

namespace Packet.SoundModem.Daemon;

/// <summary>What the BBS said about one bulletin.</summary>
internal enum MailcastVerdict
{
    /// <summary>It asked for it (FS +), took it, and the session closed cleanly.</summary>
    Accepted,

    /// <summary>It already had the BID (FS -). Final.</summary>
    AlreadyHad,

    /// <summary>It asked for it later (FS =). Offered again later.</summary>
    Deferred,

    /// <summary>It refused it, or it cannot be offered at all (a BID over 12 characters). Final.</summary>
    Refused,

    /// <summary>It asked for it and the transfer went, but the session did not close cleanly, so
    /// whether it kept it is not known. Offered again; the BBS answers FS - if it did.</summary>
    Unconfirmed,

    /// <summary>The session ended before the BBS answered for it.</summary>
    NotOffered,
}

/// <summary>The BBS's answer for one bulletin.</summary>
internal sealed record MailcastOutcome(string Bid, MailcastVerdict Verdict, string? Detail = null);

/// <summary>How one forwarding session went.</summary>
/// <param name="Failure">Why it did not end cleanly, or null when it did.</param>
/// <param name="Outcomes">One per bulletin given, in order.</param>
/// <param name="OfferedBack">Messages the BBS tried to send this login, all answered "later".</param>
internal sealed record MailcastSession(string? Failure, IReadOnlyList<MailcastOutcome> Outcomes, int OfferedBack);

/// <summary>Something that runs one forwarding session; the real one is <see cref="MailcastBbsClient"/>.</summary>
internal interface IMailcastBbs
{
    /// <summary>Offers <paramref name="bulletins"/> in one session.</summary>
    Task<MailcastSession> DeliverAsync(IReadOnlyList<Bulletin> bulletins, CancellationToken cancellation);
}

/// <summary>
/// One FBB B1F forwarding session into the local BBS as its calling partner: connect, log in,
/// propose each bulletin, send those the BBS asks for, close.
/// </summary>
/// <remarks>
/// <para>The protocol is the FBB session state machine from M0LTE.Mailcast.Fbb (pdn-bbs's); this
/// is only the transport round it, the same job pdn-mailcast's receiver BbsClient does, which is
/// not in a library. Lines go out with CR LF, transfers raw, and everything that arrives is fed
/// back in.</para>
/// <para>It only ever sends. If the BBS has mail for this login and offers it when the turn
/// passes, every message is answered FS = ("later"), never FS - ("have it"), so the BBS keeps
/// it rather than marking it delivered. A message the session would have to answer FS - itself
/// (a TO over six characters), or a second round of them, is not answered at all: the
/// connection is closed and the BBS keeps the lot.</para>
/// <para>The password goes to the BBS and nowhere else: not into a log line, not into an
/// exception message.</para>
/// </remarks>
internal sealed partial class MailcastBbsClient : IMailcastBbs
{
    private readonly MailcastBbsConfig _settings;
    private readonly TimeProvider _time;

    internal MailcastBbsClient(MailcastBbsConfig settings, TimeProvider time)
    {
        _settings = settings;
        _time = time;
    }

    /// <summary>The longest BID an FBB proposal can carry.</summary>
    internal const int MaxBidLength = 12;

    /// <summary>How long the connection may take to open.</summary>
    internal TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>How long the BBS may say nothing before the session is given up.</summary>
    internal TimeSpan IdleTimeout { get; init; } = TimeSpan.FromMinutes(2);

    /// <summary>The longest a whole session may take.</summary>
    internal TimeSpan SessionTimeout { get; init; } = TimeSpan.FromMinutes(15);

    /// <inheritdoc />
    public async Task<MailcastSession> DeliverAsync(IReadOnlyList<Bulletin> bulletins, CancellationToken cancellation)
    {
        var outcomes = new Dictionary<string, MailcastOutcome>(StringComparer.OrdinalIgnoreCase);
        var offerable = new List<Bulletin>();
        foreach (Bulletin bulletin in bulletins)
        {
            if (bulletin.Bid.Length > MaxBidLength)
            {
                outcomes[bulletin.Bid] = new(bulletin.Bid, MailcastVerdict.Refused, $"its BID is longer than FBB's {MaxBidLength} characters");
            }
            else
            {
                offerable.Add(bulletin);
            }
        }

        var run = new Run(this, offerable);
        string? failure = null;
        if (offerable.Count > 0)
        {
            using var whole = new Deadline(SessionTimeout, _time, cancellation);
            try
            {
                await run.ExecuteAsync(whole.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
            {
                failure = whole.TimedOut
                    ? $"the session was still going after {SessionTimeout.TotalMinutes:F0} min"
                    : "the BBS stopped answering";
            }
            catch (SessionFailed e)
            {
                failure = e.Message;
            }
            catch (Exception e) when (e is SocketException or IOException)
            {
                failure = MailcastOnAir.Ascii(e.Message);
            }
            catch (FbbProtocolException e)
            {
                failure = "protocol error: " + MailcastOnAir.Ascii(e.Message);
            }
        }

        bool graceful = run.Graceful && failure is null;
        foreach (Bulletin bulletin in offerable)
        {
            outcomes[bulletin.Bid] = !run.Answers.TryGetValue(bulletin.Bid, out FsAnswer? answer)
                ? new(bulletin.Bid, MailcastVerdict.NotOffered)
                : answer.Kind switch
                {
                    FsAnswerKind.Accept => graceful
                        ? new(bulletin.Bid, MailcastVerdict.Accepted)
                        : new(bulletin.Bid, MailcastVerdict.Unconfirmed, "the session did not close cleanly after the transfer"),
                    FsAnswerKind.AlreadyHave => new(bulletin.Bid, MailcastVerdict.AlreadyHad),
                    FsAnswerKind.Defer => new(bulletin.Bid, MailcastVerdict.Deferred),
                    _ => new(bulletin.Bid, MailcastVerdict.Refused, $"the BBS answered {answer.Kind}"),
                };
        }

        if (failure is null && !graceful && offerable.Count > 0)
        {
            failure = run.Failure ?? "the session ended early";
        }

        return new MailcastSession(failure, [.. bulletins.Select(b => outcomes[b.Bid])], run.OfferedBack);
    }

    /// <summary>The message as a partner sends it: from, @BBS (the BBS's own call when the
    /// bulletin has none), to, BID, title, and the R: lines and body as text.</summary>
    internal static FbbOutboundMessage ToOutbound(Bulletin bulletin, string fallbackAt) => new()
    {
        MessageType = bulletin.Type is 'P' or 'B' or 'T' ? bulletin.Type : 'B',
        From = FaProposal.NormalizeCallsign(bulletin.From),
        AtBbs = bulletin.At.Length > 0 ? (bulletin.At.Length > 40 ? bulletin.At[..40] : bulletin.At) : fallbackAt,
        To = FaProposal.NormalizeCallsign(bulletin.To),
        Bid = bulletin.Bid,
        Title = bulletin.Title.Replace('\0', ' '),
        Body = Bulletin.TextEncoding.GetBytes(bulletin.MessageText),
    };

    [GeneratedRegex(@"de ([A-Za-z0-9]+(?:-[0-9]+)?)\s*>")]
    private static partial Regex PromptCall();

    private string LoginHint() => _settings.IsLinBpq
        ? $"check that \"{_settings.Login}\" and its password match a USER= line in LinBPQ's Telnet port, that the port is its FBBPORT, and that \"{_settings.Command}\" reaches the mail application"
        : $"check that {_settings.Login} is a BBS user on FBB with this password";

    /// <summary>Cancelled by the caller, or once <c>timeout</c> has passed on the station's clock.</summary>
    private sealed class Deadline : IDisposable
    {
        private readonly CancellationTokenSource _timer;
        private readonly CancellationTokenSource _linked;

        internal Deadline(TimeSpan timeout, TimeProvider time, CancellationToken cancellation)
        {
            _timer = new CancellationTokenSource(timeout, time);
            _linked = CancellationTokenSource.CreateLinkedTokenSource(cancellation, _timer.Token);
        }

        internal CancellationToken Token => _linked.Token;

        internal bool TimedOut => _timer.IsCancellationRequested;

        public void Dispose()
        {
            _linked.Dispose();
            _timer.Dispose();
        }
    }

    /// <summary>A failure worth a sentence of its own.</summary>
    private sealed class SessionFailed(string message) : Exception(message);

    /// <summary>One session's state.</summary>
    private sealed class Run(MailcastBbsClient owner, IReadOnlyList<Bulletin> bulletins)
    {
        private readonly List<byte> _beforeSession = [];
        private readonly Dictionary<FbbOutboundMessage, string> _bids = new(ReferenceEqualityComparer.Instance);
        private FbbSession? _session;
        private NetworkStream? _stream;
        private int _rounds;
        private bool _over;

        internal Dictionary<string, FsAnswer> Answers { get; } = new(StringComparer.OrdinalIgnoreCase);

        internal bool Graceful { get; private set; }

        internal string? Failure { get; private set; }

        internal int OfferedBack { get; private set; }

        internal async Task ExecuteAsync(CancellationToken cancellation)
        {
            MailcastBbsConfig settings = owner._settings;
            using var client = new TcpClient();
            using (var connect = Idle(owner.ConnectTimeout, cancellation))
            {
                try
                {
                    await client.ConnectAsync(settings.Host, settings.Port, connect.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
                {
                    throw new SessionFailed($"no answer from {settings.Host}:{settings.Port} within {owner.ConnectTimeout.TotalSeconds:F0} s");
                }
            }

            client.NoDelay = true;
            _stream = client.GetStream();
            await FeedAsync(await LogInAsync(cancellation).ConfigureAwait(false), cancellation).ConfigureAwait(false);

            var buffer = new byte[4096];
            while (!_over)
            {
                int read;
                using (var idle = Idle(owner.IdleTimeout, cancellation))
                {
                    read = await _stream.ReadAsync(buffer, idle.Token).ConfigureAwait(false);
                }

                if (read == 0)
                {
                    Failure ??= _session is null
                        ? "the BBS closed the connection before it offered a forwarding session; " + owner.LoginHint()
                        : "the BBS closed the connection";
                    return;
                }

                await FeedAsync(buffer.AsMemory(0, read).ToArray(), cancellation).ConfigureAwait(false);
            }
        }

        private Deadline Idle(TimeSpan timeout, CancellationToken cancellation) => new(timeout, owner._time, cancellation);

        /// <summary>Logs in, returning whatever arrived after the last prompt.</summary>
        private async Task<byte[]> LogInAsync(CancellationToken cancellation)
        {
            MailcastBbsConfig settings = owner._settings;
            if (settings.IsLinBpq)
            {
                // LinBPQ's FBBPORT sends no prompts: user, password, then the node command for
                // the mail application, whose answer the session reads.
                await SendAsync(settings.Login + "\r" + settings.Password + "\r"
                    + (settings.Command.Length > 0 ? settings.Command + "\r" : ""), cancellation).ConfigureAwait(false);
                return [];
            }

            // FBB prompts for both. The leading dot asks for a binary session (drv_tcp.c,
            // tcp_check_call), without which FBB reads 0xFF in a transfer as telnet.
            byte[] rest = await ExpectAsync("allsign", [], cancellation).ConfigureAwait(false);
            await SendAsync("." + settings.Login + "\r", cancellation).ConfigureAwait(false);
            rest = await ExpectAsync("assword", rest, cancellation).ConfigureAwait(false);
            await SendAsync(settings.Password + "\r", cancellation).ConfigureAwait(false);
            return rest;
        }

        private async Task<byte[]> ExpectAsync(string prompt, byte[] already, CancellationToken cancellation)
        {
            var seen = new List<byte>(already);
            var buffer = new byte[1024];
            while (true)
            {
                string text = Encoding.Latin1.GetString([.. seen]);
                int at = text.IndexOf(prompt, StringComparison.OrdinalIgnoreCase);
                int colon = at < 0 ? -1 : text.IndexOf(':', at);
                if (colon >= 0)
                {
                    return [.. seen.Skip(colon + 1)];
                }

                foreach (string refusal in (ReadOnlySpan<string>)["Invalid callsign", "Unregistered callsign", "Callsign error", "Password error", "Bad password"])
                {
                    if (text.Contains(refusal, StringComparison.OrdinalIgnoreCase))
                    {
                        throw new SessionFailed($"FBB refused the login ({refusal}); " + owner.LoginHint());
                    }
                }

                int read;
                using (var idle = Idle(owner.IdleTimeout, cancellation))
                {
                    read = await _stream!.ReadAsync(buffer, idle.Token).ConfigureAwait(false);
                }

                if (read == 0)
                {
                    throw new SessionFailed($"FBB closed the connection while waiting for \"{prompt}\"; " + owner.LoginHint());
                }

                seen.AddRange(buffer.AsSpan(0, read));
            }
        }

        /// <summary>
        /// Feeds what arrived to the session, which is made once the BBS's SID and prompt are in:
        /// the prompt's callsign stands in for a bulletin's empty @ field.
        /// </summary>
        private async Task FeedAsync(byte[] data, CancellationToken cancellation)
        {
            if (_session is null)
            {
                _beforeSession.AddRange(data);
                string text = Encoding.Latin1.GetString([.. _beforeSession]);
                int sid = text.IndexOf('[', StringComparison.Ordinal);
                if (sid < 0 || !text.AsSpan(sid).TrimEnd().EndsWith(">", StringComparison.Ordinal))
                {
                    if (owner._settings.IsLinBpq
                        && (text.Contains("user:", StringComparison.OrdinalIgnoreCase) || text.Contains("password:", StringComparison.OrdinalIgnoreCase)))
                    {
                        throw new SessionFailed("LinBPQ refused the login; " + owner.LoginHint());
                    }

                    if (text.Contains("Invalid command", StringComparison.OrdinalIgnoreCase))
                    {
                        throw new SessionFailed($"LinBPQ did not know the command \"{owner._settings.Command}\"; " + owner.LoginHint());
                    }

                    return;
                }

                Match call = PromptCall().Match(text, sid);
                string fallbackAt = call.Success ? call.Groups[1].Value.ToUpperInvariant() : "WW";
                var messages = new List<FbbOutboundMessage>();
                foreach (Bulletin bulletin in bulletins)
                {
                    FbbOutboundMessage message = ToOutbound(bulletin, fallbackAt);
                    _bids[message] = bulletin.Bid;
                    messages.Add(message);
                }

                _session = new FbbSession(
                    new FbbSessionConfig
                    {
                        Role = FbbRole.Caller,
                        OwnCallsign = owner._settings.Login.ToUpperInvariant(),
                        SidVersion = "SOUNDMODEM" + SidVersion(),
                    },
                    messages);
                await ApplyAsync(_session.Advance(new FbbStart()), cancellation).ConfigureAwait(false);
                data = [.. _beforeSession];
                _beforeSession.Clear();
            }

            await ApplyAsync(_session.Advance(new FbbPeerData(data)), cancellation).ConfigureAwait(false);
        }

        private static string SidVersion()
        {
            // The SID's version field must hold no '-', so a prerelease suffix is cut off.
            string version = Packet.SoundModem.Waterfall.DaemonVersion.Version;
            int cut = version.IndexOfAny(['-', '+', ' ']);
            return cut > 0 ? version[..cut] : version.Length > 0 ? version : "0";
        }

        private async Task ApplyAsync(IReadOnlyList<FbbAction> actions, CancellationToken cancellation)
        {
            var queue = new Queue<FbbAction>(actions);
            while (queue.Count > 0)
            {
                switch (queue.Dequeue())
                {
                    case FbbSendLine line:
                        await SendAsync(line.Line + "\r\n", cancellation).ConfigureAwait(false);
                        break;
                    case FbbSendBytes bytes:
                        await WriteAsync(bytes.Data, cancellation).ConfigureAwait(false);
                        break;
                    case FbbOutboundResult result:
                        Answers[_bids[result.Message]] = result.Answer;
                        break;
                    case FbbProposalsReceived proposals:
                        // Never FS -: that would tell the BBS this login has them, and it would
                        // mark them delivered.
                        OfferedBack += proposals.Proposals.Count;
                        if (proposals.Proposals.Any(p => p is FaProposal { RequiresPoliteReject: true }) || ++_rounds > 1)
                        {
                            Failure = _rounds > 1
                                ? "the BBS kept offering mail to the receiver's login; hung up"
                                : "the BBS offered the receiver's login a message it cannot decline without saying it has it; hung up";
                            _over = true;
                            return;
                        }

                        foreach (FbbAction next in _session!.Advance(
                            new FbbProposalDecisions([.. proposals.Proposals.Select(_ => FsAnswer.Defer)])))
                        {
                            queue.Enqueue(next);
                        }

                        break;
                    case FbbProtocolError error:
                        Failure = Explain(error.ErrorLine);
                        break;
                    case FbbSessionOver over:
                        _over = true;
                        Graceful = over.Graceful;
                        break;
                    default:
                        break;
                }
            }
        }

        private string Explain(string errorLine)
        {
            Sid? sid = _session?.PeerSid;
            if (sid is not null && (!sid.SupportsBlockedFbb || !sid.SupportsCompression))
            {
                return $"the BBS offered {MailcastOnAir.Ascii(sid.Raw)}, which is not compressed FBB forwarding. "
                    + (owner._settings.IsLinBpq
                        ? $"In LinBPQ's mail configuration make {owner._settings.Login} a BBS user and tick Allow Blocked, Allow Compressed and Use B1 on its forwarding page"
                        : $"In FBB, give {owner._settings.Login} the BBS flag");
            }

            return "protocol error: " + MailcastOnAir.Ascii(errorLine);
        }

        private Task SendAsync(string text, CancellationToken cancellation) =>
            WriteAsync(Encoding.Latin1.GetBytes(text), cancellation);

        private async Task WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellation)
        {
            using var idle = Idle(owner.IdleTimeout, cancellation);
            await _stream!.WriteAsync(data, idle.Token).ConfigureAwait(false);
        }
    }
}
