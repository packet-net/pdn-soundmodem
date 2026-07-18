using M0LTE.Pocsag;
using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Packet.SoundModem.Channel;

namespace Packet.SoundModem.Pocsag;

/// <summary>
/// The daemon's paging endpoint: a line-based TCP service (UTF-8, one command per line)
/// through which local clients submit POCSAG pages for transmission and hear pages
/// decoded off the channel. Pages are deliberately NOT exposed as KISS frames — paging
/// is a one-way medium carrying pages, not AX.25 — but transmission goes through the
/// same channel-access path as every other mode (CSMA, PTT, TXDELAY, sample-domain
/// TX-complete) via <see cref="SoundModemChannel.EnqueueTransmit(Func{int,float[]},Action{Exception}?)"/>.
/// </summary>
/// <remarks>
/// <para>Grammar (client → server; replies go to the submitting client only):</para>
/// <code>
/// PAGE &lt;ric&gt; &lt;function&gt; ALPHA &lt;text…&gt;      → OK &lt;id&gt;  |  ERR &lt;reason&gt;
/// PAGE &lt;ric&gt; &lt;function&gt; NUMERIC &lt;text…&gt;
/// PAGE &lt;ric&gt; &lt;function&gt; TONE
/// </code>
/// <para>&lt;ric&gt; is 0…2097151, &lt;function&gt; 0…3, text runs to end of line (max
/// 240 characters; ALPHA is 7-bit ASCII, NUMERIC the POCSAG numeric set). <c>OK</c>
/// means queued for transmission, not yet on air.</para>
/// <para>Server → every connected client, for each page the decoder hears on channel
/// (the label follows the function-bit convention — 0 reads as numeric, else alpha —
/// and control characters in decoded text are replaced with spaces):</para>
/// <code>
/// HEARD &lt;ric&gt; &lt;function&gt; ALPHA|NUMERIC &lt;text…&gt;
/// HEARD &lt;ric&gt; &lt;function&gt; TONE
/// </code>
/// </remarks>
public sealed class PagingTcpServer : IAsyncDisposable
{
    /// <summary>Longest accepted page text. (DAPNET itself runs to 80 characters;
    /// 240 keeps a page inside ~13 batches.)</summary>
    public const int MaxTextLength = 240;

    private readonly TcpListener _listener;
    private readonly SoundModemChannel _channel;
    private readonly PocsagEncoder _encoder;
    private readonly int _baud;
    private readonly ConcurrentDictionary<Guid, TcpClient> _clients = [];
    private readonly CancellationTokenSource _stopping = new();
    private Task? _acceptLoop;
    private int _nextId;

    /// <summary>Creates the paging service on <paramref name="channel"/>: registers the
    /// POCSAG decoder as a receive tap and transmits through the channel's CSMA/PTT
    /// path. Call <see cref="Start"/> to accept clients.</summary>
    /// <param name="channel">The audio channel to page through.</param>
    /// <param name="port">TCP listen port (0 = ephemeral, see <see cref="LocalPort"/>).</param>
    /// <param name="baud">POCSAG bit rate; 1200 is DAPNET's.</param>
    /// <param name="polarity">TX baseband polarity (the decoder auto-detects on RX).</param>
    /// <param name="bind">Bind address; loopback by default.</param>
    public PagingTcpServer(
        SoundModemChannel channel, int port = 8106, int baud = 1200,
        PocsagPolarity polarity = PocsagPolarity.Normal, IPAddress? bind = null)
    {
        ArgumentNullException.ThrowIfNull(channel);
        _channel = channel;
        _baud = baud;
        _encoder = new PocsagEncoder(channel.SampleRate, baud, polarity);
        var decoder = new PocsagDecoder(channel.SampleRate, OnPageHeard, baud);
        channel.AddReceiveTap(decoder.Process);
        _listener = new TcpListener(bind ?? IPAddress.Loopback, port);
    }

    /// <summary>The mode label, e.g. "pocsag1200".</summary>
    public string Mode => $"pocsag{_baud}";

    /// <summary>The bound port (useful when constructed with port 0).</summary>
    public int LocalPort => ((IPEndPoint)_listener.LocalEndpoint).Port;

    /// <summary>Starts accepting clients.</summary>
    public void Start()
    {
        _listener.Start();
        _acceptLoop = AcceptLoopAsync();
    }

    private async Task AcceptLoopAsync()
    {
        try
        {
            while (!_stopping.IsCancellationRequested)
            {
                TcpClient client = await _listener.AcceptTcpClientAsync(_stopping.Token).ConfigureAwait(false);
                client.NoDelay = true;
                var id = Guid.NewGuid();
                _clients[id] = client;
                _ = ServeClientAsync(id, client);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task ServeClientAsync(Guid id, TcpClient client)
    {
        var pending = new StringBuilder();
        var buffer = new byte[4096];
        try
        {
            NetworkStream stream = client.GetStream();
            while (!_stopping.IsCancellationRequested)
            {
                int got = await stream.ReadAsync(buffer, _stopping.Token).ConfigureAwait(false);
                if (got == 0)
                {
                    break;
                }

                pending.Append(Encoding.UTF8.GetString(buffer, 0, got));
                for (int newline; (newline = IndexOfNewline(pending)) >= 0;)
                {
                    string line = pending.ToString(0, newline).TrimEnd('\r');
                    pending.Remove(0, newline + 1);
                    if (line.Length > 0)
                    {
                        Send(client, HandleLine(line));
                    }
                }

                if (pending.Length > 4096)
                {
                    Send(client, "ERR line too long");
                    break;
                }
            }
        }
        catch (Exception)
        {
            // Client errors only ever cost that client its connection.
        }
        finally
        {
            _clients.TryRemove(id, out _);
            client.Dispose();
        }
    }

    private static int IndexOfNewline(StringBuilder pending)
    {
        for (int i = 0; i < pending.Length; i++)
        {
            if (pending[i] == '\n')
            {
                return i;
            }
        }

        return -1;
    }

    private string HandleLine(string line)
    {
        string[] parts = line.Split(' ', 5, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0 || !parts[0].Equals("PAGE", StringComparison.OrdinalIgnoreCase))
        {
            return "ERR unknown command (expected PAGE)";
        }

        if (parts.Length < 4)
        {
            return "ERR usage: PAGE <ric> <function> ALPHA|NUMERIC|TONE [text]";
        }

        if (!uint.TryParse(parts[1], CultureInfo.InvariantCulture, out uint ric) || ric > 0x1FFFFF)
        {
            return "ERR ric must be 0..2097151";
        }

        if (!int.TryParse(parts[2], CultureInfo.InvariantCulture, out int function)
            || function is < 0 or > 3)
        {
            return "ERR function must be 0..3";
        }

        string text = parts.Length > 4 ? parts[4] : "";
        if (text.Length > MaxTextLength)
        {
            return $"ERR text too long (max {MaxTextLength} characters)";
        }

        PocsagMessage page;
        try
        {
            page = parts[3].ToUpperInvariant() switch
            {
                "ALPHA" => PocsagMessage.Alphanumeric(ric, text, function),
                "NUMERIC" => PocsagMessage.Numeric(ric, text, function),
                "TONE" => PocsagMessage.Tone(ric, function),
                _ => throw new ArgumentException("type must be ALPHA, NUMERIC or TONE"),
            };
        }
        catch (ArgumentException invalid)
        {
            return $"ERR {invalid.Message}";
        }

        int id = Interlocked.Increment(ref _nextId);
        // The spec preamble (576 bits) doubles as the TXDELAY budget; honour a longer
        // configured TXDELAY by stretching it (the preamble is the settling time).
        _ = _channel.EnqueueTransmit(txDelay => _encoder.Modulate(
            [page], Math.Max(PocsagEncoder.PreambleBits, (int)((long)txDelay * _baud / 1000))));
        return $"OK {id}";
    }

    private void OnPageHeard(PocsagPage page)
    {
        string content = page.ContentGroups.Count == 0
            ? "TONE"
            : page.Function == 0 ? $"NUMERIC {Sanitize(page.NumericText)}" : $"ALPHA {Sanitize(page.AlphaText)}";
        string line = $"HEARD {page.Address} {page.Function} {content}";
        foreach (TcpClient client in _clients.Values)
        {
            Send(client, line);
        }
    }

    private static string Sanitize(string text)
    {
        // HEARD is a line protocol; decoded 7-bit content can legally contain control
        // characters, which must not fake line breaks on the socket.
        var safe = new StringBuilder(text.Length);
        foreach (char c in text)
        {
            safe.Append(c is < ' ' or '\x7F' ? ' ' : c);
        }

        return safe.ToString();
    }

    private static void Send(TcpClient client, string line)
    {
        try
        {
            byte[] bytes = Encoding.UTF8.GetBytes(line + "\n");
            lock (client)
            {
                client.GetStream().Write(bytes);
            }
        }
        catch (Exception)
        {
            // Broken pipe: the client's read loop will clean it up.
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await _stopping.CancelAsync().ConfigureAwait(false);
        _listener.Stop();
        foreach (TcpClient client in _clients.Values)
        {
            client.Dispose();
        }

        if (_acceptLoop is not null)
        {
            try
            {
                await _acceptLoop.ConfigureAwait(false);
            }
            catch (Exception)
            {
            }
        }

        _stopping.Dispose();
    }
}
