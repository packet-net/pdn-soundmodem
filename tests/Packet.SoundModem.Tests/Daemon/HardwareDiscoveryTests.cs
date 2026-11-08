using System.Net;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Time.Testing;
using Packet.SoundModem.Daemon;
using Packet.SoundModem.Linux.Tests;
using Packet.SoundModem.UberSdr;

namespace Packet.SoundModem.Tests.Daemon;

/// <summary>
/// What <c>/api/config/discover</c> finds: each probe against a device tree written in the test,
/// and the whole lookup against fake network answers. Nothing here looks at the machine running
/// the tests.
/// </summary>
public sealed class HardwareDiscoveryTests
{
    private const string OneCard = """
         1 [Device         ]: USB-Audio - USB PnP Sound Device
                              C-Media Electronics Inc. USB PnP Sound Device at usb-0000:00:14.0-2, full speed
        """;

    /// <summary>A CM108 interface with its hidraw, and two USB serial adapters, one with a by-id name.</summary>
    private static FakeDeviceTree Shack() => new FakeDeviceTree()
        .Cm108(card: 1, hidraw: 1)
        .Cards(OneCard)
        .UsbSerial(0, "/sys/devices/pci0000:00/0000:00:14.0/usb3/3-3", "0403", "6001", "FTDI", "FT232R USB UART", byId: "usb-FTDI_FT232R_USB_UART_A50285BI-if00-port0")
        .UsbSerial(1, "/sys/devices/pci0000:00/0000:00:14.0/usb3/3-4", "1a86", "7523", "", "USB Serial", byId: null);

    private sealed class Mixers(MixerNames names) : IMixerReader
    {
        public MixerNames Read(string mixerCard) => names;
    }

    private static SysfsProbes Probes(FakeDeviceTree tree) =>
        new(tree, new Mixers(new MixerNames(["Mic"], ["Speaker"], null)));

    // ---------------------------------------------------------------- the probes

    [Fact]
    public void A_Cm108_Card_Is_Found_With_Its_Device_String_Mixer_Controls_And_Hidraw()
    {
        IReadOnlyList<FoundCard> cards = ((ISoundCardProbe)Probes(Shack())).Find();

        FoundCard card = cards.Should().ContainSingle().Subject;
        card.Id.Should().Be("Device");
        card.Name.Should().Be("C-Media Electronics Inc. USB PnP Sound Device");
        card.Device.Should().Be("plughw:CARD=Device,DEV=0");
        card.Kind.Should().Be("cm108");
        card.Capture.Should().BeTrue();
        card.Playback.Should().BeTrue();
        card.Hidraw.Should().Be("/dev/hidraw1");
        card.CaptureControls.Should().Equal("Mic");
        card.PlaybackControls.Should().Equal("Speaker");
    }

    [Fact]
    public void A_Mixer_That_Could_Not_Be_Read_Is_Said_On_The_Card_And_The_Card_Is_Still_Listed()
    {
        var probes = new SysfsProbes(Shack(), new Mixers(new MixerNames([], [], "no mixer here")));

        FoundCard card = ((ISoundCardProbe)probes).Find().Should().ContainSingle().Subject;

        card.MixerProblem.Should().Be("no mixer here");
        card.Device.Should().Be("plughw:CARD=Device,DEV=0");
    }

    [Fact]
    public void The_Cm108_Hid_Node_Is_Found_And_A_Keyboard_Is_Not_Taken_For_One()
    {
        FakeDeviceTree tree = Shack();
        string usb = "/sys/devices/pci0000:00/0000:00:14.0/usb3/3-5";
        string hid = $"{usb}/3-5:1.0/0003:046D:C31C.0009";
        tree.File($"{usb}/idVendor", "046d").File($"{usb}/idProduct", "c31c").File($"{usb}/product", "USB Keyboard")
            .Link("/sys/class/hidraw/hidraw4", $"{hid}/hidraw/hidraw4")
            .Link($"{hid}/hidraw/hidraw4/device", hid)
            .File($"{hid}/uevent", "HID_ID=0003:0000046D:0000C31C\nHID_NAME=Logitech USB Keyboard")
            .Node("/dev/hidraw4");
        tree.Refuse("/dev/hidraw1");

        IReadOnlyList<FoundHid> found = ((IHidProbe)Probes(tree)).Find();

        found.Select(h => h.Path).Should().Equal("/dev/hidraw1", "/dev/hidraw4");
        found[0].Cm108.Should().BeTrue();
        found[0].CanOpen.Should().BeFalse("the node exists but this process may not open it");
        found[1].Cm108.Should().BeFalse();
        found[1].Name.Should().Be("Logitech USB Keyboard");
    }

    [Fact]
    public void Two_Usb_Serial_Adapters_Are_Found_With_Their_Product_Names_And_Stable_Paths()
    {
        IReadOnlyList<FoundSerial> ports = ((ISerialProbe)Probes(Shack())).Find();

        ports.Select(p => p.Path).Should().Equal("/dev/ttyUSB0", "/dev/ttyUSB1");
        ports[0].Name.Should().Be("FTDI FT232R USB UART");
        ports[0].StablePath.Should().Be("/dev/serial/by-id/usb-FTDI_FT232R_USB_UART_A50285BI-if00-port0");
        ports[0].Usb.Should().BeTrue();
        ports[1].Name.Should().Be("USB Serial");
        ports[1].StablePath.Should().BeNull();
    }

    [Fact]
    public void A_Built_In_Serial_Port_Is_Listed_And_The_Kernels_Placeholders_Are_Not()
    {
        FakeDeviceTree tree = new FakeDeviceTree()
            .Link("/sys/class/tty/ttyS0", "/sys/devices/platform/serial8250/tty/ttyS0")
            .Link("/sys/devices/platform/serial8250/tty/ttyS0/device", "/sys/devices/platform/serial8250")
            .File("/sys/devices/platform/serial8250/uevent", "")
            .Node("/dev/ttyS0")
            .File("/sys/devices/platform/serial8250/tty/ttyS0/type", "4")
            // As the kernel lays out an 8250 port that is not there: a device link all the same,
            // and a UART type of 0.
            .Link("/sys/class/tty/ttyS1", "/sys/devices/platform/serial8250/tty/ttyS1")
            .Link("/sys/devices/platform/serial8250/tty/ttyS1/device", "/sys/devices/platform/serial8250")
            .File("/sys/devices/platform/serial8250/tty/ttyS1/type", "0");

        IReadOnlyList<FoundSerial> ports = ((ISerialProbe)Probes(tree)).Find();

        FoundSerial port = ports.Should().ContainSingle().Subject;
        port.Path.Should().Be("/dev/ttyS0");
        port.Usb.Should().BeFalse();
        port.Name.Should().Be("Built-in serial port");
    }

    [Fact]
    public void The_Pis_Pl011_Uart_Is_Listed()
    {
        FakeDeviceTree tree = new FakeDeviceTree()
            .Link("/sys/class/tty/ttyAMA0", "/sys/devices/platform/soc/fe201000.serial/tty/ttyAMA0")
            .Link("/sys/devices/platform/soc/fe201000.serial/tty/ttyAMA0/device", "/sys/devices/platform/soc/fe201000.serial")
            .File("/sys/devices/platform/soc/fe201000.serial/tty/ttyAMA0/type", "3")
            .Node("/dev/ttyAMA0");

        ((ISerialProbe)Probes(tree)).Find().Should().ContainSingle().Which.Path.Should().Be("/dev/ttyAMA0");
    }

    [Fact]
    public void An_Ubersdr_Not_On_Tls_Is_Written_As_A_Url_And_Parses_Back_To_Where_It_Is()
    {
        string plain = DirectoryUberSdrProbe.DeviceString(new UberSdrEndpoint("sdr.example.org", 8080, Ssl: false));
        string tls = DirectoryUberSdrProbe.DeviceString(new UberSdrEndpoint("sdr.example.org", 443, Ssl: true));
        string tlsOdd = DirectoryUberSdrProbe.DeviceString(new UberSdrEndpoint("sdr.example.org", 8443, Ssl: true));

        plain.Should().Be("ubersdr:http://sdr.example.org:8080/");
        UberSdrDevice.Parse(plain).Should().Be(new UberSdrEndpoint("sdr.example.org", 8080, Ssl: false));
        tls.Should().Be("ubersdr:sdr.example.org");
        UberSdrDevice.Parse(tls).Should().Be(new UberSdrEndpoint("sdr.example.org", 443, Ssl: true));
        UberSdrDevice.Parse(tlsOdd).Should().Be(new UberSdrEndpoint("sdr.example.org", 8443, Ssl: true));
    }

    [Fact]
    public async Task A_Look_At_The_Same_Moment_Shares_The_One_In_Flight()
    {
        var release = new TaskCompletionSource<IReadOnlyList<FoundFlex>>();
        var started = new TaskCompletionSource();
        var flex = new SlowFlex(release.Task, started);
        HardwareDiscovery discovery = Discovery(Shack(), flex: null, uber: null, time: null, flexProbe: flex, flexListen: TimeSpan.FromSeconds(30));

        Task<JsonObject> first = discovery.DescribeAsync(true, CancellationToken.None);
        await started.Task;
        Task<JsonObject> second = discovery.DescribeAsync(true, CancellationToken.None);
        release.SetResult([Flex]);
        await Task.WhenAll(first, second);

        flex.Calls.Should().Be(1, "the second look joined the first");
    }

    [Fact]
    public async Task A_Failed_Slow_Lookup_Is_Not_Remembered()
    {
        int calls = 0;
        var flex = new FakeFlex(() => ++calls == 1 ? throw new InvalidOperationException("no network yet") : [Flex]);
        HardwareDiscovery discovery = Discovery(Shack(), flex);

        JsonObject first = await discovery.DescribeAsync(false, CancellationToken.None);
        JsonObject second = await discovery.DescribeAsync(false, CancellationToken.None);

        first["flexRadios"]!["problem"].Should().NotBeNull();
        second["flexRadios"]!["problem"].Should().BeNull("a failure is tried again, not cached");
        second["flexRadios"]!["items"]!.AsArray().Should().HaveCount(1);
    }

    [Fact]
    public async Task A_Missing_Or_Malformed_Fixture_Gives_A_Problem_In_Every_Section_And_No_Exception()
    {
        string dir = Directory.CreateTempSubdirectory("pdnsm-fixture").FullName;
        try
        {
            string bad = Path.Combine(dir, "bad.txt");
            File.WriteAllText(bad, "link\tonly-one-field\n");

            foreach (string path in new[] { Path.Combine(dir, "missing.txt"), bad })
            {
                JsonObject found = await HardwareFixture.Load(path).DescribeAsync(true, CancellationToken.None);

                foreach (string section in new[] { "soundCards", "hidDevices", "serialPorts", "flexRadios", "uberSdr" })
                {
                    found[section]!["problem"]!.GetValue<string>().Should().Contain("discovery fixture", section);
                }

                found["rigctld"]!["problem"]!.GetValue<string>().Should().Contain("discovery fixture");
            }
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void The_Journal_Says_When_A_Fixture_Stands_In_For_The_Machine()
    {
        string? before = Environment.GetEnvironmentVariable(HardwareFixture.Variable);
        try
        {
            Environment.SetEnvironmentVariable(HardwareFixture.Variable, null);
            HardwareFixture.StartUpNotice().Should().BeNull();

            Environment.SetEnvironmentVariable(HardwareFixture.Variable, "/tmp/m.txt");
            HardwareFixture.StartUpNotice().Should().Contain("not this machine").And.Contain("/tmp/m.txt");
        }
        finally
        {
            Environment.SetEnvironmentVariable(HardwareFixture.Variable, before);
        }
    }

    [Fact]
    public void A_Machine_With_None_Of_It_Gives_Empty_Lists_And_No_Error()
    {
        SysfsProbes probes = Probes(new FakeDeviceTree());

        ((ISoundCardProbe)probes).Find().Should().BeEmpty();
        ((IHidProbe)probes).Find().Should().BeEmpty();
        ((ISerialProbe)probes).Find().Should().BeEmpty();
    }

    [Fact]
    public async Task Rigctld_Answers_When_Something_Accepts_The_Connection_And_Not_When_Nothing_Does()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        try
        {
            (await new TcpRigctldProbe("127.0.0.1", port).AnswersAsync(CancellationToken.None)).Should().BeTrue();
        }
        finally
        {
            listener.Stop();
        }

        (await new TcpRigctldProbe("127.0.0.1", port).AnswersAsync(CancellationToken.None))
            .Should().BeFalse("the port was closed again");
    }

    // ---------------------------------------------------------------- the whole lookup

    private sealed class FakeFlex(Func<IReadOnlyList<FoundFlex>> answer) : IFlexProbe
    {
        public int Calls;

        public Task<IReadOnlyList<FoundFlex>> FindAsync(TimeSpan listen, CancellationToken cancellation)
        {
            Interlocked.Increment(ref Calls);
            return Task.FromResult(answer());
        }
    }

    private sealed class FakeUber(Func<Task<IReadOnlyList<FoundUberSdr>>> answer) : IUberSdrProbe
    {
        public int Calls;

        public Task<IReadOnlyList<FoundUberSdr>> FindAsync(CancellationToken cancellation)
        {
            Interlocked.Increment(ref Calls);
            return answer();
        }
    }

    private sealed class FakeRig(bool answering) : IRigctldProbe
    {
        public string Address => "localhost:4532";

        public Task<bool> AnswersAsync(CancellationToken cancellation) => Task.FromResult(answering);
    }

    private static readonly FoundFlex Flex = new("Shack Flex", "FLEX-6400", "1234-5678", "10.45.0.76");
    private static readonly FoundUberSdr Uber = new("m9psy-1.instance.ubersdr.org", "ubersdr:m9psy-1.instance.ubersdr.org", "M9PSY", "Reading", true);

    private sealed class SlowFlex(Task<IReadOnlyList<FoundFlex>> answer, TaskCompletionSource started) : IFlexProbe
    {
        public int Calls;

        public Task<IReadOnlyList<FoundFlex>> FindAsync(TimeSpan listen, CancellationToken cancellation)
        {
            Interlocked.Increment(ref Calls);
            started.TrySetResult();
            return answer;
        }
    }

    private static HardwareDiscovery Discovery(
        FakeDeviceTree tree, FakeFlex? flex = null, FakeUber? uber = null, TimeProvider? time = null,
        IFlexProbe? flexProbe = null, TimeSpan? flexListen = null)
    {
        SysfsProbes sysfs = Probes(tree);
        return new HardwareDiscovery(
            sysfs, sysfs, sysfs, new FakeRig(false),
            flexProbe ?? flex ?? new FakeFlex(() => [Flex]),
            uber ?? new FakeUber(() => Task.FromResult<IReadOnlyList<FoundUberSdr>>([Uber])),
            time,
            fastLimit: TimeSpan.FromMilliseconds(300), slowLimit: TimeSpan.FromMilliseconds(300),
            flexListen: flexListen ?? TimeSpan.FromMilliseconds(10));
    }

    [Fact]
    public async Task The_Description_Joins_The_Hid_Node_And_The_Serial_Ports_To_The_Card_They_Share_A_Usb_Device_With()
    {
        JsonObject found = await Discovery(Shack()).DescribeAsync(false, CancellationToken.None);

        found["soundCards"]!["items"]![0]!["device"]!.GetValue<string>().Should().Be("plughw:CARD=Device,DEV=0");
        found["soundCards"]!["items"]![0]!["cm108"]!.GetValue<bool>().Should().BeTrue();
        found["hidDevices"]!["items"]![0]!["soundCard"]!.GetValue<string>().Should().Be("Device");
        found["serialPorts"]!["items"]!.AsArray().Should().HaveCount(2);
        found["serialPorts"]!["items"]![0]!["path"]!.GetValue<string>().Should().StartWith("/dev/serial/by-id/");
        found["serialPorts"]!["items"]![0]!["soundCard"].Should().BeNull();
        found["rigctld"]!["answering"]!.GetValue<bool>().Should().BeFalse();
        found["flexRadios"]!["items"]![0]!["device"]!.GetValue<string>().Should().Be("flex:10.45.0.76");
        found["uberSdr"]!["items"]![0]!["device"]!.GetValue<string>().Should().Be("ubersdr:m9psy-1.instance.ubersdr.org");
    }

    [Fact]
    public async Task An_Empty_Machine_Is_Described_As_Having_Nothing_And_Nothing_Is_Wrong()
    {
        var sysfs = Probes(new FakeDeviceTree());
        var discovery = new HardwareDiscovery(
            sysfs, sysfs, sysfs, new FakeRig(false), new FakeFlex(() => []),
            new FakeUber(() => Task.FromResult<IReadOnlyList<FoundUberSdr>>([])));

        JsonObject found = await discovery.DescribeAsync(false, CancellationToken.None);

        foreach (string section in new[] { "soundCards", "hidDevices", "serialPorts", "flexRadios", "uberSdr" })
        {
            found[section]!["items"]!.AsArray().Should().BeEmpty(section);
            found[section]!["problem"].Should().BeNull(section);
        }

        found["rigctld"]!["problem"].Should().BeNull();
    }

    [Fact]
    public async Task One_Probe_Failing_Or_Hanging_Is_Said_In_Its_Own_Section_And_The_Others_Still_Answer()
    {
        var never = new TaskCompletionSource<IReadOnlyList<FoundUberSdr>>();
        var flex = new FakeFlex(() => throw new InvalidOperationException("cannot listen for FlexRadios on UDP 4992"));

        JsonObject found = await Discovery(Shack(), flex, new FakeUber(() => never.Task))
            .DescribeAsync(false, CancellationToken.None);

        found["flexRadios"]!["problem"]!.GetValue<string>().Should().Contain("cannot listen for FlexRadios");
        found["uberSdr"]!["problem"]!.GetValue<string>().Should().Contain("did not answer");
        found["uberSdr"]!["items"]!.AsArray().Should().BeEmpty();
        found["soundCards"]!["problem"].Should().BeNull();
        found["soundCards"]!["items"]!.AsArray().Should().HaveCount(1);
    }

    [Fact]
    public async Task A_Machine_That_Is_Not_Linux_Says_So_In_Words_For_The_Local_Sections_Only()
    {
        FailingProbes unsupported = FailingProbes.NotLinux();
        var discovery = new HardwareDiscovery(
            unsupported, unsupported, unsupported, new FakeRig(false), new FakeFlex(() => [Flex]),
            new FakeUber(() => Task.FromResult<IReadOnlyList<FoundUberSdr>>([])));

        JsonObject found = await discovery.DescribeAsync(false, CancellationToken.None);

        found["soundCards"]!["problem"]!.GetValue<string>().Should().Contain("only looked for on Linux");
        found["serialPorts"]!["problem"]!.GetValue<string>().Should().Contain("only looked for on Linux");
        found["flexRadios"]!["items"]!.AsArray().Should().HaveCount(1);
    }

    [Fact]
    public async Task The_Slow_Lookups_Are_Remembered_For_Thirty_Seconds_And_Look_Again_Skips_The_Memory()
    {
        var time = new FakeTimeProvider();
        var flex = new FakeFlex(() => [Flex]);
        var uber = new FakeUber(() => Task.FromResult<IReadOnlyList<FoundUberSdr>>([Uber]));
        HardwareDiscovery discovery = Discovery(Shack(), flex, uber, time);

        await discovery.DescribeAsync(false, CancellationToken.None);
        time.Advance(TimeSpan.FromSeconds(20));
        JsonObject again = await discovery.DescribeAsync(false, CancellationToken.None);

        flex.Calls.Should().Be(1);
        uber.Calls.Should().Be(1);
        again["flexRadios"]!["ageSeconds"]!.GetValue<int>().Should().Be(20);

        time.Advance(TimeSpan.FromSeconds(11));
        await discovery.DescribeAsync(false, CancellationToken.None);
        flex.Calls.Should().Be(2, "31 seconds is past the memory");

        await discovery.DescribeAsync(true, CancellationToken.None);
        flex.Calls.Should().Be(3, "Look again asks for a fresh look");
        uber.Calls.Should().Be(3);
    }

    // ---------------------------------------------------------------- the test seam

    [Fact]
    public async Task A_Machine_Written_In_A_Fixture_File_Is_What_The_Daemon_Describes_When_The_Variable_Names_It()
    {
        string dir = Directory.CreateTempSubdirectory("pdnsm-fixture").FullName;
        try
        {
            string path = Path.Combine(dir, "machine.txt");
            File.WriteAllText(
                path,
                Shack().Describe()
                + "mixer\tDevice\tMic\tSpeaker\n"
                + "rigctld\tanswering\n"
                + "flex\tShack Flex\tFLEX-6400\t1234-5678\t10.45.0.76\n"
                + "ubersdr\tm9psy-1.instance.ubersdr.org\tubersdr:m9psy-1.instance.ubersdr.org\tM9PSY\tReading\tfree\n");

            JsonObject found = await HardwareFixture.Load(path).DescribeAsync(true, CancellationToken.None);

            found["soundCards"]!["items"]![0]!["captureControls"]![0]!.GetValue<string>().Should().Be("Mic");
            found["hidDevices"]!["items"]![0]!["soundCard"]!.GetValue<string>().Should().Be("Device");
            found["serialPorts"]!["items"]!.AsArray().Should().HaveCount(2);
            found["rigctld"]!["answering"]!.GetValue<bool>().Should().BeTrue();
            found["flexRadios"]!["items"]![0]!["ip"]!.GetValue<string>().Should().Be("10.45.0.76");
            found["uberSdr"]!["items"]![0]!["free"]!.GetValue<bool>().Should().BeTrue();
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}

/// <summary>Device trees for the serial adapters the Linux library's own fakes do not have.</summary>
internal static class SerialTrees
{
    public static FakeDeviceTree UsbSerial(
        this FakeDeviceTree tree, int n, string usb, string vendor, string product, string maker, string name, string? byId)
    {
        string port = Path.GetFileName(usb);
        string iface = $"{usb}/{port}:1.0";
        tree.File($"{usb}/idVendor", vendor).File($"{usb}/idProduct", product).File($"{usb}/product", name);
        if (maker.Length > 0)
        {
            tree.File($"{usb}/manufacturer", maker);
        }

        tree.Link($"/sys/class/tty/ttyUSB{n}", $"{iface}/ttyUSB{n}/tty/ttyUSB{n}")
            .Link($"{iface}/ttyUSB{n}/tty/ttyUSB{n}/device", iface)
            .File($"{iface}/bInterfaceNumber", "00")
            .Node($"/dev/ttyUSB{n}");
        if (byId is not null)
        {
            tree.Link($"/dev/serial/by-id/{byId}", $"/dev/ttyUSB{n}");
        }

        return tree;
    }
}
