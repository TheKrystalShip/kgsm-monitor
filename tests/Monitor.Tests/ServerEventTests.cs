using System.Diagnostics;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using TheKrystalShip.KGSM.Core.Interfaces;
using TheKrystalShip.KGSM.Core.Models;
using TheKrystalShip.KGSM.Core.Models.Enums;
using TheKrystalShip.KGSM.Events;
using TheKrystalShip.KGSM.Monitor;
using TheKrystalShip.KGSM.Monitor.Sampling;
using TheKrystalShip.KGSM.Services;

namespace TheKrystalShip.KGSM.Monitor.Tests;

/// <summary>
/// Slice 2b: the event-driven watch-list delta. Two layers of coverage:
/// <list type="bullet">
/// <item><b>Wiring (socket-free, deterministic):</b> a fake <see cref="IEventService"/>
/// proves the sampler subscribes to exactly the four lifecycle events and starts the
/// listener, and that firing one nudges an authoritative resync.</item>
/// <item><b>Integration (real socket):</b> a real <see cref="UnixSocketClient"/> +
/// <see cref="EventService"/> on a temp socket — a hand-written KGSM event envelope
/// pushed over the wire must deserialize through <c>KgsmJsonContext</c> and trigger a
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
        var sampler = NewSampler(new CountingInstanceService(), events, EventsEnabled: true);

        await sampler.StartAsync(CancellationToken.None);
        try
        {
            // ExecuteAsync's body runs just after StartAsync returns, so wait for the
            // listener to come up (the four RegisterHandler calls precede Initialize, so
            // the set is fully populated once Initialized flips).
            Assert.True(
                await WaitUntilAsync(() => events.Initialized, 5000),
                "WireEvents never initialized the listener");
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
        var instances = new CountingInstanceService();
        var events = new RecordingEventService();
        // Long resync floor so the periodic nudge can't move the counter inside the window.
        var sampler = NewSampler(instances, events, EventsEnabled: true, resyncMs: 60_000);

        await sampler.StartAsync(CancellationToken.None);
        try
        {
            // Wait for the prime resync (also guarantees handlers are registered, since
            // WireEvents runs before the prime). The 60s floor can't fire in this window.
            Assert.True(
                await WaitUntilAsync(() => instances.GetAllCalls >= 1, 5000),
                "prime resync never ran");
            int baseline = instances.GetAllCalls;

            await events.FireAsync(new InstanceStartedData
            {
                InstanceName = "factorio",
            });

            Assert.True(
                await WaitUntilAsync(() => instances.GetAllCalls > baseline, 5000),
                $"event did not trigger a resync (GetAll calls = {instances.GetAllCalls}, baseline {baseline})");
        }
        finally
        {
            await sampler.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Events_disabled_skips_the_listener_but_still_primes_the_watch_list()
    {
        var instances = new CountingInstanceService();
        var events = new RecordingEventService();
        var sampler = NewSampler(instances, events, EventsEnabled: false, resyncMs: 60_000);

        await sampler.StartAsync(CancellationToken.None);
        try
        {
            // The resync floor still primes the watch-list even with events off; waiting
            // for it also guarantees ExecuteAsync ran past WireEvents (which must not have
            // touched the listener).
            Assert.True(
                await WaitUntilAsync(() => instances.GetAllCalls >= 1, 5000),
                "prime resync never ran");
            Assert.False(events.Initialized);
            Assert.Empty(events.Registered);
        }
        finally
        {
            await sampler.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Real_event_envelope_over_the_socket_triggers_a_resync()
    {
        string socket = Path.Combine(Path.GetTempPath(), $"kgsm-mon-{Guid.NewGuid():N}.sock");
        var kgsmOptions = new KgsmOptions { KgsmPath = "/bin/true", SocketPath = socket };
        var client = new UnixSocketClient(kgsmOptions, NullLogger<UnixSocketClient>.Instance);
        await using var events = new EventService(client, NullLogger<EventService>.Instance);

        var instances = new CountingInstanceService();
        var sampler = NewSampler(instances, events, EventsEnabled: true, resyncMs: 60_000, socketPath: socket);

        await sampler.StartAsync(CancellationToken.None);
        try
        {
            // Initialize() binds the socket on a fire-and-forget task, so the file appears
            // asynchronously — poll for it before connecting.
            Assert.True(
                await WaitUntilAsync(() => File.Exists(socket), 5000),
                "event socket never appeared");
            // Establish the prime baseline (max-without-event is 1; the 60s floor can't
            // fire here), so any further increment is unambiguously the socket event.
            Assert.True(
                await WaitUntilAsync(() => instances.GetAllCalls >= 1, 5000),
                "prime resync never ran");
            int baseline = instances.GetAllCalls;

            // The exact envelope KGSM emits (PascalCase, extra top-level fields ignored by
            // EventWrapper). This must round-trip through the source-generated KgsmJsonContext.
            await SendEventAsync(socket,
                """
                {"EventType":"instance_started","Data":{"InstanceName":"7dtd"},"Timestamp":"2026-06-11T00:00:00Z","Hostname":"test","KGSMVersion":"1.2.3"}
                """);

            Assert.True(
                await WaitUntilAsync(() => instances.GetAllCalls > baseline, 5000),
                $"socket event did not trigger a resync (GetAll calls = {instances.GetAllCalls}, baseline {baseline})");
        }
        finally
        {
            await sampler.StopAsync(CancellationToken.None);
        }
    }

    // --- helpers -----------------------------------------------------------------

    private static ServerSampler NewSampler(
        IInstanceService instances,
        IEventService events,
        bool EventsEnabled,
        int resyncMs = 15_000,
        string socketPath = "/tmp/kgsm-mon-unused.sock")
    {
        var options = new MonitorOptions
        {
            KgsmPath = "/bin/true",
            KgsmSocketPath = socketPath,
            ServerResyncMs = resyncMs,
            EventsEnabled = EventsEnabled,
        };
        return new ServerSampler(NullLogger<ServerSampler>.Instance, options, instances, events);
    }

    private static async Task SendEventAsync(string socketPath, string json)
    {
        byte[] payload = Encoding.UTF8.GetBytes(json);
        for (int attempt = 0; ; attempt++)
        {
            using var s = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            try
            {
                await s.ConnectAsync(new UnixDomainSocketEndPoint(socketPath));
                await s.SendAsync(payload, SocketFlags.None);
                s.Shutdown(SocketShutdown.Both);
                return;
            }
            catch (SocketException) when (attempt < 20)
            {
                // bound-but-not-yet-listening race; retry briefly
                await Task.Delay(25);
            }
        }
    }

    private static async Task<bool> WaitUntilAsync(Func<bool> condition, int timeoutMs)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            if (condition())
                return true;
            await Task.Delay(25);
        }
        return condition();
    }
}

/// <summary>An <see cref="IInstanceService"/> that only counts <see cref="GetAll"/>; the
/// sampler touches nothing else, so the rest throws.</summary>
internal sealed class CountingInstanceService : IInstanceService
{
    private int _getAllCalls;
    public int GetAllCalls => Volatile.Read(ref _getAllCalls);

    public Dictionary<string, Instance> GetAll()
    {
        Interlocked.Increment(ref _getAllCalls);
        return new Dictionary<string, Instance>();
    }

    public Dictionary<string, Instance>? GetAllOrNull() => new();
    public Instance? GetInstanceInfo(string instanceName) => throw new NotImplementedException();
    public InstanceRuntimeStatus? GetInstanceStatus(string instanceName) => throw new NotImplementedException();
    public Dictionary<string, Reading<InstanceRuntimeStatus>> GetAllStatuses(bool fast = false) => throw new NotImplementedException();
    public KgsmResult Install(string blueprintName, string? installDir = null, string? version = null, string? name = null, string? actor = null, string? origin = null, int? port = null) => throw new NotImplementedException();
    public KgsmResult Uninstall(string instanceName, string? actor = null, string? origin = null) => throw new NotImplementedException();
    public ICollection<string> GetLogs(string instanceName, int maxLines = 10) => throw new NotImplementedException();
    public Task<ICollection<string>> GetLogsAsync(string instanceName, int maxLines = 10, CancellationToken cancellationToken = default) => throw new NotImplementedException();
    public KgsmResult GetStatus(string instanceName) => throw new NotImplementedException();
    public KgsmResult GetInfo(string instanceName) => throw new NotImplementedException();
    public bool IsActive(string instanceName) => throw new NotImplementedException();
    public KgsmResult Start(string instanceName, string? actor = null, string? origin = null) => throw new NotImplementedException();
    public KgsmResult Stop(string instanceName, string? actor = null, string? origin = null) => throw new NotImplementedException();
    public KgsmResult Restart(string instanceName, string? actor = null, string? origin = null) => throw new NotImplementedException();
    public KgsmResult GetInstalledVersion(string instanceName) => throw new NotImplementedException();
    public KgsmResult GetLatestVersion(string instanceName) => throw new NotImplementedException();
    public KgsmResult CheckUpdate(string instanceName) => throw new NotImplementedException();
    public KgsmResult Update(string instanceName, string? actor = null, string? origin = null) => throw new NotImplementedException();
    public KgsmResult GetBackups(string instanceName) => throw new NotImplementedException();
    public KgsmResult CreateBackup(string instanceName, string? actor = null, string? origin = null) => throw new NotImplementedException();
    public KgsmResult RestoreBackup(string instanceName, string backupName, string? actor = null, string? origin = null) => throw new NotImplementedException();
    public KgsmResult GenerateId(string blueprintName, string? customName = null) => throw new NotImplementedException();
    public KgsmResult Save(string instanceName) => throw new NotImplementedException();
    public KgsmResult SendInput(string instanceName, string command, string? actor = null, string? origin = null) => throw new NotImplementedException();
    public KgsmResult FindConfigPath(string instanceName) => throw new NotImplementedException();
    public KgsmResult GetInstanceConfigValue(string instanceName, string key) => throw new NotImplementedException();
    public KgsmResult SetInstanceConfigValue(string instanceName, string key, string value, string? actor = null, string? origin = null) => throw new NotImplementedException();
    public Task<LogSubscription> SubscribeToLogsAsync(string instanceName, CancellationToken cancellationToken = default) => throw new NotImplementedException();
    public Task<LogSubscription> SubscribeToLogsAsync(string instanceName, LogLevel minimumLogLevel, bool includeRawLines = true, CancellationToken cancellationToken = default) => throw new NotImplementedException();
}

/// <summary>An <see cref="IEventService"/> that records subscriptions and lets a test
/// fire an event directly, without a socket.</summary>
internal sealed class RecordingEventService : IEventService
{
    private readonly Dictionary<Type, Func<EventDataBase, Task>> _handlers = new();

    public bool Initialized { get; private set; }
    public HashSet<Type> Registered { get; } = new();

    public void Initialize() => Initialized = true;

    public void RegisterHandler<T>(Func<T, Task> handler) where T : EventDataBase
    {
        Registered.Add(typeof(T));
        _handlers[typeof(T)] = data => handler((T)data);
    }

    public Task FireAsync(EventDataBase data) =>
        _handlers.TryGetValue(data.GetType(), out var handler) ? handler(data) : Task.CompletedTask;

    public void Dispose() { }
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
