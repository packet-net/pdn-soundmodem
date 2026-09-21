using AwesomeAssertions;
using M0LTE.Dsp;
using Packet.SoundModem.CarrierSense;
using Packet.SoundModem.Channel;
using Packet.SoundModem.Modems;

namespace Packet.SoundModem.Tests.CarrierSense;

/// <summary>
/// Carrier sense on an open-squelch FM path, against a channel model where a transmission makes
/// the receiver QUIETER (<see cref="OpenSquelchFmReceiver"/>).
/// </summary>
/// <remarks>
/// <para>The first half of this file pins the defect rather than a fix: an in-band energy detector
/// is not merely deaf on this path, it is anti-correlated, and no threshold moves that. It is
/// pinned so that nobody arrives at the numbers later and tunes them, and so that the day a
/// replacement is wired in there is a scoreboard it has to beat.</para>
/// <para>The second half is the fix that is in: the station asks the radio, and what the audio
/// thinks stops being able to overrule it. See <see cref="CarrierSenseRule"/>.</para>
/// </remarks>
public class OpenSquelchFmCarrierSenseTests
{
    private const double IdleSeconds = 4.0;
    private const double KeyedSeconds = 2.0;

    /// <summary>
    /// The fixture's own check. If this ever passes by accident with the levels the right way up,
    /// everything below is testing nothing.
    /// </summary>
    [Fact]
    public void The_Channel_Is_Quieter_While_Somebody_Is_Transmitting_Not_Louder()
    {
        float[] idle = OpenSquelchFmReceiver.Idle(2.0, seed: 11);
        float[] keyed = OpenSquelchFmReceiver.Keyed(2.0, seed: 12);

        double idleBand = InBandDbfs(idle);
        double keyedBand = InBandDbfs(keyed);
        double idleHiss = HissDbfs(idle);
        double keyedHiss = HissDbfs(keyed);

        (idleBand - keyedBand).Should().BeApproximately(
            OpenSquelchFmReceiver.IdleInBandDbfs - OpenSquelchFmReceiver.KeyedInBandDbfs, 0.5,
            "the measured in-band drop on radio1 is 2.9 dB and the sign is the whole point");

        keyedBand.Should().BeLessThan(idleBand,
            "an arriving carrier captures the discriminator and REMOVES noise power");

        (idleHiss - keyedHiss).Should().BeGreaterThan(12,
            "the hiss above the signal's band collapses far harder than the signal band moves, "
            + "which is the only thing the audio has going for it here");
    }

    /// <summary>
    /// The defect, measured against the model rather than argued about: the shipped energy
    /// detector spends the whole of somebody else's transmission reading clear.
    /// </summary>
    /// <remarks>
    /// This is what an FM station's CSMA has been consulting. The detector is not broken and its
    /// threshold is not wrong; it answers "has in-band power risen above an adapting floor", and
    /// on this path the answer to that question is no for the whole of every transmission. There
    /// is no value of the threshold that changes it, because the burst is BELOW the floor rather
    /// than near it.
    /// </remarks>
    [Fact]
    public void The_Energy_Detector_Reads_Clear_For_The_Whole_Of_A_Transmission()
    {
        float[] audio = OpenSquelchFmReceiver.IdleThenKeyedThenIdle(
            IdleSeconds, KeyedSeconds, seed: 21);
        bool[] busy = RunEnergyDetector(audio);

        int from = OpenSquelchFmReceiver.KeyupSample(IdleSeconds);
        int to = from + (int)(KeyedSeconds * OpenSquelchFmReceiver.SampleRate);

        BusyFraction(busy, from, to).Should().Be(0,
            "the station is deaf to the one thing carrier sense exists to hear");
    }

    /// <summary>
    /// And the warm-up is not the excuse: it reads clear during a transmission that arrives after
    /// it has heard four seconds of the idle channel and settled its floor on it.
    /// </summary>
    [Fact]
    public void A_Settled_Floor_Does_Not_Help_It()
    {
        float[] audio = OpenSquelchFmReceiver.IdleThenKeyedThenIdle(
            IdleSeconds, KeyedSeconds, seed: 31);
        bool[] busy = RunEnergyDetector(audio);

        int keyup = OpenSquelchFmReceiver.KeyupSample(IdleSeconds);

        // Four seconds of idle is 200 of the detector's own 20 ms blocks, against the 8 it seeds
        // from, so the floor is fully established and tracking the loud state before the keyup.
        BusyFraction(busy, 0, keyup).Should().Be(0, "idle noise is not a busy channel, correctly");
        BusyFraction(busy, keyup, busy.Length).Should().Be(0,
            "and neither is a transmission, which is the defect");
    }

    /// <summary>
    /// The control that stops the two tests above being vacuous: the same detector, the same
    /// harness, and a burst that is the LOUD part, which is every other fixture in this suite and
    /// is what an SSB receiver or a wired loop actually delivers.
    /// </summary>
    /// <remarks>
    /// Without this, a harness bug that fed the detector silence would produce the same two
    /// zeroes and read as a finding. It also states the scope of the defect: the energy detector
    /// is right for an additive path and stays in service on one, so what #522 changes is which
    /// source a station consults, not whether this class exists.
    /// </remarks>
    [Fact]
    public void The_Same_Detector_Fires_When_The_Signal_Is_The_Loud_Part()
    {
        float[] quiet = OpenSquelchFmReceiver.Idle(IdleSeconds, seed: 41);
        float[] loud = OpenSquelchFmReceiver.Idle(KeyedSeconds, seed: 42);
        for (int i = 0; i < loud.Length; i++)
        {
            loud[i] *= 10f;   // 20 dB, comfortably over the 6 dB assert
        }

        var audio = new float[quiet.Length + loud.Length];
        quiet.CopyTo(audio, 0);
        loud.CopyTo(audio, quiet.Length);

        bool[] busy = RunEnergyDetector(audio);

        BusyFraction(busy, 0, quiet.Length).Should().Be(0);
        BusyFraction(busy, quiet.Length, audio.Length).Should().BeGreaterThan(0.9,
            "on an additive path this detector does exactly what it says on the tin");
    }

    /// <summary>
    /// A radio that measures the channel is not deceived by any of it.
    /// </summary>
    [Fact]
    public void A_Radio_That_Knows_Decides_Regardless_Of_Which_Way_The_Audio_Went()
    {
        var radio = new StubBusySource(false);
        SoundModemChannel station = Station(radio, out FakeModem modem);

        modem.ChannelBusy = false;
        station.ChannelBusy.Should().BeFalse();

        radio.Busy = true;
        station.ChannelBusy.Should().BeTrue("the radio's squelch and RSSI settle it");
    }

    /// <summary>
    /// The regression for the failure that took a whole evening to find: an unused sub-channel
    /// holding the transmitter shut.
    /// </summary>
    /// <remarks>
    /// Both bench stations ran an OFDM-FM mode with an <c>afsk1200</c> sub-channel alongside that
    /// nobody was using. The AFSK modem heard every OFDM burst, its energy detector latched for
    /// ten seconds on the return of the open-squelch noise at the end of each one, and the
    /// transmit gate is the OR across every modem on the channel. Measured: 0.5 s from KISS to
    /// air on a quiet channel, 8.1 s two seconds after hearing ONE burst. One station talked, one
    /// was mute, and neither was faulty. With the radio asked instead, an audio detector cannot
    /// reach the decision at all.
    /// </remarks>
    [Fact]
    public void An_Unused_Sub_Channel_Can_No_Longer_Hold_The_Transmitter_Shut()
    {
        var radio = new StubBusySource(false);
        SoundModemChannel withRadio = Station(radio, out FakeModem latched);
        latched.ChannelBusy = true;

        withRadio.ChannelBusy.Should().BeFalse(
            "the radio says the channel is clear, and a latched energy detector is not evidence "
            + "that it is not");

        // The control: the same latched modem on a station with nothing better to ask still stops
        // it transmitting, which is the behaviour every station without a control cable keeps.
        SoundModemChannel audioOnly = Station(null, out FakeModem stillLatched);
        stillLatched.ChannelBusy = true;
        audioOnly.ChannelBusy.Should().BeTrue();
    }

    /// <summary>
    /// What the radio does NOT get to overrule: a demodulator that has actually locked onto a
    /// burst.
    /// </summary>
    [Fact]
    public void A_Real_Carrier_Detect_Still_Counts_Even_When_The_Radio_Says_Clear()
    {
        var radio = new StubBusySource(false);
        SoundModemChannel station = Station(radio, out FakeModem modem);

        modem.CarrierDetect = true;

        station.ChannelBusy.Should().BeTrue(
            "a burst somebody is sending is on the air right now, whatever the radio's squelch "
            + "makes of it - this is a demodulator lock and not an energy guess");
    }

    /// <summary>Fail-open, at the station. A source with no opinion leaves everything as it was.
    /// </summary>
    [Fact]
    public void A_Source_With_No_Opinion_Hands_The_Decision_Back_To_The_Audio()
    {
        SoundModemChannel station = Station(new StubBusySource(null), out FakeModem modem);

        modem.ChannelBusy = true;
        station.ChannelBusy.Should().BeTrue();

        modem.ChannelBusy = false;
        station.ChannelBusy.Should().BeFalse();
    }

    private static SoundModemChannel Station(IChannelBusySource? source, out FakeModem modem)
    {
        var made = new FakeModem();
        var channel = new SoundModemChannel(
            OpenSquelchFmReceiver.SampleRate, channelBusySource: source);
        channel.AddModem(0, _ => made);
        modem = made;
        return channel;
    }

    /// <summary>Feeds the detector what a modem's own receive filter would hand it.</summary>
    private static bool[] RunEnergyDetector(float[] audio)
    {
        var filter = new FirFilter(FilterDesign.LowPass(
            OpenSquelchFmReceiver.SignalTopHz, OpenSquelchFmReceiver.SampleRate, 257));
        var detector = new EnergyBusyDetector(OpenSquelchFmReceiver.SampleRate);
        var busy = new bool[audio.Length];
        for (int i = 0; i < audio.Length; i++)
        {
            detector.Process(filter.Next(audio[i]));
            busy[i] = detector.Busy;
        }

        return busy;
    }

    private static double BusyFraction(bool[] busy, int from, int to)
    {
        int count = 0;
        for (int i = from; i < to; i++)
        {
            if (busy[i])
            {
                count++;
            }
        }

        return (double)count / Math.Max(to - from, 1);
    }

    private static double InBandDbfs(float[] samples) =>
        OpenSquelchFmReceiver.BandPowerDbfs(
            samples, 300, OpenSquelchFmReceiver.SignalTopHz, 1000, samples.Length);

    private static double HissDbfs(float[] samples) =>
        OpenSquelchFmReceiver.BandPowerDbfs(
            samples, OpenSquelchFmReceiver.HissFromHz, OpenSquelchFmReceiver.HissToHz,
            1000, samples.Length);

    private sealed class StubBusySource(bool? busy) : IChannelBusySource
    {
        public bool? Busy { get; set; } = busy;
    }

    /// <summary>A modem whose two answers the test sets directly.</summary>
    private sealed class FakeModem : IModem
    {
        public string Mode => "c4fsk9600";

        public event Action<byte[], FrameQuality>? FrameDecoded;

        public bool CarrierDetect { get; set; }

        public bool ChannelBusy { get; set; }

        public void Process(ReadOnlySpan<float> samples)
        {
        }

        public float[] Modulate(ReadOnlySpan<byte> ax25Frame, int txDelayMilliseconds) => [];

        public void ResetCarrierState() => FrameDecoded?.Invoke([], default!);
    }
}
