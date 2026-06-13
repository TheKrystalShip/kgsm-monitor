using BenchmarkDotNet.Attributes;
using TheKrystalShip.KGSM.Core.Models;
using TheKrystalShip.KGSM.Monitor;
using TheKrystalShip.KGSM.Monitor.Model;
using TheKrystalShip.KGSM.Monitor.Sampling;

namespace TheKrystalShip.KGSM.Monitor.Benchmarks;

/// <summary>
/// Per-source decomposition (live <c>/proc</c> + <c>/sys</c>): where does the frame
/// time go? Each measures one source's full <c>Sample()</c>/<c>Read()</c> — syscall +
/// parse + rate. The sum should ≈ <c>FrameBenchmarks.BuildFrame</c>; the dominant source
/// is the first lever for "how much can we push it".
/// <para>
/// <see cref="Server"/> measures the Slice 2 per-server cgroup read against a real
/// container (docker) cgroup discovered on the host (the frame benchmark itself runs
/// server-less — no KGSM instances are running this session — so this is the honest
/// per-server cost to project across a fleet).
/// </para>
/// </summary>
[MemoryDiagnoser]
[SimpleJob(warmupCount: 5, iterationCount: 10)]
public class SourceBenchmarks
{
    private static readonly MonitorOptions Opts = new();

    private readonly CpuSource _cpu = new();
    private readonly NetworkSource _net = new(Opts.IfaceDenyPrefixes);
    private readonly DiskSource _disk = new(Opts.MountFsDeny);

    private readonly CgroupSampler _cgroup = new();
    private Dictionary<string, Instance> _oneServer = new();

    private readonly ProcTreeSampler _procTree = new();
    private Dictionary<string, Instance> _oneNative = new();

    [GlobalSetup]
    public void Setup()
    {
        // Prime the delta sources so we measure steady state, not the cold first sample.
        _cpu.Sample();
        _net.Sample();
        _disk.Sample();

        // Point the cgroup sampler at one real, live container (docker) cgroup so the
        // per-server number reflects actual kernel reads (cpu.stat/memory.current/
        // pids.current). Empty dict on hosts with no container cgroups.
        _oneServer = DiscoverOneContainerCgroup();
        _cgroup.Sample(_oneServer); // prime the rate state

        // Slice 3 native path: one native-standalone server whose .pid points at this live
        // benchmark process. Sample() does the dominant cost — a full /proc stat scan (scales
        // with host process count, NOT server count) — plus statm/io for the (tiny) own tree.
        _oneNative = OneNativeForSelf();
        _procTree.Sample(_oneNative); // prime the rate state
    }

    [Benchmark]
    public (double, double[]) Cpu() => _cpu.Sample();

    [Benchmark]
    public MemoryMetrics Memory() => MemorySource.Read();

    [Benchmark]
    public NetworkMetrics Network() => _net.Sample();

    [Benchmark]
    public DiskMetrics Disk() => _disk.Sample();

    [Benchmark]
    public (LoadAvg, long, string) SystemInfo() => SystemSource.Read();

    [Benchmark]
    public ServerMetrics[] Server() => _cgroup.Sample(_oneServer);

    [Benchmark]
    public ServerMetrics[] ServerNative() => _procTree.Sample(_oneNative);

    private static Dictionary<string, Instance> OneNativeForSelf()
    {
        string pidFile = Path.Combine(Path.GetTempPath(), $"kgsm-bench-{Environment.ProcessId}.pid");
        File.WriteAllText(pidFile, Environment.ProcessId.ToString());
        return new Dictionary<string, Instance>
        {
            ["bench-native"] = new Instance
            {
                Name = "bench-native",
                PidFile = pidFile, // no compose_file = native
            },
        };
    }

    private static Dictionary<string, Instance> DiscoverOneContainerCgroup()
    {
        const string slice = "/sys/fs/cgroup/system.slice";
        if (Directory.Exists(slice))
        {
            foreach (var dir in Directory.GetDirectories(slice))
            {
                string name = Path.GetFileName(dir);
                if (name.StartsWith("docker-", StringComparison.Ordinal) &&
                    name.EndsWith(".scope", StringComparison.Ordinal) &&
                    File.Exists(Path.Combine(dir, "cpu.stat")) &&
                    File.Exists(Path.Combine(dir, "memory.current")))
                {
                    // The resolver reads the container id from the .pid file and rebuilds the
                    // docker-<id>.scope candidate, so feed it the id this scope encodes.
                    string id = name["docker-".Length..^".scope".Length];
                    string pidFile = Path.Combine(Path.GetTempPath(), $"kgsm-bench-ctr-{Environment.ProcessId}.pid");
                    File.WriteAllText(pidFile, id);
                    return new Dictionary<string, Instance>
                    {
                        ["bench"] = new Instance
                        {
                            Name = "bench",
                            ComposeFile = "/opt/bench/docker-compose.yml", // is-container signal
                            PidFile = pidFile,
                        },
                    };
                }
            }
        }
        return new Dictionary<string, Instance>();
    }
}
