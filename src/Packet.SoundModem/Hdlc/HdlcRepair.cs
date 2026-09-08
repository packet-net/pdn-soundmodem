namespace Packet.SoundModem.Hdlc;

/// <summary>
/// Opt-in repair of FCS-failed and bit-slipped HDLC frames by confidence-ordered bit edits -
/// the AX.25 sibling of the IL2P receiver's chase decoding. A frame whose check sequence does
/// not verify is offered to the repair engine, which flips the bits the demodulator was least
/// sure of (singles first, then pairs among the weakest) until the FCS verifies, and a frame
/// that lost or gained one bit mid-stream is realigned by deleting or inserting at its weakest
/// positions. A repair only exists because of these edits, so it is delivered through
/// <see cref="HdlcDeframer.FrameRepaired"/> rather than the ordinary frame sink, with the
/// number of edited bits, and it must clear <see cref="RepairedFrameGates"/> before it goes
/// anywhere.
/// </summary>
/// <remarks>
/// <para><b>Why this is sound rather than optimistic.</b> The FCS is a 16-bit check: a wrong
/// candidate passes it by chance with probability 2⁻¹⁶ per attempt, so a budget of a few
/// hundred attempts per failed frame carries a per-frame false-pass probability of a few
/// times 10⁻³ - and every false pass must additionally clear the structural and payload
/// gates, which a chance pass on a real-world AX.25 channel essentially never does. Measured
/// against the WA8LMF TNC Test CD corpus with these budgets and gates (docs/tnc-test-cd.md):
/// Track 1 1015 → 1021 frames, Track 2 1015 → 1021, Track 4 108 → 110 (of ~110 beacons
/// known to be on the recording), and Track 3 - one hundred transmissions of a single known
/// frame, the corpus's false-pass canary - stays exactly 100 of 100 with one distinct
/// content, i.e. zero false repairs delivered.</para>
/// <para><b>The soft magnitudes are the engine's fuel.</b> Ordering flips by confidence needs
/// <see cref="HdlcDeframer.PushBit(int, float)"/>; a caller that pushes hard bits only leaves
/// the engine without a signal and should not enable repair.</para>
/// <para>Repair is a receive-side, monitor-grade enhancement: a repaired frame passed its FCS
/// after the edits, exactly the contract an ordinary decode meets, but a link layer that wants
/// only untouched copies can leave this off (the default) or filter on the edited-bit count
/// its delivery callback reports.</para>
/// </remarks>
/// <param name="CrcSingles">How many of the weakest frame bits get a solo-flip attempt on an
/// FCS failure.</param>
/// <param name="CrcPairWidth">How many of the weakest frame bits are combined into two-flip
/// attempts (every pair among them) after the singles find nothing. Two-flip budgets grow
/// quadratically; the corpus knee sits where a pair reaches either damage of a two-hit burst
/// or both.</param>
/// <param name="SlipPositions">How many of the weakest raw-stream positions get a
/// delete-or-insert realignment attempt on a bit-slip drop.</param>
public sealed record HdlcRepairPolicy(
    int CrcSingles = 64,
    int CrcPairWidth = 20,
    int SlipPositions = 48)
{
    /// <summary>The budgets measured against the WA8LMF corpus - see the type remarks.</summary>
    public static readonly HdlcRepairPolicy Corpus = new();
}

/// <summary>
/// The gates a repaired frame must clear before delivery. A clean FCS pass is never gated -
/// these exist because a repair's bytes were chosen by search, and a search that touches the
/// FCS can satisfy it by coincidence as well as by correction. Everything here is published
/// protocol structure (AX.25 v2.2, NMEA 0183, APRS 1.0.1), not corpus-specific tuning: a
/// frame that cannot show a well-formed address field, a UI control, a known PID, a valid
/// NMEA checksum or an intact APRS position skeleton is not a frame this receiver is willing
/// to have invented.
/// </summary>
internal static class RepairedFrameGates
{
    /// <summary>Structural AX.25 validation: two to ten addresses of A-Z0-9/space characters
    /// with a properly terminated address field, a UI control byte (with or without poll /
    /// final), and one of the PIDs the protocol's users actually carry (0xF0 no layer 3,
    /// 0x08 X.25 layer 3, 0xCC ARP).</summary>
    internal static bool Ax25Structure(ReadOnlySpan<byte> content)
    {
        if (content.Length < HdlcDeframer.MinFrameBytes)
        {
            return false;
        }

        int pos = 0;
        bool last = false;
        int addresses = 0;
        while (!last && addresses < 10)
        {
            if (pos + 7 > content.Length)
            {
                return false;
            }

            for (int i = 0; i < 6; i++)
            {
                char c = (char)(content[pos + i] >> 1);
                if (!((c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == ' '))
                {
                    return false;
                }
            }

            last = (content[pos + 6] & 1) == 1;
            pos += 7;
            addresses++;
        }

        if (!last || addresses < 2 || pos + 2 > content.Length)
        {
            return false;
        }

        // UI frame, with or without poll/final
        byte control = content[pos];
        if (control != 0x03 && control != 0xEF)
        {
            return false;
        }

        byte pid = content[pos + 1];
        return pid is 0xF0 or 0x08 or 0xCC;
    }

    /// <summary>
    /// Loose entry gate deciding whether a failed frame is worth repair attempts at all: the
    /// destination and source addresses are printable shifted ASCII. This filters the noise
    /// floor's accumulations without blocking a real frame whose header carries the very
    /// error the repair is for - delivery applies <see cref="Ax25Structure"/>.
    /// </summary>
    internal static bool LooksAddressed(ReadOnlySpan<byte> content)
    {
        if (content.Length < HdlcDeframer.MinFrameBytes)
        {
            return false;
        }

        for (int i = 0; i < 12; i++)
        {
            char c = (char)(content[i] >> 1);
            if (c < ' ' || c > '~')
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Payload-protocol cross-checks: an NMEA sentence must carry a valid XOR checksum (an
    /// independent second check that a chance FCS pass on a corrupted GPS sentence essentially
    /// never survives), and an APRS position-bearing format must keep its character-class
    /// skeleton (APRS 1.0.1). Formats with no skeleton to check - messages, status text,
    /// telemetry - pass on the AX.25 structure alone.
    /// </summary>
    internal static bool PayloadConsistent(ReadOnlySpan<byte> content)
    {
        var info = InfoField(content);
        if (info.Length >= 8 && info[0] == '$')
        {
            int star = info.IndexOf((byte)'*');
            if (star < 1 || star + 3 > info.Length)
            {
                return false; // an NMEA sentence without a checksum field: not credible
            }

            byte xor = 0;
            for (int i = 1; i < star; i++)
            {
                xor ^= info[i];
            }

            int hi = HexVal(info[star + 1]);
            int lo = HexVal(info[star + 2]);
            if (hi < 0 || lo < 0 || xor != ((hi << 4) | lo))
            {
                return false;
            }
        }

        return AprsPlausible(content, info);

        static int HexVal(byte b) =>
            b is >= (byte)'0' and <= (byte)'9' ? b - '0'
            : b is >= (byte)'A' and <= (byte)'F' ? b - 'A' + 10
            : b is >= (byte)'a' and <= (byte)'f' ? b - 'a' + 10
            : -1;
    }

    /// <summary>The information field of a structurally valid frame: past the address field
    /// and the control and PID bytes.</summary>
    private static ReadOnlySpan<byte> InfoField(ReadOnlySpan<byte> content)
    {
        int pos = 7;
        while (pos + 7 <= content.Length)
        {
            bool lastAddr = (content[pos + 6] & 1) == 1;
            pos += 7;
            if (lastAddr)
            {
                break;
            }
        }

        return pos + 2 <= content.Length ? content[(pos + 2)..] : ReadOnlySpan<byte>.Empty;
    }

    /// <summary>APRS 1.0.1 payload skeleton checks. Not a parser: the character classes of
    /// each position-bearing format at their fixed offsets, which is exactly what a chance
    /// FCS pass on a corrupted payload cannot keep.</summary>
    private static bool AprsPlausible(ReadOnlySpan<byte> content, ReadOnlySpan<byte> info)
    {
        if (info.Length == 0)
        {
            return true;
        }

        switch ((char)info[0])
        {
            case '!' or '=':
                return PositionStrict(info[1..]) || Compressed(info[1..]);
            case '/' or '@':
                // optional timestamp: DDHHMMz/h or HHMMSSz/h, then the position
                if (info.Length > 7 && (info[7] == 'z' || info[7] == 'h' || info[7] == '/'))
                {
                    if (!Digits(info[1..7]))
                    {
                        return false;
                    }

                    return PositionStrict(info[8..]) || Compressed(info[8..]);
                }

                return PositionStrict(info[1..]) || Compressed(info[1..]);
            case '_':
                // weather without position: _MMDDhhmm then the wind/temperature digit fields
                return info.Length >= 9 && Digits(info[1..9]);
            case ';':
                // object: ;name(9) DDHHMM z/h position
                if (info.Length < 17 || !Digits(info[10..16]))
                {
                    return false;
                }

                char zulu = (char)info[16];
                if (zulu is not ('z' or 'h' or '/'))
                {
                    return false;
                }

                for (int i = 1; i < 10 && i < info.Length; i++)
                {
                    if (info[i] < 0x20 || info[i] > 0x7E)
                    {
                        return false;
                    }
                }

                return PositionStrict(info[17..]) || Compressed(info[17..]);
            case '`' or '\'':
                // Mic-E: the destination field carries the latitude digits, each character in
                // the spec's digit ranges 0-9 / A-L / P-Z. The longitude bytes use paired
                // offset ranges per digit position - too wide to skeleton-check cheaply, and
                // the empirical clean dialect on real channels (dots, brackets, lowercase)
                // proves any tight subset would reject genuine frames.
                for (int i = 0; i < 6; i++)
                {
                    char c = (char)(content[i] >> 1);
                    if (!((c >= '0' && c <= '9') || (c >= 'A' && c <= 'L') || (c >= 'P' && c <= 'Z')))
                    {
                        return false;
                    }
                }

                return true;
            default:
                return true; // comments, messages, status, telemetry: nothing to skeleton-check
        }
    }

    private static bool Digits(ReadOnlySpan<byte> s)
    {
        if (s.Length == 0)
        {
            return false;
        }

        foreach (byte b in s)
        {
            if (b < '0' || b > '9')
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Strict uncompressed position: DDMM.hh{N|S}, symbol table, DDDMM.hh{E|W},
    /// symbol code - digits and dots at fixed places and hemisphere letters a corrupted
    /// payload cannot keep.</summary>
    private static bool PositionStrict(ReadOnlySpan<byte> s)
    {
        if (s.Length < 18)
        {
            return false;
        }

        // latitude: DDMM.hhN - digits with the hundredths dot at index 4
        for (int i = 0; i < 7; i++)
        {
            char c = (char)s[i];
            bool ok = i is 4 or 5 or 6 ? (c == '.' || (c >= '0' && c <= '9')) : (c >= '0' && c <= '9');
            if (!ok)
            {
                return false;
            }
        }

        if (s[4] != '.' || s[7] is not ((byte)'N' or (byte)'S'))
        {
            return false;
        }

        if (s[8] < 0x21 || s[8] > 0x7E)
        {
            return false; // symbol table id / overlay
        }

        // longitude: DDDMM.hhW at 9..17
        for (int i = 9; i < 17; i++)
        {
            char c = (char)s[i];
            bool ok = i is 14 or 15 or 16 ? (c == '.' || (c >= '0' && c <= '9')) : (c >= '0' && c <= '9');
            if (!ok)
            {
                return false;
            }
        }

        if (s[14] != '.' || s[17] is not ((byte)'E' or (byte)'W'))
        {
            return false;
        }

        return s.Length <= 18 || (s[18] >= 0x21 && s[18] <= 0x7E);
    }

    /// <summary>Compressed position (APRS 1.0.1 §9): symbol-table character, four base-91
    /// latitude characters decoding to ≤ 90°, four base-91 longitude characters decoding to
    /// ≤ 180°, symbol code.</summary>
    private static bool Compressed(ReadOnlySpan<byte> s)
    {
        if (s.Length < 9)
        {
            return false;
        }

        if (s[0] != '/' && s[0] != '\\' && (s[0] < 'A' || s[0] > 'Z'))
        {
            return false;
        }

        long lat = 0;
        long lon = 0;
        for (int i = 0; i < 4; i++)
        {
            if (s[1 + i] < 0x21 || s[1 + i] > 0x7B || s[5 + i] < 0x21 || s[5 + i] > 0x7B)
            {
                return false;
            }

            lat = (lat * 91) + (s[1 + i] - 0x21);
            lon = (lon * 91) + (s[5 + i] - 0x21);
        }

        return lat <= 90L * 380926 && lon <= 180L * 190463;
    }
}
