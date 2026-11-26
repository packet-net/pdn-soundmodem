using System.Net;
using AwesomeAssertions;
using Packet.SoundModem.Waterfall;

namespace Packet.SoundModem.Tests.Waterfall;

/// <summary>
/// A listener that trips over a client connecting the moment it opens is started again, not given
/// up on.
/// </summary>
/// <remarks>
/// The managed <see cref="HttpListener"/> on Linux starts accepting inside the constructor of its
/// end point, before the end point has finished setting itself up. A connection that arrives in
/// that gap makes <c>Start()</c> throw <see cref="ArgumentNullException"/> ("Value cannot be
/// null.", from a <c>lock</c> on a field not yet assigned). The daemon took that for a bad
/// waterfall setting and exited 2, which systemd's <c>Restart=on-failure</c> does not restart,
/// so a station restarted from its own page, whose open tabs keep knocking on the port, could
/// stay down (#864, #853). With four threads connecting as fast as they could while a listener
/// opened, about 5 starts in 100 threw; that run is not repeated here because the rarer form of
/// the same race (the accept completing on a thread-pool thread) ends the test process. These
/// make the start fail by hand.
/// </remarks>
public class ListenerStartRaceTests
{
    private const int TestTimeoutMs = 30_000;

    [Fact(Timeout = TestTimeoutMs)]
    public async Task A_Start_That_Throws_Null_Is_Tried_Again_On_A_Fresh_Listener_And_Serves()
    {
        int port = FreePorts.Next();
        int starts = 0;
        HttpListener first = ListenerPrefixes.Listener("127.0.0.1", port);
        HttpListener running = HttpListenerLifetime.Start(
            first,
            () => ListenerPrefixes.Listener("127.0.0.1", port),
            l =>
            {
                if (++starts == 1)
                {
                    throw new ArgumentNullException();
                }

                l.Start();
            });
        try
        {
            starts.Should().Be(2);
            running.Should().NotBeSameAs(first, "a listener whose start threw is closed");

            Task<HttpListenerContext> accept = running.GetContextAsync();
            using var client = new HttpClient();
            Task<string> answer = client.GetStringAsync($"http://127.0.0.1:{port}/");
            HttpListenerContext context = await accept;
            context.Response.OutputStream.Close();
            await answer;
        }
        finally
        {
            HttpListenerLifetime.Shut(running);
        }
    }

    [Fact(Timeout = TestTimeoutMs)]
    public void A_Start_That_Keeps_Throwing_Null_Ends_In_That_Error_After_A_Few_Tries()
    {
        int starts = 0;
        Action act = () => HttpListenerLifetime.Start(
            new HttpListener(),
            () => new HttpListener(),
            _ =>
            {
                starts++;
                throw new ArgumentNullException();
            });

        act.Should().Throw<ArgumentNullException>();
        starts.Should().Be(5);
    }

    [Fact(Timeout = TestTimeoutMs)]
    public void Any_Other_Failure_To_Start_Is_Not_Tried_Again()
    {
        int starts = 0;
        Action act = () => HttpListenerLifetime.Start(
            new HttpListener(),
            () => new HttpListener(),
            _ =>
            {
                starts++;
                throw new HttpListenerException(98, "Address already in use");
            });

        act.Should().Throw<HttpListenerException>();
        starts.Should().Be(1);
    }
}
