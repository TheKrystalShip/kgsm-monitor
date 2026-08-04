using System.Text.Json;
using Microsoft.Extensions.Logging;
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

            _ = new EventPersistService(
                events, store, new StubConfigService(), new MonitorOptions(),
                NullLogger<EventPersistService>.Instance);

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
            _ = new EventPersistService(
                events, store, new StubConfigService(), new MonitorOptions(),
                NullLogger<EventPersistService>.Instance);

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
            _ = new EventPersistService(
                events, store, new StubConfigService(), new MonitorOptions(),
                NullLogger<EventPersistService>.Instance);

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

    // --- retention layering: the journal must reach back at least as far as the index claims to ---

    [Fact]
    public void Shorter_journal_retention_than_index_retention_is_reported_as_an_error()
    {
        var log = new CapturingLogger<EventPersistService>();
        var (store, db) = NewStore();
        try
        {
            var config = new StubConfigService(journalRetention: "7");
            var service = new EventPersistService(
                new RecordingRawEventService(), store, config,
                new MonitorOptions { EventRetentionDays = 30 }, log);

            service.CheckRetentionLayering();

            Assert.Contains("event_journal_retention_days", config.Reads);
            Assert.Contains(log.Entries, e => e.Level == LogLevel.Error && e.Message.Contains("shorter than index retention"));
        }
        finally { Cleanup(store, db); }
    }

    [Fact]
    public void A_journal_that_covers_the_index_is_reported_without_alarm()
    {
        var log = new CapturingLogger<EventPersistService>();
        var (store, db) = NewStore();
        try
        {
            var service = new EventPersistService(
                new RecordingRawEventService(), store, new StubConfigService(journalRetention: "90"),
                new MonitorOptions { EventRetentionDays = 30 }, log);

            service.CheckRetentionLayering();

            Assert.DoesNotContain(log.Entries, e => e.Level >= LogLevel.Warning);
            Assert.Contains(log.Entries, e => e.Message.Contains("covers index retention"));
        }
        finally { Cleanup(store, db); }
    }

    /// <summary>
    /// Equal is the boundary case and it is fine: the journal covering exactly the index window
    /// loses nothing on rebuild. The check must not warn on it.
    /// </summary>
    [Fact]
    public void Equal_retention_windows_are_not_a_warning()
    {
        var log = new CapturingLogger<EventPersistService>();
        var (store, db) = NewStore();
        try
        {
            var service = new EventPersistService(
                new RecordingRawEventService(), store, new StubConfigService(journalRetention: "30"),
                new MonitorOptions { EventRetentionDays = 30 }, log);

            service.CheckRetentionLayering();

            Assert.DoesNotContain(log.Entries, e => e.Level >= LogLevel.Warning);
        }
        finally { Cleanup(store, db); }
    }

    /// <summary>
    /// An engine that cannot answer must leave the check saying "unverified" — never "fine". The
    /// never-fabricate rule applies to a configuration claim exactly as it does to a metric.
    /// </summary>
    [Fact]
    public void An_unreadable_journal_retention_is_reported_as_unverified_not_assumed_safe()
    {
        var log = new CapturingLogger<EventPersistService>();
        var (store, db) = NewStore();
        try
        {
            var service = new EventPersistService(
                new RecordingRawEventService(), store, new StubConfigService(journalRetention: null),
                new MonitorOptions { EventRetentionDays = 30 }, log);

            service.CheckRetentionLayering();

            Assert.Contains(log.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("unverified"));
            Assert.DoesNotContain(log.Entries, e => e.Message.Contains("covers index retention"));
        }
        finally { Cleanup(store, db); }
    }

    /// <summary>A kgsm that is unreachable throws out of <c>Get</c>; that must not take the daemon down.</summary>
    [Fact]
    public void A_throwing_config_read_is_contained()
    {
        var log = new CapturingLogger<EventPersistService>();
        var (store, db) = NewStore();
        try
        {
            var service = new EventPersistService(
                new RecordingRawEventService(), store, new ThrowingConfigService(),
                new MonitorOptions { EventRetentionDays = 30 }, log);

            service.CheckRetentionLayering();

            Assert.Contains(log.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("unverified"));
        }
        finally { Cleanup(store, db); }
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
    public void Initialize(EventStartPosition startPosition) { }
    public void RegisterHandler<T>(Func<T, Task> handler) where T : KgsmEventDataBase { }
    public void RegisterGapHandler(Func<EventJournalGap, Task> handler) { }
    public void Dispose() { }
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

/// <summary>
/// An <see cref="IConfigService"/> fake that answers one engine config key — enough for
/// <see cref="EventPersistService"/>'s retention-layering check, which reads
/// <c>event_journal_retention_days</c> and nothing else. <see langword="null"/> models a kgsm that
/// does not know the key.
/// </summary>
internal sealed class StubConfigService : IConfigService
{
    private readonly string? _journalRetention;

    public StubConfigService(string? journalRetention = "90") => _journalRetention = journalRetention;

    /// <summary>Keys this fake was asked for — proves the check reads the key it claims to.</summary>
    public List<string> Reads { get; } = new();

    public string? Get(string key)
    {
        Reads.Add(key);
        return key == "event_journal_retention_days" ? _journalRetention : null;
    }

    public KgsmResult Set(string key, string value) => throw new NotSupportedException();
    public Dictionary<string, string> List() => new();
    public KgsmResult Reset() => throw new NotSupportedException();
    public KgsmResult Validate() => throw new NotSupportedException();
    public KgsmResult Merge() => throw new NotSupportedException();
    public KgsmResult Rollback(int generation = 0) => throw new NotSupportedException();
    public KgsmResult Diff(int generation = 0) => throw new NotSupportedException();
}

/// <summary>Models an unreachable kgsm — <c>Get</c> throws rather than returning nothing.</summary>
internal sealed class ThrowingConfigService : IConfigService
{
    public string? Get(string key) => throw new InvalidOperationException("kgsm is unreachable");

    public KgsmResult Set(string key, string value) => throw new NotSupportedException();
    public Dictionary<string, string> List() => new();
    public KgsmResult Reset() => throw new NotSupportedException();
    public KgsmResult Validate() => throw new NotSupportedException();
    public KgsmResult Merge() => throw new NotSupportedException();
    public KgsmResult Rollback(int generation = 0) => throw new NotSupportedException();
    public KgsmResult Diff(int generation = 0) => throw new NotSupportedException();
}

/// <summary>Captures formatted log entries so a test can assert what was reported, and at what level.</summary>
internal sealed class CapturingLogger<T> : ILogger<T>
{
    public List<(LogLevel Level, string Message)> Entries { get; } = new();

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter)
        => Entries.Add((logLevel, formatter(state, exception)));
}
