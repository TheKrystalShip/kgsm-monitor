using BenchmarkDotNet.Attributes;
using TheKrystalShip.KGSM.Monitor;
using TheKrystalShip.KGSM.Monitor.Model;
using TheKrystalShip.KGSM.Monitor.Sampling;

namespace TheKrystalShip.KGSM.Monitor.Benchmarks;

/// <summary>
/// Per-source decomposition (live <c>/proc</c> + <c>/sys</c>): where does the frame
/// time go? Each measures one source's full <c>Sample()</c>/<c>Read()</c> — syscall +
/// parse + rate. The sum should ≈ <c>FrameBenchmarks.BuildFrame</c>; the dominant source
/// is the first lever for "how much can we push it".
/// </summary>
[MemoryDiagnoser]
[SimpleJob(warmupCount: 5, iterationCount: 10)]
public class SourceBenchmarks
{
    private static readonly MonitorOptions Opts = new();

    private readonly CpuSource _cpu = new();
    private readonly NetworkSource _net = new(Opts.IfaceDenyPrefixes);
    private readonly DiskSource _disk = new(Opts.MountFsDeny);

    [GlobalSetup]
    public void Setup()
    {
        // Prime the delta sources so we measure steady state, not the cold first sample.
        _cpu.Sample();
        _net.Sample();
        _disk.Sample();
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
}
