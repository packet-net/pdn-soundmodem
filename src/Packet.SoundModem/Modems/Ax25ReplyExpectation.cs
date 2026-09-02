using Packet.Ax25;

namespace Packet.SoundModem.Modems;

/// <summary>
/// Whether an AX.25 frame is the kind that gets an answer - the policy a half-duplex station
/// needs in order to know when to stay off the air and let that answer through.
/// </summary>
/// <remarks>
/// <para><b>Why it lives beside the modem.</b> The question "will a reply come" cannot be
/// answered anywhere else. The host (LinBPQ) knows the protocol but not that several of its ports
/// share one transmitter; the channel knows about the shared transmitter but not the protocol. So
/// the two facts have to meet somewhere, and this is the smallest place to put the meeting.</para>
/// <para><b>It parses nothing itself.</b> The frame codec is <see cref="Ax25Frame"/> from
/// packet.net - reimplementing a control-field reader here would be a second, worse AX.25, and
/// the one in the package already handles digipeater paths, the modulo-128 two-octet control
/// field and the §6.1.2 command/response bits. What is left here is one policy decision.</para>
/// <para><b>No opinion is a valid answer.</b> The channel also carries IL2P payloads that are not
/// AX.25 and will carry more. Anything that does not parse returns false, which leaves the
/// station behaving exactly as it did before any hold existed. Being wrong that way costs
/// nothing; holding the channel after traffic whose reply timing cannot be known would cost
/// airtime on every frame of it.</para>
/// </remarks>
public static class Ax25ReplyExpectation
{
    /// <summary>
    /// True when <paramref name="frame"/> is an AX.25 frame whose sender should expect a
    /// response, and so should keep the channel clear for one.
    /// </summary>
    /// <param name="frame">A raw AX.25 frame, no flags or FCS, as the modems carry them.</param>
    /// <remarks>
    /// Three cases are answered: an I-frame, which is acknowledged data and draws an RR, RNR or
    /// REJ whether or not it polls; any command carrying the P bit, a poll being by definition a
    /// demand for a response; and the link-setup and teardown commands, which draw a UA or DM.
    /// Everything else returns false - a UI beacon, this station's own ident, and in particular a
    /// response frame, which IS somebody's answer and would otherwise have the channel held open
    /// after every acknowledgement on the link.
    /// </remarks>
    public static bool ExpectsReply(ReadOnlySpan<byte> frame)
    {
        if (!Ax25Frame.TryParse(frame, out Ax25Frame? parsed) || parsed is null)
        {
            return false;
        }

        // A response is an answer, never a question - and the P/F bit on one is the F bit, which
        // would otherwise read as a poll and hold the channel after every acknowledgement.
        if (parsed.IsResponse)
        {
            return false;
        }

        // I-frame: acknowledged data. Identified by the low control bit being clear, which is
        // the one classification Ax25Frame does not surface as a property of its own.
        if ((parsed.Control & 0x01) == 0)
        {
            return true;
        }

        // Link setup and teardown draw a UA or DM whether or not they poll. This reads the
        // control byte the codec already parsed rather than parsing anything: which subtypes are
        // worth waiting for is policy, and the package models U-frame subtypes only on the
        // outbound side (UFrameType) and behind the connected-mode classifier, which is a great
        // deal of session machinery to pull in for three constants.
        if ((parsed.Control & ~PollFinalBit) is Sabm or Sabme or Disc)
        {
            return true;
        }

        // A UI frame is unacknowledged by definition; it draws an answer only if it polls.
        // Everything else here is a supervisory or unnumbered command, where the poll bit is the
        // whole question.
        return parsed.PollFinal;
    }

    /// <summary>The poll/final bit in a modulo-8 or unnumbered control octet (AX.25 §4.3).</summary>
    private const int PollFinalBit = 0x10;

    private const int Sabm = 0x2F;
    private const int Sabme = 0x6F;
    private const int Disc = 0x43;
}
