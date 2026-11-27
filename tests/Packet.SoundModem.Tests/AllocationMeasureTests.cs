using AwesomeAssertions;

namespace Packet.SoundModem.Tests;

public class AllocationMeasureTests
{
    private static object? _sink;

    [Fact]
    public void Work_That_Allocates_Every_Time_Reads_Above_Zero()
    {
        Action work = () => _sink = new byte[64];

        AllocationMeasure.Smallest(work).Should().BeGreaterThan(0);
        AllocationMeasure.Once(work).Should().BeGreaterThan(0);
    }

    [Fact]
    public void Work_That_Allocates_Only_The_First_Time_Reads_Zero()
    {
        bool built = false;
        Action work = () =>
        {
            if (!built)
            {
                built = true;
                _sink = new byte[64];
            }
        };

        AllocationMeasure.Smallest(work).Should().Be(0);
    }

    [Fact]
    public void A_Reading_Stops_At_The_First_Clean_Run_And_Never_Exceeds_The_Attempts()
    {
        int runs = 0;
        Action cleanSecondTime = () =>
        {
            if (runs++ == 0)
            {
                _sink = new byte[64];
            }
        };
        AllocationMeasure.Smallest(cleanSecondTime).Should().Be(0);
        runs.Should().Be(2, "the second run was clean, so there was no third");

        runs = 0;
        Action always = () =>
        {
            runs++;
            _sink = new byte[64];
        };
        AllocationMeasure.Smallest(always).Should().BeGreaterThan(0);
        runs.Should().Be(AllocationMeasure.Attempts);
    }

    [Fact]
    public void The_Set_Up_Runs_Before_Every_Attempt_And_Is_Not_Measured()
    {
        int setUps = 0;
        int runs = 0;
        Action setUp = () =>
        {
            setUps++;
            _sink = new byte[4096];
        };
        Action work = () => runs++;

        AllocationMeasure.Smallest(work, setUp).Should().Be(0, "the set-up allocation is outside the reading");
        setUps.Should().Be(runs);
    }

    [Fact]
    public void Allocation_Free_Work_Reads_Zero_Every_Round_While_Other_Threads_Allocate_Hard()
    {
        const int rounds = 3000;
        bool stop = false;
        var noise = new Thread[4];
        for (int t = 0; t < noise.Length; t++)
        {
            int id = t;
            noise[t] = new Thread(() =>
            {
                // Small, large and some long-lived allocations; the list is trimmed so memory stays bounded.
                var keep = new List<object>();
                var random = new Random(id);
                long n = 0;
                while (!Volatile.Read(ref stop))
                {
                    object o = n % 500 == 0 ? new float[100_000 + random.Next(100_000)] : new byte[200 + random.Next(4000)];
                    if (random.Next(4) == 0)
                    {
                        keep.Add(o);
                    }

                    if (keep.Count > 400)
                    {
                        keep.RemoveRange(0, 200);
                    }

                    n++;
                }
            });
            noise[t].Start();
        }

        try
        {
            double[] data = new double[4096];
            double running = 0;
            Action work = () =>
            {
                for (int i = 0; i < data.Length; i++)
                {
                    data[i] = Math.Sin(i + running);
                    running += data[i];
                }
            };

            for (int round = 0; round < rounds; round++)
            {
                AllocationMeasure.Smallest(work).Should().Be(0, $"round {round}");
            }
        }
        finally
        {
            Volatile.Write(ref stop, true);
            foreach (Thread t in noise)
            {
                t.Join();
            }
        }
    }
}
