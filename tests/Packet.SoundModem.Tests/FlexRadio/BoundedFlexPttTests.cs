using System.Diagnostics;
using M0LTE.Radio.Audio;
using Packet.SoundModem.FlexRadio;

namespace Packet.SoundModem.Tests.FlexRadio;

/// <summary>
/// Issue #790: M0LTE.Flex keys and unkeys with no timeout, so a radio that stops answering
/// mid-keyup used to hold the transmitter, and the station's shutdown, for ever.
/// </summary>
public sealed class BoundedFlexPttTests
{
    /// <summary>A PTT whose radio never answers.</summary>
    private sealed class UnansweredPtt : IPttControl
    {
        private readonly ManualResetEventSlim _never = new();

        public void Key() => _never.Wait();

        public void Unkey() => _never.Wait();
    }

    private sealed class CountingPtt : IPttControl
    {
        public int Keys;

        public int Unkeys;

        public void Key() => Keys++;

        public void Unkey() => Unkeys++;
    }

    [Fact]
    public void An_Unkey_The_Radio_Never_Answers_Gives_Up_At_Its_Limit_And_Says_The_Radio_Is_Gone()
    {
        var said = new List<string>();
        var ptt = new BoundedFlexPtt(
            new UnansweredPtt(), sessionAlive: () => true, keyLimit: TimeSpan.FromSeconds(30),
            unkeyLimit: TimeSpan.FromMilliseconds(300));
        ptt.RadioUnanswered += said.Add;

        var clock = Stopwatch.StartNew();
        Action unkey = ptt.Unkey;

        unkey.Should().Throw<TimeoutException>();
        clock.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(5));
        said.Should().ContainSingle().Which.Should().Be(
            "flex: the radio did not answer the unkey in 0.3 s; treating it as gone");
    }

    [Fact]
    public async Task A_Keyup_Under_Way_When_The_Session_Ends_Gives_Up_At_Once()
    {
        bool alive = true;
        var said = new List<string>();
        var ptt = new BoundedFlexPtt(new UnansweredPtt(), () => alive, keyLimit: TimeSpan.FromMinutes(5));
        ptt.RadioUnanswered += said.Add;

        var clock = Stopwatch.StartNew();
        var keying = Task.Run(ptt.Key);
        await Task.Delay(200);
        alive = false;

        await Task.WhenAny(keying, Task.Delay(TimeSpan.FromSeconds(5)));
        keying.IsFaulted.Should().BeTrue("the keyup gave up rather than waiting on");
        keying.Exception!.InnerException.Should().BeOfType<TimeoutException>();
        clock.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(5), "the keyup's own limit is minutes; the session ending is what ended it");
        said.Should().ContainSingle().Which.Should().Be("flex: the radio's session ended during the keyup");
    }

    [Fact]
    public void A_Radio_That_Answers_Is_Keyed_And_Unkeyed_As_Before()
    {
        var inner = new CountingPtt();
        var ptt = new BoundedFlexPtt(inner, () => true, keyLimit: TimeSpan.FromSeconds(5));

        ptt.Key();
        ptt.Unkey();

        inner.Keys.Should().Be(1);
        inner.Unkeys.Should().Be(1);
    }
}
