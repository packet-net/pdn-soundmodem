using System.Reflection;
using Packet.SoundModem.Hdlc;
using Packet.SoundModem.Modems;

namespace Packet.SoundModem.Tests.Modems;

public class AfskColdStartProbe
{
    [Fact]
    public void Burst1_Envelope_State_Noise_Led_Vs_Zero_Led()
    {
        Assert.SkipWhen(Environment.GetEnvironmentVariable("AFSK_DIAG") is null, "diag only");
        using var report = new StreamWriter("/tmp/afskdiag.txt");

        var (raw, wavRate) = Packet.SoundModem.Audio.WavFile.ReadMono(
            "/tmp/afsk-floor/0110-keep040ms.wav");
        int rate = ModemCatalog.DspRateFor("afsk1200");
        var decimator = new M0LTE.Dsp.Decimator(wavRate, wavRate / rate);
        var samples = new float[decimator.MaxOutput(raw.Length)];
        int produced = decimator.Process(raw, samples);

        // locate burst 1 by envelope
        float peak = 0;
        for (int i = 0; i < produced; i++) { peak = Math.Max(peak, Math.Abs(samples[i])); }
        int burstStart = 0;
        while (burstStart < produced && Math.Abs(samples[burstStart]) < 0.15f * peak) { burstStart++; }
        int quiet = 0;
        int burstEnd = burstStart;
        for (int i = burstStart; i < produced && quiet < rate / 12; i++)
        {
            if (Math.Abs(samples[i]) > 0.05f * peak) { quiet = 0; burstEnd = i; } else { quiet++; }
        }
        burstEnd = Math.Min(produced, burstEnd + (rate / 5));
        report.WriteLine(FormattableString.Invariant($"burst1: {burstStart * 1000.0 / rate:F0}..{burstEnd * 1000.0 / rate:F0} ms"));

        // Phase-lottery sweep: shave 0..11 ms off the lead so the burst arrives at a
        // different DPLL phase each run; count decodes per lead type.
        foreach (bool zeroLead in new[] { false, true })
        {
            var results = new List<int>();
            for (int shaveMs = 0; shaveMs < 12; shaveMs++)
            {
                var frames = new List<byte[]>();
                var deframer = new HdlcDeframer(frames.Add);
                var nrzi = new NrziDecoder();
                var demod = new AfskDemodulator(rate, level => deframer.PushBit(nrzi.Decode(level)));

                int leadLen = burstStart - (shaveMs * rate / 1000);
                var lead = new float[leadLen];
                if (!zeroLead) { Array.Copy(samples, lead, leadLen); }
                demod.Process(lead);
                var burst = new float[burstEnd - burstStart];
                Array.Copy(samples, burstStart, burst, 0, burst.Length);
                demod.Process(burst);
                results.Add(frames.Count);
            }

            report.WriteLine($"{(zeroLead ? "zero" : "noise")}-lead sweep: [{string.Join(",", results)}]");
        }
    }
}
