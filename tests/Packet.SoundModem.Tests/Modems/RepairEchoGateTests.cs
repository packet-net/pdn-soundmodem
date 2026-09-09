using Packet.SoundModem.Modems;

namespace Packet.SoundModem.Tests.Modems;

/// <summary>
/// <see cref="RepairEchoGate"/>: the near-duplicate rule that stops a repaired copy of a
/// burst the receiver also decoded cleanly from being delivered as a second frame.
/// </summary>
public class RepairEchoGateTests
{
    private const int Rate = 12000;

    private static byte[] Frame(byte fill = 0x42, int length = 40)
    {
        var frame = new byte[length];
        Array.Fill(frame, fill);
        return frame;
    }

    [Fact]
    public void An_Identical_Copy_Inside_The_Window_Is_An_Echo()
    {
        var gate = new RepairEchoGate(Rate);
        byte[] frame = Frame();
        gate.RecordClean(frame, 10_000);

        gate.IsEcho(frame, 10_000 + (long)(0.2 * Rate)).Should().BeTrue();
    }

    [Fact]
    public void A_Near_Copy_Inside_The_Window_Is_An_Echo()
    {
        var gate = new RepairEchoGate(Rate);
        byte[] frame = Frame();
        gate.RecordClean(frame, 10_000);

        var damaged = Frame();
        for (int i = 0; i < RepairEchoGate.HammingThreshold; i++)
        {
            damaged[i] ^= 0xFF;
        }

        gate.IsEcho(damaged, 10_000 + (long)(0.2 * Rate)).Should().BeTrue();
    }

    [Fact]
    public void A_Copy_Differing_By_More_Than_The_Threshold_Is_Not_An_Echo()
    {
        var gate = new RepairEchoGate(Rate);
        byte[] frame = Frame();
        gate.RecordClean(frame, 10_000);

        var different = Frame();
        for (int i = 0; i <= RepairEchoGate.HammingThreshold; i++)
        {
            different[i] ^= 0xFF;
        }

        gate.IsEcho(different, 10_000).Should().BeFalse();
    }

    [Fact]
    public void A_Copy_Of_A_Different_Length_Is_Not_An_Echo()
    {
        var gate = new RepairEchoGate(Rate);
        gate.RecordClean(Frame(length: 40), 10_000);

        gate.IsEcho(Frame(length: 41), 10_000).Should().BeFalse();
    }

    [Fact]
    public void A_Copy_Outside_The_Window_Is_Not_An_Echo()
    {
        var gate = new RepairEchoGate(Rate);
        byte[] frame = Frame();
        gate.RecordClean(frame, 10_000);

        long outside = 10_000 + (long)(RepairEchoGate.WindowFraction * Rate) + 2;
        gate.IsEcho(frame, outside).Should().BeFalse();
    }

    [Fact]
    public void An_Echo_Before_The_Clean_Delivery_Is_Still_An_Echo()
    {
        // The window is symmetric: a repaired copy whose burst's clean decode lands a
        // moment LATER is the same transmission - the hold the modems apply exists to give
        // that clean copy time to arrive and be judged against.
        var gate = new RepairEchoGate(Rate);
        byte[] frame = Frame();
        gate.RecordClean(frame, 10_000);

        gate.IsEcho(frame, 10_000 - (long)(0.2 * Rate)).Should().BeTrue();
    }

    [Fact]
    public void Clear_Forgets_Everything()
    {
        var gate = new RepairEchoGate(Rate);
        byte[] frame = Frame();
        gate.RecordClean(frame, 10_000);
        gate.Clear();

        gate.IsEcho(frame, 10_000).Should().BeFalse();
    }
}
