using System.Buffers.Binary;
using Packet.SoundModem.Modems;
using Packet.SoundModem.Waterfall;

namespace Packet.SoundModem.Tests.Modems;

/// <summary>
/// Issue #339: over a perfectly silent channel, bpsk300 deterministically failed to decode
/// certain ordinary frames - and only those - while any noise at all, even at 120 dB SNR,
/// made them decode first time.
/// </summary>
/// <remarks>
/// <para>The mechanism, for the record: the failing frames' IL2P-scrambled bit streams each
/// contain a 48-bit run of one bit value (4 of the 256 values of one payload byte produce
/// one; the scrambler makes long runs improbable, not impossible). A run of reversals is a
/// full-strength alternating carrier whose differential product never changes sign, so the
/// slicer sees no transitions; the product grazes zero once per symbol at the envelope
/// nulls, and on a mathematically perfect input the float residue that decides each graze's
/// side decays to nothing within a few symbols - after which <see cref="PacketDcd"/>'s
/// quiet-symbol path counted the run as silence, dropped DCD mid-frame, and the falling
/// edge reset the deframers with the sync word already consumed (no RS failure, no CRC
/// failure: nothing was ever attempted again). Any noise keeps the grazes chattering -
/// well-timed transitions - which is why only the noiseless channel ever showed it. The fix
/// feeds each symbol's decision magnitude to the quiet path, so a transition-free run at
/// full strength no longer reads as silence.</para>
/// <para>The frames are pinned from the wild: pdn-qso's perf-stream shape (its PR #21),
/// whose session-byte sweep found the four failing values. Roll-off 0.35 - what
/// <see cref="BpskMultiModem"/>'s branches run - reproduces it; the run is in the bits, the
/// shaping only sets which frames' grazes die.</para>
/// </remarks>
public class NoiselessRunTests
{
    private const int SampleRate = 12000;

    /// <summary>pdn-qso's perf-stream UI frame: M0LTE-7&gt;QSO, 40 payload bytes of
    /// seq/total/timestamp header and deterministic filler, one session byte swept.</summary>
    private static byte[] PerfFrame(byte session, ushort seq)
    {
        var info = new byte[42];
        info[0] = 0x20;   // PerfStream
        info[1] = session;
        BinaryPrimitives.WriteUInt16BigEndian(info.AsSpan(2, 2), seq);
        BinaryPrimitives.WriteUInt16BigEndian(info.AsSpan(4, 2), 8);
        BinaryPrimitives.WriteUInt32BigEndian(info.AsSpan(6, 4), 0);
        for (int i = 8; i < 40; i++)
        {
            info[2 + i] = unchecked((byte)i);
        }

        return Ax25UiFrame.Build("M0LTE-7", "QSO", info);
    }

    [Theory]
    [InlineData(194, 7)]
    [InlineData(219, 5)]
    [InlineData(233, 1)]
    [InlineData(240, 3)]
    public void A_Frame_Whose_Scrambled_Bits_Hold_A_Long_Run_Decodes_Over_A_Silent_Channel(
        byte session, ushort seq)
    {
        byte[] frame = PerfFrame(session, seq);
        var frames = new List<byte[]>();
        var receiver = new BpskModem(SampleRate, frames.Add, crc: true, 1500, 300, rollOff: 0.35);
        var transmitter = new BpskModem(SampleRate, _ => { }, crc: true, 1500, 300, rollOff: 0.35);

        float[] audio = transmitter.Modulate(frame, txDelayMilliseconds: 300);
        var padded = new float[audio.Length + 4800];
        audio.CopyTo(padded, 0);
        receiver.Process(padded);

        frames.Should().ContainSingle(
            "a bit-exact noiseless burst is the easiest signal a receiver is ever offered")
            .Which.Should().Equal(frame);
    }

    [Fact]
    public void The_Deployed_Bank_Decodes_Such_A_Frame_Over_A_Silent_Channel_Too()
    {
        // The end-to-end shape the finding arrived in: the catalogue's bpsk300 is this bank,
        // and no branch of it ever found sync on these frames.
        byte[] frame = PerfFrame(194, 7);
        var frames = new List<byte[]>();
        var receiver = BpskMultiModem.Bpsk300(SampleRate, frames.Add);
        var transmitter = BpskMultiModem.Bpsk300(SampleRate, _ => { });

        float[] audio = transmitter.Modulate(frame, txDelayMilliseconds: 300);
        var padded = new float[audio.Length + 4800];
        audio.CopyTo(padded, 0);
        receiver.Process(padded);

        frames.Should().ContainSingle().Which.Should().Equal(frame);
    }

    [Fact]
    public void A_Transition_Free_Run_At_Full_Strength_Does_Not_Drop_Dcd()
    {
        // The root cause in one place: symbols keep arriving at full decision magnitude
        // with no transitions - a held tone, a constant phase, a run of one scrambled bit
        // value. That is a carrier, not silence, however long it lasts.
        var dcd = new PacketDcd();
        for (int i = 0; i < 32; i++)
        {
            dcd.OnSymbol(1.0);
            dcd.OnTransition(0.01);
        }

        dcd.Asserted.Should().BeTrue("32 well-timed transitions are a packet signal");

        for (int i = 0; i < 200; i++)
        {
            dcd.OnSymbol(1.0);
        }

        dcd.Asserted.Should().BeTrue("a transition-free run at full strength is not silence");
    }

    [Fact]
    public void Silence_Still_Drops_Dcd_Within_The_Quiet_Count()
    {
        // The hole OnSymbol exists to close stays closed: digital silence has no
        // transitions to score badly, and must still release DCD on its own.
        var dcd = new PacketDcd();
        for (int i = 0; i < 32; i++)
        {
            dcd.OnSymbol(1.0);
            dcd.OnTransition(0.01);
        }

        dcd.Asserted.Should().BeTrue();

        for (int i = 0; i < 24; i++)
        {
            dcd.OnSymbol(0.0);
        }

        dcd.Asserted.Should().BeFalse("24 signal-free symbols mean the carrier stopped");
    }
}
