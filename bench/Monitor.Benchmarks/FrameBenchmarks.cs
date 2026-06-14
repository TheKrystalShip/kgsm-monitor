using System.Text.Json;
using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.Logging.Abstractions;
using TheKrystalShip.KGSM.Monitor;
using TheKrystalShip.KGSM.Monitor.Contracts;
using TheKrystalShip.KGSM.Monitor.Sampling;

namespace TheKrystalShip.KGSM.Monitor.Benchmarks;

/// <summary>
/// The headline numbers: how long to produce one full diagnostic frame, to serialize
/// it (source-gen JSON), and the two combined — i.e. the real per-scrape cost. This is
/// the budget that the 1 Hz (1000 ms) tick must fit inside, with room for Slice 2's
/// per-server cgroup reads on top.
/// </summary>
[MemoryDiagnoser]
[SimpleJob(warmupCount: 5, iterationCount: 10)]
public class FrameBenchmarks
{
    private MetricsSampler _sampler = null!;
    private Snapshot _frame = null!;

    [GlobalSetup]
    public void Setup()
    {
        _sampler = new MetricsSampler(NullLogger<MetricsSampler>.Instance, new MonitorOptions());
        _sampler.Build();          // prime the delta sources -> measure steady state, not the cold path
        _frame = _sampler.Build(); // a representative frame to serialize
    }

    /// <summary>Full frame: CPU + memory + network + disk + load/uptime, rates computed.</summary>
    [Benchmark(Baseline = true)]
    public Snapshot BuildFrame() => _sampler.Build();

    /// <summary>Serialize an existing frame to JSON via the source-gen context.</summary>
    [Benchmark]
    public string SerializeFrame() => JsonSerializer.Serialize(_frame, MonitorJsonContext.Default.Snapshot);

    /// <summary>End-to-end per scrape: build a fresh frame and serialize it.</summary>
    [Benchmark]
    public string BuildAndSerialize() =>
        JsonSerializer.Serialize(_sampler.Build(), MonitorJsonContext.Default.Snapshot);
}
