using AwesomeAssertions;
using Packet.SoundModem.Iq;
using Packet.SoundModem.UberSdr;

namespace Packet.SoundModem.Tests.UberSdr;

/// <summary>
/// The end-to-end check against a real receiver: pre-flight, WebSocket, zstd, the packet framing,
/// the SSB demodulator and the ring buffer, all at once and all against something we do not
/// control.
/// </summary>
/// <remarks>
/// Off by default — it needs the internet and somebody else's radio, and a receiver that is down
/// for maintenance is not a regression in this repo. Run it with
/// <c>UBERSDR_LIVE=&lt;instance&gt;</c> (a device string, or just the host):
/// <code>UBERSDR_LIVE=m9psy-1.instance.ubersdr.org dotnet test</code>
/// What it can assert without owning the band is plumbing, not decodes: that audio arrives at
/// the promised rate, with the right amount of it per second, carrying something other than
/// digital silence. Whether the demodulator is *right* is settled by
/// <see cref="StreamingIqToAudioConverterTests"/>, which does not need a band open.
/// </remarks>
public sealed class UberSdrLiveStreamTests
{
    /// <summary>40 m FT8. Chosen because it is busy at essentially all hours from anywhere in
    /// Europe, so "there was signal" is a fair expectation rather than a propagation gamble.</summary>
    private const int DialHz = 7074000;

    private static string? Instance =>
        Environment.GetEnvironmentVariable("UBERSDR_LIVE") is { Length: > 0 } value ? value : null;

    [Fact]
    public async Task A_live_instance_delivers_audio_at_the_rate_it_promises()
    {
        Assert.SkipUnless(Instance is not null,
            "set UBERSDR_LIVE=<instance> to stream from a real UberSDR receiver");

        string device = Instance!.StartsWith("ubersdr:", StringComparison.OrdinalIgnoreCase)
            ? Instance
            : "ubersdr:" + Instance;
        UberSdrEndpoint endpoint = UberSdrDevice.Parse(device);

        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var tuning = new UberSdrTuning
        {
            FrequencyHz = DialHz,
            Sideband = Sideband.Upper,
            OutputRate = 12000,
            // The guard exists to drop the connect ramp; keeping it here means the level assert
            // below is measuring the stream rather than its first second.
            StartupGuardMs = 1000,
        };

        using UberSdrAudioInput input = await UberSdrAudioInput.OpenAsync(
            endpoint, tuning, log: null, cancellation.Token);

        input.SampleRate.Should().Be(12000);
        input.Connection.Allowed.Should().BeTrue();

        var audio = new List<float>();
        var buffer = new float[4096];
        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        while (elapsed.Elapsed < TimeSpan.FromSeconds(6) && !cancellation.IsCancellationRequested)
        {
            audio.AddRange(buffer.AsSpan(0, input.Read(buffer)));
        }

        // Six seconds of reading, less the one-second startup guard, is five seconds of audio —
        // and the point of the assertion is the rate, so it is bounded both ways. Too little
        // means the stream is stalling; too much would mean the audio rate is not what
        // SampleRate says it is, which would put every modem's timing out.
        double seconds = audio.Count / (double)input.SampleRate;
        seconds.Should().BeInRange(4.0, 6.5,
            "the stream is real time: six seconds of reading, less the one-second startup guard, "
            + "is about five seconds of audio at the rate this claims to deliver");

        double sum = 0;
        foreach (float sample in audio)
        {
            sum += sample * (double)sample;
        }

        double rms = Math.Sqrt(sum / audio.Count);
        TestContext.Current.SendDiagnosticMessage(
            $"{endpoint} {DialHz / 1e6:F6} MHz USB: {seconds:F2} s, RMS {rms:F5} "
            + $"({20 * Math.Log10(rms):F1} dBFS)");
        rms.Should().BeGreaterThan(1e-5,
            "a live HF receiver always has a noise floor; digital silence means the IQ never "
            + "reached the demodulator");
        rms.Should().BeLessThan(1.0, "SSB audio that has clipped is not audio we can decode from");

        input.DroppedSamples.Should().Be(0, "nothing else was competing for the buffer");
    }
}
