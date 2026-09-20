using Packet.SoundModem.Modems.OfdmFm;
using Packet.SoundModem.Modems;

namespace Packet.SoundModem.Tests.Modems.OfdmFm;

/// <summary>
/// OFDM-FM driven as an <see cref="IModem"/>: the streaming receive adapter, and the frame going
/// out and coming back.
/// </summary>
/// <remarks>
/// The adapter is the part that could be wrong in a way the burst codec's own tests cannot see.
/// <see cref="Streamed_In_Blocks_Decodes_Exactly_What_The_Whole_Buffer_Path_Does"/> is the one that
/// matters: a modem that only works when handed the entire burst works on a bench and nowhere near
/// a sound card.
/// </remarks>
public class OfdmFmModemTests
{
    private static readonly OfdmFmParameters Profile = OfdmFmParameters.Synthetic;

    private static byte[] Frame(int length, int seed = 3)
    {
        var bytes = new byte[length];
        new Random(seed).NextBytes(bytes);
        return bytes;
    }

    private static (OfdmFmModem Modem, List<byte[]> Delivered, List<FrameQuality> Monitored) Build(
        OfdmFmParameters? profile = null)
    {
        var delivered = new List<byte[]>();
        var monitored = new List<FrameQuality>();
        var modem = new OfdmFmModem("ofdm-fm:test", profile ?? Profile, delivered.Add);
        modem.FrameDecoded += (_, quality) => monitored.Add(quality);
        return (modem, delivered, monitored);
    }

    [Fact]
    public void A_Frame_Goes_Out_Through_Modulate_And_Comes_Back_Through_Process()
    {
        (OfdmFmModem modem, List<byte[]> delivered, List<FrameQuality> monitored) = Build();
        byte[] frame = Frame(48);

        modem.Process(modem.Modulate(frame, txDelayMilliseconds: 0));

        delivered.Should().ContainSingle().Which.Should().Equal(frame);
        monitored.Should().ContainSingle();
        monitored[0].Mode.Should().Be("ofdm-fm:test");
        monitored[0].FrameBytes.Should().Be(frame.Length);
        monitored[0].CrcValid.Should().BeTrue("the burst's own CRC-16 was checked and passed");
        monitored[0].MonitorOnly.Should().BeFalse("every frame this modem reads goes to the host");
    }

    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(64)]
    [InlineData(1200)]  // 100 ms at 12 kHz - what the daemon actually hands a modem
    [InlineData(100_000)]
    public void Streamed_In_Blocks_Decodes_Exactly_What_The_Whole_Buffer_Path_Does(int block)
    {
        // The adapter's whole job, stated as a test: the same audio through the same decode,
        // however it is chopped up on the way in.
        byte[] frame = Frame(64);
        (OfdmFmModem modem, List<byte[]> delivered, _) = Build();
        float[] audio = modem.Modulate(frame, txDelayMilliseconds: 0);

        byte[]? wholeBuffer = new OfdmFmBurstCodec(Profile).Demodulate(audio)?.Payload;
        wholeBuffer.Should().Equal(frame, "the whole-buffer path is the reference here");

        for (int at = 0; at < audio.Length; at += block)
        {
            modem.Process(audio.AsSpan(at, Math.Min(block, audio.Length - at)));
        }

        delivered.Should().ContainSingle().Which.Should().Equal(wholeBuffer);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(1200)]
    public void The_Streaming_Search_Times_The_Burst_Where_The_Whole_Buffer_Search_Does(int block)
    {
        // Stronger than "both produced the same bytes", which one lucky vector could manage: the
        // two searches have to agree on where the burst actually is. They are different code -
        // one is a global argmax over a fixed buffer, the other an incremental peak-track over a
        // stream - so this is the assertion that keeps them honest.
        byte[] frame = Frame(48);
        (OfdmFmModem modem, List<byte[]> delivered, _) = Build();
        float[] audio = modem.Modulate(frame, txDelayMilliseconds: 3);

        int wholeBuffer = new OfdmFmBurstCodec(Profile).Demodulate(audio)!.StartSample;

        for (int at = 0; at < audio.Length; at += block)
        {
            modem.Process(audio.AsSpan(at, Math.Min(block, audio.Length - at)));
        }

        delivered.Should().ContainSingle();
        modem.LastSyncAtSample.Should().Be(wholeBuffer);
    }

    [Fact]
    public void A_Burst_Arriving_Split_Across_Every_Possible_Boundary_Still_Decodes()
    {
        // One sample per call is the pathological case: every piece of state has to survive being
        // interrupted between any two samples.
        byte[] frame = Frame(16);
        (OfdmFmModem modem, List<byte[]> delivered, _) = Build();
        float[] audio = modem.Modulate(frame, txDelayMilliseconds: 0);

        foreach (float sample in audio)
        {
            modem.Process([sample]);
        }

        delivered.Should().ContainSingle().Which.Should().Equal(frame);
    }

    [Fact]
    public void Two_Bursts_In_One_Stream_Both_Come_Out()
    {
        // The receiver has to go back to hunting after a burst rather than staying latched, and it
        // has to resume past the burst it just read rather than inside it.
        (OfdmFmModem modem, List<byte[]> delivered, _) = Build();
        byte[] first = Frame(24, seed: 1);
        byte[] second = Frame(40, seed: 2);

        float[] a = modem.Modulate(first, txDelayMilliseconds: 0);
        float[] b = modem.Modulate(second, txDelayMilliseconds: 0);
        var gap = new float[Profile.SymbolSamples * 5];

        var stream = new List<float>();
        stream.AddRange(a);
        stream.AddRange(gap);
        stream.AddRange(b);

        float[] all = [.. stream];
        for (int at = 0; at < all.Length; at += 1200)
        {
            modem.Process(all.AsSpan(at, Math.Min(1200, all.Length - at)));
        }

        delivered.Should().HaveCount(2);
        delivered[0].Should().Equal(first);
        delivered[1].Should().Equal(second);
    }

    [Fact]
    public void Bursts_Inside_One_Keyup_May_Follow_Each_Other_With_No_Silence_Between()
    {
        // The host asks for about 30 ms of lead-in before every frame after the first in a keyup,
        // and that used to round up to a whole silent symbol on every frame. A profile that opts
        // in gets none there, so the next sync symbol starts exactly where the previous burst
        // ended, which is exactly where the receiver resumes its search. The first frame of a
        // keyup keeps its full lead-in: that one is the radio's keying time.
        var profile = Profile with { ContiguousBursts = true };
        (OfdmFmModem modem, List<byte[]> delivered, _) = Build(profile);
        byte[] first = Frame(24, seed: 1);
        byte[] second = Frame(40, seed: 2);
        byte[] third = Frame(64, seed: 3);

        float[] a = modem.Modulate(first, txDelayMilliseconds: 300);
        float[] b = modem.Modulate(second, txDelayMilliseconds: 30);
        float[] c = modem.Modulate(third, txDelayMilliseconds: 30);
        int burstOnly = new OfdmFmBurstCodec(profile).Modulate(second, profile.Constellation, 0).Length;

        b.Length.Should().Be(burstOnly, "30 ms is less than a symbol, so no lead-in at all");
        a.Length.Should().BeGreaterThan(burstOnly, "the first frame's lead-in is untouched");

        float[] all = [.. a, .. b, .. c, .. new float[Profile.SymbolSamples * 2]];
        for (int at = 0; at < all.Length; at += 1200)
        {
            modem.Process(all.AsSpan(at, Math.Min(1200, all.Length - at)));
        }

        delivered.Should().HaveCount(3);
        delivered[0].Should().Equal(first);
        delivered[1].Should().Equal(second);
        delivered[2].Should().Equal(third);

        // And the default still rounds 30 ms up to whole symbols, at least one, as it always did.
        // On this short-symbol synthetic profile that is three of them.
        (OfdmFmModem plain, _, _) = Build();
        int roundedUp = (int)Math.Ceiling(30 * Profile.SymbolRate / 1000.0);
        roundedUp.Should().BeGreaterThan(0);
        plain.Modulate(second, txDelayMilliseconds: 30).Length.Should().Be(
            burstOnly + (roundedUp * Profile.SymbolSamples));
    }

    [Fact]
    public void A_Long_Contiguous_Keyup_Is_Not_Cut_Off_By_The_Sync_Attempt_Budget()
    {
        // The sync search allows a run of correlated samples a few commits and then refuses
        // more until the correlation breaks, so an unmodulated carrier cannot put a header read
        // on the receive thread at every sample. A host that plays its frames back to back never
        // lets the correlation break, because the hunt resumes exactly at the next sync symbol;
        // measured on air 2026-09-19, four bursts of every five decoded and the fifth refused.
        // A burst that copied ends the run: the budget must reset on it. Coded, as every real
        // profile is: the synthetic profile is uncoded and one payload pattern in a couple of
        // hundred does not survive even a clean channel uncoded (issue #11), which is not what
        // this test is about.
        var profile = Profile with
        {
            ContiguousBursts = true,
            Coding = new OfdmFmCoding(OfdmFmFec.Convolutional, 7, 1, 2, true),
        };
        (OfdmFmModem modem, List<byte[]> delivered, _) = Build(profile);
        byte[][] frames = Enumerable.Range(0, 11).Select(i => Frame(20 + (i * 7), seed: 100 + i)).ToArray();

        var pieces = new List<float[]>();
        for (int i = 0; i < frames.Length; i++)
        {
            pieces.Add(modem.Modulate(frames[i], txDelayMilliseconds: i == 0 ? 300 : 30));
        }

        pieces.Add(new float[Profile.SymbolSamples * 2]);
        float[] all = [.. pieces.SelectMany(p => p)];
        for (int at = 0; at < all.Length; at += 1200)
        {
            modem.Process(all.AsSpan(at, Math.Min(1200, all.Length - at)));
        }

        for (int i = 0; i < frames.Length; i++)
        {
            byte[] frame = frames[i];
            delivered.Should().Contain(f => f.SequenceEqual(frame), "frame {0} of {1} contiguous bursts", i, frames.Length);
        }

        delivered.Should().HaveCount(frames.Length);
    }

    [Fact]
    public void The_Buffer_Stays_Bounded_Through_A_Channel_That_Never_Goes_Quiet()
    {
        // The claim the adapter exists to make. A receiver that accumulated its stream and
        // re-searched it would show both of these climbing without limit, and would be quadratic
        // in the bargain.
        (OfdmFmModem modem, List<byte[]> delivered, _) = Build();
        var random = new Random(11);
        var block = new float[1200];

        int capacityAfterFirstSecond = 0;
        for (int second = 0; second < 20; second++)
        {
            for (int b = 0; b < 10; b++)
            {
                for (int n = 0; n < block.Length; n++)
                {
                    block[n] = (float)((random.NextDouble() * 2) - 1);
                }

                modem.Process(block);
            }

            if (second == 0)
            {
                capacityAfterFirstSecond = modem.BufferCapacity;
            }
        }

        // 20 seconds of noise at 12 kHz is 240,000 samples fed in.
        modem.BufferedSamples.Should().BeLessThan(Profile.SymbolSamples * 8);
        modem.BufferCapacity.Should().Be(capacityAfterFirstSecond);
        delivered.Should().BeEmpty("noise must not become a frame");
    }

    [Fact]
    public void A_Buffer_Sized_For_A_Burst_Is_Released_When_The_Burst_Is_Done()
    {
        (OfdmFmModem modem, List<byte[]> delivered, _) = Build();
        byte[] frame = Frame(512);

        modem.Process(modem.Modulate(frame, txDelayMilliseconds: 0));

        delivered.Should().ContainSingle();
        modem.BufferedSamples.Should().BeLessThan(
            Profile.SymbolSamples * 4, "the burst is decoded and gone, not still held");
    }

    [Fact]
    public void Silence_Is_Not_A_Frame_And_Not_A_Carrier()
    {
        (OfdmFmModem modem, List<byte[]> delivered, _) = Build();

        modem.Process(new float[Profile.SymbolSamples * 40]);

        delivered.Should().BeEmpty();
        modem.CarrierDetect.Should().BeFalse();
    }

    [Fact]
    public void Streaming_Never_Copies_Fewer_Frames_Than_The_Whole_Buffer_Path()
    {
        // The whole-buffer path is the reference the adapter has to be at least as good as, and on
        // a clean burst that is trivially true. It stops being trivial in noise, because the two
        // choose their sync differently: the reference takes a global argmax over the buffer, the
        // adapter takes the peak of the first plateau that crosses the threshold, and noise moves
        // both. This is the assertion that keeps the adapter honest across that whole regime
        // rather than on one clean vector.
        //
        // It is also the test that caught the case for retrying after a payload CRC failure. A
        // header that read and a payload that did not is not proof the timing was right - the
        // header is coded BPSK and survives an offset the QAM payload does not - and skipping
        // the whole announced burst threw away a position a few samples on that would have
        // decoded.
        const int Trials = 200;
        int reference = 0;
        int streamed = 0;

        for (int trial = 0; trial < Trials; trial++)
        {
            byte[] frame = Frame(40, seed: 1000 + trial);
            double sigma = 0.02 + (0.12 * (trial % 10) / 9.0);

            (OfdmFmModem modem, List<byte[]> delivered, _) = Build();
            float[] clean = modem.Modulate(frame, txDelayMilliseconds: 0);

            var random = new Random(trial);
            var noisy = new float[clean.Length];
            for (int n = 0; n < clean.Length; n++)
            {
                double u1 = 1.0 - random.NextDouble();
                double u2 = random.NextDouble();
                double gauss = Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
                noisy[n] = (float)(clean[n] + (sigma * gauss));
            }

            byte[]? whole = new OfdmFmBurstCodec(Profile).Demodulate(noisy)?.Payload;
            if (whole is not null && whole.AsSpan().SequenceEqual(frame))
            {
                reference++;
            }

            for (int at = 0; at < noisy.Length; at += 1200)
            {
                modem.Process(noisy.AsSpan(at, Math.Min(1200, noisy.Length - at)));
            }

            if (delivered.Count == 1 && delivered[0].AsSpan().SequenceEqual(frame))
            {
                streamed++;
            }

            delivered.Should().OnlyContain(
                f => f.SequenceEqual(frame),
                "noise must never become a different frame (trial {0})", trial);
        }

        Console.WriteLine($"MEASURED streamed={streamed} reference={reference} of {Trials}");
        streamed.Should().BeGreaterThanOrEqualTo(
            reference,
            "the streaming adapter must be at least as good as the whole-buffer path it wraps "
            + "(streamed {0}, reference {1}, of {2})",
            streamed,
            reference,
            Trials);
    }

    [Fact]
    public void A_Wrecked_Payload_Is_Refused_Rather_Than_Delivered()
    {
        (OfdmFmModem modem, List<byte[]> delivered, List<FrameQuality> monitored) = Build();
        float[] audio = modem.Modulate(Frame(32), txDelayMilliseconds: 0);

        // Past the sync, preamble and header, into the payload symbols. Derived rather than
        // counted: the header spans more than one symbol and how many depends on the profile.
        // txDelayMilliseconds: 0 gives ONE lead-in symbol, not two. Hand-counting it was the very
        // mistake this line was changed to remove, and with a single payload symbol the old
        // arithmetic would have run past the end of the burst and failed for an unrelated reason.
        int from = Profile.SymbolSamples + new OfdmFmBurstCodec(Profile).HeaderEndOffset;
        for (int n = from; n < Math.Min(from + Profile.SymbolSamples, audio.Length); n++)
        {
            audio[n] = 0f;
        }

        modem.Process(audio);

        delivered.Should().BeEmpty("a frame that failed its CRC must never reach the host");
        monitored.Should().BeEmpty();
    }

    [Fact]
    public void A_Burst_Whose_Payload_Fails_Is_Retried_A_Few_Times_And_Not_Once_Per_Plateau_Sample()
    {
        // A failed payload is retried a sample on, because the commonest cause is a sync
        // committed a few samples off a peak that noise moved. The retries were meant to be
        // capped at MaxSyncAttemptsPerRun per plateau, and were not: the budget was reset on the
        // way to every commit, so a burst that could not decode was decoded once per sample of
        // its sync plateau. On a long cyclic prefix that is a hundred full decodes of one burst,
        // measured on air as a second of the receive thread per failed burst and the bursts
        // behind it lost. The prefix here is long so the difference is visible.
        var profile = Profile with { CyclicPrefix = 64 };
        var heard = new List<OfdmFmBurst>();
        var modem = new OfdmFmModem("ofdm-fm:test", profile, _ => { });
        modem.BurstHeard += heard.Add;
        float[] audio = modem.Modulate(Frame(32), txDelayMilliseconds: 0);
        int from = profile.SymbolSamples + new OfdmFmBurstCodec(profile).HeaderEndOffset;
        for (int n = from; n < Math.Min(from + profile.SymbolSamples, audio.Length); n++)
        {
            audio[n] = 0f;
        }

        modem.Process(audio);
        modem.Process(new float[profile.SymbolSamples * 4]);

        heard.Should().NotBeEmpty("the header reads; it is the payload that is wrecked");
        heard.Should().OnlyContain(b => b.Payload == null);
        heard.Count.Should().BeLessThanOrEqualTo(OfdmFmModem.MaxSyncAttemptsPerRun);
    }

    [Fact]
    public void Carrier_Detect_Comes_Up_On_A_Burst_And_Goes_Down_After_It()
    {
        (OfdmFmModem modem, _, _) = Build();
        float[] audio = modem.Modulate(Frame(64), txDelayMilliseconds: 0);

        // Feed only as far as the header: the burst is committed and incomplete, so the receiver
        // is holding it and knows the channel is in use.
        int intoTheBurst = Profile.SymbolSamples * 4;
        modem.Process(audio.AsSpan(0, intoTheBurst));
        modem.CarrierDetect.Should().BeTrue();

        modem.Process(audio.AsSpan(intoTheBurst));
        modem.CarrierDetect.Should().BeFalse("the burst has been read");
    }

    [Fact]
    public void Resetting_Carrier_State_Drops_A_Burst_In_Progress()
    {
        // What a station does while it transmits: whatever was half heard is not a frame.
        (OfdmFmModem modem, List<byte[]> delivered, _) = Build();
        float[] audio = modem.Modulate(Frame(64), txDelayMilliseconds: 0);
        int half = Profile.SymbolSamples * 4;

        modem.Process(audio.AsSpan(0, half));
        modem.CarrierDetect.Should().BeTrue();
        modem.ResetCarrierState();
        modem.CarrierDetect.Should().BeFalse();

        modem.Process(audio.AsSpan(half));

        delivered.Should().BeEmpty("the first half is gone, so there is no burst to complete");
    }

    [Fact]
    public void A_Frame_Longer_Than_This_Waveform_Carries_Is_Refused_At_The_Transmitter()
    {
        (OfdmFmModem modem, _, _) = Build();

        // Everything the header can name is carried, and nothing more: a cap below the wire
        // format's own would sit on the biggest throughput lever this waveform has, and one
        // above it would be a length the header cannot write.
        OfdmFmModem.MaxPayloadBytes.Should().Be(OfdmFmBurstCodec.MaxPayloadBytes);

        Action act = () => modem.Modulate(new byte[OfdmFmModem.MaxPayloadBytes + 1], 0);

        act.Should().Throw<ArgumentException>().WithMessage("*longer than*");
    }

    [Fact]
    public void Tx_Delay_Becomes_Silence_In_Front_Of_The_Burst()
    {
        (OfdmFmModem modem, List<byte[]> delivered, _) = Build();
        byte[] frame = Frame(16);

        float[] none = modem.Modulate(frame, txDelayMilliseconds: 0);
        float[] delayed = modem.Modulate(frame, txDelayMilliseconds: 200);

        double expected = 200 * Profile.SymbolRate / 1000.0;
        int extraSymbols = (delayed.Length - none.Length) / Profile.SymbolSamples;
        extraSymbols.Should().BeInRange((int)expected - 1, (int)expected + 1);

        modem.Process(delayed);
        delivered.Should().ContainSingle().Which.Should().Equal(frame);
    }

    [Fact]
    public void The_Same_Waveform_On_A_Finer_Grid_Carries_The_Same_Frame()
    {
        // Rescaling is not resampling: same spacing, same occupied band, same symbol duration,
        // more samples describing them. It is what lets a profile written at one rate run on a
        // channel at another with no filter anywhere in the path.
        OfdmFmParameters fine = Profile.Rescaled(48000)!;

        fine.SampleRate.Should().Be(48000);
        fine.FftSize.Should().Be(Profile.FftSize * 4);
        fine.CyclicPrefix.Should().Be(Profile.CyclicPrefix * 4);
        fine.FirstCarrier.Should().Be(Profile.FirstCarrier, "the bins do not move, the grid does");
        fine.SubcarrierSpacingHz.Should().BeApproximately(Profile.SubcarrierSpacingHz, 1e-9);
        fine.SymbolRate.Should().BeApproximately(Profile.SymbolRate, 1e-9);
        fine.Occupancy.LowHz.Should().BeApproximately(Profile.Occupancy.LowHz, 1e-9);
        fine.Occupancy.HighHz.Should().BeApproximately(Profile.Occupancy.HighHz, 1e-9);

        (OfdmFmModem modem, List<byte[]> delivered, _) = Build(fine);
        byte[] frame = Frame(32);

        modem.Process(modem.Modulate(frame, txDelayMilliseconds: 0));

        delivered.Should().ContainSingle().Which.Should().Equal(frame);
    }

    [Theory]
    [InlineData(48000)]
    [InlineData(24000)]
    [InlineData(12000)]
    [InlineData(6000)]
    [InlineData(3000)]
    public void Every_Runnable_Profile_Runs_At_48_kHz_And_Nothing_Else(int profileRate)
    {
        // One rate, deliberately. It is what a CM108 runs natively, so the capture rate and the
        // DSP rate are the same number and nothing resamples anywhere; and the rate is measured to
        // cost the modem nothing either way, so a second option would only be a second path to
        // test. This modem is not expected to share a radio with modes that want 12 kHz.
        (Profile with { SampleRate = profileRate }).HostSampleRate.Should().Be(48000);
    }

    [Theory]
    [InlineData(11025)]  // no integer ratio at all
    [InlineData(8000)]   // 48000/8000 is 6, and a non-power-of-two ratio is not a power-of-two FFT
    [InlineData(16000)]  // 48000/16000 is 3, likewise
    public void A_Profile_That_Cannot_Reach_A_Host_Rate_Says_So(int profileRate)
    {
        (Profile with { SampleRate = profileRate }).HostSampleRate.Should().BeNull();
    }
}
