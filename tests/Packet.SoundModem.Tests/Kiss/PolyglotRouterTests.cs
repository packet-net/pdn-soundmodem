using System.Text;
using AwesomeAssertions;
using Microsoft.Extensions.Time.Testing;
using Packet.SoundModem.Kiss;

namespace Packet.SoundModem.Tests.Kiss;

/// <summary>
/// The rules behind a polyglot port (#450): who a received frame is heard from, who a frame to
/// send is for, how long a station is remembered, and what happens to a burst two modems decode.
/// </summary>
public class PolyglotRouterTests
{
    private const int Legacy = 0;   // the default: the mode every peer has
    private const int Fancy = 1;

    private readonly FakeTimeProvider _time = new();
    private readonly PolyglotRouter _router;
    private readonly List<PolyglotLearnedEvent> _learned = [];

    public PolyglotRouterTests()
    {
        _router = new PolyglotRouter([Legacy, Fancy], Legacy, TimeSpan.FromMinutes(60), _time);
        _router.Learned += _learned.Add;
    }

    /// <summary>An AX.25 UI frame. A digipeater written with a trailing <c>*</c> has repeated it
    /// (its H bit is set).</summary>
    internal static byte[] Frame(string destination, string source, params string[] via)
    {
        var bytes = new List<byte>();
        string[] addresses = [destination, source, .. via];
        for (int n = 0; n < addresses.Length; n++)
        {
            string address = addresses[n];
            bool repeated = address.EndsWith('*');
            address = address.TrimEnd('*');
            string[] parts = address.Split('-');
            string call = parts[0].PadRight(6);
            int ssid = parts.Length > 1 ? int.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture) : 0;
            foreach (char c in call)
            {
                bytes.Add((byte)(c << 1));
            }

            byte last = (byte)(0x60 | (ssid << 1));
            if (repeated)
            {
                last |= 0x80;
            }

            if (n == addresses.Length - 1)
            {
                last |= 0x01;
            }

            bytes.Add(last);
        }

        bytes.Add(0x03);
        bytes.Add(0xF0);
        bytes.AddRange(Encoding.ASCII.GetBytes("hello"));
        return [.. bytes];
    }

    [Fact]
    public void A_Station_Never_Heard_Gets_The_Default()
    {
        _router.Route(Frame("G4ABC", "M0LTE")).Should().Be(Legacy);
    }

    [Fact]
    public void A_Station_Heard_On_Another_Modem_Gets_That_Modem()
    {
        _router.Heard(Fancy, Frame("M0LTE", "G4ABC-1")).Should().BeTrue();

        _router.Route(Frame("G4ABC-1", "M0LTE")).Should().Be(Fancy);
        _router.Route(Frame("G4ABC", "M0LTE")).Should().Be(
            Legacy, "the SSID is part of the station: G4ABC-1 is not G4ABC");
    }

    [Fact]
    public void The_Most_Recent_Hearing_Wins()
    {
        _router.Heard(Fancy, Frame("M0LTE", "G4ABC"));
        _time.Advance(TimeSpan.FromSeconds(30));
        _router.Heard(Legacy, Frame("M0LTE", "G4ABC"));

        _router.Route(Frame("G4ABC", "M0LTE")).Should().Be(Legacy);
    }

    [Fact]
    public void A_Station_Is_Forgotten_After_The_Window()
    {
        _router.Heard(Fancy, Frame("M0LTE", "G4ABC"));

        _time.Advance(TimeSpan.FromMinutes(59));
        _router.Route(Frame("G4ABC", "M0LTE")).Should().Be(Fancy);

        _time.Advance(TimeSpan.FromMinutes(1));
        _router.Route(Frame("G4ABC", "M0LTE")).Should().Be(
            Legacy, "a station not heard for an hour may have changed radios; the default reaches it");
    }

    [Fact]
    public void A_Frame_Through_A_Digipeater_Teaches_The_Digipeater_Not_The_Source()
    {
        // What reached our receiver was the digipeater's transmission. The source may be out of
        // range entirely, and whatever it can do says nothing about this hop.
        _router.Heard(Fancy, Frame("M0LTE", "G4ABC", "GB7DIG*"));

        _router.HeardOn("GB7DIG").Should().Be(Fancy);
        _router.HeardOn("G4ABC").Should().BeNull();
    }

    [Fact]
    public void The_Last_Digipeater_That_Repeated_It_Is_The_One_Heard()
    {
        _router.Heard(Fancy, Frame("M0LTE", "G4ABC", "GB7ONE*", "GB7TWO*", "GB7SIX"));

        _router.HeardOn("GB7TWO").Should().Be(Fancy);
        _router.HeardOn("GB7ONE").Should().BeNull();
        _router.HeardOn("GB7SIX").Should().BeNull("it has not repeated the frame yet");
    }

    [Fact]
    public void A_Frame_Sent_Through_A_Digipeater_Goes_In_The_Digipeaters_Mode()
    {
        _router.Heard(Fancy, Frame("M0LTE", "GB7DIG"));

        _router.Route(Frame("G4ABC", "M0LTE", "GB7DIG")).Should().Be(
            Fancy, "the first hop is the digipeater, and that is who has to decode it");
        _router.Route(Frame("GB7DIG", "M0LTE", "G4ABC")).Should().Be(
            Legacy, "here the first hop is G4ABC, who has not been heard");
    }

    [Fact]
    public void A_Broadcast_Takes_The_Default_Because_Its_Destination_Never_Transmits()
    {
        _router.Heard(Fancy, Frame("M0LTE", "G4ABC"));

        _router.Route(Frame("NODES", "M0LTE")).Should().Be(Legacy);
        _router.Route(Frame("ID", "M0LTE")).Should().Be(Legacy);
    }

    [Fact]
    public void A_Burst_Decoded_By_Two_Modems_Reaches_The_Host_Once_And_Teaches_The_First()
    {
        byte[] frame = Frame("M0LTE", "G4ABC");

        _router.Heard(Fancy, frame).Should().BeTrue();
        _router.Heard(Legacy, frame).Should().BeFalse("it is the same burst, decoded twice");

        _router.HeardOn("G4ABC").Should().Be(Fancy);
        _router.WasDelivered(Fancy, frame).Should().BeTrue();
        _router.WasDelivered(Legacy, frame).Should().BeFalse(
            "the quality frame describing the withheld copy must be withheld with it");
    }

    [Fact]
    public void The_Same_Frame_Twice_From_One_Modem_Is_Two_Transmissions()
    {
        // Two digipeaters repeating one WIDE2-1 frame produce identical bytes, and the shared
        // port delivers both; a polyglot port must not quietly deliver fewer.
        byte[] first = Frame("APRS", "G4ABC", "WIDE1*", "WIDE2-1");
        byte[] second = (byte[])first.Clone();

        _router.Heard(Legacy, first).Should().BeTrue();
        _router.Heard(Legacy, second).Should().BeTrue();

        _router.WasDelivered(Legacy, first).Should().BeTrue();
        _router.WasDelivered(Legacy, second).Should().BeTrue();
    }

    [Fact]
    public void A_Frame_Not_Yet_Heard_Was_Not_Delivered()
    {
        // The quality frame is asked about after its data frame. Asked first, there is no data
        // frame yet for it to describe.
        _router.WasDelivered(Legacy, Frame("M0LTE", "G4ABC")).Should().BeFalse();
    }

    [Fact]
    public void An_Identical_Frame_After_The_Window_Is_A_New_Frame()
    {
        // A host retrying an unacknowledged frame sends the same bytes again, seconds later.
        byte[] frame = Frame("M0LTE", "G4ABC");
        _router.Heard(Legacy, frame);

        _time.Advance(PolyglotRouter.DuplicateWindow);

        _router.Heard(Legacy, frame).Should().BeTrue();
    }

    [Fact]
    public void A_Modem_Outside_The_Port_Is_Neither_Delivered_Nor_Learned()
    {
        _router.Heard(5, Frame("M0LTE", "G4ABC")).Should().BeFalse();

        _router.HeardOn("G4ABC").Should().BeNull();
    }

    [Fact]
    public void A_Frame_Whose_Header_Does_Not_Read_Is_Delivered_But_Teaches_Nothing()
    {
        byte[] frame = Frame("M0LTE", "G4ABC");
        frame[8] = (byte)('!' << 1);

        _router.Heard(Fancy, frame).Should().BeTrue("the host decides what to make of it");
        _learned.Should().BeEmpty();
        _router.Route(frame).Should().Be(Legacy);
    }

    [Fact]
    public void Only_A_Change_Of_Modem_Is_Announced()
    {
        _router.Heard(Legacy, Frame("M0LTE", "G4ABC"));
        _learned.Should().BeEmpty("heard on the default, its frames were going there anyway");

        _router.Heard(Fancy, Frame("M0LTE", "G4ABC", "X"));
        _router.Heard(Fancy, Frame("M0LTE", "G4ABC", "Y"));
        _learned.Should().Equal(new PolyglotLearnedEvent("G4ABC", Fancy, Legacy));

        _time.Advance(TimeSpan.FromSeconds(5));
        _router.Heard(Legacy, Frame("M0LTE", "G4ABC"));
        _learned.Should().HaveCount(2).And.EndWith(new PolyglotLearnedEvent("G4ABC", Legacy, Fancy));
    }

    [Fact]
    public void A_Station_Forgotten_And_Heard_Again_On_Its_Old_Modem_Is_Announced_Again()
    {
        _router.Heard(Fancy, Frame("M0LTE", "G4ABC"));
        _time.Advance(TimeSpan.FromMinutes(61));

        _router.Heard(Fancy, Frame("M0LTE", "G4ABC"));

        _learned.Should().HaveCount(2, "in between, its frames went to the default");
    }

    [Theory]
    [InlineData(new[] { 0 }, 0)]
    [InlineData(new[] { 0, 0 }, 0)]
    [InlineData(new[] { 0, 1 }, 2)]
    public void A_Port_That_Cannot_Route_Is_Refused(int[] subChannels, int defaultSubChannel)
    {
        Action build = () => _ = new PolyglotRouter(subChannels, defaultSubChannel, TimeSpan.FromMinutes(1));

        build.Should().Throw<ArgumentException>();
    }
}
