using System.Text.Json;

using TheKrystalShip.KGSM.Monitor.Contracts;
using TheKrystalShip.KGSM.Monitor.History;
using TheKrystalShip.KGSM.Monitor.Sampling;

namespace TheKrystalShip.KGSM.Monitor.Tests;

/// <summary>
/// Deterministic tests for the per-leaf path: descriptor discovery, the <c>systemctl show</c> parse, and
/// cgroup resolution. The counter parsing and rate math are <see cref="CgroupSampler"/>'s, shared rather
/// than reimplemented, and covered by <c>ServerSamplingTests</c>.
/// <para>
/// The resolution tests use this host's real layout, which is the case that motivates the whole design:
/// <c>kgsm-watchdog</c> runs itself in a <c>supervisor</c> child of its unit cgroup and spawns each game
/// server into a sibling, so sampling the unit cgroup would charge the daemon for the servers.
/// </para>
/// </summary>
public class LeafSamplingTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("kgsm-leaf-test-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string Dir(params string[] parts) => Path.Combine([_root, .. parts]);

    private void WriteDescriptor(string file, string body)
    {
        Directory.CreateDirectory(Dir("leaves"));
        File.WriteAllText(Dir("leaves", file), body);
    }

    // ---- descriptor discovery ----

    [Fact]
    public void ReadDescriptors_takes_id_and_unit_from_each_file()
    {
        WriteDescriptor("monitor.json", """{"id":"monitor","unit":"kgsm-monitor.service","fields":[]}""");
        WriteDescriptor("watchdog.json", """{"id":"watchdog","unit":"kgsm-watchdog.service","fields":[]}""");

        var found = LeafSampler.ReadDescriptors(Dir("leaves"));

        Assert.Equal(2, found.Count);
        Assert.Contains(found, d => d.Id == "monitor" && d.Unit == "kgsm-monitor.service");
        Assert.Contains(found, d => d.Id == "watchdog" && d.Unit == "kgsm-watchdog.service");
        Assert.All(found, d => Assert.Empty(d.GpuBackendUnits));
    }

    [Fact]
    public void ReadDescriptors_skips_only_the_unusable_file()
    {
        // A deploy writing a descriptor right now, or a stray json, must not cost us the whole scan.
        WriteDescriptor("monitor.json", """{"id":"monitor","unit":"kgsm-monitor.service"}""");
        WriteDescriptor("half-written.json", """{"id":"bot","unit":""" );
        WriteDescriptor("no-unit.json", """{"id":"ghost"}""");
        WriteDescriptor("blank-unit.json", """{"id":"ghost2","unit":"  "}""");

        var found = LeafSampler.ReadDescriptors(Dir("leaves"));

        (string Id, string Unit, string[] GpuBackendUnits) only = Assert.Single(found);
        Assert.Equal("monitor", only.Id);
        Assert.Equal("kgsm-monitor.service", only.Unit);
    }

    [Fact]
    public void ReadDescriptors_is_empty_when_the_directory_is_absent()
    {
        // A host where no leaf has deployed yet: no leaves measured, and nothing thrown.
        Assert.Empty(LeafSampler.ReadDescriptors(Dir("nope")));
    }

    [Fact]
    public void ReadDescriptors_reads_the_real_shipped_descriptor()
    {
        // Pin discovery to the actual file this repo generates — if the descriptor's own shape ever moves
        // the id/unit fields, leaf discovery breaks silently and this is what catches it.
        string shipped = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..",
            "deploy", "kgsm-monitor.leaf.json");
        Assert.True(File.Exists(shipped), $"expected the generated descriptor at {Path.GetFullPath(shipped)}");

        using JsonDocument doc = JsonDocument.Parse(File.ReadAllBytes(shipped));
        Assert.Equal("monitor", doc.RootElement.GetProperty("id").GetString());
        Assert.Equal("kgsm-monitor.service", doc.RootElement.GetProperty("unit").GetString());
    }

    [Fact]
    public void ReadDescriptors_takes_the_declared_gpu_backend_units()
    {
        // How a leaf claims GPU spent in a process it does not own: the assistant drives llama-server,
        // whose video memory is the assistant's cost even though the pid belongs to another unit.
        WriteDescriptor("assistant.json", """
            {"id":"assistant","unit":"kgsm-assistant-service.service",
             "gpuBackendUnits":["kgsm-llama-chat.service","kgsm-llama-embed.service"]}
            """);

        (string Id, string Unit, string[] GpuBackendUnits) only =
            Assert.Single(LeafSampler.ReadDescriptors(Dir("leaves")));

        Assert.Equal(["kgsm-llama-chat.service", "kgsm-llama-embed.service"], only.GpuBackendUnits);
    }

    [Fact]
    public void ReadDescriptors_ignores_a_malformed_gpu_backend_list()
    {
        // A wrong-typed or junk-bearing key costs the leaf its backends, never the leaf itself.
        WriteDescriptor("a.json", """{"id":"a","unit":"a.service","gpuBackendUnits":"not-an-array"}""");
        WriteDescriptor("b.json", """{"id":"b","unit":"b.service","gpuBackendUnits":["ok.service","",7]}""");

        var found = LeafSampler.ReadDescriptors(Dir("leaves"));

        Assert.Empty(found.Single(d => d.Id == "a").GpuBackendUnits);
        Assert.Equal(["ok.service"], found.Single(d => d.Id == "b").GpuBackendUnits);
    }

    // ---- gpu unit resolution ----

    [Theory]
    [InlineData("0::/system.slice/kgsm-llama-chat.service", "kgsm-llama-chat.service")]
    [InlineData("0::/system.slice/kgsm-speech.service", "kgsm-speech.service")]
    [InlineData("0::/kgsm.slice/kgsm-factorio.scope", "kgsm-factorio.scope")]
    [InlineData("0::/user.slice/user-1000.slice/session-2.scope", "session-2.scope")]
    [InlineData("0::/", null)]
    [InlineData("", null)]
    public void UnitFromCgroupLine_names_the_owning_unit(string line, string? expected) =>
        Assert.Equal(expected, GpuSource.UnitFromCgroupLine(line));

    [Fact]
    public void UnitFromCgroupLine_takes_the_deepest_unit_segment()
    {
        // A supervisor spawning children into nested cgroups: the work belongs to the unit it runs in,
        // not to the slice above it.
        Assert.Equal("child.service",
            GpuSource.UnitFromCgroupLine("0::/system.slice/parent.service/child.service"));
    }

    // ---- systemctl show parsing ----

    [Fact]
    public void ParseMainPids_maps_each_block_to_its_unit()
    {
        // Verbatim shape of `systemctl show --property=Id,MainPID <units>`: blank-line separated blocks.
        const string Output =
            "Id=kgsm-monitor.service\nMainPID=618\n\n" +
            "Id=kgsm-watchdog.service\nMainPID=621\n\n" +
            "Id=kgsm-bot.service\nMainPID=1849\n";

        var pids = LeafSampler.ParseMainPids(Output);

        Assert.Equal(3, pids.Count);
        Assert.Equal(618, pids["kgsm-monitor.service"]);
        Assert.Equal(621, pids["kgsm-watchdog.service"]);
        Assert.Equal(1849, pids["kgsm-bot.service"]);
    }

    [Fact]
    public void ParseMainPids_omits_units_with_no_main_process()
    {
        // MainPID=0 is "not running". Absent from the map, never a leaf sampled as pid 0 — this is how a
        // socket-activated leaf sitting idle stays out of the frame instead of appearing with zeros.
        const string Output =
            "Id=kgsm-firewall.service\nMainPID=0\n\n" +
            "Id=kgsm-api.service\nMainPID=4242\n";

        var pids = LeafSampler.ParseMainPids(Output);

        Assert.False(pids.ContainsKey("kgsm-firewall.service"));
        Assert.Equal(4242, pids["kgsm-api.service"]);
    }

    [Fact]
    public void ParseMainPids_handles_a_trailing_block_without_a_blank_line()
    {
        Assert.Equal(7, LeafSampler.ParseMainPids("Id=x.service\nMainPID=7").GetValueOrDefault("x.service"));
    }

    // ---- cgroup resolution ----

    [Theory]
    [InlineData("0::/system.slice/kgsm-bot.service\n", "system.slice/kgsm-bot.service")]
    // The watchdog: its own process lives one level below the unit cgroup, beside the servers it supervises.
    [InlineData("0::/kgsm.slice/kgsm-watchdog.service/supervisor\n", "kgsm.slice/kgsm-watchdog.service/supervisor")]
    // Hybrid hosts also list v1 controllers; only the unified line addresses the hierarchy these counters live in.
    [InlineData("12:pids:/system.slice/a.service\n0::/system.slice/b.service\n", "system.slice/b.service")]
    [InlineData("0::/\n", null)]                                // the root cgroup exposes no counters
    [InlineData("11:devices:/system.slice/a.service\n", null)]   // cgroup v1 only — nothing unified to read
    [InlineData("", null)]
    public void ParseUnifiedPath_takes_only_the_unified_line(string content, string? expected)
        => Assert.Equal(expected, LeafSampler.ParseUnifiedPath(content));

    [Fact]
    public void ResolveCgroupDir_points_at_the_process_cgroup_not_the_units()
    {
        // The headline: the watchdog's unit cgroup holds the game servers, so resolving through the main
        // pid is what keeps a supervisor from being charged for what it supervises.
        Directory.CreateDirectory(Dir("proc", "621"));
        File.WriteAllText(Dir("proc", "621", "cgroup"), "0::/kgsm.slice/kgsm-watchdog.service/supervisor\n");

        string? dir = LeafSampler.ResolveCgroupDir(621, Dir("proc"), Dir("cgroup"));

        Assert.Equal(Dir("cgroup", "kgsm.slice", "kgsm-watchdog.service", "supervisor"), dir);
        Assert.NotEqual(Dir("cgroup", "kgsm.slice", "kgsm-watchdog.service"), dir);
    }

    [Fact]
    public void ResolveCgroupDir_is_null_for_a_process_that_is_gone()
    {
        // The ordinary race: the leaf exited between the systemctl read and this one. Null drops it from
        // the frame, which is honest — there is nothing running to measure.
        Assert.Null(LeafSampler.ResolveCgroupDir(999999, Dir("proc"), Dir("cgroup")));
    }

    // ---- history mapping ----

    [Fact]
    public void MapLeafMetrics_writes_the_leaf_kind_under_the_shared_metric_names()
    {
        var rows = new List<HistoryRow>();
        MetricsPersistService.MapLeafMetrics(
            rows, new LeafMetrics("watchdog", "kgsm-watchdog.service", 3.5, 56_336_384, 1024, 2048, 17), 1000);

        Assert.All(rows, r => Assert.Equal("leaf", r.Kind));
        Assert.All(rows, r => Assert.Equal("watchdog", r.Id));
        // The same names the server rows use, so one chart component renders either without knowing which.
        Assert.Equal(["cpuPctCore", "memBytes", "ioReadBps", "ioWriteBps", "pids"], rows.Select(r => r.Metric));
        Assert.Equal(56_336_384d, rows.Single(r => r.Metric == "memBytes").Value);
    }

    [Fact]
    public void MapLeafMetrics_omits_unaccounted_io_rather_than_writing_zero()
    {
        // io.stat absent (the controller isn't accounted for this cgroup) must leave a gap in history, not
        // a flat zero line that reads as "measured, and idle".
        var rows = new List<HistoryRow>();
        MetricsPersistService.MapLeafMetrics(
            rows, new LeafMetrics("bot", "kgsm-bot.service", 0, 111_624_192, null, null, 17), 1000);

        Assert.DoesNotContain(rows, r => r.Metric.StartsWith("io", StringComparison.Ordinal));
        Assert.Equal(["cpuPctCore", "memBytes", "pids"], rows.Select(r => r.Metric));
    }
}
