using TheKrystalShip.KGSM.Monitor.Contracts;
using TheKrystalShip.KGSM.Monitor.Sampling;

namespace TheKrystalShip.KGSM.Monitor.Tests;

/// <summary>
/// <see cref="SliceSource"/> against a synthetic <c>kgsm.slice</c> tree. The figure this source exists
/// for is the honest "game servers vs the rest of the host" split, so the assertions are the honesty
/// edges: an absent slice is null (not zeros), the first observation carries no CPU rate, a torn-down
/// slice resets the rate anchor, and an unreadable counter file is a null field.
/// </summary>
public sealed class SliceSourceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "kgsm-slice-test-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { /* never created, or already gone */ }
    }

    private void WriteSlice(long usageUsec, long? memBytes = 1024, long? pids = 3)
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "cpu.stat"),
            $"usage_usec {usageUsec}\nuser_usec {usageUsec / 2}\nsystem_usec {usageUsec / 2}\n");
        if (memBytes is { } m)
            File.WriteAllText(Path.Combine(_dir, "memory.current"), m + "\n");
        if (pids is { } p)
            File.WriteAllText(Path.Combine(_dir, "pids.current"), p + "\n");
    }

    [Fact]
    public void Absent_slice_is_null_not_zeros()
    {
        var source = new SliceSource(_dir);
        Assert.Null(source.Sample());
    }

    [Fact]
    public void First_observation_carries_counters_but_no_cpu_rate()
    {
        WriteSlice(5_000_000, memBytes: 123_456_789, pids: 42);
        var source = new SliceSource(_dir);

        SliceMetrics? m = source.Sample();

        Assert.NotNull(m);
        Assert.Null(m.CpuPctCore);            // one sample is not a rate
        Assert.Equal(123_456_789, m.MemBytes);
        Assert.Equal(42, m.Pids);
    }

    [Fact]
    public void Second_observation_rates_the_delta()
    {
        WriteSlice(5_000_000);
        var source = new SliceSource(_dir);
        source.Sample();

        WriteSlice(6_000_000);
        Thread.Sleep(30);                     // TickCount64 must advance for dt > 0
        SliceMetrics? m = source.Sample();

        Assert.NotNull(m);
        Assert.NotNull(m.CpuPctCore);
        Assert.True(m.CpuPctCore > 0);
    }

    [Fact]
    public void Missing_counter_files_are_null_fields()
    {
        WriteSlice(5_000_000, memBytes: null, pids: null);
        var source = new SliceSource(_dir);

        SliceMetrics? m = source.Sample();

        Assert.NotNull(m);
        Assert.Null(m.MemBytes);
        Assert.Null(m.Pids);
    }

    [Fact]
    public void Torn_down_slice_resets_the_rate_anchor()
    {
        WriteSlice(5_000_000);
        var source = new SliceSource(_dir);
        source.Sample();

        Directory.Delete(_dir, true);
        Assert.Null(source.Sample());         // gone → null, and the anchor is dropped

        // Recreated (watchdog restarted, counters start over): the first frame back must be
        // rate-less, not a rate against the dead slice's counter.
        WriteSlice(1_000);
        SliceMetrics? m = source.Sample();
        Assert.NotNull(m);
        Assert.Null(m.CpuPctCore);
    }
}
