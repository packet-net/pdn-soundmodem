using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Packet.Fbb;
using Packet.Mailcast;
using Packet.SoundModem.Modems;

namespace Packet.SoundModem.Tests.Mailcast;

/// <summary>A message the fake BBS took.</summary>
internal sealed record TakenMessage(string Bid, string Title, string Body);

/// <summary>
/// A BBS on a loopback port that answers like LinBPQ's FBBPORT: it reads the user, the password
/// and the application command, then forwards as the answering partner, with the FBB session
/// from Packet.Fbb.
/// </summary>
internal sealed class FakeFbbBbs : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _accepting;

    internal FakeFbbBbs()
    {
        _listener.Start();
        _accepting = AcceptAsync();
    }

    internal int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    internal string Password { get; init; } = "secret";

    /// <summary>BIDs the BBS already has: answered FS -.</summary>
    internal ConcurrentDictionary<string, bool> Known { get; } = new(StringComparer.OrdinalIgnoreCase);

    internal ConcurrentQueue<TakenMessage> Taken { get; } = new();

    internal ConcurrentQueue<string[]> Logins { get; } = new();

    /// <summary>Messages the BBS offers back when the turn passes to it.</summary>
    internal List<FbbOutboundMessage> Queued { get; } = [];

    /// <summary>The receiver's answers to <see cref="Queued"/>.</summary>
    internal ConcurrentQueue<FsAnswerKind> ReverseAnswers { get; } = new();

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        _listener.Stop();
        await _accepting;
        _stop.Dispose();
    }

    private async Task AcceptAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_stop.Token);
            }
            catch (Exception e) when (e is OperationCanceledException or SocketException or ObjectDisposedException)
            {
                return;
            }

            _ = Task.Run(() => ServeAsync(client));
        }
    }

    private async Task ServeAsync(TcpClient client)
    {
        using (client)
        {
            NetworkStream stream = client.GetStream();
            var buffer = new byte[4096];
            var pending = new List<byte>();
            var lines = new List<string>();
            try
            {
                while (lines.Count < 3)
                {
                    int read = await stream.ReadAsync(buffer, _stop.Token);
                    if (read == 0)
                    {
                        return;
                    }

                    pending.AddRange(buffer.AsSpan(0, read));
                    int cr;
                    while (lines.Count < 3 && (cr = pending.IndexOf((byte)'\r')) >= 0)
                    {
                        lines.Add(Encoding.Latin1.GetString([.. pending.Take(cr)]));
                        pending.RemoveRange(0, cr + 1);
                    }
                }

                Logins.Enqueue([.. lines]);
                if (lines[1] != Password)
                {
                    await stream.WriteAsync(Encoding.Latin1.GetBytes("password:"), _stop.Token);
                    return;
                }

                await stream.WriteAsync(Encoding.Latin1.GetBytes("TST:GB7TST} Connected to BBS\r"), _stop.Token);
                var session = new FbbSession(new FbbSessionConfig { Role = FbbRole.Answerer, OwnCallsign = "GB7TST" }, Queued);
                await ApplyAsync(stream, session, session.Advance(new FbbStart()));
                if (pending.Count > 0)
                {
                    await ApplyAsync(stream, session, session.Advance(new FbbPeerData(pending.ToArray())));
                }

                while (session.Phase is not (FbbSessionPhase.Finished or FbbSessionPhase.Failed))
                {
                    int read = await stream.ReadAsync(buffer, _stop.Token);
                    if (read == 0)
                    {
                        return;
                    }

                    await ApplyAsync(stream, session, session.Advance(new FbbPeerData(buffer.AsMemory(0, read).ToArray())));
                }
            }
            catch (Exception e) when (e is IOException or OperationCanceledException or SocketException)
            {
            }
        }
    }

    private async Task ApplyAsync(NetworkStream stream, FbbSession session, IReadOnlyList<FbbAction> actions)
    {
        var queue = new Queue<FbbAction>(actions);
        while (queue.Count > 0)
        {
            switch (queue.Dequeue())
            {
                case FbbSendLine line:
                    await stream.WriteAsync(Encoding.Latin1.GetBytes(line.Line + "\r"), _stop.Token);
                    break;
                case FbbSendBytes bytes:
                    await stream.WriteAsync(bytes.Data, _stop.Token);
                    break;
                case FbbProposalsReceived proposals:
                    var answers = proposals.Proposals
                        .Select(p => p is FaProposal fa && Known.ContainsKey(fa.Bid) ? FsAnswer.AlreadyHave : FsAnswer.Accept)
                        .ToList();
                    foreach (FbbAction next in session.Advance(new FbbProposalDecisions(answers)))
                    {
                        queue.Enqueue(next);
                    }

                    break;
                case FbbMessageDelivered delivered:
                    string bid = delivered.Proposal is FaProposal taken ? taken.Bid : "?";
                    Taken.Enqueue(new TakenMessage(bid, delivered.Title, Encoding.Latin1.GetString(delivered.Body.Span)));
                    Known[bid] = true;
                    break;
                case FbbOutboundResult result:
                    ReverseAnswers.Enqueue(result.Answer.Kind);
                    break;
                default:
                    break;
            }
        }
    }
}

/// <summary>
/// A slot as a station hears it, made without a radio: GB7RDG's frames planned by Packet.Mailcast's
/// own scheduler, each modulated as its own MS110D WN4 burst, after the 10 s opening tone, in
/// seeded Gaussian noise.
/// </summary>
internal static class MailcastSlotAudio
{
    internal const int Rate = 48000;

    internal static readonly DateTimeOffset Noon = new(2026, 10, 5, 12, 0, 0, TimeSpan.Zero);

    /// <summary>Two small bulletins, in GB7RDG's format.</summary>
    internal static IReadOnlyList<Bulletin> Bulletins() =>
    [
        Bulletin.FromMessageText('B', "G8ABC", "ALL", "GBR", "1001_GB7ABC", "Net tonight", Noon.AddHours(-3),
            "R:261005/0900Z 1001@GB7ABC.#38.GBR.EURO\r\n\r\nThe net is at 1930 on 145.500 tonight.\r\n73 de G8ABC\r\n"),
        Bulletin.FromMessageText('B', "M0XYZ", "SALE", "WW", "2002_GB7XYZ", "For sale: a TS-50", Noon.AddHours(-2),
            "R:261005/1000Z 2002@GB7XYZ.#42.GBR.EURO\r\n\r\nA TS-50 with its mic and manual, offers please.\r\n"),
    ];

    /// <summary>The slot's frames as AX.25 UI frames from GB7RDG to MCAST, in sending order.</summary>
    internal static IReadOnlyList<byte[]> Frames(IReadOnlyList<Bulletin>? bulletins = null)
    {
        ScheduleOptions options = ScheduleOptions.HourlyDaylight with
        {
            Timetable = new SlotTimetable(new TimeOnly(0, 0), 60, DaylightRule.Gb7rdg),
            SymbolSize = 240,
        };
        SlotBroadcast slot = BroadcastScheduler.Plan(
            (bulletins ?? Bulletins()).Select(b => new BroadcastBulletin(b, DateOnly.FromDateTime(Noon.UtcDateTime), Noon)),
            Noon, seed: 7, Compression.Default, options);
        return [.. slot.Frames.Select(f => Ui("GB7RDG", "MCAST", f.ToBytes()))];
    }

    /// <summary>An AX.25 UI frame, PID F0.</summary>
    internal static byte[] Ui(string source, string destination, ReadOnlySpan<byte> payload)
    {
        var frame = new List<byte>();
        frame.AddRange(Address(destination, last: false));
        frame.AddRange(Address(source, last: true));
        frame.Add(0x03);
        frame.Add(0xF0);
        frame.AddRange(payload.ToArray());
        return [.. frame];
    }

    /// <summary>An address field for <paramref name="call"/>, which may carry an SSID ("M0LTE-7").</summary>
    private static byte[] Address(string call, bool last)
    {
        var field = new byte[7];
        string[] parts = call.Split('-');
        int ssid = parts.Length > 1 ? int.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture) : 0;
        string padded = parts[0].PadRight(6);
        for (int i = 0; i < 6; i++)
        {
            field[i] = (byte)(padded[i] << 1);
        }

        field[6] = (byte)(0x60 | (ssid << 1) | (last ? 1 : 0));
        return field;
    }

    /// <summary>
    /// The slot's audio: 2 s of noise, the tone at <paramref name="centreHz"/> for 10 s, a gap,
    /// then each frame's burst with 0.5 s between, in noise <paramref name="snrDb"/> under the
    /// bursts in 3 kHz.
    /// </summary>
    internal static float[] Render(IReadOnlyList<byte[]> frames, double centreHz = 1800, double snrDb = 20, int seed = 3, double toneOffsetHz = 0)
    {
        IModem modem = ModemCatalog.Create(
            "ms110d-wn4", Rate, static _ => { },
            Math.Abs(centreHz - 1800) < 0.5 ? default : new ModemOptions(CentreFrequencyHz: centreHz));
        var bursts = frames.Select(f => modem.Modulate(f, 0)).ToList();
        double power = bursts.Average(b => b.Average(s => (double)s * s));
        var audio = new List<float>();
        void Silence(double seconds) => audio.AddRange(new float[(int)(seconds * Rate)]);

        Silence(2);
        double amplitude = Math.Sqrt(2 * power);
        double toneHz = centreHz + toneOffsetHz;
        for (int i = 0; i < 10 * Rate; i++)
        {
            audio.Add((float)(amplitude * Math.Sin(2 * Math.PI * toneHz * i / Rate)));
        }

        Silence(3);
        foreach (float[] burst in bursts)
        {
            audio.AddRange(burst);
            Silence(0.5);
        }

        Silence(2);
        double sigma = Math.Sqrt(power / Math.Pow(10, snrDb / 10) * (Rate / 2.0 / 3000.0));
        var random = new Random(seed);
        var samples = new float[audio.Count];
        for (int i = 0; i < samples.Length; i++)
        {
            double u1 = 1.0 - random.NextDouble();
            double u2 = random.NextDouble();
            samples[i] = (float)(audio[i] + (sigma * Math.Sqrt(-2 * Math.Log(u1)) * Math.Cos(2 * Math.PI * u2)));
        }

        return samples;
    }
}
