using TheKrystalShip.KGSM.Core.Models;
using TheKrystalShip.KGSM.Core.Models.Enums;
using TheKrystalShip.KGSM.Monitor.Sampling;

namespace TheKrystalShip.KGSM.Monitor.Tests;

/// <summary>
/// Slice 3 — the standalone-native process-tree fallback. Pure helpers (stat/statm/io parse,
/// tree inversion + walk, CPU rate math) are tested deterministically against hand-built
/// inputs; <see cref="ProcTreeSampler.Sample"/> is exercised end-to-end against a synthetic
/// <c>/proc</c> directory for the structural outcomes that don't depend on wall-clock <c>dt</c>
/// (tree membership, first-tick zero rates, the PID-recycle guard, and the no-native cost gate).
/// </summary>
public class ProcTreeSamplerHelperTests
{
    [Fact]
    public void ParseProcStat_handles_comm_with_spaces_and_parentheses()
    {
        // Field 2 (comm) contains both a space and nested parens — the naive whitespace split
        // would mis-align every later field. pid=1234, ppid=1, utime=50, stime=25, starttime=9876543.
        const string stat =
            "1234 (My Game (x64)) S 1 1234 1234 0 -1 4194304 1000 0 0 0 50 25 0 0 20 0 4 0 9876543 123456789 512";

        var info = ProcTreeSampler.ParseProcStat(stat);

        Assert.NotNull(info);
        Assert.Equal(1234, info!.Value.Pid);
        Assert.Equal(1, info.Value.Ppid);
        Assert.Equal(75, info.Value.CpuTicks); // utime 50 + stime 25
        Assert.Equal(9876543, info.Value.StartTime);
    }

    [Fact]
    public void ParseProcStat_returns_null_for_malformed_lines()
    {
        Assert.Null(ProcTreeSampler.ParseProcStat(""));
        Assert.Null(ProcTreeSampler.ParseProcStat("1234 no-parens here"));
        Assert.Null(ProcTreeSampler.ParseProcStat("1234 (short) S 1")); // too few fields
    }

    [Fact]
    public void ParseStatmRssPages_reads_resident_field()
    {
        // statm: size resident shared text lib data dt — resident (field 2) is what RSS sums.
        Assert.Equal(512, ProcTreeSampler.ParseStatmRssPages("2048 512 128 1 0 256 0\n"));
        Assert.Equal(0, ProcTreeSampler.ParseStatmRssPages(""));
    }

    [Fact]
    public void ParseProcIo_reads_storage_layer_byte_counters()
    {
        const string io =
            "rchar: 999\nwchar: 888\nsyscr: 10\nsyscw: 20\nread_bytes: 40960\nwrite_bytes: 8192\ncancelled_write_bytes: 0\n";

        var (read, write) = ProcTreeSampler.ParseProcIo(io);

        Assert.Equal(40960, read);  // read_bytes, not rchar
        Assert.Equal(8192, write);  // write_bytes, not wchar
    }

    [Fact]
    public void BuildChildren_and_CollectTree_gather_the_whole_subtree_only()
    {
        // 100 -> 101 -> 102, plus a sibling 103 under 100, plus an unrelated 200 under init.
        var procs = new Dictionary<int, ProcTreeSampler.ProcInfo>
        {
            [100] = new(100, 1, 0, 0),
            [101] = new(101, 100, 0, 0),
            [102] = new(102, 101, 0, 0),
            [103] = new(103, 100, 0, 0),
            [200] = new(200, 1, 0, 0),
        };

        var tree = ProcTreeSampler.CollectTree(100, ProcTreeSampler.BuildChildren(procs));

        Assert.Equal(new HashSet<int> { 100, 101, 102, 103 }, tree.ToHashSet());
        Assert.DoesNotContain(200, tree); // unrelated process must not leak in
    }

    [Fact]
    public void CollectTree_terminates_on_a_ppid_cycle()
    {
        // A post-recycle race could briefly produce a cycle (100<->101); the walk must still end.
        var procs = new Dictionary<int, ProcTreeSampler.ProcInfo>
        {
            [100] = new(100, 101, 0, 0),
            [101] = new(101, 100, 0, 0),
        };

        var tree = ProcTreeSampler.CollectTree(100, ProcTreeSampler.BuildChildren(procs));

        Assert.Equal(new HashSet<int> { 100, 101 }, tree.ToHashSet());
    }

    [Theory]
    [InlineData(1000, 1100, 100, 1.0, 100.0)]  // 100 ticks @ 100Hz = 1 core-second over 1s = 100%
    [InlineData(1000, 1050, 100, 1.0, 50.0)]   // half a core
    [InlineData(1000, 1200, 100, 1.0, 200.0)]  // two cores (htop convention)
    [InlineData(1000, 1050, 100, 0.5, 100.0)]  // dt-aware: 0.5 core-s over 0.5s = 100%
    public void ComputeCpuPctCore_matches_hand_computed(long prev, long cur, long hz, double dt, double expected)
    {
        Assert.Equal(expected, ProcTreeSampler.ComputeCpuPctCore(prev, cur, hz, dt), 1);
    }

    [Fact]
    public void ComputeCpuPctCore_clamps_a_child_exit_negative_delta_to_zero()
    {
        // A tree child exiting drops its ticks from the current sum -> negative delta -> 0,
        // never a phantom reduction.
        Assert.Equal(0.0, ProcTreeSampler.ComputeCpuPctCore(5000, 1000, 100, 1.0), 1);
    }
}

/// <summary>
/// End-to-end coverage of <see cref="ProcTreeSampler.Sample"/> against a synthetic
/// <c>/proc</c> on disk. Only dt-independent outcomes are asserted (first tick always yields
/// 0 rates; membership/guard logic is deterministic), so these never flake on timing.
/// </summary>
public class ProcTreeSamplerSampleTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"kgsm-proc-{Guid.NewGuid():N}");

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public void First_tick_sums_the_tree_and_reports_zero_rates()
    {
        // Tree: 500 (root) -> 501 -> 502, plus unrelated 900. RSS pages 10+20+30 (+999 for 900).
        WriteProc(500, ppid: 1, utime: 30, stime: 10, start: 111, rssPages: 10, readB: 4096, writeB: 1024);
        WriteProc(501, ppid: 500, utime: 5, stime: 5, start: 222, rssPages: 20, readB: 2048, writeB: 512);
        WriteProc(502, ppid: 501, utime: 1, stime: 0, start: 333, rssPages: 30, readB: 0, writeB: 0);
        WriteProc(900, ppid: 1, utime: 99, stime: 99, start: 444, rssPages: 999, readB: 9, writeB: 9);

        var sampler = new ProcTreeSampler(_root);
        var instances = OneNative("survival", rootPid: 500);

        var servers = sampler.Sample(instances);

        var s = Assert.Single(servers);
        Assert.Equal("survival", s.Id);
        Assert.Equal("native", s.Kind);
        Assert.Equal(3, s.Pids);                                       // 500,501,502 — NOT 900
        Assert.Equal(60L * Environment.SystemPageSize, s.MemBytes);    // (10+20+30) pages summed
        Assert.Equal(0.0, s.CpuPctCore);                               // first tick: no prior sample
        Assert.Equal(0L, s.IoReadBps);                                 // measured-but-idle, not null
        Assert.Equal(0L, s.IoWriteBps);
    }

    [Fact]
    public void A_server_whose_pid_is_not_running_is_absent()
    {
        WriteProc(900, ppid: 1, utime: 1, stime: 1, start: 1, rssPages: 1, readB: 0, writeB: 0);

        var sampler = new ProcTreeSampler(_root);
        var instances = OneNative("ghost", rootPid: 12345); // pid not present in /proc

        Assert.Empty(sampler.Sample(instances));
    }

    [Fact]
    public void Pid_recycle_across_ticks_drops_the_server_for_one_tick_then_re_primes()
    {
        // Scope: the *continuous-observation* guard. A change in the root PID's starttime between
        // ticks means the pid now names a different process; we suppress the server for that tick
        // (so no bogus cross-process CPU rate is emitted) and resume tracking whatever the .pid
        // names once identity is stable. This does NOT claim to tell a legitimate same-pid restart
        // (KGSM rewrote .pid) apart from a foreign reuse — that disambiguation is KGSM's job; the
        // cold-adopt boundary is documented in First_observation_trusts_the_pid_file_identity + PLAN §12.
        WriteProc(500, ppid: 1, utime: 10, stime: 0, start: 111, rssPages: 10, readB: 0, writeB: 0);

        var sampler = new ProcTreeSampler(_root);
        var instances = OneNative("survival", rootPid: 500);

        // Tick 1: primes, pinning starttime 111.
        Assert.Single(sampler.Sample(instances));

        // The pid is now held by a different process (starttime changed).
        WriteProc(500, ppid: 1, utime: 7, stime: 0, start: 999, rssPages: 10, readB: 0, writeB: 0);

        // Tick 2: change detected -> absent (no rate computed across two different processes).
        Assert.Empty(sampler.Sample(instances));

        // Tick 3: starttime now stable at 999 -> tracking resumes per the (authoritative) .pid.
        Assert.Single(sampler.Sample(instances));
    }

    [Fact]
    public void First_observation_trusts_the_pid_file_identity()
    {
        // Characterization test for the known boundary (PLAN §12): the sampler trusts the .pid as
        // server identity, exactly as `kgsm instances status` does. KGSM removes the .pid on a
        // clean stop, so the only way to reach here is a crash that bypassed cleanup AND a pid the
        // kernel reused. With no prior starttime to compare on the FIRST observation, the reusing
        // process's (real, measured) metrics are attributed to the server. This is mis-attribution,
        // not fabrication — and it matches what KGSM itself would report for the same stale pid.
        // Pinned here so the behavior is intentional and visible, not an accident.
        WriteProc(700, ppid: 1, utime: 42, stime: 0, start: 555, rssPages: 25, readB: 0, writeB: 0);

        var sampler = new ProcTreeSampler(_root);
        var instances = OneNative("crashed-but-pid-reused", rootPid: 700);

        var s = Assert.Single(sampler.Sample(instances));
        Assert.Equal("native", s.Kind);
        Assert.Equal(25L * Environment.SystemPageSize, s.MemBytes); // the foreign process's RSS, trusted
    }

    [Fact]
    public void A_watch_list_with_no_native_servers_yields_nothing()
    {
        // Point at a non-existent /proc to prove the cost gate skips the scan entirely: a
        // systemd-only watch-list must produce no servers and must not throw.
        var sampler = new ProcTreeSampler("/nonexistent-proc-root");
        var instances = new Dictionary<string, Instance>
        {
            ["sysd"] = new()
            {
                Name = "sysd",
                LifecycleManager = LifecycleManager.Systemd,
                SystemdServiceFile = "/etc/systemd/system/sysd.service",
            },
        };

        Assert.Empty(sampler.Sample(instances));
    }

    // --- helpers ---

    private Dictionary<string, Instance> OneNative(string id, int rootPid)
    {
        string pidFile = Path.Combine(_root, $"{id}.pid");
        File.WriteAllText(pidFile, rootPid + "\n");
        return new Dictionary<string, Instance>
        {
            [id] = new()
            {
                Name = id,
                LifecycleManager = LifecycleManager.Standalone, // native: standalone + no compose_file
                PidFile = pidFile,
            },
        };
    }

    private void WriteProc(int pid, int ppid, long utime, long stime, long start, long rssPages, long readB, long writeB)
    {
        string dir = Path.Combine(_root, pid.ToString());
        Directory.CreateDirectory(dir);

        // /proc/<pid>/stat: fields 14/15 = utime/stime, 22 = starttime. comm deliberately carries
        // a space + parens to exercise the last-')' parse through the real read path.
        string stat =
            $"{pid} (proc ({pid})) S {ppid} {pid} {pid} 0 -1 4194304 100 0 0 0 " +
            $"{utime} {stime} 0 0 20 0 1 0 {start} 123456789 {rssPages}";
        File.WriteAllText(Path.Combine(dir, "stat"), stat);

        File.WriteAllText(Path.Combine(dir, "statm"), $"4096 {rssPages} 64 1 0 512 0\n");
        File.WriteAllText(Path.Combine(dir, "io"),
            $"rchar: 1\nwchar: 1\nsyscr: 1\nsyscw: 1\nread_bytes: {readB}\nwrite_bytes: {writeB}\ncancelled_write_bytes: 0\n");
    }
}
