using TheKrystalShip.KGSM.Core.Models;
using TheKrystalShip.KGSM.Monitor.Sampling;

namespace TheKrystalShip.KGSM.Monitor.Tests;

/// <summary>
/// Golden-file + deterministic tests for the per-server cgroup path (Slice 2).
/// The <c>cgroup.*.txt</c> fixtures are real captures from a live systemd service
/// cgroup (and the host io controller); rate math is checked against hand-computed
/// deltas so it is host-independent.
/// </summary>
public class CgroupSamplerTests
{
    [Fact]
    public void ParseCpuUsageUsec_reads_usage_from_real_cpu_stat()
    {
        long usage = CgroupSampler.ParseCpuUsageUsec(Fixtures.Read("cgroup.cpu.stat.txt"));
        Assert.Equal(1406967658L, usage);
    }

    [Fact]
    public void ParseCpuUsageUsec_returns_negative_one_when_field_absent()
    {
        Assert.Equal(-1L, CgroupSampler.ParseCpuUsageUsec("user_usec 5\nsystem_usec 3\n"));
    }

    [Fact]
    public void ParseCounter_reads_memory_and_pids_fixtures()
    {
        Assert.Equal(547254272L, CgroupSampler.ParseCounter(Fixtures.Read("cgroup.memory.current.txt")));
        Assert.Equal(45L, CgroupSampler.ParseCounter(Fixtures.Read("cgroup.pids.current.txt")));
    }

    [Fact]
    public void ParseCounter_is_zero_for_non_numeric()
    {
        // memory.max can read "max"; guard against it being treated as a number.
        Assert.Equal(0L, CgroupSampler.ParseCounter("max\n"));
        Assert.Equal(0L, CgroupSampler.ParseCounter(""));
    }

    [Fact]
    public void ParseIoStat_sums_real_single_device_fixture()
    {
        var (read, write) = CgroupSampler.ParseIoStat(Fixtures.Read("cgroup.io.stat.txt"));
        Assert.Equal(20074357760L, read);
        Assert.Equal(31202464768L, write);
    }

    [Fact]
    public void ParseIoStat_sums_across_multiple_devices()
    {
        // io accounting is per block device; the snapshot reports the whole-server total.
        const string ioStat =
            "259:0 rbytes=1000 wbytes=2000 rios=1 wios=2 dbytes=0 dios=0\n" +
            "8:0 rbytes=500 wbytes=750 rios=3 wios=4 dbytes=0 dios=0\n";
        var (read, write) = CgroupSampler.ParseIoStat(ioStat);
        Assert.Equal(1500L, read);
        Assert.Equal(2750L, write);
    }

    [Theory]
    [InlineData(1_000_000_000L, 1_000_500_000L, 1.0, 50.0)]   // 0.5 core-seconds over 1s = 50% of one core
    [InlineData(1_000_000_000L, 1_002_000_000L, 1.0, 200.0)]  // 2 core-seconds over 1s = 200% (multi-core, htop convention)
    [InlineData(1_000_000_000L, 1_000_250_000L, 0.5, 50.0)]   // dt-aware: 0.25 core-s over 0.5s = 50%
    public void ComputeCpuPctCore_matches_hand_computed(long prev, long cur, double dt, double expected)
    {
        Assert.Equal(expected, CgroupSampler.ComputeCpuPctCore(prev, cur, dt), 1);
    }

    [Fact]
    public void ComputeCpuPctCore_clamps_counter_reset_to_zero()
    {
        // A restarted server's cgroup resets usage_usec; a negative delta must not surface.
        Assert.Equal(0.0, CgroupSampler.ComputeCpuPctCore(5_000_000L, 1_000L, 1.0), 1);
    }
}

/// <summary>
/// Resolver maps is-container to candidate cgroup paths and never throws. Existence is
/// not asserted here — that is the sampler's stat-and-skip job.
/// </summary>
public class ServerCgroupResolverTests
{
    [Fact]
    public void Container_resolves_to_docker_scope_candidates()
    {
        string pidFile = Path.GetTempFileName();
        try
        {
            File.WriteAllText(pidFile, "abc123def456\n");
            var instance = new Instance
            {
                Name = "valheim",
                ComposeFile = "/opt/valheim/docker-compose.yml",
                PidFile = pidFile,
            };

            var target = ServerCgroupResolver.Resolve(instance);

            Assert.Equal("container", target.Kind);
            Assert.Equal(2, target.Candidates.Count);
            Assert.Equal("/sys/fs/cgroup/system.slice/docker-abc123def456.scope", target.Candidates[0]);
            Assert.Equal("/sys/fs/cgroup/docker/abc123def456", target.Candidates[1]);
        }
        finally
        {
            File.Delete(pidFile);
        }
    }

    [Fact]
    public void Container_without_readable_pid_file_is_not_addressable()
    {
        var instance = new Instance
        {
            Name = "ghost",
            ComposeFile = "/opt/ghost/docker-compose.yml",
            PidFile = "/nonexistent/.ghost.pid",
        };

        var target = ServerCgroupResolver.Resolve(instance);

        Assert.Equal("container", target.Kind);
        Assert.False(target.IsAddressable);
    }

    [Fact]
    public void Native_without_cgroup_path_is_not_addressable_deferred_to_slice3()
    {
        var instance = new Instance { Name = "raw" }; // native: no compose_file, no cgroup_path

        var target = ServerCgroupResolver.Resolve(instance);

        Assert.Equal("native", target.Kind);
        Assert.False(target.IsAddressable);
        Assert.Empty(target.Candidates);
    }

    [Fact]
    public void Native_with_cgroup_path_resolves_to_that_single_candidate()
    {
        // Inc 4: KGSM derives kgsm.slice/<inst> and surfaces it as Instance.CgroupPath; the
        // resolver hands it straight to CgroupSampler as the sole candidate. Kind stays "native".
        var instance = new Instance { Name = "factorio-01", CgroupPath = "/sys/fs/cgroup/kgsm.slice/factorio-01" };

        var target = ServerCgroupResolver.Resolve(instance);

        Assert.Equal("native", target.Kind);
        Assert.True(target.IsAddressable);
        Assert.Equal("/sys/fs/cgroup/kgsm.slice/factorio-01", Assert.Single(target.Candidates));
    }
}

/// <summary>
/// The Inc 4 partition: a native server with a <em>live</em> cgroup is sampled by
/// <see cref="CgroupSampler"/> and ceded by <see cref="ProcTreeSampler"/>; a native with no
/// live cgroup is the proc-tree's job. Both samplers decide on the same arbiter
/// (<see cref="ServerCgroupResolver.FirstExisting"/>), so a server lands in exactly one — the
/// highest-risk property (no double-count) is pinned here with a real on-disk cgroup dir
/// (CgroupPath points at it) and a synthetic <c>/proc</c> the proc-tree reads.
/// </summary>
public class NativeCgroupPartitionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"kgsm-part-{Guid.NewGuid():N}");

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public void Native_with_a_live_cgroup_is_sampled_by_cgroup_and_ceded_by_proctree()
    {
        // A real cgroup dir with the counters CgroupSampler reads (cpu.stat is the rate anchor).
        string cgDir = Path.Combine(_root, "cg", "factorio-01");
        Directory.CreateDirectory(cgDir);
        File.WriteAllText(Path.Combine(cgDir, "cpu.stat"), "usage_usec 1000000\n");
        File.WriteAllText(Path.Combine(cgDir, "memory.current"), "1048576\n");
        File.WriteAllText(Path.Combine(cgDir, "pids.current"), "7\n");

        // The same instance also has a running root PID in the synthetic /proc, so it WOULD be
        // visible to the proc-tree too — the partition is the only thing keeping it from being
        // counted twice.
        string pidFile = Path.Combine(_root, "factorio-01.pid");
        File.WriteAllText(pidFile, "500\n");
        WriteProc(500, ppid: 1, utime: 10, stime: 5, start: 111, rssPages: 10);

        var instances = new Dictionary<string, Instance>
        {
            ["factorio-01"] = new() { Name = "factorio-01", CgroupPath = cgDir, PidFile = pidFile },
        };

        var cgroupOut = new CgroupSampler().Sample(instances);
        var procOut = new ProcTreeSampler(_root).Sample(instances);

        // Cgroup sampler owns it (Kind native, from the live cgroup); proc-tree cedes it.
        var s = Assert.Single(cgroupOut);
        Assert.Equal("factorio-01", s.Id);
        Assert.Equal("native", s.Kind);
        Assert.Empty(procOut);
    }

    [Fact]
    public void Native_with_no_live_cgroup_is_ceded_by_cgroup_and_sampled_by_proctree()
    {
        // CgroupPath points at a directory that does not exist (cgroups disabled, or the
        // instance not yet placed in its cgroup) -> CgroupSampler skips, proc-tree covers it.
        Directory.CreateDirectory(_root);
        string missingCg = Path.Combine(_root, "cg", "does-not-exist");
        string pidFile = Path.Combine(_root, "survival.pid");
        File.WriteAllText(pidFile, "600\n");
        WriteProc(600, ppid: 1, utime: 3, stime: 1, start: 222, rssPages: 5);

        var instances = new Dictionary<string, Instance>
        {
            ["survival"] = new() { Name = "survival", CgroupPath = missingCg, PidFile = pidFile },
        };

        var cgroupOut = new CgroupSampler().Sample(instances);
        var procOut = new ProcTreeSampler(_root).Sample(instances);

        Assert.Empty(cgroupOut);
        var s = Assert.Single(procOut);
        Assert.Equal("survival", s.Id);
        Assert.Equal("native", s.Kind);
    }

    private void WriteProc(int pid, int ppid, long utime, long stime, long start, long rssPages)
    {
        string dir = Path.Combine(_root, pid.ToString());
        Directory.CreateDirectory(dir);
        string stat =
            $"{pid} (game) S {ppid} {pid} {pid} 0 -1 4194304 100 0 0 0 " +
            $"{utime} {stime} 0 0 20 0 1 0 {start} 123456789 {rssPages}";
        File.WriteAllText(Path.Combine(dir, "stat"), stat);
        File.WriteAllText(Path.Combine(dir, "statm"), $"4096 {rssPages} 64 1 0 512 0\n");
        File.WriteAllText(Path.Combine(dir, "io"),
            "rchar: 1\nwchar: 1\nsyscr: 1\nsyscw: 1\nread_bytes: 0\nwrite_bytes: 0\ncancelled_write_bytes: 0\n");
    }
}
