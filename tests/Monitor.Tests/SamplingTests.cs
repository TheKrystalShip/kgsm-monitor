using System.Linq;
using TheKrystalShip.KGSM.Monitor.Sampling;

namespace TheKrystalShip.KGSM.Monitor.Tests;

/// <summary>
/// Golden-file tests over captured <c>/proc</c> snapshots. The expected values were
/// computed independently from the same fixtures (see PLAN §11) and pinned here, so
/// any regression in the parse or rate math is caught without touching a live host.
/// </summary>
public class CpuSourceTests
{
    [Fact]
    public void Parse_extracts_aggregate_plus_per_core_counters()
    {
        var (idle, total) = CpuSource.Parse(Fixtures.Read("stat.a.txt"));

        Assert.Equal(17, idle.Length);   // 1 aggregate + 16 cores
        Assert.Equal(17, total.Length);
    }

    [Fact]
    public void ComputeRates_matches_pinned_busy_percentages()
    {
        var (idleA, totalA) = CpuSource.Parse(Fixtures.Read("stat.a.txt"));
        var (idleB, totalB) = CpuSource.Parse(Fixtures.Read("stat.b.txt"));

        var (totalPct, perCore) = CpuSource.ComputeRates(idleA, totalA, idleB, totalB);

        Assert.Equal(8.2, totalPct, 1);
        Assert.Equal(16, perCore.Length);
        Assert.Equal(5.0, perCore[0], 1);    // core0
        Assert.Equal(72.5, perCore[1], 1);   // core1 (loaded during capture)
        Assert.Equal(0.0, perCore[15], 1);   // core15 (idle)
    }

    [Fact]
    public void ComputeRates_returns_zero_on_first_sample_before_priming()
    {
        var (idle, total) = CpuSource.Parse(Fixtures.Read("stat.a.txt"));

        // No previous counters -> shape mismatch -> all zero.
        var (totalPct, perCore) = CpuSource.ComputeRates([], [], idle, total);

        Assert.Equal(0.0, totalPct);
        Assert.All(perCore, p => Assert.Equal(0.0, p));
    }
}

public class MemorySourceTests
{
    [Fact]
    public void Parse_matches_pinned_meminfo_values()
    {
        var mem = MemorySource.Parse(Fixtures.Read("meminfo.txt"));

        Assert.Equal(32779764, mem.TotalKb);
        Assert.Equal(27668344, mem.AvailableKb);
        Assert.Equal(5111420, mem.UsedKb);
        Assert.Equal(15.6, mem.UsedPct, 1);
        Assert.Equal(0, mem.SwapTotalKb);
        Assert.Equal(0, mem.SwapUsedKb);
    }

    [Fact]
    public void Parse_maps_cached_and_buffers_verbatim()
    {
        // Slice-1 diagnostics: Cached/Buffers come straight from the same fixture.
        var mem = MemorySource.Parse(Fixtures.Read("meminfo.txt"));

        Assert.Equal(22911024, mem.CachedKb);   // /proc/meminfo Cached line
        Assert.Equal(1002928, mem.BuffersKb);    // /proc/meminfo Buffers line
    }

    [Fact]
    public void Parse_does_not_fold_SReclaimable_into_Cached()
    {
        // The honesty guard: the fixture has Cached=22911024 and SReclaimable=782004.
        // Some tools report Cached+SReclaimable; we map Cached verbatim, so the reported
        // value must be the bare Cached line, never the sum.
        var mem = MemorySource.Parse(Fixtures.Read("meminfo.txt"));

        Assert.Equal(22911024, mem.CachedKb);
        Assert.NotEqual(22911024 + 782004, mem.CachedKb);
    }
}

public class NetworkSourceTests
{
    [Fact]
    public void ComputeRates_at_one_second_equals_raw_delta()
    {
        var prev = NetworkSource.Parse(Fixtures.Read("netdev.a.txt"));
        var cur = NetworkSource.Parse(Fixtures.Read("netdev.b.txt"));

        var metrics = NetworkSource.ComputeRates(prev, cur, dt: 1.0, denyPrefixes: []);

        var eth = metrics.Ifaces.Single(i => i.Name == "enp4s0");
        Assert.Equal(3820, eth.RxBps);
        Assert.Equal(4854, eth.TxBps);
        Assert.Equal(30, eth.RxPps);
        Assert.Equal(37, eth.TxPps);
    }

    [Fact]
    public void ComputeRates_excludes_loopback()
    {
        var prev = NetworkSource.Parse(Fixtures.Read("netdev.a.txt"));
        var cur = NetworkSource.Parse(Fixtures.Read("netdev.b.txt"));

        var metrics = NetworkSource.ComputeRates(prev, cur, 1.0, []);

        Assert.DoesNotContain(metrics.Ifaces, i => i.Name == "lo");
    }

    [Fact]
    public void ComputeRates_honours_deny_prefixes()
    {
        var prev = NetworkSource.Parse(Fixtures.Read("netdev.a.txt"));
        var cur = NetworkSource.Parse(Fixtures.Read("netdev.b.txt"));

        // Deny the real NIC's prefix and confirm it is dropped.
        var metrics = NetworkSource.ComputeRates(prev, cur, 1.0, ["enp"]);

        Assert.DoesNotContain(metrics.Ifaces, i => i.Name == "enp4s0");
    }

    [Fact]
    public void ComputeRates_scales_by_interval()
    {
        var prev = NetworkSource.Parse(Fixtures.Read("netdev.a.txt"));
        var cur = NetworkSource.Parse(Fixtures.Read("netdev.b.txt"));

        // dt = 2s halves the per-second rate (3820 / 2 = 1910).
        var metrics = NetworkSource.ComputeRates(prev, cur, 2.0, []);

        Assert.Equal(1910, metrics.Ifaces.Single(i => i.Name == "enp4s0").RxBps);
    }
}

public class DiskSourceTests
{
    [Fact]
    public void ParseBlockStat_reads_sectors_read_and_written()
    {
        // /sys/block/<dev>/stat: field[2]=sectors read, field[6]=sectors written.
        const string line = "  100 20 5000 80   200 30 9000 120 0 1500 300";
        var (read, write) = DiskSource.ParseBlockStat(line);

        Assert.Equal(5000, read);
        Assert.Equal(9000, write);
    }

    [Fact]
    public void ParseBlockStat_tolerates_short_lines()
    {
        Assert.Equal((0L, 0L), DiskSource.ParseBlockStat("1 2 3"));
    }

    [Theory]
    [InlineData("ext4", true)]
    [InlineData("vfat", true)]
    [InlineData("xfs", true)]
    [InlineData("tmpfs", false)]      // pseudo-fs always filtered
    [InlineData("overlay", false)]
    [InlineData("efivarfs", false)]
    public void IncludeMount_filters_pseudo_filesystems(string fs, bool expected)
    {
        Assert.Equal(expected, DiskSource.IncludeMount(fs, new HashSet<string>()));
    }

    [Fact]
    public void IncludeMount_honours_extra_deny_set()
    {
        var deny = new HashSet<string> { "nfs" };
        Assert.False(DiskSource.IncludeMount("nfs", deny));
        Assert.True(DiskSource.IncludeMount("ext4", deny));
    }
}

public class SystemSourceTests
{
    [Fact]
    public void ParseLoadAvg_matches_pinned_values()
    {
        var load = SystemSource.ParseLoadAvg(Fixtures.Read("loadavg.txt"));

        Assert.Equal(0.41, load.One, 2);
        Assert.Equal(0.14, load.Five, 2);
        Assert.Equal(0.10, load.Fifteen, 2);
    }

    [Fact]
    public void ParseUptime_truncates_to_whole_seconds()
    {
        // uptime.txt starts "183976.34 ..." -> 183976.
        Assert.Equal(183976, SystemSource.ParseUptime(Fixtures.Read("uptime.txt")));
    }
}
