using System.Text;

namespace Packet.SoundModem.Kiss;

/// <summary>
/// The routing behind a polyglot KISS port: several modems overlaid on one channel, presented to
/// the host as one KISS port, with each transmitted frame sent in the mode its next hop was last
/// heard in (packet-net/pdn-soundmodem#450).
/// </summary>
/// <remarks>
/// <para><b>What it is for.</b> A node with one frequency and peers on different equipment - some
/// on AFSK 1200, some on IL2P or 9600 - can run one modem per mode on the same channel and have
/// them all hear every burst. Which one decodes a burst says what the sender can do. The host sees
/// one port and never chooses: a frame for a station heard in 9600 goes out in 9600, and a frame
/// for anyone else goes out in the default mode, which should be the one every peer has.</para>
/// <para><b>Which station a frame is "from" and "for".</b> The station we heard is the one whose
/// transmitter we actually received: the last digipeater that has repeated the frame (its H bit
/// set), or the source when no digipeater has. The station a frame is for is its next hop: the
/// first digipeater that has not yet repeated it, or the destination when there is none. So a
/// path through a digipeater follows the digipeater's mode, which is the only one that matters on
/// this hop.</para>
/// <para><b>Broadcasts need no rule of their own.</b> Beacons, APRS and NODES go to destinations
/// like <c>ID</c>, <c>BEACON</c> or <c>NODES</c> that never transmit, so they are never heard,
/// never learned, and fall through to the default mode like any other stranger.</para>
/// <para><b>Duplicates.</b> Two modems of different modes do not decode the same burst. Two of
/// one modulation can - plain AFSK 1200 reads the AX.25 inside an FX.25 frame - so a frame that
/// arrives again, byte for byte, from another member within <see cref="DuplicateWindow"/> is
/// withheld from the host, and the first copy's modem is the one learned. The same bytes twice
/// from the SAME modem are two transmissions (two digipeaters repeating one WIDE2-1 frame) and
/// both are delivered, as the shared port would. Pairing a mode with its
/// own backwards-compatible variant gains nothing here anyway: FX.25 already reaches legacy
/// stations on its own.</para>
/// <para>Thread-safe: frames arrive on the audio thread, transmissions from each host's session.</para>
/// </remarks>
public sealed class PolyglotRouter
{
    /// <summary>How close together two identical frames from different members must arrive to be
    /// one burst decoded twice. Different decoders finish the same burst tens of milliseconds
    /// apart; a host retrying an identical frame does so after its FRACK, seconds later.</summary>
    public static readonly TimeSpan DuplicateWindow = TimeSpan.FromSeconds(1);

    /// <summary>Most addresses an AX.25 header carries: destination, source, eight digipeaters.</summary>
    private const int MaxAddresses = 10;

    /// <summary>Stations remembered before expired ones are swept out. A sweep is cheap, and
    /// running one only past this size keeps it off the per-frame path on an ordinary station.</summary>
    private const int SweepAbove = 256;

    /// <summary>The least time between sweeps, so a busy channel with more than
    /// <see cref="SweepAbove"/> live stations does not sweep on every frame.</summary>
    private static readonly TimeSpan SweepInterval = TimeSpan.FromMinutes(1);

    private readonly Lock _lock = new();
    private readonly Dictionary<string, (int SubChannel, DateTimeOffset HeardAt)> _heard =
        new(StringComparer.Ordinal);
    private readonly List<Arrival> _recent = [];
    private readonly HashSet<int> _members;
    private readonly TimeProvider _time;
    private DateTimeOffset _lastSweep = DateTimeOffset.MinValue;

    /// <summary>Creates a router over <paramref name="subChannels"/>.</summary>
    /// <param name="subChannels">The overlaid modems' sub-channels; at least two, no repeats.</param>
    /// <param name="defaultSubChannel">Where a frame goes when its next hop has not been heard
    /// within <paramref name="forgetAfter"/>; one of <paramref name="subChannels"/>.</param>
    /// <param name="forgetAfter">How long a station is remembered after it was last heard.</param>
    /// <param name="time">Wall clock; the system's when null.</param>
    public PolyglotRouter(
        IReadOnlyList<int> subChannels, int defaultSubChannel, TimeSpan forgetAfter, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(subChannels);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(forgetAfter, TimeSpan.Zero);
        _members = [.. subChannels];
        if (subChannels.Count < 2 || _members.Count != subChannels.Count)
        {
            throw new ArgumentException(
                "a polyglot port needs at least two different sub-channels", nameof(subChannels));
        }

        if (!_members.Contains(defaultSubChannel))
        {
            throw new ArgumentException(
                $"the default sub-channel {defaultSubChannel} is not one of the port's own",
                nameof(defaultSubChannel));
        }

        SubChannels = [.. subChannels];
        DefaultSubChannel = defaultSubChannel;
        ForgetAfter = forgetAfter;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>The overlaid modems, in the order they were given.</summary>
    public IReadOnlyList<int> SubChannels { get; }

    /// <summary>Where frames for a station not heard recently go.</summary>
    public int DefaultSubChannel { get; }

    /// <summary>How long a station is remembered after it was last heard.</summary>
    public TimeSpan ForgetAfter { get; }

    /// <summary>
    /// A station's frames will now go out on a different modem than they would have a moment ago:
    /// it was heard for the first time on a modem other than the default, or on a different one
    /// from before. Not raised when nothing changes, so a single-mode channel says nothing.
    /// </summary>
    public event Action<PolyglotLearnedEvent>? Learned;

    /// <summary>Whether <paramref name="subChannel"/> is one of this port's modems.</summary>
    public bool IsMember(int subChannel) => _members.Contains(subChannel);

    /// <summary>
    /// A member modem decoded <paramref name="frame"/>. Learns which modem its sender was heard
    /// on, and says whether the host should be given it: false for a copy of a frame another
    /// member has just delivered, and for a sub-channel that is not a member at all.
    /// </summary>
    public bool Heard(int subChannel, byte[] frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (!_members.Contains(subChannel))
        {
            return false;
        }

        DateTimeOffset now = _time.GetUtcNow();
        PolyglotLearnedEvent? learned = null;
        lock (_lock)
        {
            ForgetOldFrames(now);
            bool duplicate = false;
            foreach (Arrival earlier in _recent)
            {
                if (earlier.SubChannel != subChannel && earlier.Delivered
                    && frame.AsSpan().SequenceEqual(earlier.Copy))
                {
                    duplicate = true;
                    break;
                }
            }

            // Kept either way, so the quality frame that follows can be given the same answer.
            // The copy is what later frames are compared against; the array itself is only ever
            // compared by identity, because a modem is free to reuse it once this call returns.
            _recent.Add(new Arrival(frame, frame.ToArray(), subChannel, now, Delivered: !duplicate));
            if (duplicate)
            {
                return false;
            }

            if (HeardStation(frame) is string station)
            {
                int before = Lookup(station, now) ?? DefaultSubChannel;
                _heard[station] = (subChannel, now);
                if (before != subChannel)
                {
                    learned = new PolyglotLearnedEvent(station, subChannel, before);
                }

                if (_heard.Count > SweepAbove && now - _lastSweep >= SweepInterval)
                {
                    _lastSweep = now;
                    SweepExpired(now);
                }
            }
        }

        // Outside the lock: a handler writes to the journal, and nothing it does may hold up
        // the next frame or a host's transmission.
        if (learned is { } e)
        {
            Learned?.Invoke(e);
        }

        return true;
    }

    /// <summary>
    /// Whether <paramref name="frame"/> from <paramref name="subChannel"/> was given to the host,
    /// for the per-frame extras that follow a data frame (the quality frame): those describe a
    /// frame the host received, so a withheld duplicate gets none. Asked with the same array the
    /// data frame arrived in, which the channel passes to both.
    /// </summary>
    public bool WasDelivered(int subChannel, byte[] frame)
    {
        lock (_lock)
        {
            for (int n = _recent.Count - 1; n >= 0; n--)
            {
                if (ReferenceEquals(_recent[n].Frame, frame) && _recent[n].SubChannel == subChannel)
                {
                    return _recent[n].Delivered;
                }
            }
        }

        return false;
    }

    /// <summary>The sub-channel a host's frame goes out on: the modem its next hop was last heard
    /// on, if that was within <see cref="ForgetAfter"/>, and the default otherwise.</summary>
    public int Route(ReadOnlySpan<byte> frame)
    {
        if (NextHop(frame) is not string station)
        {
            return DefaultSubChannel;
        }

        lock (_lock)
        {
            return Lookup(station, _time.GetUtcNow()) ?? DefaultSubChannel;
        }
    }

    /// <summary>The modem <paramref name="station"/> was last heard on, or null when it has not
    /// been heard within <see cref="ForgetAfter"/>. <c>"G4ABC-1"</c> form; SSID 0 has no suffix.</summary>
    public int? HeardOn(string station)
    {
        ArgumentNullException.ThrowIfNull(station);
        lock (_lock)
        {
            return Lookup(station, _time.GetUtcNow());
        }
    }

    private int? Lookup(string station, DateTimeOffset now) =>
        _heard.TryGetValue(station, out var entry) && now - entry.HeardAt < ForgetAfter
            ? entry.SubChannel
            : null;

    private void ForgetOldFrames(DateTimeOffset now) =>
        _recent.RemoveAll(r => now - r.At >= DuplicateWindow);

    private void SweepExpired(DateTimeOffset now)
    {
        foreach (string station in _heard.Where(h => now - h.Value.HeardAt >= ForgetAfter).Select(h => h.Key).ToList())
        {
            _heard.Remove(station);
        }
    }

    /// <summary>The station whose transmitter a received frame came from, or null when the
    /// header does not read.</summary>
    internal static string? HeardStation(ReadOnlySpan<byte> frame)
    {
        if (!TryReadPath(frame, out int count))
        {
            return null;
        }

        // Latest repeater first: the last digipeater with H set is the one on the air.
        for (int n = count - 1; n >= 2; n--)
        {
            if ((frame[(n * 7) + 6] & 0x80) != 0)
            {
                return ReadStation(frame.Slice(n * 7, 7));
            }
        }

        return ReadStation(frame.Slice(7, 7));
    }

    /// <summary>The station a frame to be sent must reach first, or null when the header does
    /// not read.</summary>
    internal static string? NextHop(ReadOnlySpan<byte> frame)
    {
        if (!TryReadPath(frame, out int count))
        {
            return null;
        }

        for (int n = 2; n < count; n++)
        {
            if ((frame[(n * 7) + 6] & 0x80) == 0)
            {
                return ReadStation(frame.Slice(n * 7, 7));
            }
        }

        return ReadStation(frame[..7]);
    }

    /// <summary>Counts the address fields: they run until one has the extension bit set. False
    /// for anything that cannot be an AX.25 header, and when any address in it does not read as a
    /// callsign, since a header with one damaged address says nothing trustworthy about any.</summary>
    private static bool TryReadPath(ReadOnlySpan<byte> frame, out int count)
    {
        count = 0;
        while (count < MaxAddresses && (count + 1) * 7 <= frame.Length)
        {
            ReadOnlySpan<byte> field = frame.Slice(count * 7, 7);
            if (ReadStation(field) is null)
            {
                return false;
            }

            count++;
            if ((field[6] & 0x01) != 0)
            {
                // A control byte must follow the last address.
                return count >= 2 && count * 7 < frame.Length;
            }
        }

        return false;
    }

    private static string? ReadStation(ReadOnlySpan<byte> field)
    {
        var callsign = new StringBuilder(9);
        bool ended = false;
        for (int n = 0; n < 6; n++)
        {
            char c = (char)(field[n] >> 1);
            if (c == ' ')
            {
                ended = true;
                continue;
            }

            // Letters and digits only, and no letter after the padding starts.
            if (ended || c is not ((>= 'A' and <= 'Z') or (>= '0' and <= '9')))
            {
                return null;
            }

            callsign.Append(c);
        }

        if (callsign.Length == 0)
        {
            return null;
        }

        int ssid = (field[6] >> 1) & 0x0F;
        return ssid == 0 ? callsign.ToString() : callsign.Append('-').Append(ssid).ToString();
    }
}

/// <summary>One frame a member decoded in the last <see cref="PolyglotRouter.DuplicateWindow"/>.</summary>
/// <param name="Frame">The array it arrived in, for identity only.</param>
/// <param name="Copy">Its bytes as they were, for comparing later frames against.</param>
/// <param name="SubChannel">The modem that decoded it.</param>
/// <param name="At">When.</param>
/// <param name="Delivered">Whether the host was given it.</param>
internal readonly record struct Arrival(byte[] Frame, byte[] Copy, int SubChannel, DateTimeOffset At, bool Delivered);

/// <summary>A station's frames on a polyglot port now go out on a different modem.</summary>
/// <param name="Station">The station, <c>"G4ABC-1"</c> form.</param>
/// <param name="SubChannel">The modem it was just heard on, where its frames now go.</param>
/// <param name="Previous">Where they went before: the modem it was last heard on, or the default
/// when it had not been heard recently.</param>
public readonly record struct PolyglotLearnedEvent(string Station, int SubChannel, int Previous);
