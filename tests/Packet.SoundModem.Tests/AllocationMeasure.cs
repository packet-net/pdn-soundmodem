namespace Packet.SoundModem.Tests;

/// <summary>
/// Measures how many bytes a piece of work allocates on the calling thread, soundly enough to assert
/// "allocates nothing". A single before-and-after reading of <see cref="GC.GetAllocatedBytesForCurrentThread"/>
/// is not sound: when other threads are busy the runtime can take back a thread's part-used allocation
/// buffer, and the thread's counter then rises by the unused part (never more than 8 KB) though it
/// allocated nothing. Work that really allocates does so on every attempt; a phantom rise comes from
/// elsewhere and is rare. So the work is run up to <see cref="Attempts"/> times and the smallest reading
/// returned: it is zero exactly when the work can run without allocating.
/// </summary>
/// <remarks>
/// <para>The work must be the same each time it runs, so a reading from any one run stands for all of
/// them, and it must not depend on running only once. Whatever the work needs put back between runs goes
/// in <c>setUp</c>, which is not measured.</para>
/// <para>A test warms up every call it then measures before measuring: the first use of a path may
/// allocate, and this helper reports that as it finds it. Build the delegates before the call (invoking
/// one does not allocate), and keep anything a lambda cannot capture, such as a span or a stack buffer,
/// in an array allocated beforehand.</para>
/// </remarks>
internal static class AllocationMeasure
{
    /// <summary>The most times the work is run to get one clean reading.</summary>
    public const int Attempts = 5;

    /// <summary>The smallest number of bytes the work allocated over at most <see cref="Attempts"/> runs;
    /// stops at the first run that allocated nothing.</summary>
    /// <param name="work">The measured work.</param>
    /// <param name="setUp">Run before every attempt and not measured; null for none.</param>
    public static long Smallest(Action work, Action? setUp = null)
    {
        long smallest = long.MaxValue;
        for (int attempt = 0; attempt < Attempts && smallest != 0; attempt++)
        {
            long allocated = Once(work, setUp);
            if (allocated < smallest)
            {
                smallest = allocated;
            }
        }

        return smallest;
    }

    /// <summary>One raw reading, for a test that asserts a first use does allocate (a phantom rise can only
    /// add to that).</summary>
    public static long Once(Action work, Action? setUp = null)
    {
        setUp?.Invoke();
        long before = GC.GetAllocatedBytesForCurrentThread();
        work();
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }
}
