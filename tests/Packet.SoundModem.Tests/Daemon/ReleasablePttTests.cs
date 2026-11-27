using M0LTE.Radio.Audio;
using Packet.SoundModem.Daemon;

namespace Packet.SoundModem.Tests.Daemon;

/// <summary>
/// Issue #790: the last unkey before a forced exit runs beside the transmitter, so the two must
/// not interleave inside the PTT, and nothing may key the radio after it.
/// </summary>
public class ReleasablePttTests
{
    /// <summary>A PTT that records the order of what reached it, with a keyup that waits for the
    /// test to let it finish.</summary>
    private sealed class OrderedPtt : IPttControl
    {
        private readonly Lock _gate = new();

        public List<string> Events { get; } = [];

        public ManualResetEventSlim KeyStarted { get; } = new();

        public ManualResetEventSlim LetKeyFinish { get; } = new(initialState: true);

        public void Key()
        {
            Add("key started");
            KeyStarted.Set();
            LetKeyFinish.Wait();
            Add("key finished");
        }

        public void Unkey() => Add("unkey");

        /// <summary>What has reached the PTT so far, copied under the lock.</summary>
        public string[] Snapshot()
        {
            lock (_gate)
            {
                return [.. Events];
            }
        }

        private void Add(string what)
        {
            lock (_gate)
            {
                Events.Add(what);
            }
        }
    }

    /// <summary>Only ever turns a hang into a failure: nothing passes or fails on how long it took.</summary>
    private static readonly TimeSpan HangAfter = TimeSpan.FromSeconds(60);

    [Fact]
    public void The_Last_Unkey_Waits_For_A_Keyup_In_Progress_And_Comes_After_It()
    {
        var inner = new OrderedPtt();
        inner.LetKeyFinish.Reset();
        var ptt = new ReleasablePtt(inner);

        // Threads of their own, not the pool: on a busy build machine a pool task can sit unstarted
        // for seconds, which is how this test used to fail (#860).
        var keying = new Thread(ptt.Key) { IsBackground = true };
        keying.Start();
        inner.KeyStarted.Wait(HangAfter).Should().BeTrue("the keyup should have started");

        var releasing = new Thread(ptt.Release) { IsBackground = true };
        releasing.Start();

        // Wait until the release is blocked (it has reached the PTT and is held out by the keyup in
        // progress) or has finished (it was not held out, and the next line says so), by its thread's
        // state and not by a sleep.
        Eventually.Until(
            () => (releasing.ThreadState & ThreadState.WaitSleepJoin) != 0 || !releasing.IsAlive,
            "the last unkey to reach the PTT");
        inner.Snapshot().Should().Equal(["key started"], "the last unkey must not land inside the keyup");

        inner.LetKeyFinish.Set();
        keying.Join(HangAfter).Should().BeTrue();
        releasing.Join(HangAfter).Should().BeTrue();

        inner.Snapshot().Should().Equal(["key started", "key finished", "unkey"]);
    }

    [Fact]
    public void Nothing_Keys_Once_Released()
    {
        var inner = new OrderedPtt();
        var ptt = new ReleasablePtt(inner);

        ptt.Release();
        Action key = ptt.Key;

        key.Should().Throw<InvalidOperationException>().WithMessage(ReleasablePtt.Refusal);
        inner.Events.Should().Equal(["unkey"]);
        ptt.Released.Should().BeTrue();
    }
}
