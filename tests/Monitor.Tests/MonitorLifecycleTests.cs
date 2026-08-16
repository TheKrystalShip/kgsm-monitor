using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using TheKrystalShip.KGSM.Core.Interfaces;
using TheKrystalShip.KGSM.Lifecycle;
using TheKrystalShip.KGSM.Monitor.History;

namespace TheKrystalShip.KGSM.Monitor.Tests;

/// <summary>
/// What this daemon reports about its own state.
/// </summary>
/// <remarks>
/// ⚠ Every one of these was a log line and nothing else. <c>/health</c> answers a literal <c>ok</c>, so
/// a monitor serving a frozen frame, with no per-server network numbers and a dead event listener,
/// reports itself operational to every surface on this host.
/// </remarks>
public sealed class MonitorLifecycleTests
{
    [Fact]
    public void A_meter_that_comes_back_recovers_without_a_restart()
    {
        // The pin is re-probed every tick, so this is a real round trip rather than a one-way fault:
        // running the setup script after the monitor started fixes it in place. The reporter is called
        // on every tick and holds no state of its own — the transition is the emitter's to decide.
        var recorder = new RecordingLifecycle();

        Report(recorder, available: false);
        Report(recorder, available: false);
        Report(recorder, available: true);
        Report(recorder, available: true);
        Report(recorder, available: false);

        Assert.Equal(
            [LeafLifecycleEvents.Degraded, LeafLifecycleEvents.Recovered, LeafLifecycleEvents.Degraded],
            recorder.Types);

        Assert.All(recorder.Recorded, e =>
            Assert.Equal(MonitorComponents.NetworkMeter, e.Component));
    }

    [Fact]
    public void A_meter_that_was_never_missing_is_never_reported_recovered()
    {
        // A recovery for something that never broke is a transition that did not happen, and a
        // consumer clearing an alert it never raised is the mildest thing that follows from one.
        var recorder = new RecordingLifecycle();

        Report(recorder, available: true);
        Report(recorder, available: true);

        Assert.Empty(recorder.Recorded);
    }

    [Fact]
    public void A_frozen_frame_is_reported_and_a_recovered_sample_clears_it()
    {
        // ⚠ Keeping the previous frame on a failed sample is right — a gap would read as a host with no
        // metrics — but it means a broken monitor serves a plausible snapshot indefinitely.
        var recorder = new RecordingLifecycle();

        recorder.Lifecycle.MarkDegraded(MonitorComponents.Sampling, "the host sample failed (boom)");
        recorder.Lifecycle.MarkDegraded(MonitorComponents.Sampling, "the host sample failed (boom)");
        recorder.Lifecycle.MarkRecovered(MonitorComponents.Sampling);

        Assert.Equal([LeafLifecycleEvents.Degraded, LeafLifecycleEvents.Recovered], recorder.Types);
    }

    [Fact]
    public void Readiness_is_the_first_frame_and_not_the_process_starting()
    {
        // /metrics answers 503 until the first frame lands, so a readiness reported at startup would
        // claim something the socket was still refusing.
        var recorder = new RecordingLifecycle();

        Assert.True(recorder.Lifecycle.MarkReady("first frame on a 1000ms interval"));
        Assert.False(recorder.Lifecycle.MarkReady("first frame on a 1000ms interval"));

        Assert.Equal([LeafLifecycleEvents.Ready], recorder.Types);
    }

    [Fact]
    public void The_components_this_daemon_can_report_are_distinct()
    {
        string[] components =
            [MonitorComponents.Sampling, MonitorComponents.NetworkMeter, MonitorComponents.EventListener];

        Assert.Equal(components.Length, components.Distinct(StringComparer.Ordinal).Count());
    }

    /// <summary>
    /// The reporting MetricsSampler does each tick.
    /// </summary>
    /// <remarks>
    /// Mirrors the sampler's own expression rather than driving the sampler, which would mean an
    /// eBPF map and a background loop. What is worth pinning is that it reports what it observes and
    /// keeps no state — the dedup belongs to the emitter, and that is what these tests measure.
    /// </remarks>
    private static void Report(RecordingLifecycle recorder, bool available)
    {
        if (available)
            recorder.Lifecycle.MarkRecovered(MonitorComponents.NetworkMeter);
        else
            recorder.Lifecycle.MarkDegraded(MonitorComponents.NetworkMeter, "unreadable");
    }

    /// <summary>A real <see cref="LeafLifecycle"/> over an in-memory writer.</summary>
    private sealed class RecordingLifecycle
    {
        private readonly Writer _writer = new();

        public LeafLifecycle Lifecycle { get; }

        public RecordingLifecycle() =>
            Lifecycle = new LeafLifecycle(_writer, NullLogger<LeafLifecycle>.Instance);

        public IReadOnlyList<Entry> Recorded => _writer.Recorded;

        public string[] Types => [.. _writer.Recorded.Select(e => e.Type)];

        public sealed record Entry(string Type, string? Component);

        private sealed class Writer : IEventJournalWriter
        {
            public List<Entry> Recorded { get; } = [];

            public string Producer => "kgsm-monitor";

            public ValueTask<bool> AppendAsync(
                string eventType, JsonElement data, string? actor = null, string? origin = null,
                CancellationToken token = default)
            {
                string? component =
                    data.TryGetProperty(LeafLifecycleFields.Component, out JsonElement c)
                    && c.ValueKind == JsonValueKind.String
                        ? c.GetString()
                        : null;

                Recorded.Add(new Entry(eventType, component));
                return ValueTask.FromResult(true);
            }
        }
    }
}
