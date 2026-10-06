using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using Packet.SoundModem.Channel;
using Packet.SoundModem.Kiss;

namespace Packet.SoundModem.Tests.Kiss;

/// <summary>
/// A test's end of a KISS-over-TCP session, which waits on things the server does rather than on
/// time going by.
/// </summary>
/// <remarks>
/// <para>Two waits these tests used to make on the wall clock are made on the server instead.
/// A connect returns once the kernel has finished the handshake, which is before the server's
/// accept loop has registered the session, and a frame received in between is broadcast to
/// nobody. On a loaded machine that gap is long enough to lose a whole burst, so
/// <see cref="ConnectAsync"/> returns only once the server has raised
/// <see cref="KissTcpServer.ClientConnected"/> for this connection. And "nothing more is coming"
/// used to be a read left running for a fixed window, which a slow run can also outlast.
/// <see cref="FenceAsync"/> sends an ACKMODE request with no data instead: the server answers
/// that at once, on the same queue and behind everything it has already queued for this session,
/// so its answer arriving proves every frame queued before it has been read.</para>
/// <para>None of this waits on <see cref="KissTestWait.HangGuard"/> unless the server has stopped
/// answering; the guard only turns that hang into a failure that says what it was waiting for.</para>
/// </remarks>
internal sealed class KissTestClient : IDisposable
{
    /// <summary>The first byte of a fence's ACKMODE id, unlike any id a test sends itself.</summary>
    private const byte FenceTag = 0xFD;

    private readonly KissDecoder _decoder;
    private readonly List<KissFrame> _frames = [];
    private readonly List<byte[]> _fenceIds = [];
    private readonly byte[] _buffer = new byte[4096];
    private int _fenceCount;

    private KissTestClient(TcpClient client, int maxFrame)
    {
        Client = client;
        Stream = client.GetStream();
        _decoder = new KissDecoder(_frames.Add, maxFrame);
    }

    public TcpClient Client { get; }

    public NetworkStream Stream { get; }

    /// <summary>The port this end connected from, which is how the server's events name it.</summary>
    public int LocalPort => ((IPEndPoint)Client.Client.LocalEndPoint!).Port;

    /// <summary>Everything the server has sent, fence answers included, in order.</summary>
    public IReadOnlyList<KissFrame> Frames => _frames;

    /// <summary>
    /// Connects to <paramref name="server"/> and returns once the server holds the session, so a
    /// frame received from then on is certain to be offered to it.
    /// </summary>
    /// <param name="server">The server to connect to.</param>
    /// <param name="receiveBufferBytes">The socket's receive buffer, set before connecting so the
    /// window is pinned from the start; the kernel's default when null.</param>
    /// <param name="maxFrame">The largest frame this end decodes.</param>
    public static async Task<KissTestClient> ConnectAsync(
        KissTcpServer server, int? receiveBufferBytes = null, int maxFrame = KissDecoder.DefaultMaxFrame)
    {
        // Recorded from before the connect: the accept can be raised before ConnectAsync returns,
        // and this client's port is not known until it has.
        // Not disposed: another session's accept may still be raising this handler as it is
        // removed, and a throw from it would end the server's accept loop.
        var accepted = new ConcurrentDictionary<int, bool>();
        var changed = new SemaphoreSlim(0);
        void OnConnected(KissClientEvent connected)
        {
            if (connected.Remote is IPEndPoint remote)
            {
                accepted[remote.Port] = true;
                changed.Release();
            }
        }

        server.ClientConnected += OnConnected;
        var client = new TcpClient();
        if (receiveBufferBytes is int bytes)
        {
            client.Client.ReceiveBufferSize = bytes;
        }

        try
        {
            await client.ConnectAsync(IPAddress.Loopback, server.LocalPort);
            int port = ((IPEndPoint)client.Client.LocalEndPoint!).Port;
            using var guard = new CancellationTokenSource(KissTestWait.HangGuard);
            while (!accepted.ContainsKey(port))
            {
                try
                {
                    await changed.WaitAsync(guard.Token);
                }
                catch (OperationCanceledException)
                {
                    throw KissTestWait.Hung($"the server to accept the session from port {port}");
                }
            }

            return new KissTestClient(client, maxFrame);
        }
        catch
        {
            client.Dispose();
            throw;
        }
        finally
        {
            server.ClientConnected -= OnConnected;
        }
    }

    public async Task SendAsync(KissFrame frame) => await Stream.WriteAsync(KissCodec.Encode(frame));

    /// <summary>Reads until <paramref name="complete"/> holds over everything read so far.</summary>
    public async Task ReadUntilAsync(Func<IReadOnlyList<KissFrame>, bool> complete, string what)
    {
        using var guard = new CancellationTokenSource(KissTestWait.HangGuard);
        while (!complete(_frames))
        {
            int got;
            try
            {
                got = await Stream.ReadAsync(_buffer, guard.Token);
            }
            catch (OperationCanceledException)
            {
                throw KissTestWait.Hung(what);
            }

            if (got == 0)
            {
                throw new IOException($"connection closed while waiting for {what}");
            }

            // Every frame in a read is kept, and a partial frame carries over to the next one,
            // so an extra or duplicated frame is seen rather than hidden behind the first.
            _decoder.Push(_buffer.AsSpan(0, got));
        }
    }

    /// <summary>Sends an ACKMODE request carrying only <paramref name="id"/> and reads until
    /// its answer, which the server sends behind everything already queued for this session.</summary>
    public async Task ReadThroughMarkerAsync(byte[] id)
    {
        int already = _frames.Count;
        await SendAsync(new KissFrame(0, KissCommand.AckModeData, id));
        await ReadUntilAsync(
            frames => frames.Skip(already).Any(frame => IsAnswerTo(frame, id)),
            $"the answer to marker {Convert.ToHexString(id)}");
    }

    /// <summary>
    /// Every frame the server had queued for this session by the time this was called, fence
    /// answers left out: what a read left running for ever would have collected, without the wait.
    /// </summary>
    public async Task<List<KissFrame>> FenceAsync()
    {
        byte[] id = [FenceTag, (byte)_fenceCount++];
        _fenceIds.Add(id);
        await ReadThroughMarkerAsync(id);
        return [.. _frames.Where(frame => !_fenceIds.Any(fence => IsAnswerTo(frame, fence)))];
    }

    private static bool IsAnswerTo(KissFrame frame, byte[] id) =>
        frame.Command == KissCommand.AckModeData && frame.Payload.SequenceEqual(id);

    public void Dispose() => Client.Dispose();
}

/// <summary>Waits on what the channel and the server raise, under a guard that only a hang reaches.</summary>
internal static class KissTestWait
{
    /// <summary>
    /// How long a wait goes before it is called a hang. Every wait it guards is on something the
    /// code under test raises, so a slow run only makes the wait longer; this is here so that a
    /// genuine hang fails naming what it waited for, rather than holding the run until a CI
    /// timeout says nothing useful.
    /// </summary>
    public static readonly TimeSpan HangGuard = TimeSpan.FromMinutes(1);

    public static TimeoutException Hung(string what) =>
        new($"nothing happened for {HangGuard.TotalSeconds:F0} s while waiting for {what}: a hang, not a slow run");

    /// <summary>Awaits <paramref name="task"/>, failing by name if it never completes.</summary>
    public static async Task Within(this Task task, string what)
    {
        try
        {
            await task.WaitAsync(HangGuard);
        }
        catch (TimeoutException)
        {
            throw Hung(what);
        }
    }

    /// <summary>Awaits <paramref name="task"/>, failing by name if it never completes.</summary>
    public static async Task<T> Within<T>(this Task<T> task, string what)
    {
        try
        {
            return await task.WaitAsync(HangGuard);
        }
        catch (TimeoutException)
        {
            throw Hung(what);
        }
    }

    /// <summary>
    /// Completes with the next frame <paramref name="channel"/> transmits. The channel raises
    /// <see cref="SoundModemChannel.FrameTransmitted"/> once the frame's audio has been written to
    /// the output, so a snapshot taken after this holds the whole frame. Subscribe before sending.
    /// </summary>
    public static Task<byte[]> NextTransmissionAsync(SoundModemChannel channel)
    {
        var sent = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnSent(int subChannel, byte[] frame)
        {
            channel.FrameTransmitted -= OnSent;
            sent.TrySetResult(frame);
        }

        channel.FrameTransmitted += OnSent;
        return sent.Task;
    }
}
