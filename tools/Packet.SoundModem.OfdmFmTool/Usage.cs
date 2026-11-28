namespace Packet.SoundModem.OfdmFmTool;

/// <summary>What --help prints.</summary>
internal static class Usage
{
    internal const string Text = """
        pdn-ofdm: drive the OFDM-FM modem directly

        Usage:
          pdn-ofdm listen --device SPEC [--ptt SPEC] --call CALL [OPTIONS]
          pdn-ofdm send   --device SPEC --ptt SPEC --call CALL --to ADDRESS [OPTIONS] TEXT...
          pdn-ofdm ping   --device SPEC --ptt SPEC --call CALL --to ADDRESS [--count N] [--size BYTES] [OPTIONS]
          pdn-ofdm answer --device SPEC --ptt SPEC --call CALL [OPTIONS]
          pdn-ofdm call   --device SPEC --ptt SPEC --call CALL --to CALLSIGN [--get BYTES] [OPTIONS]

        Options:
          --device SPEC    Audio device: an ALSA name such as plughw:CARD=Device,DEV=0,
                           or pipe:IN,OUT[,RATE].
          --ptt SPEC       serial:DEVICE[:rts|:dtr] or cm108:HIDRAW[:GPIO]. Without it
                           listen is receive-only.
          --call CALL      This station's callsign and link address.
          --to ADDRESS     The other station's link address (a callsign, for call).
          --count N        Pings to send (default 5).
          --size BYTES     Ping size (default 32).
          --get BYTES      call: ask for BYTES, time them and disconnect.
          --detail         Trace in detail.
          --log FILE       Append every line to FILE.
          --record FILE    Write the received audio to FILE, 48 kHz mono WAV.
        """;
}
