using System.Net;
using System.Net.Sockets;
using Packet.SoundModem.RtlTcp;

namespace Packet.SoundModem.Tests.RtlTcp;

/// <summary>
/// An rtl_tcp server on loopback for the tests: one client at a time, as rtl_tcp serves; sends
/// the header (or not) as each client connects, records every command it is sent, and streams
/// whatever bytes the test gives it. It can drop the client, go silent, or stop listening.
/// </summary>
internal sealed class FakeRtlTcpServer : IDisposable
{
    /// <summary>
    /// The longest a test here waits for something that happens at once over loopback: a
    /// connection, a command, a byte. Only a hang runs it out, and it is also what the tests give
    /// the source's own connect and no-data timeouts when those are not what is under test.
    /// </summary>
    /// <remarks>The waits were 10 s, polled, and a loaded CI runner went past that (#712): the
    /// quiet-server test waited 10 s for a reconnect that takes a second and a handful of thread
    /// pool hops, while the MS110D fade tests had the machine. Nothing waits this long when the
    /// source is working.</remarks>
    public static readonly TimeSpan HangGuard = TimeSpan.FromSeconds(60);

    private readonly TcpListener _listener;
    private readonly object _gate = new();
    private readonly List<(int Connection, RtlTcpCommand Command, uint Parameter)> _commands = [];
    private readonly CancellationTokenSource _stopping = new();
    private readonly Task _accepting;
    private TcpClient? _client;
    private int _connections;
    private long _bytesFromClient;

    public FakeRtlTcpServer(RtlTcpHeader? header = null)
    {
        Header = header ?? new RtlTcpHeader(RtlTcpTuner.R820T, 29);
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _accepting = Task.Run(AcceptLoop);
    }

    public int Port { get; }

    public RtlTcpEndpoint Endpoint => new("127.0.0.1", Port);

    /// <summary>The header sent to each client; null sends none (samples from the first byte).</summary>
    public RtlTcpHeader? Header { get; set; }

    public int Connections => Volatile.Read(ref _connections);

    /// <summary>Takes each new connection, sends the header, and closes it at once.</summary>
    public bool CloseOnAccept { get; set; }

    public long BytesFromClient => Interlocked.Read(ref _bytesFromClient);

    public IReadOnlyList<(int Connection, RtlTcpCommand Command, uint Parameter)> Commands
    {
        get
        {
            lock (_gate)
            {
                return [.. _commands];
            }
        }
    }

    /// <summary>Sends <paramref name="bytes"/> to the connected client.</summary>
    public void Send(ReadOnlySpan<byte> bytes)
    {
        TcpClient client;
        lock (_gate)
        {
            client = _client ?? throw new InvalidOperationException("no client is connected");
        }

        client.GetStream().Write(bytes);
    }

    /// <summary>Closes the connection to the current client; the listener stays up.</summary>
    public void DropClient()
    {
        lock (_gate)
        {
            _client?.Dispose();
            _client = null;
        }
    }

    /// <summary>Stops listening and drops the client: the server is gone.</summary>
    public void Stop()
    {
        _stopping.Cancel();
        _listener.Stop();
        DropClient();
    }

    /// <summary>Waits until connection <paramref name="count"/> has been taken and is still the
    /// current client; woken by the accept, not polled, and bounded only by <see cref="HangGuard"/>.</summary>
    public void WaitForConnections(int count) =>
        WaitFor(() => _connections >= count && _client is not null, () => $"waited for connection {count}; have {Connections}");

    /// <summary>Waits until <paramref name="count"/> commands have arrived, in the same way.</summary>
    public void WaitForCommands(int count) =>
        WaitFor(() => _commands.Count >= count, () => $"waited for {count} commands; have {Commands.Count}");

    /// <summary>Waits under the lock for <paramref name="condition"/>, which every change it reads
    /// pulses.</summary>
    private void WaitFor(Func<bool> condition, Func<string> failure)
    {
        long giveUp = Environment.TickCount64 + (long)HangGuard.TotalMilliseconds;
        lock (_gate)
        {
            while (!condition())
            {
                long left = giveUp - Environment.TickCount64;
                if (left <= 0)
                {
                    throw new TimeoutException(failure());
                }

                System.Threading.Monitor.Wait(_gate, (int)left);
            }
        }
    }

    public void Dispose()
    {
        Stop();
        try
        {
            _accepting.Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException)
        {
        }
    }

    private async Task AcceptLoop()
    {
        while (!_stopping.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_stopping.Token);
            }
            catch (Exception e) when (e is OperationCanceledException or SocketException or ObjectDisposedException)
            {
                return;
            }

            client.NoDelay = true;
            if (Header is RtlTcpHeader header)
            {
                var bytes = new byte[RtlTcpProtocol.HeaderLength];
                RtlTcpProtocol.WriteHeader(bytes, header);
                try
                {
                    await client.GetStream().WriteAsync(bytes);
                }
                catch (Exception e) when (e is IOException or SocketException or ObjectDisposedException)
                {
                    // A client that gave up waiting for this accept and closed its end: rtl_tcp
                    // would take the next one, and so does this, rather than ending the loop.
                    client.Dispose();
                    continue;
                }
            }

            if (CloseOnAccept)
            {
                lock (_gate)
                {
                    _connections++;
                    System.Threading.Monitor.PulseAll(_gate);
                }

                client.Dispose();
                continue;
            }

            int connection;
            lock (_gate)
            {
                _client?.Dispose();
                _client = client;
                connection = ++_connections;
                System.Threading.Monitor.PulseAll(_gate);
            }

            _ = Task.Run(() => ReadCommands(client, connection));
        }
    }

    private async Task ReadCommands(TcpClient client, int connection)
    {
        var buffer = new byte[RtlTcpProtocol.CommandLength];
        try
        {
            NetworkStream stream = client.GetStream();
            while (true)
            {
                await stream.ReadExactlyAsync(buffer);
                Interlocked.Add(ref _bytesFromClient, buffer.Length);
                (RtlTcpCommand command, uint parameter) = RtlTcpProtocol.Decode(buffer);
                lock (_gate)
                {
                    _commands.Add((connection, command, parameter));
                    System.Threading.Monitor.PulseAll(_gate);
                }
            }
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException or EndOfStreamException or SocketException or InvalidOperationException)
        {
        }
    }
}
