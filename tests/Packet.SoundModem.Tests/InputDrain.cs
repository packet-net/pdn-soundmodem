using M0LTE.Radio.Audio;

namespace Packet.SoundModem.Tests;

/// <summary>Reads an input until it has handed over as many samples as a test needs.</summary>
/// <remarks>
/// The reads are the input's own: each one returns what has arrived, or nothing after the input's
/// own wait, and the loop ends on the count, never on a time. A run in which the samples never
/// come is turned into a failure by a hang guard on the whole read, which does not decide
/// anything a slow machine could.
/// </remarks>
internal static class InputDrain
{
    public static float[] ReadAtLeast(IAudioInput input, int count, string because)
    {
        var abandoned = new CancellationTokenSource();
        Task<float[]> reading = Task.Run(() =>
        {
            var got = new List<float>();
            var buffer = new float[4096];
            while (got.Count < count && !abandoned.IsCancellationRequested)
            {
                int n = input.Read(buffer);
                got.AddRange(buffer.AsSpan(0, n).ToArray());
            }

            return got.ToArray();
        });

        if (!reading.Wait(Eventually.HangGuard))
        {
            abandoned.Cancel();
            throw new TimeoutException($"waited {Eventually.HangGuard.TotalSeconds:0} s for {because}, and it never happened");
        }

        return reading.Result;
    }
}
