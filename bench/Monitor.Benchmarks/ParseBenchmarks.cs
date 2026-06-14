using BenchmarkDotNet.Attributes;
using TheKrystalShip.KGSM.Monitor.Contracts;
using TheKrystalShip.KGSM.Monitor.Sampling;

namespace TheKrystalShip.KGSM.Monitor.Benchmarks;

/// <summary>
/// Pure parse / rate math on in-memory snapshots (read once in setup — no per-op IO).
/// Isolates the managed CPU cost from the syscall cost the live source benchmarks include.
/// If this tier is a small fraction of a frame, the frame is syscall-bound and the JIT
/// baseline is representative of the AOT artifact (codegen only moves this tier).
/// </summary>
[MemoryDiagnoser]
[SimpleJob(warmupCount: 5, iterationCount: 10)]
public class ParseBenchmarks
{
    private static readonly string[] NoDeny = [];

    private string _stat = "", _meminfo = "", _netdev = "", _loadavg = "", _uptime = "";
    private long[] _cpuIdleA = [], _cpuTotalA = [], _cpuIdleB = [], _cpuTotalB = [];
    private Dictionary<string, long[]> _netA = new(), _netB = new();

    [GlobalSetup]
    public void Setup()
    {
        _stat = File.ReadAllText("/proc/stat");
        _meminfo = File.ReadAllText("/proc/meminfo");
        _netdev = File.ReadAllText("/proc/net/dev");
        _loadavg = File.ReadAllText("/proc/loadavg");
        _uptime = File.ReadAllText("/proc/uptime");

        (_cpuIdleA, _cpuTotalA) = CpuSource.Parse(_stat);
        (_cpuIdleB, _cpuTotalB) = CpuSource.Parse(_stat);
        _netA = NetworkSource.Parse(_netdev);
        _netB = NetworkSource.Parse(_netdev);
    }

    [Benchmark]
    public (long[], long[]) Cpu_Parse() => CpuSource.Parse(_stat);

    [Benchmark]
    public (double, double[]) Cpu_ComputeRates() =>
        CpuSource.ComputeRates(_cpuIdleA, _cpuTotalA, _cpuIdleB, _cpuTotalB);

    [Benchmark]
    public MemoryMetrics Mem_Parse() => MemorySource.Parse(_meminfo);

    [Benchmark]
    public Dictionary<string, long[]> Net_Parse() => NetworkSource.Parse(_netdev);

    [Benchmark]
    public NetworkMetrics Net_ComputeRates() =>
        NetworkSource.ComputeRates(_netA, _netB, 1.0, NoDeny);

    [Benchmark]
    public LoadAvg Load_Parse() => SystemSource.ParseLoadAvg(_loadavg);

    [Benchmark]
    public long Uptime_Parse() => SystemSource.ParseUptime(_uptime);
}
