using M0LTE.Il2p;
using Packet.SoundModem.Modems;

namespace Packet.SoundModem.Tests.Modems;

/// <summary>
/// The symbol-instant seam the sim's timing oracle drives (rx-roadmap workstream 4): the
/// demodulator can report the input-sample index of every symbol it read, and can be told to read
/// at a given list of instants instead of at its own DPLL's.
/// </summary>
/// <remarks>
/// The seam's whole worth is that a null result from it is trustworthy, so these tests are its
/// falsification: a schedule that reproduces the DPLL's own instants must reproduce its decode
/// exactly, and a schedule slipped half a symbol must destroy that decode. A seam that quietly did
/// nothing would pass the first test and fail the second, which is why both are here.
/// </remarks>
public class TimingOracleSeamTests
{
    private const int SampleRate = 12000;
    private const int SamplesPerSymbol = SampleRate / 300;

    private static readonly byte[] TestFrame =
        Convert.FromHexString("968264888AAEE4969668908A9465B8CF303132333435363738");

    private static float[] ModulateFrame()
    {
        byte[] wire = Il2pCodec.Encode(TestFrame, appendCrc: true);
        byte[] bits = Il2pFramer.FrameBits(wire, 96, Il2pFramer.PreambleStyle.Zeros);
        float[] audio = new BpskModulator(SampleRate, rollOff: 0.20).Modulate(bits);
        int pad = SampleRate / 5;
        var padded = new float[audio.Length + (2 * pad)];
        audio.CopyTo(padded, pad);
        return padded;
    }

    private static (List<byte[]> Frames, List<long> Instants) Run(
        float[] audio, IReadOnlyList<long>? schedule)
    {
        var frames = new List<byte[]>();
        var instants = new List<long>();
        var deframer = new Il2pDeframer((frame, _) => frames.Add(frame), crcMode: true);
        var demodulator = new BpskDemodulator(SampleRate, deframer.PushBit)
        {
            SymbolInstantObserver = instants.Add,
            SymbolInstantSchedule = schedule,
        };
        demodulator.Process(audio);
        return (frames, instants);
    }

    [Fact]
    public void The_Demodulator_Reports_One_Symbol_Instant_Per_Symbol_It_Read()
    {
        float[] audio = ModulateFrame();

        var (frames, instants) = Run(audio, schedule: null);

        frames.Should().ContainSingle().Which.Should().Equal(TestFrame);
        // Instants ascend, and land about a symbol apart across the burst: the clock is a clock.
        instants.Should().BeInAscendingOrder().And.HaveCountGreaterThan(100);
        long span = instants[^1] - instants[0];
        double meanSpacing = span / (double)(instants.Count - 1);
        meanSpacing.Should().BeApproximately(SamplesPerSymbol, 0.5);
    }

    [Fact]
    public void Replaying_The_Recovered_Instants_Reproduces_The_Decode()
    {
        float[] audio = ModulateFrame();
        var (_, instants) = Run(audio, schedule: null);

        var (frames, _) = Run(audio, instants);

        frames.Should().ContainSingle().Which.Should().Equal(TestFrame);
    }

    [Fact]
    public void A_Schedule_Slipped_Half_A_Symbol_Destroys_The_Decode()
    {
        float[] audio = ModulateFrame();
        var (_, instants) = Run(audio, schedule: null);
        List<long> slipped = instants.Select(i => i + (SamplesPerSymbol / 2)).ToList();

        var (frames, _) = Run(audio, slipped);

        // Sampling on the symbol boundaries rather than at their centres: the seam is driving
        // the receiver, so a null measured through it is a null in the receiver, not in the
        // instrument.
        frames.Should().BeEmpty();
    }

    [Fact]
    public void Instants_Before_The_Stream_Starts_Are_Stepped_Over_Not_Waited_For()
    {
        float[] audio = ModulateFrame();
        var (_, instants) = Run(audio, schedule: null);
        // A schedule displaced earlier than the first sample: its opening entries are in the past
        // before any audio arrives. Waiting for them would stall the schedule and emit nothing -
        // which reads exactly like "this timing does not decode", the most dangerous kind of
        // instrument fault.
        List<long> displaced = instants.Select(i => i - 3).ToList();

        var (frames, _) = Run(audio, displaced);

        frames.Should().ContainSingle().Which.Should().Equal(TestFrame);
    }

    [Fact]
    public void A_Schedule_Shorter_Than_The_Burst_Simply_Stops_Emitting()
    {
        float[] audio = ModulateFrame();
        var (_, instants) = Run(audio, schedule: null);

        var (frames, _) = Run(audio, instants.Take(20).ToList());

        frames.Should().BeEmpty();
    }
}
