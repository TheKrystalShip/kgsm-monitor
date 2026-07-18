using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using TheKrystalShip.KGSM.Core.Interfaces;
using TheKrystalShip.KGSM.Core.Models;
using TheKrystalShip.KGSM.Events;
using TheKrystalShip.KGSM.Monitor.History;

namespace TheKrystalShip.KGSM.Monitor.Tests;

/// <summary>
/// <see cref="EventPersistService"/>: the raw-handler wiring must happen in the constructor (the
/// registration-order assumption documented on the class — see its remarks) and a persist failure
/// must never escape the handler.
/// </summary>
public class EventPersistServiceTests
{
    [Fact]
    public void Constructor_registers_a_raw_handler_before_any_hosted_lifecycle_call()
    {
        var events = new RecordingRawEventService();
        var (store, db) = NewStore();
        try
        {
            Assert.Empty(events.RawHandlers);

            _ = new EventPersistService(events, store, new MonitorOptions(), NullLogger<EventPersistService>.Instance);

            // Registered synchronously inside the constructor -- no StartAsync/ExecuteAsync call
            // needed, which is the whole point (it must beat ServerSampler's Initialize()).
            Assert.Single(events.RawHandlers);
        }
        finally { Cleanup(store, db); }
    }

    [Fact]
    public async Task Registered_handler_appends_the_event_to_the_store()
    {
        var events = new RecordingRawEventService();
        var (store, db) = NewStore();
        try
        {
            _ = new EventPersistService(events, store, new MonitorOptions(), NullLogger<EventPersistService>.Instance);

            var wrapper = new EventWrapper
            {
                EventType = "instance_started",
                Data = JsonSerializer.Deserialize<JsonElement>("""{"InstanceName":"factorio-test"}"""),
                Timestamp = DateTimeOffset.UtcNow,
                Hostname = "hotrod",
            };
            await events.FireRawAsync(wrapper);

            EventHistoryResponse resp = await store.QueryEventsAsync(
                instance: null, type: null, sinceMs: null, untilMs: null,
                beforeTs: null, beforeId: null, limit: 10);
            Assert.Single(resp.Events);
        }
        finally { Cleanup(store, db); }
    }

    [Fact]
    public async Task A_persist_failure_is_swallowed_not_thrown_to_the_caller()
    {
        var events = new RecordingRawEventService();
        // A disposed store makes AppendAsync throw ObjectDisposedException-ish failures on the
        // underlying connection -- close over a store we dispose immediately to force a failure path.
        var (store, db) = NewStore();
        store.Dispose();
        try
        {
            _ = new EventPersistService(events, store, new MonitorOptions(), NullLogger<EventPersistService>.Instance);

            var wrapper = new EventWrapper
            {
                EventType = "instance_started",
                Data = JsonSerializer.Deserialize<JsonElement>("""{"InstanceName":"factorio-test"}"""),
                Timestamp = DateTimeOffset.UtcNow,
                Hostname = "hotrod",
            };

            // Must not throw -- OnRawEventAsync's own try/catch is what makes this safe to await
            // directly from what would otherwise be the event socket's read loop.
            await events.FireRawAsync(wrapper);
        }
        finally
        {
            foreach (string suffix in new[] { "", "-wal", "-shm" })
                File.Delete(db + suffix);
        }
    }

    private static (EventHistoryStore store, string db) NewStore()
    {
        string db = Path.Combine(Path.GetTempPath(), $"kgsm-monitor-persist-{Guid.NewGuid():N}.db");
        var opts = new MonitorOptions { EventsDbPath = db };
        return (new EventHistoryStore(opts, NullLogger<EventHistoryStore>.Instance), db);
    }

    private static void Cleanup(EventHistoryStore store, string db)
    {
        store.Dispose();
        foreach (string suffix in new[] { "", "-wal", "-shm" })
            File.Delete(db + suffix);
    }
}

/// <summary>A minimal <see cref="IEventService"/> fake that only supports raw-handler registration
/// and firing, for <see cref="EventPersistService"/>'s constructor-wiring tests.</summary>
internal sealed class RecordingRawEventService : IEventService
{
    public List<Func<EventWrapper, Task>> RawHandlers { get; } = new();

    public void RegisterRawHandler(Func<EventWrapper, Task> handler) => RawHandlers.Add(handler);

    public Task FireRawAsync(EventWrapper wrapper) =>
        Task.WhenAll(RawHandlers.Select(h => h(wrapper)));

    public void Initialize() { }
    public void RegisterHandler<T>(Func<T, Task> handler) where T : EventDataBase { }
    public void Dispose() { }
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
