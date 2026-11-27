using System.Buffers.Binary;
using AwesomeAssertions;
using Packet.SoundModem.SdrConnect;

namespace Packet.SoundModem.Tests.SdrConnect;

[CollectionDefinition(nameof(SdrConnectAllocationTests), DisableParallelization = true)]
public sealed class SdrConnectAllocationCollection;

/// <summary>
/// The SDRconnect IQ path in steady state allocates nothing per message. The receive loop runs on
/// pool threads, so the per-thread counter cannot see it; the whole process is measured instead,
/// with nothing else running (this collection is not run in parallel) and the fake's messages built
/// beforehand, less what the sending thread itself allocates: the fake's async send allocates about
/// 72 bytes a message of its own (measured), which is the test's cost, not the receive path's.
/// </summary>
[Collection(nameof(SdrConnectAllocationTests))]
public sealed class SdrConnectAllocationTests
{
    [Fact]
    public Task A_Steady_Iq_Stream_Allocates_Nothing_Per_Message() => MeasureAsync(push: null);

    /// <summary>
    /// SDRconnect may push measurements it keeps changing (signal_power, signal_snr) as often as it
    /// likes; the document does not say how often. Those the station does not use cost nothing.
    /// </summary>
    [Fact]
    public Task Pushes_Of_Properties_The_Station_Does_Not_Use_Allocate_Nothing() =>
        MeasureAsync(push: System.Text.Encoding.UTF8.GetBytes(
            "{\"event_type\":\"property_changed\",\"property\":\"signal_power\",\"device\":\"primary\",\"value\":\"-71.5\"}"));

    private static async Task MeasureAsync(byte[]? push)
    {
        using var host = new FakeSdrConnectHost();
        using var source = new SdrConnectIqSource(FakeSdrConnectHost.Endpoint, new SdrConnectOptions
        {
            CentreFrequencyHz = 7_075_000,
            SampleRate = 2_000_000,
            RetuneSettle = TimeSpan.Zero,
            WatchRate = true,
        }, host.ConnectAsync);
        Task opening = source.OpenAsync(TestContext.Current.CancellationToken);
        FakeSdrConnect fake = await host.NextAsync();
        await opening;
        await fake.Streaming.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        // One message of 4096 pairs, built once and sent over and over.
        var message = new byte[2 + (4 * 4096)];
        BinaryPrimitives.WriteUInt16LittleEndian(message, SdrConnectProtocol.PrimaryIq);
        new Random(3).NextBytes(message.AsSpan(2));

        using var reading = new CancellationTokenSource();
        long read = 0;
        var reader = new Thread(() =>
        {
            var buffer = new float[16384];
            while (!reading.IsCancellationRequested)
            {
                int n = source.Read(buffer);
                if (n == 0)
                {
                    return;
                }

                Interlocked.Add(ref read, n);
            }
        });
        reader.Start();

        // Paced: each message is sent once the reader has had the last, so the fake's sends
        // complete at once and its own async machinery allocates nothing either. A sender that
        // ran ahead would find its sends waiting, and each waiting send allocates on its side.
        // What the sender thread itself allocates (the fake's: a few dozen bytes a message, and a
        // one-off of about 32 KB on the first send after the collection, the counter charging a fresh
        // allocation buffer) is read for every message and taken off the
        // total below. That is deliberately the plain sum, not the smallest of several readings: a
        // message cannot be sent twice. A rise the runtime charges the sender thread without it
        // allocating is then taken off as well, which can only hide a few KB of the 2000 messages'
        // total, far below what a per-message allocation on the receive path adds (48 bytes or more
        // a message is 96 KB), and never fails the test wrongly.
        int sent = 0;
        long readBase = 0;
        Action pace = () =>
        {
            long target = readBase + ((long)sent * 2 * 4096);
            while (Interlocked.Read(ref read) < target)
            {
                Thread.Yield();
            }
        };
        Action sendOne = () =>
        {
            if (push is not null)
            {
                fake.SendRawTextAsync(push).GetAwaiter().GetResult();
            }

            fake.SendRawAsync(message).GetAwaiter().GetResult();
        };

        // Sends this many messages, each once the reader has had the one before; returns what the
        // sender thread allocated doing it.
        long Send(int count)
        {
            readBase = Interlocked.Read(ref read);
            long allocated = 0;
            for (sent = 0; sent < count;)
            {
                allocated += AllocationMeasure.Once(sendOne, pace);
                sent++;
            }

            pace();
            return allocated;
        }

        Send(500); // warm-up: buffers sized, code jitted, pools filled
        GC.Collect();
        GC.WaitForPendingFinalizers();
        long before = GC.GetTotalAllocatedBytes(precise: true);
        const int messages = 2000;
        long sending = Send(messages);
        long after = GC.GetTotalAllocatedBytes(precise: true) - sending;
        reading.Cancel();
        source.Dispose();
        reader.Join(TimeSpan.FromSeconds(5));

        long perMessage = (after - before) / messages;
        TestContext.Current.SendDiagnosticMessage($"sdrconnect: {after - before} bytes over {messages} messages, {perMessage} a message");
        Console.WriteLine($"sdrconnect allocation: {after - before} bytes over {messages} messages, {perMessage} a message");
        source.Overflows.Should().Be(0);
        // Measured: 256 bytes over the 2000 messages, nothing a message. One small allocation a
        // message on the receive path would be 48 bytes or more a message, so this bound fails it.
        perMessage.Should().BeLessThan(16, "a steady stream, rate watch and all, allocates nothing a message on the receive path");
    }
}
