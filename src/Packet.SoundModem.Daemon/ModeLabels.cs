using System.Text.Json.Nodes;
using Packet.SoundModem.Modems;

namespace Packet.SoundModem.Daemon;

/// <summary>
/// What the configuration page calls each mode, who it talks to, and which group of the "Add a
/// modem" picker it is under.
/// </summary>
/// <remarks>
/// <para><b>One entry per mode, drift-tested.</b> The catalogue knows how to build a mode; this
/// table is the part a person has to write. <c>ConfigPageTests</c> fails when a mode of
/// <see cref="ModemCatalog.KnownModes"/> has no entry here, so a new mode cannot reach the page
/// unlabelled.</para>
/// <para><b>Facts come from docs/05-modes.md.</b> The "Interops with" column there is where every
/// <c>talksTo</c> sentence and every NinoTNC mode number is taken from; a mode that column does
/// not name a far end for says only what the mode is. A NinoTNC's mode number is the decimal of
/// the four switch settings that page gives (1000 is mode 8).</para>
/// <para><b>Groups.</b> A mode has one home group. A mode with a NinoTNC number also appears
/// under "A NinoTNC", which is the one group the picker asks a second question of.</para>
/// </remarks>
internal static class ModeLabels
{
    /// <summary>The group id for a NinoTNC: its members are the modes that carry a mode number.</summary>
    internal const string NinoGroup = "nino";

    /// <summary>One group of the picker.</summary>
    /// <param name="Id">Its id, as <see cref="Label.Group"/> names it.</param>
    /// <param name="Title">What it is called on the button.</param>
    /// <param name="Blurb">The line under the title.</param>
    internal sealed record Group(string Id, string Title, string Blurb);

    /// <summary>One mode's words.</summary>
    /// <param name="Name">The mode in plain words: the title of its card.</param>
    /// <param name="TalksTo">What it talks to; the card reads "Talks to" and this.</param>
    /// <param name="Group">Its home group.</param>
    /// <param name="NinoMode">The NinoTNC mode number that matches it, or null.</param>
    internal sealed record Label(string Name, string TalksTo, string Group, int? NinoMode = null);

    /// <summary>The picker's groups, in the order it lists them.</summary>
    internal static readonly Group[] Groups =
    [
        new(NinoGroup, "A NinoTNC", "Pick its mode number next; the modem matches it"),
        new("p1200", "1200 baud packet", "Most VHF and UHF APRS and packet nodes"),
        new("p9600", "9600 baud packet and faster", "FM radios with a 9600 data port, and its relatives"),
        new("hf300", "300 baud HF packet", "AFSK and BPSK on the HF bands"),
        new("hffast", "Faster HF packet", "PSK packet for a steadier HF path"),
        new("hfdata", "Mailcast and HF data", "MIL-STD-188-110D and FreeDV data"),
        new("ofdmfm", "Fast data over FM", "OFDM across an ordinary FM radio, between two pdn-soundmodem stations"),
        new("ardop", "ARDOP", "For Winlink and other ARDOP software"),
    ];

    private static readonly string[] Ms110dRates =
    [
        "75 bps Walsh fallback", "BPSK rate 1/8", "BPSK rate 1/4", "BPSK rate 1/3", "BPSK rate 2/3",
        "BPSK rate 3/4", "QPSK rate 3/4", "8PSK rate 3/4", "16QAM rate 3/4", "QPSK rate 9/16",
    ];

    private static readonly int[] Ms110dWaveforms = [0, 1, 2, 3, 4, 5, 6, 7, 8, 13];

    /// <summary>Every mode's entry, by the name the catalogue knows it by; <c>ardop</c> is in it too.</summary>
    internal static IReadOnlyDictionary<string, Label> Table => Cached.Value;

    private static readonly Lazy<Dictionary<string, Label>> Cached = new(Build);

    /// <summary>The entry for a mode, or null when it has none (a test fails on that).</summary>
    internal static Label? For(string mode) => Table.TryGetValue(mode, out Label? label) ? label : null;

    /// <summary>The modes and groups as the schema serves them.</summary>
    internal static JsonObject Json()
    {
        var groups = new JsonArray();
        foreach (Group g in Groups)
        {
            groups.Add(new JsonObject { ["id"] = g.Id, ["title"] = g.Title, ["blurb"] = g.Blurb });
        }

        var list = new JsonArray();
        foreach ((string mode, Label label) in Table)
        {
            bool ardop = DaemonConfig.IsArdop(mode);
            var entry = new JsonObject
            {
                ["mode"] = mode,
                ["name"] = label.Name,
                ["talksTo"] = label.TalksTo,
                ["group"] = label.Group,
                ["tunable"] = ardop || ModemCatalog.AcceptsCentreFrequency(mode),
            };
            if (label.NinoMode is int nino)
            {
                entry["ninoMode"] = nino;
            }

            entry["widthHz"] = WidthHz(mode);

            double? centre = ardop ? ArdopChannelBridge.NativeCentreHz : ModemCatalog.DefaultCentreFrequencyFor(mode);
            if (centre is double c)
            {
                entry["defaultCentreHz"] = c;
            }

            list.Add(entry);
        }

        return new JsonObject { ["groups"] = groups, ["list"] = list };
    }

    private static readonly Dictionary<string, double> Widths = new(StringComparer.Ordinal);

    /// <summary>
    /// How much audio a mode occupies, as the band planner would plan it (measured off the modem,
    /// ARDOP at its widest), so the page can find room for a new modem before the station has
    /// been asked. Measured once per mode per process; a probe renders a frame, which is not free.
    /// </summary>
    internal static double WidthHz(string mode)
    {
        lock (Widths)
        {
            if (!Widths.TryGetValue(mode, out double width))
            {
                int rate = DaemonConfig.IsArdop(mode) ? 12000 : ModemCatalog.DspRateFor(mode);
                width = Math.Round(BandPlanner.WidthOf(new ModemConfig { Mode = mode }, rate));
                Widths[mode] = width;
            }

            return width;
        }
    }

    private static Dictionary<string, Label> Build()
    {
        var table = new Dictionary<string, Label>(StringComparer.Ordinal);

        void M(string mode, string name, string talksTo, string group, int? nino = null) =>
            table.Add(mode, new Label(name, talksTo, group, nino));

        const string NinoIl2p = "other IL2P+CRC stations";
        const string QtV26 = "QtSoundModem V26A";

        // ------------------------------------------------------------ 1200 baud AFSK
        M("afsk1200", "1200 baud AFSK packet", "Most VHF and UHF APRS and packet nodes: a NinoTNC, Dire Wolf, QtSoundModem", "p1200");
        M("afsk1200-multi", "1200 baud AFSK packet, extra decoders", "The same stations as 1200 baud AFSK packet, with a bank of decoders for ones a little off tune", "p1200");
        M("afsk1200-fx25", "1200 baud AFSK with FX.25", "Dire Wolf stations that add FX.25 error correction", "p1200");
        M("afsk1200-fx25rx", "1200 baud AFSK, hearing FX.25", "Dire Wolf stations that add FX.25 error correction (this modem only receives it)", "p1200");
        M("afsk1200-il2p", "1200 baud AFSK with IL2P", $"NinoTNC mode 7 (0111), Dire Wolf IL2P and {NinoIl2p}", "p1200", 7);
        M("afsk1200-il2p-nocrc", "1200 baud AFSK with IL2P, no CRC", "1200 baud IL2P stations that send no CRC (untested)", "p1200");

        // ------------------------------------------------------------ 300 baud HF
        M("afsk300", "300 baud AFSK packet", "NinoTNC mode 12 (1100) and other plain 300 baud AFSK stations", "hf300", 12);
        M("afsk300-il2p", "300 baud AFSK with IL2P", "NinoTNC mode 13 (1101) and other 300 baud IL2P stations", "hf300", 13);
        M("afsk300-il2pc", "300 baud AFSK with IL2P+CRC", $"NinoTNC mode 14 (1110) and {NinoIl2p}, as on the UK 40 m IL2P network", "hf300", 14);
        M("bpsk300", "300 baud BPSK", $"NinoTNC mode 8 (1000), {QtV26} and {NinoIl2p}", "hf300", 8);
        M("bpsk300-multi", "300 baud BPSK, under its other name", "The same stations as 300 baud BPSK: this is that modem, spelled differently", "hf300");
        M("bpsk300-nocrc", "300 baud BPSK, no CRC", "300 baud IL2P stations that send no CRC (untested)", "hf300");

        // ------------------------------------------------------------ faster PSK
        M("bpsk1200", "1200 baud BPSK", $"NinoTNC mode 10 (1010), {QtV26} and {NinoIl2p}", "hffast", 10);
        M("bpsk1200-multi", "1200 baud BPSK, under its other name", "The same stations as 1200 baud BPSK: this is that modem, spelled differently", "hffast");
        M("qpsk600", "600 baud QPSK", $"NinoTNC mode 9 (1001), {QtV26} and {NinoIl2p}", "hffast", 9);
        M("qpsk2400", "2400 baud QPSK", $"NinoTNC mode 11 (1011), {QtV26} type 12 (not the older type 10) and {NinoIl2p}", "hffast", 11);

        // ------------------------------------------------------------ 9600 and faster
        M("qpsk3600", "3600 baud QPSK, for FM", $"NinoTNC mode 5 (0101) and {NinoIl2p}; it fills one FM voice channel", "p9600", 5);
        M("fsk9600", "9600 baud packet (G3RUH)", "NinoTNC mode 0 (0000), Dire Wolf and QtSoundModem on a 9600 data port", "p9600", 0);
        M("fsk9600-il2p", "9600 baud packet with IL2P", "NinoTNC mode 2 (0010) on a 9600 data port", "p9600", 2);
        M("fsk4800-il2p", "4800 baud packet with IL2P", "NinoTNC mode 4 (0100), Dire Wolf and QtSoundModem 4800 baud stations", "p9600", 4);
        M("c4fsk9600", "9600 baud 4-level FSK", "NinoTNC mode 3 (0011) and MMDVM-TNC", "p9600", 3);
        M("c4fsk19200", "19200 baud 4-level FSK", "NinoTNC mode 1 (0001) and MMDVM-TNC", "p9600", 1);

        // ------------------------------------------------------------ FreeDV and MS110D
        const string FreeDv = "Another pdn-soundmodem running the same FreeDV mode";
        M("freedv-datac0", "FreeDV DATAC0, 291 bps", FreeDv, "hfdata");
        M("freedv-datac1", "FreeDV DATAC1, 980 bps", FreeDv, "hfdata");
        M("freedv-datac3", "FreeDV DATAC3, 321 bps", FreeDv, "hfdata");
        M("freedv-datac4", "FreeDV DATAC4, 87 bps", FreeDv, "hfdata");
        M("freedv-datac13", "FreeDV DATAC13, 64 bps", FreeDv, "hfdata");
        M("freedv-datac14", "FreeDV DATAC14, 58 bps", FreeDv, "hfdata");
        for (int i = 0; i < Ms110dWaveforms.Length; i++)
        {
            int wn = Ms110dWaveforms[i];
            M($"ms110d-wn{wn}", $"MIL-STD-188-110D waveform {wn}, {Ms110dRates[i]}",
                "Other MIL-STD-188-110D Appendix D stations, such as Mailcast; it hears every waveform and sends this one", "hfdata");
        }

        // ------------------------------------------------------------ OFDM over FM
        const string Ofdm = "Another pdn-soundmodem running OFDM-FM";
        M("ofdm-fm-narrow", "OFDM over FM, voice width", $"{Ofdm}; the one that fits a voice-bandwidth path", "ofdmfm");
        M("ofdm-fm-6k", "OFDM over FM, 6 kHz", $"{Ofdm}; the robust fallback for a data port", "ofdmfm");
        M("ofdm-fm-6k-fast", "OFDM over FM, 6 kHz fast", Ofdm, "ofdmfm");
        M("ofdm-fm-8k", "OFDM over FM, 8 kHz", $"{Ofdm}; the one to start with", "ofdmfm");
        M("ofdm-fm-8k-r56", "OFDM over FM, 8 kHz faster", Ofdm, "ofdmfm");
        M("ofdm-fm-8k-r78", "OFDM over FM, 8 kHz fastest", $"{Ofdm}; marks where the margin runs out", "ofdmfm");
        M("ofdm-fm-8k-follow", "OFDM over FM, 8 kHz follow-on", $"{Ofdm}; later frames of a keyup follow the first", "ofdmfm");
        M("ofdm-fm-8k-adaptive", "OFDM over FM, 8 kHz adaptive", $"{Ofdm}, point to point; one controller per channel", "ofdmfm");

        // ------------------------------------------------------------ ARDOP (not a catalogue mode)
        M("ardop", "ARDOP", "Pat, Winlink Express and other ARDOP software", "ardop");

        return table;
    }
}
