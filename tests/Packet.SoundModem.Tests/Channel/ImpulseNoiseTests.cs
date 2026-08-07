using Packet.SoundModem.Audio;
using Packet.SoundModem.Tests.Channel;

namespace Packet.SoundModem.Tests.Channel;

/// <summary>
/// The impulse-noise injector (rx-roadmap workstream 6 item 2). The statistical
/// calibration is validated through the closed loop - the env-gated synthesis fact below
/// writes injector audio that the capture campaign's own impulse-stats instrument
/// re-analyses (docs/bench/impulse-model-validation-2026-08-07.txt records the loop's
/// result); the blocking tests here pin determinism and the mechanical contracts.
/// </summary>
public class ImpulseNoiseTests
{
    private const int Rate = 12000;

    private static float[] Carrier(int seconds)
    {
        // A tone at 850 Hz: inside the passband, outside both of the measuring
        // instrument's detection bands (2500-2950 and 300-700), so the instrument's
        // background estimate sees pure AWGN.
        var audio = new float[Rate * seconds];
        for (int i = 0; i < audio.Length; i++)
        {
            audio[i] = 0.1f * (float)Math.Sin(2 * Math.PI * 850 * i / Rate);
        }

        return audio;
    }

    [Fact]
    public void A_Zero_Rate_Profile_Is_Byte_Identical_To_No_Profile()
    {
        float[] burst = Carrier(2);
        float[] without = new WattersonChannel(Rate, seed: 7).Apply(burst, snrDb: 10);
        float[] with = new WattersonChannel(Rate, seed: 7).Apply(
            burst, snrDb: 10, impulses: new ImpulseNoiseProfile(0));

        with.Should().Equal(without,
            "a zero rate must draw nothing from the random stream");
    }

    [Fact]
    public void The_Same_Seed_Reproduces_The_Same_Impulses()
    {
        float[] burst = Carrier(5);
        float[] first = new WattersonChannel(Rate, seed: 11).Apply(
            burst, snrDb: 10, impulses: ImpulseNoiseProfile.Evening);
        float[] second = new WattersonChannel(Rate, seed: 11).Apply(
            burst, snrDb: 10, impulses: ImpulseNoiseProfile.Evening);

        second.Should().Equal(first);
    }

    [Fact]
    public void Impulses_Add_Energy_At_Roughly_The_Requested_Rate()
    {
        float[] burst = Carrier(60);
        var channel = new WattersonChannel(Rate, seed: 13);
        float[] clean = new WattersonChannel(Rate, seed: 13).Apply(burst, snrDb: 10);
        float[] noisy = channel.Apply(burst, snrDb: 10, impulses: new ImpulseNoiseProfile(120));

        // Count 20 ms windows where the impulse run carries clearly more energy than the
        // clean run - a coarse arrival check (crashes can merge or hide in the tone, so
        // the bound is generous: at least half the nominal 120 arrivals must be visible).
        int hop = Rate / 50;
        int visible = 0;
        for (int start = 0; start + hop <= clean.Length; start += hop)
        {
            double cleanEnergy = 0;
            double noisyEnergy = 0;
            for (int i = start; i < start + hop; i++)
            {
                cleanEnergy += (double)clean[i] * clean[i];
                noisyEnergy += (double)noisy[i] * noisy[i];
            }

            if (noisyEnergy > 4 * cleanEnergy)
            {
                visible++;
            }
        }

        visible.Should().BeGreaterThan(60, "a minute at 120/min must show most arrivals");
    }

    [Fact]
    public void A_Noiseless_Run_Refuses_Impulses()
    {
        Action act = () => new WattersonChannel(Rate, seed: 3).Apply(
            Carrier(1), snrDb: double.PositiveInfinity, impulses: ImpulseNoiseProfile.Evening);

        act.Should().Throw<ArgumentException>(
            "impulse amplitudes stand on the AWGN floor and a noiseless run has none");
    }

    [Fact]
    public void Synthesis_For_The_Validation_Loop()
    {
        // Not a test: the calibration-loop generator. Set IMPULSE_SYNTH_DIR to write
        // 30 minutes of injector output (tone + AWGN + SunrisePeak-rate impulses) as
        // raw-<UTC>.wav chunks for the impulse-stats instrument to re-analyse.
        string? dir = Environment.GetEnvironmentVariable("IMPULSE_SYNTH_DIR");
        Assert.SkipWhen(dir is null, "set IMPULSE_SYNTH_DIR for the calibration synthesis");

        Directory.CreateDirectory(dir!);
        for (int chunk = 0; chunk < 3; chunk++)
        {
            var channel = new WattersonChannel(Rate, seed: 100 + chunk);
            float[] audio = channel.Apply(
                Carrier(600), snrDb: 10, impulses: ImpulseNoiseProfile.SunrisePeak);
            WavFile.WriteMono(
                Path.Combine(dir!, $"raw-20200101T{chunk:00}0000Z.wav"), audio, Rate);
        }
    }
}
