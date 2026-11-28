using Packet.SoundModem.CarrierSense;

namespace Packet.SoundModem.OfdmFmTool;

/// <summary>The <c>--carrier</c> option: where the radio's own carrier indication comes from.</summary>
internal static class CarrierSpec
{
    public const string Prefix = "tait:";

    /// <summary>Reads a spec. Null when it is good, with the Tait's control port in <paramref name="port"/>; else the one line to say.</summary>
    public static string? Parse(string spec, out string port)
    {
        port = "";
        if (!spec.StartsWith(Prefix, StringComparison.Ordinal) || spec.Length == Prefix.Length || spec[Prefix.Length..].Any(char.IsWhiteSpace))
        {
            return "--carrier must be tait:DEVICE, such as tait:/dev/ttyUSB0";
        }

        port = spec[Prefix.Length..];
        return null;
    }
}

/// <summary>
/// The radio's carrier source with the tool's two lines about it: one when it first answers, and one each time it stops answering or answers again. Everything else passes through untouched.
/// The station's own trace says, at the detail level, each time carrier comes and goes.
/// </summary>
internal sealed class LoggedCarrier(IChannelBusySource inner, string where, Action<string> say) : IChannelBusySource
{
    private readonly Lock _gate = new();
    private bool _everKnown;
    private bool _known;

    public bool? Busy
    {
        get
        {
            bool? now = inner.Busy;
            bool knows = now is not null;
            lock (_gate)
            {
                if (knows != _known)
                {
                    say(!knows ? $"Carrier indication from {where} lost: the station goes by what it hears"
                        : _everKnown ? $"Carrier indication from {where} back"
                        : $"Carrier indication from {where} available");
                    _known = knows;
                    _everKnown |= knows;
                }
            }

            return now;
        }
    }
}
