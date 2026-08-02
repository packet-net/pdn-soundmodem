using AwesomeAssertions;
using Packet.SoundModem.UberSdr;

namespace Packet.SoundModem.Tests.UberSdr;

/// <summary>
/// The parts of the live UberSDR receive device that can be checked without a receiver: the IQ
/// mode arithmetic, and the receiver description that goes into the start-up banner and the
/// waterfall's top bar.
/// </summary>
public sealed class UberSdrAudioInputTests
{
    [Theory]
    [InlineData("iq48", 48000)]
    [InlineData("iq96", 96000)]
    [InlineData("IQ192", 192000)]
    public void An_iq_mode_name_states_its_sample_rate(string mode, int expected)
    {
        UberSdrAudioInput.IqRateFor(mode).Should().Be(expected);
    }

    [Theory]
    [InlineData("usb")]        // the instance's own demodulated audio, not IQ
    [InlineData("iq")]
    [InlineData("iqfast")]
    [InlineData("")]
    public void A_mode_that_is_not_an_iq_stream_is_refused_by_name(string mode)
    {
        // Asking a receiver for demodulated audio would work, and would quietly put its filter
        // and its AGC in the path — the two things taking IQ exists to keep out. Better to say
        // so than to accept it.
        Action rate = () => UberSdrAudioInput.IqRateFor(mode);

        rate.Should().Throw<InvalidOperationException>().WithMessage("*iq48*");
    }

    [Fact]
    public void The_receiver_description_names_who_is_listening_and_from_where()
    {
        // Trimmed from a real /api/description off m9psy-1.
        const string json = """
            {
              "receiver": {
                "callsign": "M9PSY-1",
                "name": "RX888 with 40m Full Wave Loop (GPSDO)",
                "location": "Dalgety Bay, Scotland, UK",
                "asl": 30
              },
              "frequency_reference": {
                "enabled": true,
                "expected_frequency": 25000000,
                "detected_frequency": 25000000,
                "frequency_offset": 0,
                "snr": 59.2
              }
            }
            """;

        UberSdrAudioInput.Describe(json).Should().Be(
            "M9PSY-1 · RX888 with 40m Full Wave Loop (GPSDO) · Dalgety Bay, Scotland, UK · "
            + "reference offset 0 Hz");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json at all")]
    [InlineData("[1, 2, 3]")]
    [InlineData("{}")]
    [InlineData("{\"receiver\": {\"callsign\": 42}}")]
    public void A_description_it_cannot_read_costs_a_banner_line_and_nothing_else(string? json)
    {
        // Every field there is the receiver operator's to fill in as they please, so none of its
        // shape is guaranteed. Losing the line is acceptable; failing to open the stream is not.
        UberSdrAudioInput.Describe(json).Should().BeNull();
    }

    [Fact]
    public void A_receiver_without_a_frequency_reference_is_described_without_one()
    {
        const string json = """
            {
              "receiver": { "callsign": "G0XXX", "location": "Reading, UK" },
              "frequency_reference": { "enabled": false, "frequency_offset": 12 }
            }
            """;

        UberSdrAudioInput.Describe(json).Should().Be("G0XXX · Reading, UK");
    }
}
