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
    private static readonly UberSdrEndpoint Endpoint = new("rx.example.org", 443, true);

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
        // and its AGC in the path - the two things taking IQ exists to keep out. Better to say
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
            "M9PSY-1, RX888 with 40m Full Wave Loop (GPSDO), Dalgety Bay, Scotland, UK, "
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

        UberSdrAudioInput.Describe(json).Should().Be("G0XXX, Reading, UK");
    }

    [Fact]
    public void A_Session_That_Delivered_Nothing_Is_Timed_In_Milliseconds()
    {
        // Issue #409: six hours of "the session ended after only 0.0 s of audio" could not tell
        // a receiver that accepts and drops on the spot from one that holds the socket for a
        // moment first, and the two want different explanations. The close reason is left off,
        // because the receive threw and the stream-ended line above already prints it.
        string line = UberSdrAudioInput.SessionEndedLine(
            Endpoint,
            healthy: false,
            lasted: TimeSpan.FromMilliseconds(41),
            audioSamples: 0,
            outputRate: 12000,
            closeReason: "The remote party closed the WebSocket connection without completing "
                + "the close handshake.",
            pause: TimeSpan.FromSeconds(300));

        line.Should().Be(
            "the session ended after 41 ms with only 0 ms of audio; backing off 300s before "
            + "reconnecting to rx.example.org");
    }

    [Fact]
    public void A_Session_That_Closed_With_No_Error_Carries_The_Reason_Itself()
    {
        // Nothing threw, so no stream-ended line above this one says why the session stopped.
        string line = UberSdrAudioInput.SessionEndedLine(
            Endpoint,
            healthy: false,
            lasted: TimeSpan.FromMilliseconds(2500),
            audioSamples: 14400,
            outputRate: 12000,
            closeReason: null,
            pause: TimeSpan.FromSeconds(5));

        line.Should().Be(
            "the session ended after 2500 ms with only 1200 ms of audio (the receiver closed the "
            + "stream); backing off 5s before reconnecting to rx.example.org");
    }

    [Fact]
    public void The_Ordinary_Session_Rollover_Just_Says_It_Is_Reconnecting()
    {
        // Three hours of audio and the instance's max_session_time: unremarkable, and it stays
        // the one-line event it has always been.
        UberSdrAudioInput.SessionEndedLine(
            Endpoint,
            healthy: true,
            lasted: TimeSpan.FromHours(3),
            audioSamples: 129_600_000,
            outputRate: 12000,
            closeReason: null,
            pause: TimeSpan.FromSeconds(1))
            .Should().Be("reconnecting to rx.example.org");
    }

    [Fact]
    public void A_Journal_With_One_Sink_Writes_Both_Kinds_Of_Line_To_It()
    {
        // What the always-on device gets: no viewer count, because there are no viewers to
        // count, and a backoff line indistinguishable in shape from any other.
        var written = new List<string>();
        var journal = new UberSdrJournal(sentence => written.Add($"ubersdr: {sentence}"));

        journal.Note("reconnected to rx.example.org");
        journal.Waiting("rx.example.org unreachable (connection refused); retrying in 1s");

        written.Should().Equal(
            "ubersdr: reconnected to rx.example.org",
            "ubersdr: rx.example.org unreachable (connection refused); retrying in 1s");
    }
}
