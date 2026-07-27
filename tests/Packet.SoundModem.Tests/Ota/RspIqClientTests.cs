using System.Buffers.Binary;
using Packet.SoundModem.Ota;
using Packet.SoundModem.UberSdr;

namespace Packet.SoundModem.Tests.Ota;

/// <summary>
/// The RSP1 capture backend: the hardware-free parts (CF32 → int16 stereo conversion and the
/// remote command assembly) exhaustively, plus one live smoke test against the real RSP1 that is
/// gated on an environment variable so it never runs in CI.
/// </summary>
public class RspIqClientTests
{
    private static byte[] Cf32(params float[] vals)
    {
        var b = new byte[vals.Length * 4];
        for (int k = 0; k < vals.Length; k++)
        {
            BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan(k * 4), vals[k]);
        }

        return b;
    }

    private static short[] Int16s(ReadOnlySpan<byte> pcm)
    {
        var s = new short[pcm.Length / 2];
        for (int k = 0; k < s.Length; k++)
        {
            s[k] = BinaryPrimitives.ReadInt16LittleEndian(pcm[(k * 2)..]);
        }

        return s;
    }

    [Fact]
    public void Full_scale_and_beyond_clamps_without_wrapping()
    {
        // Two frames: (I=+1, Q=−1) at the rails, then (I=+2, Q=−2) beyond them. The point is the
        // over-range pair SATURATES — a wrap would turn +2 into a large negative and hand the
        // scorer a phantom sample.
        byte[] input = Cf32(1f, -1f, 2f, -2f);
        var output = new byte[8];

        int n = new Cf32ToInt16Stereo().Convert(input, output);

        n.Should().Be(8);
        short[] s = Int16s(output);
        s[0].Should().Be(32767);   // +1.0 → full scale
        s[1].Should().Be(-32767);  // −1.0
        s[2].Should().Be(32767);   // +2.0 clamps (a wrap would be −2)
        s[3].Should().Be(-32768);  // −2.0 clamps to the negative rail
    }

    [Fact]
    public void A_mid_level_tone_round_trips_to_the_expected_int16_amplitude()
    {
        // A half-scale complex tone: a constant-envelope exponential, so every frame's |I+jQ|
        // should come back at 0.5 × 32767 ≈ 16383.5, and no channel should clip.
        const int fs = 96000;
        const int frames = 4096;
        const double amp = 0.5;
        var vals = new float[frames * 2];
        for (int k = 0; k < frames; k++)
        {
            double phase = 2 * Math.PI * 1200 * k / fs;
            vals[2 * k] = (float)(amp * Math.Cos(phase));
            vals[(2 * k) + 1] = (float)(amp * Math.Sin(phase));
        }

        var output = new byte[frames * 4];
        int n = new Cf32ToInt16Stereo().Convert(Cf32(vals), output);
        n.Should().Be(frames * 4);

        short[] s = Int16s(output);
        int peak = 0;
        for (int k = 0; k < frames; k++)
        {
            double env = Math.Sqrt(((double)s[2 * k] * s[2 * k]) + ((double)s[(2 * k) + 1] * s[(2 * k) + 1]));
            env.Should().BeApproximately(16383.5, 2.0, "a constant-envelope tone must keep its amplitude");
            peak = Math.Max(peak, Math.Max(Math.Abs((int)s[2 * k]), Math.Abs((int)s[(2 * k) + 1])));
        }

        peak.Should().BeInRange(16382, 16384, "the per-channel peak is the tone amplitude scaled to int16");
    }

    [Fact]
    public void Iq_ordering_is_preserved_with_i_on_the_left_and_q_on_the_right()
    {
        // Distinct magnitudes so a swap could not pass: |I| = 0.25 → ~8192, |Q| = 0.75 → ~24575.
        var output = new byte[4];
        new Cf32ToInt16Stereo().Convert(Cf32(0.25f, -0.75f), output);

        short[] s = Int16s(output);
        s[0].Should().BeInRange(8191, 8192);       // I → left, positive, small
        s[1].Should().BeInRange(-24576, -24575);   // Q → right, negative, large
    }

    [Fact]
    public void A_frame_split_across_two_reads_is_carried_and_not_lost()
    {
        // 16 bytes = two whole frames. Feed 11 bytes (one frame + 3 spare), then the last 5. The
        // second frame straddles the two calls, so it exercises the carry.
        byte[] full = Cf32(0.1f, 0.2f, 0.3f, 0.4f);
        var converter = new Cf32ToInt16Stereo();
        var output = new byte[8];

        int n1 = converter.Convert(full.AsSpan(0, 11), output.AsSpan(0));
        n1.Should().Be(4); // only the first whole frame this call
        int n2 = converter.Convert(full.AsSpan(11), output.AsSpan(4));
        n2.Should().Be(4); // the straddling frame, completed from the carry

        // Identical to converting the whole buffer in one call.
        var oneShot = new byte[8];
        new Cf32ToInt16Stereo().Convert(full, oneShot).Should().Be(8);
        output.Should().Equal(oneShot);
    }

    [Fact]
    public void The_partial_bytes_of_a_lone_incomplete_frame_produce_no_output()
    {
        // Fewer than 8 bytes on their own: nothing to emit yet, everything carried.
        var output = new byte[8];
        new Cf32ToInt16Stereo().Convert(Cf32(0.5f, 0.5f).AsSpan(0, 5), output).Should().Be(0);
    }

    [Fact]
    public void The_remote_command_carries_freq_rate_cf32_gain_and_the_agc_off_request()
    {
        var opt = new RspCaptureOptions { FrequencyHz = 18_100_000, SampleRate = 96000, DurationSeconds = 0 };

        string cmd = RspIqClient.BuildRemoteCommand(opt);

        cmd.Should().Contain("rx_sdr -d driver=sdrplay");
        cmd.Should().Contain("-f 18100000");
        cmd.Should().Contain("-s 96000");
        cmd.Should().Contain("-F CF32");
        cmd.Should().Contain("-g AGC=false,IFGR=40,RFGR=0");
        cmd.Should().Contain("AGC=false", "the command requests AGC off in rx_tools' conventional form");
        cmd.Should().Contain("echo RXPID:$$", "the PID is reported so the remote process can be killed cleanly");
        cmd.Should().EndWith(" -", "CF32 is streamed to stdout");
        cmd.Should().NotContain("-n ", "no duration means no sample-count backstop (runs until stopped)");
    }

    [Fact]
    public void A_fixed_duration_adds_an_n_sample_backstop_so_a_missed_kill_cannot_orphan_the_device()
    {
        var opt = new RspCaptureOptions
        {
            FrequencyHz = 18_100_000,
            SampleRate = 96000,
            StartupGuardMs = 1000,
            DurationSeconds = 10,
        };

        // guard 96000 + target 10×96000 + 1 s margin 96000 = 1 152 000
        RspIqClient.BuildRemoteCommand(opt).Should().Contain("-n 1152000");
    }

    [Fact]
    public void The_ssh_arguments_carry_the_identity_the_target_and_the_command_last()
    {
        var opt = new RspCaptureOptions
        {
            FrequencyHz = 18_100_000,
            Host = "studybox",
            SshKeyPath = "/home/tf/.ssh/id_ed25519",
        };
        string cmd = RspIqClient.BuildRemoteCommand(opt);

        List<string> args = RspIqClient.BuildSshArguments(opt, cmd);

        args.Should().ContainInOrder("-i", "/home/tf/.ssh/id_ed25519");
        args.Should().Contain("tf@studybox", "a bare host is reached as the tf user");
        args.Should().Contain("BatchMode=yes", "the capture is non-interactive");
        args[^1].Should().Be(cmd, "ssh joins trailing args into the remote command, so it must be last");
    }

    [Fact]
    public void An_explicit_user_in_the_host_is_left_alone()
    {
        var opt = new RspCaptureOptions { FrequencyHz = 18_100_000, Host = "pi@othersdr" };
        RspIqClient.BuildSshArguments(opt, "cmd").Should().Contain("pi@othersdr");
    }

    [Fact(Timeout = 90_000)]
    public async Task Captures_from_the_real_rsp1_into_a_wav_the_scorer_can_open()
    {
        // Live RX-only smoke test on the real RSP1 (studybox). It needs ssh to the rig, so it is
        // gated: set SM_OTA_RSP_HW=1 to run it on the bench box; unset (CI, other machines) it
        // skips. There is no burst to decode — the point is that capture → WAV → PcmWavReader
        // works end to end on real hardware.
        Assert.SkipUnless(
            Environment.GetEnvironmentVariable("SM_OTA_RSP_HW") == "1",
            "set SM_OTA_RSP_HW=1 to run the live RSP1 capture (requires ssh to studybox)");

        string dir = Path.Combine(Path.GetTempPath(), "rsp-hw-" + Guid.NewGuid().ToString("N"));
        try
        {
            var opt = new RspCaptureOptions
            {
                Host = Environment.GetEnvironmentVariable("SM_OTA_RSP_HOST") ?? "studybox",
                FrequencyHz = 18_100_000,
                SampleRate = 96000,
                DurationSeconds = 4,
                OutputDir = dir,
                Name = "rsp-hwtest",
            };

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            CaptureResult r = await new RspIqClient(m => Console.Error.WriteLine($"[rsp] {m}"))
                .CaptureAsync(opt, cts.Token);

            r.SampleRate.Should().Be(96000);
            r.Frames.Should().BeGreaterThan(3 * 96000, "a 4 s capture should keep close to 4 s of frames");
            File.Exists(r.WavPath).Should().BeTrue();
            File.Exists(r.SidecarPath).Should().BeTrue();
            r.WavSha256.Should().HaveLength(64);

            // The scorer's own reader must accept it, at the right rate and channel count.
            using var reader = new PcmWavReader(r.WavPath);
            reader.Channels.Should().Be(2, "the capture is interleaved I/Q");
            reader.SampleRate.Should().Be(96000);
            reader.FrameCount.Should().Be(r.Frames);
        }
        finally
        {
            try
            {
                Directory.Delete(dir, recursive: true);
            }
            catch (IOException)
            {
                // best-effort cleanup
            }
        }
    }
}
