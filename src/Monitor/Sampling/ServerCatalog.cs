using TheKrystalShip.KGSM.Engine.Contracts.Blueprints;
using TheKrystalShip.KGSM.Engine.Reading;

namespace TheKrystalShip.KGSM.Monitor.Sampling;

/// <summary>
/// One installed server as the samplers need it: where its files are and where its cgroup would be.
/// Everything else the engine knows about an instance stays with the engine.
/// </summary>
public sealed record WatchedServer
{
    /// <summary>The instance id.</summary>
    public required string Name { get; init; }

    /// <summary>The instance's working directory, which the disk walk measures.</summary>
    public string WorkingDir { get; init; } = string.Empty;

    /// <summary>The pid file: a process id for a native server, a container id for a container.</summary>
    public string PidFile { get; init; } = string.Empty;

    /// <summary>The compose file of a container server; empty for a native one.</summary>
    public string ComposeFile { get; init; } = string.Empty;

    /// <summary>The cgroup directory a native server's supervisor creates for it; empty when it has none.</summary>
    public string CgroupPath { get; init; } = string.Empty;
}

/// <summary>The engine's installed servers, as the watch-list sees them.</summary>
public interface IServerCatalog
{
    /// <summary>Every registered server keyed by id, running or not.</summary>
    IReadOnlyDictionary<string, WatchedServer> List();
}

/// <summary>Reads the watch-list from the engine's registry, in-process.</summary>
internal sealed class EngineServerCatalog(EngineReader engine) : IServerCatalog
{
    public IReadOnlyDictionary<string, WatchedServer> List()
    {
        var cgroup = engine.Settings.Cgroup;
        var servers = new Dictionary<string, WatchedServer>(StringComparer.Ordinal);

        foreach (var instance in engine.Instances())
        {
            var layout = instance.Layout;
            bool container = instance.Config?.Runtime == BlueprintRuntime.Container;
            servers[instance.Id] = new WatchedServer
            {
                Name = instance.Id,
                WorkingDir = layout.WorkingDirectory,
                PidFile = layout.PidFile,
                ComposeFile = container ? layout.ComposeFile : string.Empty,
                CgroupPath = container ? string.Empty : layout.CgroupPath(cgroup.MountPoint, cgroup.BaseName),
            };
        }

        return servers;
    }
}
