using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using TheKrystalShip.KGSM.Core.Interfaces;
using TheKrystalShip.KGSM.Core.Models;
using TheKrystalShip.KGSM.Events;
using TheKrystalShip.KGSM.Monitor;
using TheKrystalShip.KGSM.Monitor.Sampling;
using TheKrystalShip.KGSM.Services;
using TheKrystalShip.Testing;

namespace TheKrystalShip.KGSM.Monitor.Tests;

/// <summary>
/// The event-driven watch-list delta. Two layers of coverage:
/// <list type="bullet">
/// <item><b>Wiring (transport-free, deterministic):</b> a fake <see cref="IEventService"/>
/// proves the sampler subscribes to exactly the four lifecycle events and starts the
/// listener, and that firing one nudges an authoritative resync.</item>
/// <item><b>Integration (a real journal):</b> a real <see cref="EventJournalReader"/> +
/// <see cref="EventService"/> over a temp journal directory — a line appended exactly as
/// the engine writes it must deserialize through <c>JournalJsonContext</c> and trigger a
/// resync. This is the high-risk surface (real deserialization + dispatch) the fakes
/// can't reach.</item>
/// </list>
/// </summary>
public class ServerEventTests
{
    [Fact]
    public async Task WireEvents_subscribes_to_the_four_lifecycle_events_and_initializes()
    {
        var events = new RecordingEventService();
        var sampler = NewSampler(new CountingServerCatalog(), events, EventsEnabled: true);

        await sampler.StartAsync(CancellationToken.None);
        try
        {
            // ExecuteAsync's body runs just after StartAsync returns, so wait for the
            // listener to come up (the four RegisterHandler calls precede Initialize, so
            // the set is fully populated once Initialized flips).
            await Eventually.Holds(() => events.Initialized, "WireEvents initializes the listener");
            Assert.Equal(
                new HashSet<Type>
                {
                    typeof(InstanceStartedData), typeof(InstanceStoppedData),
                    typeof(InstanceRemovedData), typeof(InstanceUninstalledData),
                },
                events.Registered);
        }
        finally
        {
            await sampler.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Firing_a_lifecycle_event_nudges_a_resync()
    {
        var instances = new CountingServerCatalog();
        var events = new RecordingEventService();
        // Long resync floor so the periodic nudge can't move the counter inside the window.
        var sampler = NewSampler(instances, events, EventsEnabled: true, resyncMs: 60_000);

        await sampler.StartAsync(CancellationToken.None);
        try
        {
            // Wait for the prime resync (also guarantees handlers are registered, since
            // WireEvents runs before the prime). The 60s floor can't fire in this window.
            await Eventually.Holds(() => instances.ListCalls >= 1, "the prime resync runs");
            int baseline = instances.ListCalls;

            await events.FireAsync(new InstanceStartedData
            {
                InstanceName = "factorio",
            });

            await Eventually.Holds(() => instances.ListCalls > baseline, $"the event triggers a resync past the baseline of {baseline} List calls");
        }
        finally
        {
            await sampler.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Events_disabled_skips_the_listener_but_still_primes_the_watch_list()
    {
        var instances = new CountingServerCatalog();
        var events = new RecordingEventService();
        var sampler = NewSampler(instances, events, EventsEnabled: false, resyncMs: 60_000);

        await sampler.StartAsync(CancellationToken.None);
        try
        {
            // The resync floor still primes the watch-list even with events off; waiting
            // for it also guarantees ExecuteAsync ran past WireEvents (which must not have
            // touched the listener).
            await Eventually.Holds(() => instances.ListCalls >= 1, "the prime resync runs");
            Assert.False(events.Initialized);
            Assert.Empty(events.Registered);
        }
        finally
        {
            await sampler.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Real_event_envelope_in_the_journal_triggers_a_resync()
    {
        string journal = Path.Combine(Path.GetTempPath(), $"kgsm-mon-journal-{Guid.NewGuid():N}");
        Directory.CreateDirectory(journal);

        var kgsmOptions = new JournalReadOptions
        {
            EventJournalDirectory = journal,
            EventStartPosition = EventStartPosition.CursorOrOldest
        };
        var reader = new EventJournalReader(
            kgsmOptions, new NullEventCursorStore(), NullLogger<EventJournalReader>.Instance);
        await using var events = new EventService(reader, NullLogger<EventService>.Instance);

        var instances = new CountingServerCatalog();
        var sampler = NewSampler(instances, events, EventsEnabled: true, resyncMs: 60_000, journalDir: journal);

        await sampler.StartAsync(CancellationToken.None);
        try
        {
            // Establish the prime baseline (max-without-event is 1; the 60s floor can't
            // fire here), so any further increment is unambiguously the journal event.
            await Eventually.Holds(() => instances.ListCalls >= 1, "the prime resync runs");
            int baseline = instances.ListCalls;

            // The exact line KGSM appends (PascalCase, compact, one event per line, extra
            // top-level fields ignored by EventWrapper). This must round-trip through the
            // source-generated JournalJsonContext.
            await AppendEventAsync(journal,
                """
                {"EventType":"server.started","Data":{"InstanceName":"7dtd"},"Timestamp":"2026-06-11T00:00:00Z","Hostname":"test","KGSMVersion":"1.2.3"}
                """);

            await Eventually.Holds(() => instances.ListCalls > baseline, $"the journal event triggers a resync past the baseline of {baseline} List calls");
        }
        finally
        {
            await sampler.StopAsync(CancellationToken.None);
            Directory.Delete(journal, recursive: true);
        }
    }

    // --- helpers -----------------------------------------------------------------

    private static ServerSampler NewSampler(
        IServerCatalog instances,
        IEventService events,
        bool EventsEnabled,
        int resyncMs = 15_000,
        string journalDir = "/tmp/kgsm-mon-unused-journal")
    {
        var options = new MonitorOptions
        {
            KgsmPath = "/bin/true",
            KgsmJournalDir = journalDir,
            ServerResyncMs = resyncMs,
            EventsEnabled = EventsEnabled,
        };
        return new ServerSampler(NullLogger<ServerSampler>.Instance, options, instances, events);
    }

    /// <summary>Appends one event the way the engine does: a single complete line, compact.</summary>
    private static async Task AppendEventAsync(string journalDir, string json)
    {
        string segment = Path.Combine(journalDir, $"{DateTime.UtcNow:yyyy-MM-dd}.ndjson");
        await File.AppendAllTextAsync(segment, json + "\n");
    }

}

/// <summary>An <see cref="IServerCatalog"/> that only counts its listings.</summary>
internal sealed class CountingServerCatalog : IServerCatalog
{
    private int _listCalls;
    public int ListCalls => Volatile.Read(ref _listCalls);

    public IReadOnlyDictionary<string, WatchedServer> List()
    {
        Interlocked.Increment(ref _listCalls);
        return new Dictionary<string, WatchedServer>();
    }
}

/// <summary>An <see cref="IEventService"/> that records subscriptions and lets a test
/// fire an event directly, with no transport behind it.</summary>
internal sealed class RecordingEventService : IEventService
{
    // Keyed on the interface's own constraint (KgsmEventDataBase), not the narrower
    // EventDataBase the sampler happens to handle: a handler may be registered for any event
    // payload, including one with no instance.
    private readonly Dictionary<Type, Func<KgsmEventDataBase, Task>> _handlers = new();

    public bool Initialized { get; private set; }
    public HashSet<Type> Registered { get; } = new();

    public EventStartPosition? StartPosition { get; private set; }

    public void Initialize() => Initialized = true;

    public void Initialize(EventStartPosition startPosition)
    {
        StartPosition = startPosition;
        Initialized = true;
    }

    public void RegisterHandler<T>(Func<T, Task> handler) where T : KgsmEventDataBase
    {
        Registered.Add(typeof(T));
        _handlers[typeof(T)] = data => handler((T)data);
    }

    public Task FireAsync(EventDataBase data) =>
        _handlers.TryGetValue(data.GetType(), out var handler) ? handler(data) : Task.CompletedTask;

    // Not exercised by ServerEventTests (that's the raw-handler dispatch path covered by
    // EventHistoryStore/EventPersistService tests against the real EventService); a no-op
    // fake here just needs to satisfy the interface.
    public void RegisterRawHandler(Func<EventWrapper, EventPosition, Task> handler) { }
    public void RegisterGapHandler(Func<EventJournalGap, Task> handler) { }

    public void Dispose() { }
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
