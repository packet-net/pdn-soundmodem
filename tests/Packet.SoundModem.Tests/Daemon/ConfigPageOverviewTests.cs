using System.Net.Http;
using System.Text.Json.Nodes;
using Jint;
using Packet.SoundModem.Daemon;

namespace Packet.SoundModem.Tests.Daemon;

/// <summary>
/// The overview's plain sentences and its "worth a look" nudges: the pure part of the page, run
/// in Jint against a wide range of configurations, odd ones included.
/// </summary>
public partial class ConfigPageTests
{
    private const string FoundMachine = """
        {
          "soundCards": { "items": [ { "id": "Device", "number": 1, "name": "USB Audio Device", "device": "plughw:CARD=Device,DEV=0" } ] },
          "hidDevices": { "items": [ { "path": "/dev/hidraw0", "name": "C-Media CM108" } ] },
          "flexRadios": { "items": [ { "name": "Shack", "model": "FLEX-6500", "serial": "1234", "ip": "10.45.0.76", "device": "flex:10.45.0.76" } ] },
          "uberSdr": { "items": [ { "host": "m9psy-1.example.org", "device": "ubersdr:m9psy-1.example.org", "name": "M9PSY-1", "location": "Reading", "free": true } ] }
        }
        """;

    private const string UsbPlan = """
        { "ok": true, "radio": "usb", "axis": "rf", "dialHz": 7050000, "hasDial": true,
          "dspRateHz": 12000, "limitAudioLowHz": 300, "limitAudioHighHz": 2700, "passband": { "audioLowHz": 300, "audioHighHz": 2700 },
          "modems": [ { "subChannel": 0, "mode": "bpsk300" }, { "subChannel": 1, "mode": "qpsk600" } ], "warnings": [] }
        """;

    private const string FlexPlan = """
        { "ok": true, "radio": "usb", "axis": "rf", "dialHz": 7050000, "hasDial": true, "dspRateHz": 12000,
          "limitAudioLowHz": 300, "limitAudioHighHz": 10000, "passband": { "audioLowHz": 300, "audioHighHz": 3100 },
          "modems": [], "warnings": [] }
        """;

    private static string[] Lines(string doc, string area, string facts = "{}")
    {
        Engine js = Model();
        js.SetValue("schemaJson", ConfigSchema.Json);
        js.SetValue("docJson", doc);
        js.SetValue("factsJson", facts);
        js.SetValue("areaId", area);
        string answer = js.Evaluate("""
            JSON.stringify(ConfigModel.describe(JSON.parse(schemaJson), JSON.parse(docJson), areaId, JSON.parse(factsJson)))
            """).AsString();
        return [.. JsonNode.Parse(answer)!.AsArray().Select(n => n!.GetValue<string>())];
    }

    private static (string Id, string Text, string Area, string Action)[] Nudges(string doc, string facts = "{}")
    {
        Engine js = Model();
        js.SetValue("schemaJson", ConfigSchema.Json);
        js.SetValue("docJson", doc);
        js.SetValue("factsJson", facts);
        string answer = js.Evaluate("""
            JSON.stringify(ConfigModel.nudgesFor(JSON.parse(schemaJson), JSON.parse(docJson), JSON.parse(factsJson)).list)
            """).AsString();
        return [.. JsonNode.Parse(answer)!.AsArray().Select(n => (
            n!["id"]!.GetValue<string>(), n["text"]!.GetValue<string>(), n["area"]!.GetValue<string>(), n["action"]!.GetValue<string>()))];
    }

    private static string[] NudgeIds(string doc, string facts = "{}") => [.. Nudges(doc, facts).Select(n => n.Id)];

    // ---------------------------------------------------------------- radio and audio

    [Fact]
    public void The_Radio_Sentence_For_A_Flex_Says_The_Model_The_Slice_And_The_Dax_Channel()
    {
        string[] lines = Lines("""{ "device": "flex:10.45.0.76:A", "flex": { "daxChannel": "1" } }""", "radio",
            $$"""{ "found": {{FoundMachine}}, "plan": {{FlexPlan}} }""");

        lines[0].Should().Be("A FlexRadio 6500 called \"Shack\" at 10.45.0.76, slice A, receiving and transmitting through DAX channel 1.");
        lines[1].Should().Be("Dial 7.0500 MHz, upper sideband, 2.8 kHz wide.");
    }

    [Theory]
    [InlineData("""{ "device": "flex:discover" }""", "A FlexRadio found on the network when the station starts, on a slice it makes itself, receiving and transmitting through DAX channel 2.")]
    [InlineData("""{ "device": "flex:mock:B" }""", "A pretend FlexRadio on this machine, slice B, receiving and transmitting through DAX channel 2.")]
    [InlineData("""{ "device": "flex:serial=1234:C", "flex": { "receiveOnly": true } }""", "A FlexRadio with serial 1234, slice C, receiving only through DAX channel 2.")]
    [InlineData("""{ "device": "flex:name=Loft" }""", "The FlexRadio named \"Loft\", on a slice it makes itself, receiving and transmitting through DAX channel 2.")]
    [InlineData("""{ "device": "flex:192.168.1.9:4992@Bob" }""", "A FlexRadio at 192.168.1.9:4992, using the slice of the running SmartSDR station \"Bob\", receiving and transmitting through DAX channel 1.")]
    public void The_Radio_Sentence_For_A_Flex_Follows_Every_Form_Of_The_Device_String(string doc, string expected) =>
        Lines(doc, "radio")[0].Should().Be(expected);

    [Theory]
    [InlineData("""{ "device": "plughw:CARD=Device,DEV=0" }""", "Audio goes in and out through the sound card \"USB Audio Device\".")]
    [InlineData("""{ "device": "hw:1,0" }""", "Audio goes in and out through the sound card \"USB Audio Device\".")]
    [InlineData("""{ "device": "plughw:CARD=Other,DEV=0" }""", "Audio goes in and out through the sound card plughw:CARD=Other,DEV=0.")]
    [InlineData("""{ }""", "Audio goes in and out through the system's default sound card.")]
    [InlineData("""{ "device": "default" }""", "Audio goes in and out through the system's default sound card.")]
    [InlineData("""{ "device": "plughw:CARD=Device,DEV=0", "playbackDevice": "plughw:CARD=Other,DEV=0" }""",
        "Receiving through the sound card \"USB Audio Device\" and transmitting through the sound card plughw:CARD=Other,DEV=0.")]
    [InlineData("""{ "device": "null" }""", "No audio device is set, so the station neither hears nor sends anything.")]
    [InlineData("""{ "device": "pipe:/tmp/a,/tmp/b" }""", "Two named pipes stand in for the sound card and the radio, for testing.")]
    [InlineData("""{ "device": "ubersdr:m9psy-1.example.org" }""", "The UberSDR web receiver \"M9PSY-1\" in Reading (m9psy-1.example.org), listening only.")]
    [InlineData("""{ "device": "ubersdr:https://far.example.net:8443/x" }""", "The UberSDR web receiver at far.example.net:8443, listening only.")]
    [InlineData("""{ "device": "openwebrx:https://sdr.example.net/" }""", "The OpenWebRX web receiver at sdr.example.net, listening only.")]
    [InlineData("""{ "device": "iqcard:plughw:CARD=Device,DEV=0" }""", "IQ from the left and right inputs of the sound card \"USB Audio Device\", listening only.")]
    [InlineData("""{ "device": "rtltcp:[::1]:1234" }""", "An RTL-SDR behind an rtl_tcp server at [::1]:1234, listening only.")]
    [InlineData("""{ "device": "sdrconnect:10.0.0.4" }""", "An SDRplay radio through SDRconnect at 10.0.0.4, listening only.")]
    [InlineData("""{ "device": "iqpipe:-" }""", "Raw IQ from a command or a pipe, listening only.")]
    public void The_Radio_Sentence_Says_What_The_Audio_Device_Is_For_Every_Kind(string doc, string expected) =>
        Lines(doc, "radio", $$"""{ "found": {{FoundMachine}} }""")[0].Should().Be(expected);

    [Fact]
    public void A_Sound_Card_That_Is_Named_Also_Keeps_Its_Device_String_As_Small_Print()
    {
        string[] lines = Lines("""{ "device": "plughw:CARD=Device,DEV=0" }""", "radio", $$"""{ "found": {{FoundMachine}}, "plan": {{UsbPlan}} }""");

        lines.Should().Equal(
            "Audio goes in and out through the sound card \"USB Audio Device\".",
            "Dial 7.0500 MHz, upper sideband, taken to pass 300 to 2700 Hz.",
            "Device: plughw:CARD=Device,DEV=0");
    }

    [Fact]
    public void The_Band_Sentence_Falls_Back_To_The_File_When_There_Is_No_Plan_Yet()
    {
        Lines("""{ "sideband": "lsb", "dialFrequency": 3600000 }""", "radio")
            .Should().Contain("Dial 3.6000 MHz, lower sideband.");
        Lines("""{ "device": "plughw:CARD=Device,DEV=0" }""", "radio", $$"""{ "found": {{FoundMachine}} }""")
            .Should().Contain("Device: plughw:CARD=Device,DEV=0")
            .And.Contain("Upper sideband. No dial is set, so the modems are placed by audio tone.");
        Lines("""{ "device": "flex:discover", "flex": { "mode": "DIGL" } }""", "radio")
            .Should().Contain("Slice at 14.1000 MHz, lower sideband.");
    }

    [Fact]
    public void An_Fm_Station_Says_Fm_And_The_Channel_Not_A_Dial_Or_A_Passband()
    {
        Lines("""{ "sideband": "fm", "modems": [ { "mode": "afsk1200", "rfFrequency": 144800000 } ] }""", "radio",
            """{ "plan": { "ok": true, "radio": "fm", "axis": "audio", "dialHz": null, "hasDial": false, "passband": null, "modems": [], "warnings": [] } }""")
            .Should().Contain("FM, on the channel at 144.8000 MHz.");
        Lines("""{ "sideband": "fm" }""", "radio").Should().Contain("FM. The modems are placed by audio tone, not by radio frequency.");
    }

    [Fact]
    public void The_Rig_Section_Is_Mentioned_With_Where_Rigctld_Listens()
    {
        Lines("""{ "rig": { } }""", "radio").Should().Contain("The radio is controlled through Hamlib's rigctld at 127.0.0.1:4532.");
        Lines("""{ "rig": { "rigctld": "shack.lan:4533" } }""", "radio").Should().Contain("The radio is controlled through Hamlib's rigctld at shack.lan:4533.");
    }

    // ---------------------------------------------------------------- keying

    [Theory]
    [InlineData("""{ }""", "Nothing keys the radio: it relies on its own VOX, or has no keying line.")]
    [InlineData("""{ "ptt": { "type": "cm108", "device": "/dev/hidraw0" } }""", "The PTT pin (GPIO 3) of the CM108-type interface \"C-Media CM108\" at /dev/hidraw0 keys the radio.")]
    [InlineData("""{ "ptt": { "type": "cm108", "device": "/dev/hidraw9", "gpio": 4 } }""", "The PTT pin (GPIO 4) of the CM108-type interface at /dev/hidraw9 keys the radio.")]
    [InlineData("""{ "ptt": { "type": "serial", "device": "/dev/ttyUSB0" } }""", "The RTS line of the serial port /dev/ttyUSB0 keys the radio.")]
    [InlineData("""{ "ptt": { "device": "/dev/ttyUSB1", "line": "dtr" } }""", "The DTR line of the serial port /dev/ttyUSB1 keys the radio.")]
    [InlineData("""{ "ptt": { "type": "rigctld" }, "rig": { "rigctld": "10.0.0.2:4532" } }""", "Hamlib's rigctld at 10.0.0.2:4532 keys the radio over its control cable.")]
    [InlineData("""{ "ptt": { "type": "serial" } }""", "The RTS line of the serial port (no port chosen yet) keys the radio.")]
    [InlineData("""{ "device": "flex:discover" }""", "The radio keys itself; this station asks it to transmit over the network.")]
    [InlineData("""{ "device": "flex:discover", "flex": { "arbitration": true } }""", "The radio keys itself; this station asks it to transmit over the network, after waiting for the radio to be quiet.")]
    [InlineData("""{ "device": "flex:discover", "flex": { "receiveOnly": true } }""", "This station only listens, so it never keys the FlexRadio.")]
    [InlineData("""{ "device": "ubersdr:x.example" }""", "This station only listens, so there is no radio to key.")]
    [InlineData("""{ "device": "null" }""", "There is no audio device, so nothing is keyed.")]
    [InlineData("""{ "device": "pipe:a,b" }""", "There is no radio on a pipe, so nothing is keyed.")]
    public void The_Keying_Sentence_Says_How_The_Radio_Is_Keyed(string doc, string expected) =>
        Lines(doc, "keying", $$"""{ "found": {{FoundMachine}} }""")[0].Should().Be(expected);

    [Theory]
    [InlineData("""{ }""", "Carrier sense listens to the audio.")]
    [InlineData("""{ "carrierSense": { "audioFallback": false } }""", "Carrier sense uses only the modems' own energy detectors.")]
    [InlineData("""{ "carrierSense": { "radio": "tait", "port": "/dev/ttyUSB1" } }""", "Carrier sense asks the radio over /dev/ttyUSB1.")]
    [InlineData("""{ "carrierSense": { "port": "/dev/ttyUSB1", "busyAboveDbm": -75 } }""", "Carrier sense asks the radio over /dev/ttyUSB1, and also counts a signal above -75 dBm as busy.")]
    [InlineData("""{ "carrierSense": { "radio": "none", "port": "/dev/ttyUSB1" } }""", "Carrier sense listens to the audio.")]
    public void The_Carrier_Sense_Line_Follows_The_Section(string doc, string expected) =>
        Lines(doc, "keying").Last().Should().Be(expected);

    // ---------------------------------------------------------------- modes

    [Fact]
    public void The_Modes_Sentence_Lists_Every_Modem_With_Its_Frequency_And_Whether_They_Fit()
    {
        string[] lines = Lines("""
            { "modems": [ { "subChannel": 0, "mode": "bpsk300", "rfFrequency": 7050800 },
                          { "subChannel": 1, "mode": "qpsk600", "rfFrequency": 7051600 } ] }
            """, "modes", $$"""{ "plan": {{UsbPlan}} }""");

        lines[0].Should().Be("Two modems: "
            + "300 baud BPSK".Replace("300 baud BPSK", Name("bpsk300")) + " on 7.0508 MHz and " + Name("qpsk600") + " on 7.0516 MHz.");
        lines[1].Should().Be("Both fit inside the radio's passband.");
    }

    private static string Name(string mode)
    {
        Engine js = Model();
        js.SetValue("schemaJson", ConfigSchema.Json);
        js.SetValue("mode", mode);
        return js.Evaluate("ConfigModel.modeInfo(JSON.parse(schemaJson), mode).name").AsString();
    }

    [Fact]
    public void The_Modes_Sentence_Counts_One_Three_And_Many_And_Says_What_Is_Left_Out()
    {
        Lines("""{ "modems": [ { "mode": "bpsk300", "frequency": 1500 } ] }""", "modes")[0]
            .Should().Be("One modem: " + Name("bpsk300") + " at an audio tone of 1500 Hz.");
        Lines("""{ "modems": [ { "mode": "bpsk300" } ] }""", "modes")[0]
            .Should().Be("One modem: " + Name("bpsk300") + " at its usual audio tone, 1500 Hz.");
        Lines("""{ "modems": [ { "subChannel": 0, "mode": "bpsk300" }, { "subChannel": 1, "mode": "bpsk300" }, { "subChannel": 2, "mode": "bpsk300" } ] }""", "modes",
            """{ "plan": { "ok": true, "radio": "usb", "axis": "rf", "passband": { "audioLowHz": 300, "audioHighHz": 2700 }, "modems": [], "warnings": [] } }""")
            .Should().Contain("All three fit inside the radio's passband.");
        Lines("""{ "modems": [ { "mode": "bpsk300" }, {}, {}, {}, {}, {}, {}, {}, {}, {}, {} ] }""", "modes")[0].Should().StartWith("11 modems: ");
        Lines("""{ }""", "modes")[0].Should().StartWith("No modems are listed, so the station runs the default one: ");
        Lines("""{ "ardop": { } }""", "modes")[0].Should().Be("No modems are listed; ARDOP runs from its own section.");
        Lines("""{ "modems": [ { "mode": "bpsk300", "transmitOnlyUnderLease": true, "rfFrequency": 7053800 } ] }""", "modes")[0]
            .Should().EndWith(" on 7.0538 MHz (transmitting only under a lease).");
    }

    [Fact]
    public void The_Modes_Sentence_Says_When_A_Modem_Is_Outside_The_Passband_Or_Overlaps_Another()
    {
        const string plan = """
            { "ok": true, "radio": "usb", "axis": "rf", "dialHz": 7050000, "passband": { "audioLowHz": 300, "audioHighHz": 2700 },
              "modems": [ { "subChannel": 0, "mode": "bpsk300" }, { "subChannel": 1, "mode": "bpsk300" }, { "subChannel": 2, "mode": "qpsk600" } ],
              "warnings": [ { "subChannel": 2, "kind": "outside", "text": "x" },
                            { "subChannel": 0, "kind": "overlap", "other": 1, "text": "y" }, { "subChannel": 1, "kind": "overlap", "other": 0, "text": "y" } ] }
            """;
        string[] lines = Lines("""{ "modems": [ { "subChannel": 0, "mode": "bpsk300" }, { "subChannel": 1, "mode": "bpsk300" }, { "subChannel": 2, "mode": "qpsk600" } ] }""",
            "modes", $$"""{ "plan": {{plan}} }""");

        lines.Should().Contain(Name("qpsk600") + " falls outside what the radio hears.");
        lines.Should().Contain(Name("bpsk300") + " (sub-channel 0) and " + Name("bpsk300") + " (sub-channel 1) overlap on the band.", "a pair is said once, not once each way");
        lines.Should().NotContain(l => l.Contains("fit inside"), "it does not claim a fit when a modem is outside");
    }

    [Fact]
    public void On_Fm_The_Modes_Sentence_Says_The_Modems_Share_The_Channel_And_Claims_No_Passband()
    {
        string[] lines = Lines("""{ "sideband": "fm", "modems": [ { "subChannel": 0, "mode": "afsk1200" }, { "subChannel": 1, "mode": "fsk9600" } ] }""", "modes");

        lines.Should().HaveCount(2);
        lines[1].Should().Be("On FM every modem shares the one channel the radio is set to.");
        Lines("""{ "sideband": "fm", "modems": [ { "mode": "afsk1200" } ] }""", "modes").Should().HaveCount(1);
    }

    [Fact]
    public void The_Modes_Sentence_Makes_No_Claim_About_A_Plan_That_Refused_Or_Has_Not_Come()
    {
        Lines("""{ "modems": [ { "mode": "bpsk300" } ] }""", "modes", """{ "plan": { "ok": false, "problem": "no" } }""").Should().HaveCount(1);
        Lines("""{ "modems": [ { "mode": "bpsk300" } ] }""", "modes").Should().HaveCount(1);
        Lines("""{ "modems": [ { "mode": "bpsk300" } ] }""", "modes",
            """{ "plan": { "ok": true, "radio": "usb", "axis": "audio", "passband": { "audioLowHz": 300, "audioHighHz": 2700 }, "modems": [], "warnings": [] } }""")
            .Should().HaveCount(1, "with no radio frequencies the passband is only the modems' own span, so fitting it says nothing");
    }

    // ---------------------------------------------------------------- connect your software

    [Theory]
    [InlineData("""{ }""", "Node and BBS software connect over KISS to port 8105 on this machine only.")]
    [InlineData("""{ "bind": "127.0.0.1", "kissPort": 9000 }""", "Node and BBS software connect over KISS to port 9000 on this machine only.")]
    [InlineData("""{ "bind": "*" }""", "Node and BBS software connect over KISS to port 8105 on every network interface.")]
    [InlineData("""{ "bind": "0.0.0.0" }""", "Node and BBS software connect over KISS to port 8105 on every network interface.")]
    [InlineData("""{ "bind": "192.168.1.5", "kissPort": 8100 }""", "Node and BBS software connect over KISS to port 8100 on the interface 192.168.1.5.")]
    [InlineData("""{ "bind": "::1" }""", "Node and BBS software connect over KISS to port 8105 on this machine only.")]
    [InlineData("""{ "bind": "" }""", "Node and BBS software connect over KISS to port 8105 on this machine only.")]
    public void The_Software_Sentence_Says_The_Port_And_Who_Can_Reach_It(string doc, string expected) =>
        Lines(doc, "software")[0].Should().Be(expected);

    [Fact]
    public void The_Software_Area_Says_About_Extra_Ports_Polyglot_And_Who_Is_Connected()
    {
        string[] lines = Lines("""
            { "polyglotPort": 8120, "paging": { },
              "modems": [ { "subChannel": 0, "mode": "bpsk300", "port": 8110 }, { "subChannel": 1, "mode": "ardop" }, { "subChannel": 2, "mode": "qpsk600" } ] }
            """, "software");

        lines.Should().Contain("Port 8120 puts all two packet modems behind one KISS port, sending each frame in the mode its next hop was last heard in.");
        lines.Should().Contain(Name("bpsk300") + " also has a KISS port of its own, 8110.");
        lines.Should().Contain("ARDOP listens on port 8515, with its data on port 8516.");
        lines.Should().Contain("POCSAG paging listens on port 8106.");
        Lines("""{ "polyglot": [ { "port": 8120, "subChannels": [0, 1], "default": 0 } ] }""", "software")
            .Should().Contain("Port 8120 puts sub-channels 0 and 1 behind one KISS port, sending each frame in the mode its next hop was last heard in.");

        Lines("""{ }""", "software").Should().HaveCount(1, "who is connected is not said until the station page socket has told us");
        Lines("""{ }""", "software", """{ "hosts": [ { "port": 8105, "clients": 0 } ] }""").Last().Should().Be("Nothing is connected to it now.");
        Lines("""{ }""", "software", """{ "hosts": [ { "port": 8105, "clients": 1 } ] }""").Last().Should().Be("One program is connected to it now.");
        Lines("""{ }""", "software", """{ "hosts": [ { "port": 8105, "clients": 2 }, { "port": 8110, "clients": 1 } ] }""").Last()
            .Should().Be("Two programs are connected to it now. One more is connected to the other ports.");
        Lines("""{ "device": "ubersdr:x.example" }""", "software").Should().Contain("This station only listens, so frames sent to it over KISS are dropped.");
    }

    // ---------------------------------------------------------------- sharing and logging

    [Theory]
    [InlineData("""{ "waterfall": { "port": 8099 }, "bind": "*", "api": { "key": "k" } }""", "The station page is at port 8099 on every network interface, and asks for the access key before any change.")]
    [InlineData("""{ "waterfall": { } }""", "The station page is at port 8107 on this machine only. It asks for no key, but only this machine can reach it.")]
    [InlineData("""{ "waterfall": { "port": 8099 }, "bind": "10.0.0.5" }""", "The station page is at port 8099 on the interface 10.0.0.5, and asks for no key, so anyone who can reach it can change settings.")]
    [InlineData("""{ "waterfall": { "port": 8099, "public": true } }""", "The station page is public, at port 8099 on this machine only: visitors can watch it, and it never carries the mixer or the transmitter test.")]
    [InlineData("""{ }""", "No station page is set up.")]
    public void The_Sharing_Sentence_Says_Where_The_Station_Page_Is_And_Who_May_Change_Settings(string doc, string expected) =>
        Lines(doc, "sharing")[0].Should().Be(expected);

    [Fact]
    public void The_Sharing_Area_Says_About_The_Frame_Log_The_Monitor_And_Metrics()
    {
        Lines("""{ "waterfall": { } }""", "sharing")[1].Should().Be("Heard frames are not kept. Not sent to a public monitor.");
        string[] lines = Lines("""
            { "waterfall": { }, "frameLog": { "path": "/var/lib/x/frames.db" }, "metrics": { },
              "publish": { "url": "wss://monitor.example.org/uplink", "callsign": "GB7XYZ" } }
            """, "sharing");
        lines[1].Should().Be("Heard frames are kept in /var/lib/x/frames.db. This station is offered on the monitor at monitor.example.org as GB7XYZ.");
        lines[2].Should().Be("Prometheus metrics are served at /metrics on the station page's port, with no key.");
        Lines("""{ "waterfall": { }, "frameLog": { } }""", "sharing")[1].Should().StartWith("Heard frames are kept in frames.db in the station's state folder.");
        Lines("""{ "waterfall": { }, "metrics": { "enabled": false } }""", "sharing").Should().HaveCount(2, "metrics that are switched off are not served");
    }

    // ---------------------------------------------------------------- whatever the file holds

    [Fact]
    public void Every_Sentence_For_A_File_With_Every_Key_Set_Is_Plain_Ascii_And_Names_No_Undefined()
    {
        JsonObject all = EveryKeySet();
        all.Remove("monitor");
        string[] docs =
        [
            all.ToJsonString(),
            "{}",
            """{ "modems": "none", "ptt": "serial", "waterfall": 5, "flex": [], "rig": true, "frameLog": "x", "device": 4, "bind": 9, "kissPort": "high" }""",
            """{ "modems": [ null, 3, "x", { "mode": 7 }, { "mode": "nonesuch", "rfFrequency": "soon", "port": "p" } ], "polyglot": [ null, { } ], "polyglotPort": "x" }""",
            """{ "device": "flex:", "carrierSense": { "port": 5 }, "ptt": { "type": 4, "device": 5 }, "publish": { }, "metrics": [] }""",
            """{ "device": "ubersdr:", "sideband": 4, "dialFrequency": "x", "paging": 1 }""",
            """{ "device": "pipe:", "captureDevice": "", "playbackDevice": 3 }""",
            """{ "device": "iqcard:", "waterfall": { "port": "x", "public": "yes" }, "api": 7 }""",
        ];
        const string facts = $$"""
            { "found": {{FoundMachine}}, "plan": {{UsbPlan}},
              "hosts": [ { "port": 8105, "clients": 1 } ], "source": "ephemeral" }
            """;

        foreach (string doc in docs)
        {
            foreach (string area in (string[])["radio", "keying", "modes", "software", "sharing", "advanced"])
            {
                foreach (string facts2 in (string[])["{}", facts])
                {
                    foreach (string line in Lines(doc, area, facts2))
                    {
                        line.Should().NotBeNullOrWhiteSpace($"{area} for {doc}");
                        line.Should().MatchRegex("^[\\x20-\\x7E]*$", $"{area}: {line}");
                        line.Should().NotContain("undefined").And.NotContain("NaN").And.NotContain("[object").And.NotContain("null");
                    }
                }
            }

            foreach ((string _, string text, string _, string _) in Nudges(doc, facts))
            {
                text.Should().MatchRegex("^[\\x20-\\x7E]*$").And.NotContain("undefined").And.NotContain("[object");
            }
        }
    }

    // ---------------------------------------------------------------- nudges

    [Fact]
    public void A_Station_That_Is_Set_Up_Sensibly_Has_Nothing_Worth_A_Look()
    {
        NudgeIds("""{ "device": "plughw:CARD=Device,DEV=0", "ptt": { "type": "cm108", "device": "/dev/hidraw0" }, "waterfall": { } }""", $$"""{ "plan": {{UsbPlan}} }""")
            .Should().BeEmpty();
        NudgeIds("""{ "device": "flex:discover", "bind": "*" , "api": { "key": "k" } }""", $$"""{ "plan": {{UsbPlan}} }""").Should().BeEmpty();
    }

    [Theory]
    [InlineData("""{ "bind": "*", "waterfall": { } }""", true)]
    [InlineData("""{ "bind": "0.0.0.0", "device": "flex:discover", "waterfall": { } }""", true)]
    [InlineData("""{ "bind": "192.168.1.5", "device": "pipe:a,b", "waterfall": { } }""", true)]
    [InlineData("""{ "bind": "*" }""", true)]
    [InlineData("""{ "bind": "*", "waterfall": { "public": true } }""", true)]
    [InlineData("""{ "bind": "*", "device": "ubersdr:x.example", "waterfall": { } }""", true)]
    [InlineData("""{ "bind": "*", "api": { "key": "k" }, "waterfall": { } }""", false)]
    [InlineData("""{ "bind": "127.0.0.1", "waterfall": { } }""", false)]
    [InlineData("""{ "bind": "127.0.0.2", "waterfall": { } }""", false)]
    [InlineData("""{ "bind": "127.5.6.7" }""", false)]
    [InlineData("""{ "bind": "*", "device": "ubersdr:x.example" }""", false)]
    [InlineData("""{ "bind": "*", "device": "null" }""", false)]
    [InlineData("""{ }""", false)]
    public void The_Network_Nudge_Is_For_An_Open_Listener_With_No_Access_Key(string doc, bool shows) =>
        NudgeIds(doc).Contains("open-network").Should().Be(shows);

    [Fact]
    public void The_Network_Nudge_Says_Only_What_Is_Open_And_What_Anyone_Could_Do()
    {
        Nudges("""{ "bind": "*", "waterfall": { } }""").Single(n => n.Id == "open-network").Text
            .Should().Be("The settings page, the station page and the KISS port can be reached from your network and there is no access key, so anyone on the network can change settings and key the radio.");
        Nudges("""{ "bind": "*", "device": "ubersdr:x.example", "waterfall": { } }""").Single(n => n.Id == "open-network").Text
            .Should().EndWith("can change settings.");
        Nudges("""{ "bind": "*" }""").Single(n => n.Id == "open-network").Text
            .Should().Be("The KISS port can be reached from your network, and KISS has no access control, so anyone on the network can send through this station and key the radio.");
        Nudges("""{ "bind": "*", "waterfall": { "public": true } }""").Single(n => n.Id == "open-network").Text
            .Should().StartWith("The KISS port", "a public page does not open the settings");
        Nudges("""{ "bind": "*", "waterfall": { } }""").Single(n => n.Id == "open-network").Area.Should().Be("sharing");
    }

    [Theory]
    [InlineData("""{ }""", true)]
    [InlineData("""{ "device": "plughw:CARD=Device,DEV=0" }""", true)]
    [InlineData("""{ "ptt": { "type": "serial", "device": "/dev/ttyUSB0" } }""", false)]
    [InlineData("""{ "ptt": { } }""", false)]
    [InlineData("""{ "device": "flex:discover" }""", false)]
    [InlineData("""{ "device": "null" }""", false)]
    [InlineData("""{ "device": "pipe:a,b" }""", false)]
    [InlineData("""{ "device": "ubersdr:x.example" }""", false)]
    public void The_Vox_Nudge_Is_For_A_Sound_Card_With_No_Keying(string doc, bool shows) =>
        NudgeIds(doc).Contains("no-ptt").Should().Be(shows);

    [Theory]
    [InlineData("""{ "sideband": "fm" }""", """{ "plan": { "radio": "fm", "ok": true, "dspRateHz": 48000, "warnings": [] } }""", true)]
    [InlineData("""{ "sideband": "FM", "carrierSense": { "audioFallback": false } }""", """{ "plan": { "radio": "fm", "ok": true, "dspRateHz": 48000, "warnings": [] } }""", true)]
    [InlineData("""{ }""", """{ "plan": { "radio": "fm", "ok": true, "warnings": [] } }""", true)]
    [InlineData("""{ "sideband": "fm" }""", "{}", false)]
    [InlineData("""{ "sideband": "fm", "carrierSense": { "port": "/dev/ttyUSB1" } }""", """{ "plan": { "radio": "fm", "ok": true, "warnings": [] } }""", false)]
    [InlineData("""{ "sideband": "fm", "carrierSense": { "radio": "none", "port": "/dev/ttyUSB1" } }""", """{ "plan": { "radio": "fm", "ok": true, "warnings": [] } }""", true)]
    [InlineData("""{ "sideband": "usb" }""", """{ "plan": { "radio": "usb", "ok": true, "warnings": [] } }""", false)]
    [InlineData("""{ "sideband": "fm", "device": "null" }""", """{ "plan": { "radio": "fm", "ok": true, "warnings": [] } }""", false)]
    public void The_Fm_Nudge_Is_For_Audio_Carrier_Sense_On_Fm(string doc, string facts, bool shows) =>
        NudgeIds(doc, facts).Contains("fm-audio-sense").Should().Be(shows);

    [Fact]
    public void The_Fm_Nudge_Says_More_When_The_Audio_Fallback_Is_Off()
    {
        const string plan = """{ "plan": { "radio": "fm", "ok": true, "warnings": [] } }""";
        Nudges("""{ "sideband": "fm" }""", plan).Single(n => n.Id == "fm-audio-sense").Text.Should().Contain("strongly recommended");
        Nudges("""{ "sideband": "fm", "carrierSense": { "audioFallback": false } }""", plan).Single(n => n.Id == "fm-audio-sense").Text
            .Should().Contain("clear while someone else is transmitting");
    }

    [Theory]
    [InlineData("ephemeral", true)]
    [InlineData("file", false)]
    [InlineData("", false)]
    public void The_One_Run_Nudge_Is_For_A_One_Run_Change_In_Force(string source, bool shows) =>
        NudgeIds("{}", $$"""{ "source": "{{source}}" }""").Contains("one-run").Should().Be(shows);

    [Fact]
    public void The_Plan_Nudges_Come_From_The_Plans_Warnings_And_Wait_For_The_Plan()
    {
        const string modems = """{ "modems": [ { "subChannel": 0, "mode": "bpsk300" }, { "subChannel": 1, "mode": "qpsk600" } ] }""";
        NudgeIds(modems).Should().NotContain(["modem-outside", "modem-overlap"], "no plan, no claim");
        NudgeIds(modems, $$"""{ "plan": {{UsbPlan}} }""").Should().NotContain(["modem-outside", "modem-overlap"]);

        const string outside = """{ "plan": { "ok": true, "radio": "usb", "axis": "rf", "modems": [ { "subChannel": 1, "mode": "qpsk600" } ], "warnings": [ { "subChannel": 1, "kind": "outside", "text": "x" } ] } }""";
        Nudges(modems, outside).Single(n => n.Id == "modem-outside").Text.Should().Be(Name("qpsk600") + " falls outside what the radio hears, so it may be clipped or not heard.");
        NudgeIds(modems, outside).Should().NotContain("modem-overlap");

        const string overlap = """
            { "plan": { "ok": true, "radio": "usb", "modems": [ { "subChannel": 0, "mode": "bpsk300" }, { "subChannel": 1, "mode": "qpsk600" } ],
              "warnings": [ { "subChannel": 0, "kind": "overlap", "other": 1, "text": "x" }, { "subChannel": 1, "kind": "overlap", "other": 0, "text": "x" } ] } }
            """;
        Nudges(modems, overlap).Single(n => n.Id == "modem-overlap").Text
            .Should().Be("Modems overlap on the band, so they share part of it: " + Name("bpsk300") + " and " + Name("qpsk600") + ".");
        Nudges(modems, overlap).Single(n => n.Id == "modem-overlap").Area.Should().Be("modes");
    }

    [Theory]
    [InlineData("""{ "waterfall": { "public": true, "enableAudioControls": true } }""", true)]
    [InlineData("""{ "waterfall": { "public": true } }""", false)]
    [InlineData("""{ "waterfall": { "enableAudioControls": true } }""", false)]
    [InlineData("""{ "waterfall": { "public": false, "enableAudioControls": true } }""", false)]
    [InlineData("""{ }""", false)]
    public void The_Audio_Controls_Nudge_Is_For_A_Public_Page_That_Ignores_The_Setting(string doc, bool shows) =>
        NudgeIds(doc).Contains("public-audio-controls").Should().Be(shows);

    [Theory]
    [InlineData("""{ "ptt": { "type": "serial", "device": "/dev/ttyUSB0" }, "device": "null" }""", true)]
    [InlineData("""{ "ptt": { "type": "serial", "device": "/dev/ttyUSB0" }, "device": "ubersdr:x.example" }""", true)]
    [InlineData("""{ "ptt": { "type": "serial", "device": "/dev/ttyUSB0" }, "device": "flex:discover", "flex": { "receiveOnly": true } }""", true)]
    [InlineData("""{ "ptt": { "type": "serial", "device": "/dev/ttyUSB0" } }""", false)]
    [InlineData("""{ "device": "null" }""", false)]
    public void The_Unused_Keying_Nudge_Is_For_Keying_On_A_Station_That_Cannot_Transmit(string doc, bool shows) =>
        NudgeIds(doc).Contains("ptt-without-transmitter").Should().Be(shows);

    [Fact]
    public void A_Station_Gets_No_Site_Nudges_And_A_Site_Gets_No_Station_Ones()
    {
        string[] stationOnly = ["open-network", "no-ptt", "fm-audio-sense", "modem-outside", "modem-overlap", "public-audio-controls", "ptt-without-transmitter"];
        NudgeIds("""{ "monitor": { "directory": "https://d.example.org/x", "uplinks": [ { "callsign": "A1B" } ] }, "bind": "*", "frameLog": { } }""")
            .Should().NotContain(stationOnly).And.Contain(["site-open-directory", "site-uplink-no-token", "site-framelog-folder"]);
        NudgeIds("""{ "monitor": { } }""", """{ "source": "ephemeral" }""").Should().Contain("one-run");
        NudgeIds("""{ "bind": "*", "frameLog": { }, "monitor": null }""").Should().NotContain(["site-open-directory", "site-uplink-no-token", "site-framelog-folder"]);
    }

    [Theory]
    [InlineData("""{ "monitor": { } }""", true)]
    [InlineData("""{ "monitor": { "directory": "https://instances.example.org/api" } }""", true)]
    [InlineData("""{ "monitor": { "directory": "http://127.0.0.1:1/instances" } }""", false)]
    [InlineData("""{ "monitor": { "directory": "http://directory.lan/x" } }""", false)]
    [InlineData("""{ "monitor": { "directory": "http://192.168.1.4:8080/x" } }""", false)]
    [InlineData("""{ "monitor": { "allow": [ "a.example.org" ] } }""", false)]
    public void The_Open_Directory_Nudge_Is_For_A_Site_With_No_Allow_List_On_A_Public_Directory(string doc, bool shows) =>
        NudgeIds(doc).Contains("site-open-directory").Should().Be(shows);

    [Fact]
    public void The_Site_Nudges_Name_The_Uplinks_Without_A_Token_And_The_Frame_Log_Without_A_Folder()
    {
        Nudges("""{ "monitor": { "allow": ["x.example"], "uplinks": [ { "callsign": "A1B", "tokenSha256": "abc" }, { "callsign": "C2D" }, { "callsign": "E3F", "tokenSha256": "" } ] } }""")
            .Single(n => n.Id == "site-uplink-no-token").Text.Should().Be("C2D and E3F have no token yet, so those stations cannot publish here.");
        Nudges("""{ "monitor": { "allow": ["x.example"], "uplinks": [ { "callsign": "C2D" } ] } }""")
            .Single(n => n.Id == "site-uplink-no-token").Text.Should().Be("C2D has no token yet, so that station cannot publish here.");
        NudgeIds("""{ "monitor": { "allow": ["x.example"], "uplinks": [ { "callsign": "A1B", "tokenSha256": "abc" } ] } }""").Should().BeEmpty();

        (string, bool)[] cases =
        [
            ("\"frameLog\": { }", true),
            ("\"frameLog\": { \"path\": \"/var/lib/x/frames.db\" }", true),
            ("\"frameLog\": { \"path\": \"/var/lib/x\" }", false),
            ("\"x\": 1", false),
        ];
        foreach ((string path, bool shows) in cases)
        {
            NudgeIds($$"""{ "monitor": { "allow": ["x.example"] }, {{path}} }""").Contains("site-framelog-folder").Should().Be(shows, path);
        }
    }

    [Fact]
    public void A_Dismissed_Nudge_Stays_Away_Until_It_Says_Something_Different_Or_Stops_Applying()
    {
        Engine js = Model();
        js.SetValue("schemaJson", ConfigSchema.Json);

        string answer = js.Evaluate("""
            (function () {
              var schema = JSON.parse(schemaJson);
              function shown(doc, facts, dismissed) {
                var s = ConfigModel.nudgesShown(ConfigModel.nudgesFor(schema, doc, facts), dismissed);
                return { ids: s.shown.map(function (n) { return n.id; }), keep: s.keep };
              }
              var open = { bind: "*", waterfall: {}, ptt: { type: "serial", device: "/dev/ttyUSB0" } };
              var first = shown(open, {}, {});
              var text = ConfigModel.nudgesFor(schema, open, {}).list[0].text;
              var dismissed = {}; dismissed[first.ids[0]] = text;
              var out = {};
              out.first = first.ids;
              out.after = shown(open, {}, dismissed);
              out.changed = shown({ bind: "*", waterfall: {}, device: "ubersdr:x.example" }, {}, dismissed).ids;
              out.gone = shown({}, {}, dismissed);
              var late = { "modem-outside": "x" };
              out.waiting = shown({ modems: [ { mode: "bpsk300" } ] }, {}, late).keep;
              return JSON.stringify(out);
            })()
            """).AsString();

        JsonNode result = JsonNode.Parse(answer)!;
        result["first"]!.AsArray().Select(n => n!.GetValue<string>()).Should().Equal("open-network");
        result["after"]!["ids"]!.AsArray().Should().BeEmpty("it was dismissed");
        result["after"]!["keep"]!.AsObject().Should().ContainKey("open-network");
        result["changed"]!.AsArray().Select(n => n!.GetValue<string>()).Should().Contain("open-network", "its words changed, so it is back");
        result["gone"]!["keep"]!.AsObject().Should().BeEmpty("the situation ended, so the dismissal is forgotten and the nudge is new if it returns");
        result["waiting"]!.AsObject().Should().ContainKey("modem-outside", "a nudge that waits for the plan is not forgotten before the plan has come");
    }

    [Fact]
    public void The_Nudge_Table_Has_Unique_Ids_Each_With_A_Condition_A_Sentence_And_A_Target()
    {
        Engine js = Model();
        string answer = js.Evaluate("""
            JSON.stringify(ConfigModel.nudgeTable.map(function (n) {
              return { id: n.id, scope: n.scope, when: typeof n.when, text: typeof n.text, target: n.path || n.hash || "", link: n.link };
            }))
            """).AsString();

        JsonArray table = JsonNode.Parse(answer)!.AsArray();
        table.Select(n => n!["id"]!.GetValue<string>()).Should().OnlyHaveUniqueItems();
        foreach (JsonNode? n in table)
        {
            n!["when"]!.GetValue<string>().Should().Be("function");
            n["text"]!.GetValue<string>().Should().Be("function");
            n["target"]!.GetValue<string>().Should().NotBeEmpty(n["id"]!.GetValue<string>());
            n["link"]!.GetValue<string>().Should().NotBeEmpty();
            n["scope"]!.GetValue<string>().Should().BeOneOf("station", "site", "any");
        }
    }

    // ---------------------------------------------------------------- the running configuration and the plan

    private async Task<string> RealPlanAsync(string doc)
    {
        using var station = new Station(_dir, key: "");
        HttpResponseMessage answer = await station.Client.PostAsync(
            station.Url("api/config/plan"), new StringContent(doc, System.Text.Encoding.UTF8, "application/json"));
        string text = await answer.Content.ReadAsStringAsync();
        answer.StatusCode.Should().Be(System.Net.HttpStatusCode.OK, text);
        return text;
    }

    [Fact]
    public async Task A_Pinned_Dial_That_Pushes_A_Modem_Out_Is_Not_Called_A_Fit()
    {
        const string doc = """
            { "device": "pipe:a,b", "sideband": "usb", "dialFrequency": 7050000,
              "modems": [ { "subChannel": 0, "mode": "afsk300", "rfFrequency": 7051000 }, { "subChannel": 1, "mode": "afsk300", "rfFrequency": 7053500 } ] }
            """;
        string plan = await RealPlanAsync(doc);
        JsonNode served = JsonNode.Parse(plan)!;
        served["dspRateHz"]!.GetValue<double>().Should().Be(12000);
        served["limitAudioLowHz"]!.GetValue<double>().Should().Be(300);
        served["limitAudioHighHz"]!.GetValue<double>().Should().Be(2700);

        string[] lines = Lines(doc, "modes", $$"""{ "plan": {{plan}} }""");
        lines.Should().NotContain(l => l.Contains("fit inside"), "the plan's own passband is stretched round the modems");
        lines.Should().NotContain(l => l.Contains("falls outside"), "the daemon's own warnings say nothing for a stretched passband");
        Nudges(doc, $$"""{ "plan": {{plan}} }""").Single(n => n.Id == "plan-notes").Text
            .Should().Contain("outside the nominal 300-");
        Lines(doc, "radio", $$"""{ "plan": {{plan}} }""").Should().Contain("Dial 7.0500 MHz, upper sideband, taken to pass 300 to 2700 Hz.");
    }

    [Fact]
    public async Task A_Plan_That_Fits_Is_Called_A_Fit_And_A_Flex_Is_Judged_By_Its_Own_Wider_Window()
    {
        const string doc = """
            { "device": "pipe:a,b", "sideband": "usb", "dialFrequency": 7050000,
              "modems": [ { "subChannel": 0, "mode": "afsk300", "rfFrequency": 7051000 }, { "subChannel": 1, "mode": "afsk300", "rfFrequency": 7051800 } ] }
            """;
        string plan = await RealPlanAsync(doc);
        Lines(doc, "modes", $$"""{ "plan": {{plan}} }""").Should().Contain("Both fit inside the radio's passband.");
        NudgeIds(doc, $$"""{ "plan": {{plan}} }""").Should().NotContain(["modem-outside", "plan-notes"]);

        const string flex = """
            { "device": "flex:mock", "dialFrequency": 7050000,
              "modems": [ { "subChannel": 0, "mode": "afsk300", "rfFrequency": 7051000 }, { "subChannel": 1, "mode": "afsk300", "rfFrequency": 7052000 } ] }
            """;
        string flexPlan = await RealPlanAsync(flex);
        JsonNode.Parse(flexPlan)!["limitAudioHighHz"]!.GetValue<double>().Should().Be(10000);
        NudgeIds(flex, $$"""{ "plan": {{flexPlan}} }""").Should().NotContain("modem-outside");
    }

    [Fact]
    public void The_Band_Sentence_Does_Not_Say_No_Dial_When_The_Plan_Has_Not_Chosen_One_Or_Refused()
    {
        const string placed = """{ "modems": [ { "mode": "bpsk300", "rfFrequency": 7050800 } ] }""";
        Lines(placed, "radio").Should().Contain("Upper sideband.").And.NotContain(l => l.Contains("No dial"));
        Lines(placed, "radio", """{ "plan": { "ok": true, "radio": "usb", "axis": "rf", "dialHz": null, "modems": [], "warnings": [] } }""")
            .Should().Contain("Upper sideband. The band plan chooses the dial.");
        Lines(placed, "radio", """{ "plan": { "ok": false, "radio": "usb", "problem": "band plan: no dial puts every modem inside 300-2700 Hz:\nmodem 0 ..." } }""")
            .Should().Contain("The band plan cannot place these modems: band plan: no dial puts every modem inside 300-2700 Hz.");
        Nudges(placed, """{ "plan": { "ok": false, "radio": "usb", "problem": "band plan: no dial puts every modem inside 300-2700 Hz:" } }""")
            .Should().ContainSingle(n => n.Id == "plan-refused");
    }

    [Fact]
    public void The_Plan_Notes_Become_A_Nudge_Unless_A_Modem_Outside_Already_Says_It()
    {
        const string notes = """{ "plan": { "ok": true, "radio": "usb", "notes": [ "band plan: dialFrequency 7050000 USB puts these outside the nominal 300-2700 Hz:" ], "modems": [], "warnings": [] } }""";
        Nudges("""{ "modems": [ { "mode": "bpsk300" } ] }""", notes).Single(n => n.Id == "plan-notes").Text
            .Should().Be("The band plan says: band plan: dialFrequency 7050000 USB puts these outside the nominal 300-2700 Hz.");
        NudgeIds("""{ "modems": [ { "mode": "bpsk300" } ] }""", """{ "plan": { "ok": true, "radio": "usb", "notes": [], "modems": [], "warnings": [] } }""")
            .Should().NotContain("plan-notes");
    }

    [Fact]
    public void A_Plan_That_Did_Not_Come_Back_Decides_Nothing()
    {
        Engine js = Model();
        js.SetValue("schemaJson", ConfigSchema.Json);
        string answer = js.Evaluate("""
            (function () {
              var schema = JSON.parse(schemaJson);
              var found = ConfigModel.nudgesFor(schema, { modems: [ { mode: "bpsk300" } ] }, { plan: null });
              var s = ConfigModel.nudgesShown(found, { "modem-outside": "x", "plan-refused": "y", "plan-notes": "z", "fm-audio-sense": "w" });
              return JSON.stringify({ undecided: found.undecided, keep: Object.keys(s.keep) });
            })()
            """).AsString();
        JsonNode result = JsonNode.Parse(answer)!;
        result["undecided"]!.AsArray().Select(n => n!.GetValue<string>()).Should().BeEquivalentTo("modem-outside", "modem-overlap", "plan-refused", "plan-notes", "fm-audio-sense");
        result["keep"]!.AsArray().Select(n => n!.GetValue<string>()).Should().BeEquivalentTo("modem-outside", "plan-refused", "plan-notes", "fm-audio-sense");
    }

    [Fact]
    public void The_Fm_Nudge_Follows_The_Channel_Rate_The_Plan_Reports()
    {
        string Text(string facts) => Nudges("""{ "sideband": "fm" }""", facts).Single(n => n.Id == "fm-audio-sense").Text;
        NudgeIds("""{ "sideband": "fm" }""").Should().NotContain("fm-audio-sense", "its words depend on the plan, so it waits for it");

        Text("""{ "plan": { "radio": "fm", "ok": true, "dspRateHz": 48000, "warnings": [] } }""").Should().StartWith("Carrier sense on this FM station is read from the audio.").And.Contain("strongly recommended");
        Text("""{ "plan": { "radio": "fm", "ok": true, "dspRateHz": 12000, "warnings": [] } }""").Should().Contain("runs at 12 kHz").And.Contain("energy detector");
        Text("""{ "plan": { "radio": "fm", "ok": true, "warnings": [] } }""").Should().Contain("energy detector").And.Contain("can read the channel as clear");
    }

    [Fact]
    public void The_One_Run_Nudge_Offers_To_Save_And_Says_What_Saving_Does()
    {
        Engine js = Model();
        string answer = js.Evaluate("""
            JSON.stringify(ConfigModel.nudgeTable.filter(function (n) { return n.id === "one-run"; }).map(function (n) { return n.action; }))
            """).AsString();
        answer.Should().Be("[\"save\"]");
        Nudges("{}", """{ "source": "ephemeral" }""").Single(n => n.Id == "one-run").Text
            .Should().Contain("restarts the station for a few seconds").And.Contain("does not keep the file's comments");
        Nudges("{}", """{ "source": "ephemeral" }""").Single(n => n.Id == "one-run").Action.Should().Be("save");
    }

    [Fact]
    public void Keying_Words_Are_Right_Where_The_Daemon_Refuses_A_Ptt_Section()
    {
        foreach (string device in (string[])["flex:discover", "ubersdr:x.example", "openwebrx:https://x.example/", "rtltcp:10.0.0.1", "sdrconnect:10.0.0.2"])
        {
            Lines($$"""{ "device": "{{device}}", "ptt": { "type": "serial", "device": "/dev/ttyUSB0" } }""", "keying")[0]
                .Should().NotContain("serial port", device);
        }

        Lines("""{ "device": "flex:discover", "ptt": { "type": "serial", "device": "/dev/ttyUSB0" } }""", "keying")[0].Should().StartWith("The radio keys itself");
        Lines("""{ "device": "pipe:a,b", "ptt": { "type": "serial", "device": "/dev/ttyUSB0" } }""", "keying")[0].Should().Be("The RTS line of the serial port /dev/ttyUSB0 keys the radio.");
        Lines("""{ "device": "flex:discover", "rig": { } }""", "radio").Should().NotContain(l => l.Contains("rigctld"), "a Flex is refused a rig section");
    }

    [Fact]
    public void Metrics_With_No_Station_Page_Are_Said_To_Have_Nothing_To_Serve_Them()
    {
        Lines("""{ "metrics": { } }""", "sharing").Should().Contain("Metrics are switched on, but there is no station page to serve them.");
        Lines("""{ "metrics": { "enabled": false } }""", "sharing").Should().NotContain(l => l.Contains("Metrics"));
    }

    [Fact]
    public void A_Polyglot_Port_Is_Not_Claimed_Without_Two_Packet_Modems()
    {
        Lines("""{ "polyglotPort": 8120 }""", "software").Should().Contain(l => l.Contains("needs at least two packet modems"));
        Lines("""{ "polyglotPort": 8120, "modems": [ { "mode": "bpsk300" } ] }""", "software").Should().Contain(l => l.Contains("but that needs at least two packet modems and there is one."));
        Lines("""{ "polyglotPort": 8120, "modems": [ { "mode": "bpsk300" }, { "subChannel": 1, "mode": "qpsk600" } ] }""", "software")
            .Should().Contain(l => l.StartsWith("Port 8120 puts all two packet modems"));
    }

    [Fact]
    public void The_Schema_Serves_The_Daemons_Facts_About_Each_Device_Kind()
    {
        JsonArray served = JsonNode.Parse(ConfigSchema.Json)!["deviceKindFacts"]!.AsArray();

        served.Select(k => (k!["name"]!.GetValue<string>(), k["hasMixer"]!.GetValue<bool>(), k["transmits"]!.GetValue<bool>(),
                k["takesRig"]!.GetValue<bool>(), k["mayPublish"]!.GetValue<bool>()))
            .Should().Equal(ConfigKeys.Kinds.Select(k => (k.Name, k.HasMixer, k.Transmits, k.TakesRig, k.MayPublish)));
    }

    [Fact]
    public async Task The_Hosts_Endpoint_Answers_With_No_Ports_Before_The_Station_Has_Any_And_Wants_The_Key()
    {
        using var station = new Station(_dir, key: "the-key");

        (await station.Client.GetAsync(station.Url("api/config/hosts"))).StatusCode.Should().Be(System.Net.HttpStatusCode.Unauthorized);
        using var request = new HttpRequestMessage(HttpMethod.Get, station.Url("api/config/hosts"));
        request.Headers.Add("X-API-Key", "the-key");
        HttpResponseMessage answer = await station.Client.SendAsync(request);
        answer.StatusCode.Should().Be(System.Net.HttpStatusCode.OK);
        JsonNode.Parse(await answer.Content.ReadAsStringAsync())!["ports"]!.AsArray().Should().BeEmpty();
    }

    [Fact]
    public void A_Sites_Frame_Log_With_No_Folder_Is_Not_Called_Absent()
    {
        Lines("""{ "monitor": { }, "frameLog": { } }""", "monitor-logging").Should().Equal("The frame log is switched on but has no folder to keep its files in.");
        Lines("""{ "monitor": { } }""", "monitor-logging").Should().Equal("Heard frames are not kept.");
    }

    [Theory]
    [InlineData("""{ "device": "null", "rig": { }, "modems": [ { "subChannel": 0, "mode": "bpsk300", "rfFrequency": 7050300 }, { "subChannel": 1, "mode": "afsk1200", "rfFrequency": 7051800 } ] }""")]
    [InlineData("""{ "device": "null", "modems": [ { "subChannel": 0, "mode": "ms110d-wn3", "frequency": 1800 } ] }""")]
    [InlineData("""{ "device": "null", "modems": [ { "subChannel": 0, "mode": "ms110d-wn3", "rfFrequency": 7053800 } ] }""")]
    public async Task A_Valid_Plan_The_Daemon_Accepts_Is_Not_Called_Outside(string doc)
    {
        string plan = await RealPlanAsync(doc);

        JsonNode.Parse(plan)!["ok"]!.GetValue<bool>().Should().BeTrue(plan);
        string[] ids = NudgeIds(doc, $$"""{ "plan": {{plan}} }""");
        ids.Should().NotContain(["modem-outside", "modem-overlap", "plan-refused"], plan);
        Lines(doc, "modes", $$"""{ "plan": {{plan}} }""").Should().NotContain(l => l.Contains("falls outside"));
    }

    [Fact]
    public void The_Band_Sentence_Says_Only_What_Is_Known_About_The_Radios_Width()
    {
        const string plan = """{ "ok": true, "radio": "usb", "axis": "rf", "dialHz": 7050000, "limitAudioLowHz": 300, "limitAudioHighHz": 2700, "passband": { "audioLowHz": 300, "audioHighHz": 2700 }, "modems": [], "warnings": [] }""";
        string Band(string doc) => Lines(doc, "radio", $$"""{ "plan": {{plan}} }""").Single(l => l.StartsWith("Dial "));

        Band("""{ "device": "plughw:CARD=Device,DEV=0" }""").Should().Be("Dial 7.0500 MHz, upper sideband, taken to pass 300 to 2700 Hz.");
        Band("""{ "rig": { "passbandHz": 2000 } }""").Should().Be("Dial 7.0500 MHz, upper sideband, 2 kHz wide.");
        Band("""{ "rig": { "passbandHz": 0 } }""").Should().Be("Dial 7.0500 MHz, upper sideband.");
        Band("""{ "device": "ubersdr:x.example" }""").Should().Be("Dial 7.0500 MHz, upper sideband, 3.3 kHz wide.");
        Band("""{ "device": "ubersdr:x.example", "ubersdr": { "ssbLowHz": 300, "ssbHighHz": 2700 } }""").Should().Be("Dial 7.0500 MHz, upper sideband, 2.4 kHz wide.");
    }

    [Fact]
    public async Task The_Hosts_Endpoint_Is_Open_To_The_Machines_Own_Name_Without_A_Key_And_Is_Not_A_Monitors()
    {
        using var station = new Station(_dir, key: "");
        HttpResponseMessage open = await station.Client.GetAsync(station.Url("api/config/hosts"));
        open.StatusCode.Should().Be(System.Net.HttpStatusCode.OK, "a keyless station answers the settings endpoints to the machine's own address");

        await using var site = await SiteHost.StartAsync(_dir, "a-long-site-key");
        using var request = new HttpRequestMessage(HttpMethod.Get, site.Url("api/config/hosts"));
        request.Headers.Add("X-API-Key", "a-long-site-key");
        (await site.Client.SendAsync(request)).StatusCode.Should().Be(System.Net.HttpStatusCode.NotFound, "a site has no KISS ports");
    }
}
