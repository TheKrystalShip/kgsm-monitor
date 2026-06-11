using TheKrystalShip.KGSM.Core.Models;
using TheKrystalShip.KGSM.Core.Models.Enums;
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
/// Resolver maps (lifecycle, is-container) to candidate cgroup paths and never
/// throws. Existence is not asserted here — that is the sampler's stat-and-skip job.
/// </summary>
public class ServerCgroupResolverTests
{
    [Fact]
    public void Systemd_instance_resolves_to_system_slice_unit()
    {
        var instance = new Instance
        {
            Name = "factorio",
            LifecycleManager = LifecycleManager.Systemd,
            SystemdServiceFile = "/etc/systemd/system/factorio.service",
        };

        var target = ServerCgroupResolver.Resolve(instance);

        Assert.Equal("systemd", target.Kind);
        Assert.True(target.IsAddressable);
        Assert.Equal("/sys/fs/cgroup/system.slice/factorio.service", Assert.Single(target.Candidates));
    }

    [Fact]
    public void Systemd_without_service_file_falls_back_to_unit_from_name()
    {
        var instance = new Instance { Name = "minecraft", LifecycleManager = LifecycleManager.Systemd };

        var target = ServerCgroupResolver.Resolve(instance);

        Assert.Equal("/sys/fs/cgroup/system.slice/minecraft.service", Assert.Single(target.Candidates));
    }

    [Fact]
    public void Standalone_container_resolves_to_docker_scope_candidates()
    {
        string pidFile = Path.GetTempFileName();
        try
        {
            File.WriteAllText(pidFile, "abc123def456\n");
            var instance = new Instance
            {
                Name = "valheim",
                LifecycleManager = LifecycleManager.Standalone,
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
            LifecycleManager = LifecycleManager.Standalone,
            ComposeFile = "/opt/ghost/docker-compose.yml",
            PidFile = "/nonexistent/.ghost.pid",
        };

        var target = ServerCgroupResolver.Resolve(instance);

        Assert.Equal("container", target.Kind);
        Assert.False(target.IsAddressable);
    }

    [Fact]
    public void Standalone_native_is_not_addressable_deferred_to_slice3()
    {
        var instance = new Instance { Name = "raw", LifecycleManager = LifecycleManager.Standalone };

        var target = ServerCgroupResolver.Resolve(instance);

        Assert.Equal("native", target.Kind);
        Assert.False(target.IsAddressable);
        Assert.Empty(target.Candidates);
    }

    [Theory]
    [InlineData("factorio.ini", "factorio.service")]
    [InlineData("factorio", "factorio.service")]
    public void UnitFromName_strips_ini_suffix(string name, string expected)
    {
        Assert.Equal(expected, ServerCgroupResolver.UnitFromName(name));
    }
}
