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

        private void Add(string what)
        {
            lock (_gate)
            {
                Events.Add(what);
            }
        }
    }

    [Fact]
    public async Task The_Last_Unkey_Waits_For_A_Keyup_In_Progress_And_Comes_After_It()
    {
        var inner = new OrderedPtt();
        inner.LetKeyFinish.Reset();
        var ptt = new ReleasablePtt(inner);

        Task keying = Task.Run(ptt.Key);
        inner.KeyStarted.Wait(TimeSpan.FromSeconds(5)).Should().BeTrue();
        Task releasing = Task.Run(ptt.Release);
        await Task.Delay(200);

        inner.Events.Should().Equal(["key started"], "the last unkey must not land inside the keyup");

        inner.LetKeyFinish.Set();
        await Task.WhenAll(keying, releasing).WaitAsync(TimeSpan.FromSeconds(5));

        inner.Events.Should().Equal(["key started", "key finished", "unkey"]);
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
