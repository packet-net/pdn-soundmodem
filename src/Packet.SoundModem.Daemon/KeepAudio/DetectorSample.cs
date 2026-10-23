using System.Globalization;
using System.Text;
using Packet.SoundModem.CarrierSense;
using Packet.SoundModem.Channel;

namespace Packet.SoundModem.Daemon.KeepAudio;

/// <summary>
/// One look at the station's carrier sense, taken every <see cref="KeepAudioRecorder.SampleInterval"/>
/// whether or not anyone has asked for a window yet, so a window that starts in the past has
/// its reasons as well as its audio.
/// </summary>
/// <param name="At">When it was taken.</param>
/// <param name="Busy">The answer, as the transmit lease would report it.</param>
/// <param name="Transmitting">Whether this station was transmitting.</param>
/// <param name="LeaseSubChannel">The sub-channel holding the transmit lease, whose view
/// <paramref name="Busy"/> is; null for the whole station.</param>
/// <param name="DecidedBy">What made it busy; null when clear.</param>
/// <param name="Reading">The deciding detector's reading, or when clear the one nearest to firing.</param>
/// <param name="RadioSignalDbm">The radio's own signal reading, where it gives one.</param>
/// <param name="Detectors">Every detector, as one line of plain ASCII.</param>
internal sealed record DetectorSample(
    DateTimeOffset At,
    bool Busy,
    bool Transmitting,
    int? LeaseSubChannel,
    string? DecidedBy,
    BusyReading? Reading,
    double? RadioSignalDbm,
    string Detectors)
{
    /// <summary>Takes the sample from a channel, in the lease holder's view when there is one.</summary>
    public static DetectorSample Take(DateTimeOffset at, SoundModemChannel channel)
    {
        // HolderNow, not Holder: Holder ends a lease that is due, and a diagnostic timer must
        // never be what runs a lease's teardown and its closing transmission.
        int? holder = channel.TransmitLease.HolderNow();
        return From(at, holder, channel.ExplainCarrierSense(holder));
    }

    /// <summary>Builds a sample from an explanation.</summary>
    public static DetectorSample From(DateTimeOffset at, int? holder, CarrierSenseExplanation why) =>
        new(at, why.Busy, why.Transmitting, holder, why.DecidedBy, why.Reading, why.RadioSignalDbm,
            Describe(why.Detectors));

    /// <summary>
    /// Every detector as <c>name=state</c> items joined by <c>"; "</c>: busy, clear or unknown,
    /// <c>dcd</c> while a modem hears a packet signal, its level over its threshold in dB, and
    /// <c>(not counted)</c> when its answer did not count.
    /// </summary>
    internal static string Describe(IReadOnlyList<CarrierSenseDetector> detectors)
    {
        var text = new StringBuilder();
        foreach (CarrierSenseDetector d in detectors)
        {
            if (text.Length > 0)
            {
                text.Append("; ");
            }

            text.Append(Ascii(d.Name)).Append('=')
                .Append(d.Busy switch { true => "busy", false => "clear", null => "unknown" });
            if (d.CarrierDetect == true)
            {
                text.Append(" dcd");
            }

            if (d.Reading is { } r)
            {
                text.Append(' ').Append(Number(r.LevelDb)).Append('/').Append(Number(r.ThresholdDb));
            }

            if (!d.Counts)
            {
                text.Append(" (not counted)");
            }
        }

        return text.ToString();
    }

    /// <summary>A number for the log: one decimal place, invariant, empty for nothing.</summary>
    internal static string Number(double? value) =>
        value is double v && double.IsFinite(v) ? v.ToString("0.0", CultureInfo.InvariantCulture) : "";

    private static string Ascii(string text)
    {
        var plain = new StringBuilder(text.Length);
        foreach (char c in text)
        {
            plain.Append(c is >= ' ' and <= '~' and not ';' and not '"' ? c : '?');
        }

        return plain.ToString();
    }
}
