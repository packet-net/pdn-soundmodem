using System;
using System.Globalization;
using System.Numerics;
using System.Text;

namespace Packet.SoundModem.Channel;

/// <summary>Why a transmission waited, where one reason accounts for most of the wait.</summary>
public enum TransmitWaitCause
{
    /// <summary>It did not wait, or no one reason accounts for as much as half of the wait.</summary>
    None,

    /// <summary>Carrier sense said the channel was occupied.</summary>
    ChannelBusy,

    /// <summary>The channel was clear and the p-persistence roll kept losing.</summary>
    Backoff,

    /// <summary>The turnaround hold was keeping the channel clear for somebody's reply.</summary>
    TurnaroundHold,

    /// <summary>Another service held the channel (<see cref="SoundModemChannel.TransmitInhibit"/>).</summary>
    TransmitInhibit,

    /// <summary>This station was transmitting - its own earlier frames, in this keyup or another.</summary>
    OurTransmission,

    /// <summary>Another of this station's transmitters was being served.</summary>
    OurTurn,
}

/// <summary>
/// Where one transmission's wait went: the same total as <see cref="TransmitReport.HeldFor"/>,
/// split by what was holding the frame at each moment of it.
/// </summary>
/// <remarks>
/// <para><b>Why the total is not enough.</b> A frame that waited 8.3 s because the channel was
/// occupied and a frame that waited 8.3 s because it was the third frame of a MAXFRAME=3 window,
/// sitting behind this station's own two earlier bursts, report the same number and mean opposite
/// things. The first says the frequency is busy and the station is losing airtime to somebody
/// else; the second says the station is working normally and the figure is the cost of its own
/// window. An operator asking "is this frequency usable" cannot tell them apart from the total,
/// and on GB7RDG-2 on 2026-09-21 the 8.3 s row was the benign one.</para>
/// <para><b>What the split is of.</b> Not of the queue - of the CHANNEL. A frame queued behind
/// another of this station's frames inherits whatever was holding the frame in front of it, so a
/// window of three that waited out one minute of carrier sense reports a minute of carrier sense
/// on all three rather than "behind our own traffic" on two of them. That is the honest answer to
/// "where did this station's airtime go": the channel was occupied, and nothing this station
/// queued was going anywhere. The one thing that does read as our own traffic is the part of the
/// wait the station spent KEYED UP - <see cref="OurTransmission"/> - because that airtime is ours
/// and it is what separates the third frame of a window from a busy channel.</para>
/// <para><b>Properties rather than a positional record</b>, deliberately. Seven durations of the
/// same type in a row is the shape that silently accepts two of them transposed, which is exactly
/// what was suspected of this figure and had to be ruled out by hand.</para>
/// </remarks>
public readonly struct TransmitWaits : IEquatable<TransmitWaits>
{
    /// <summary>The whole wait: the same figure as <see cref="TransmitReport.HeldFor"/>.</summary>
    public TimeSpan Total { get; init; }

    /// <summary>Carrier sense said the channel was occupied. See <see cref="BusySubChannels"/>.</summary>
    public TimeSpan ChannelBusy { get; init; }

    /// <summary>The channel was clear and the p-persistence roll lost, so the station waited a slot.</summary>
    public TimeSpan Backoff { get; init; }

    /// <summary>
    /// The turnaround hold was running: this station had sent something that expects an answer,
    /// and the channel was being kept clear for it.
    /// </summary>
    public TimeSpan TurnaroundHold { get; init; }

    /// <summary>
    /// Another service held the channel against this frame - an ARDOP ARQ session running it. The
    /// frame was queued the whole time, in the order the host handed it over; what waited was the
    /// transmitter (<see cref="SoundModemChannel.TransmitInhibit"/>).
    /// </summary>
    public TimeSpan TransmitInhibit { get; init; }

    /// <summary>
    /// This station was keyed up: its own earlier frames, whether in front of this one in the same
    /// keyup or on another of its transmitters.
    /// </summary>
    public TimeSpan OurTransmission { get; init; }

    /// <summary>
    /// Another of this station's transmitters was being served - a different modem's frame was
    /// working its way onto the air, and this one waited its turn behind it.
    /// </summary>
    public TimeSpan OurTurn { get; init; }

    /// <summary>
    /// The remainder: scheduling, modulation and the moments between one wait and the next.
    /// Small on a healthy station, and here so that the parts always add up to
    /// <see cref="Total"/> rather than nearly.
    /// </summary>
    public TimeSpan Unattributed { get; init; }

    /// <summary>
    /// Which sub-channels asserted carrier sense during <see cref="ChannelBusy"/>, as a bit per
    /// sub-channel (bit 0 = sub-channel 0), with <see cref="RadioBit"/> set when the station's
    /// radio gave the answer for the whole station rather than any one modem.
    /// </summary>
    /// <remarks>
    /// This is the diagnostic for packet-net/pdn-soundmodem#526. Carrier sense is answered per
    /// sub-channel now - a frame defers to its own modem's detector and to any modem sharing its
    /// passband, and not to one 1.3 kHz away it could not collide with - so "which sub-channel
    /// held this frame" is the question that says whether that rule is doing its job, and whether
    /// a station is deferring to something it has no business deferring to.
    /// </remarks>
    public int BusySubChannels { get; init; }

    /// <summary>The bit in <see cref="BusySubChannels"/> that means the radio said so.</summary>
    public const int RadioBit = 1 << 16;

    /// <summary>
    /// The sub-channel that accounted for most of <see cref="ChannelBusy"/>, or null when the
    /// radio answered for the whole station or nothing asserted it.
    /// </summary>
    public int? BusiestSubChannel { get; init; }

    /// <summary>True when the station's radio, rather than a modem, said the channel was busy.</summary>
    public bool RadioSaidBusy => (BusySubChannels & RadioBit) != 0;

    /// <summary>
    /// The one reason that accounts for at least half the wait, or <see cref="TransmitWaitCause.None"/>
    /// when no single reason does.
    /// </summary>
    /// <remarks>
    /// Half rather than "the largest", because the point of naming a cause on one line is to let a
    /// reader skip the other five, and a largest-of-six that is a quarter of the total would have
    /// them skip five reasons that between them mattered more. A wait with no dominant cause says
    /// so, and the breakdown in the frame log is where the mixture gets read.
    /// </remarks>
    public TransmitWaitCause Dominant
    {
        get
        {
            if (Total <= TimeSpan.Zero)
            {
                return TransmitWaitCause.None;
            }

            TimeSpan half = Total / 2;
            if (ChannelBusy >= half) return TransmitWaitCause.ChannelBusy;
            if (OurTransmission >= half) return TransmitWaitCause.OurTransmission;
            if (TransmitInhibit >= half) return TransmitWaitCause.TransmitInhibit;
            if (TurnaroundHold >= half) return TransmitWaitCause.TurnaroundHold;
            if (Backoff >= half) return TransmitWaitCause.Backoff;
            if (OurTurn >= half) return TransmitWaitCause.OurTurn;
            return TransmitWaitCause.None;
        }
    }

    /// <summary>
    /// The dominant cause and what it cost, as one short ASCII phrase for a log line - or null
    /// when no single cause accounts for half the wait.
    /// </summary>
    /// <remarks>
    /// ASCII and no punctuation that a pager will mangle: this ends up in journald, whose pager
    /// runs under a C locale on a stock Debian box and renders anything else as escape codes.
    /// </remarks>
    public string? Describe() => Dominant switch
    {
        TransmitWaitCause.ChannelBusy => $"{Show(ChannelBusy)} channel busy{Where()}",
        TransmitWaitCause.OurTransmission => $"{Show(OurTransmission)} behind our own transmissions",
        TransmitWaitCause.TransmitInhibit => $"{Show(TransmitInhibit)} another service held the channel",
        TransmitWaitCause.TurnaroundHold => $"{Show(TurnaroundHold)} holding the channel for a reply",
        TransmitWaitCause.Backoff => $"{Show(Backoff)} losing the p-persistence roll",
        TransmitWaitCause.OurTurn => $"{Show(OurTurn)} behind another link of ours",
        _ => null,
    };

    /// <summary>The sub-channels that asserted carrier sense, as "0" or "0,2" - empty when none did.</summary>
    public string SubChannelList()
    {
        int bits = BusySubChannels & ~RadioBit;
        if (bits == 0)
        {
            return "";
        }

        var text = new StringBuilder();
        while (bits != 0)
        {
            int sub = BitOperations.TrailingZeroCount(bits);
            bits &= bits - 1;
            if (text.Length > 0)
            {
                text.Append(',');
            }

            text.Append(sub.ToString(CultureInfo.InvariantCulture));
        }

        return text.ToString();
    }

    private string Where()
    {
        string subs = SubChannelList();
        return (subs, RadioSaidBusy) switch
        {
            ("", true) => " (the radio said so)",
            ("", false) => "",
            (_, true) => $" (the radio said so, with ch{subs})",
            _ => $" on ch{subs}",
        };
    }

    /// <summary>
    /// Seconds to one decimal under a minute, minutes and seconds above it: the same shape as the
    /// total it is written beside, so the two read as one figure split rather than as two
    /// measurements that happen to be near each other.
    /// </summary>
    private static string Show(TimeSpan span) =>
        span >= TimeSpan.FromMinutes(1)
            ? $"{(int)span.TotalMinutes}m{span.Seconds:00}s"
            : $"{span.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture)}s";

    /// <inheritdoc />
    public bool Equals(TransmitWaits other) =>
        Total == other.Total && ChannelBusy == other.ChannelBusy && Backoff == other.Backoff
        && TurnaroundHold == other.TurnaroundHold && TransmitInhibit == other.TransmitInhibit
        && OurTransmission == other.OurTransmission && OurTurn == other.OurTurn
        && Unattributed == other.Unattributed && BusySubChannels == other.BusySubChannels
        && BusiestSubChannel == other.BusiestSubChannel;

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is TransmitWaits other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() =>
        HashCode.Combine(Total, ChannelBusy, Backoff, TurnaroundHold, TransmitInhibit, OurTransmission, OurTurn, BusySubChannels);

    /// <summary>Equality operator.</summary>
    public static bool operator ==(TransmitWaits left, TransmitWaits right) => left.Equals(right);

    /// <summary>Inequality operator.</summary>
    public static bool operator !=(TransmitWaits left, TransmitWaits right) => !left.Equals(right);
}
