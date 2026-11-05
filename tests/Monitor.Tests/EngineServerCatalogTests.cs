using TheKrystalShip.KGSM.Engine.Reading;
using TheKrystalShip.KGSM.Monitor.Sampling;

namespace TheKrystalShip.KGSM.Monitor.Tests;

/// <summary>
/// The watch-list as the engine's registry gives it: a sandboxed reader over registry files written
/// into a temporary directory, so nothing outside the test is read.
/// </summary>
public class EngineServerCatalogTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "kgsm-catalog-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort */ }
    }

    private EngineReader ReaderWith(string id, string blueprint)
    {
        string library = Path.Combine(_root, "library");
        string working = Path.Combine(library, "instances", blueprint, id);
        Directory.CreateDirectory(working);
        File.WriteAllText(Path.Combine(library, ".kgsm-library"), """{"schemaVersion":1,"id":"lib1","name":"main"}""");

        string data = Path.Combine(_root, "data", "kgsm");
        Directory.CreateDirectory(data);
        File.WriteAllText(Path.Combine(data, "libraries.json"),
            "{\"schemaVersion\":1,\"libraries\":[{\"name\":\"main\",\"id\":\"lib1\",\"path\":\"" + library + "\"}]}");
        File.WriteAllText(Path.Combine(data, "instances.json"),
            "{\"schemaVersion\":1,\"instances\":{\"" + id + "\":{\"blueprint\":\"" + blueprint
            + "\",\"library\":\"lib1\",\"workingDirectory\":\"" + working + "\"}}}");

        return new EngineReader(new EngineReaderOptions
        {
            InstallRoot = Path.Combine(_root, "install"),
            HomeFallback = Path.Combine(_root, "home"),
            Environment = new Dictionary<string, string?>
            {
                ["HOME"] = Path.Combine(_root, "home"),
                ["XDG_CONFIG_HOME"] = Path.Combine(_root, "config"),
                ["XDG_DATA_HOME"] = Path.Combine(_root, "data"),
                ["XDG_STATE_HOME"] = Path.Combine(_root, "state"),
            },
        });
    }

    [Fact]
    public void A_registered_instance_is_watched_with_the_paths_the_engine_derives_for_it()
    {
        using EngineReader engine = ReaderWith("factorio-1", "factorio");

        var servers = new EngineServerCatalog(engine).List();

        WatchedServer server = Assert.Single(servers).Value;
        Assert.Equal("factorio-1", server.Name);
        Assert.EndsWith(Path.Combine("instances", "factorio", "factorio-1"), server.WorkingDir, StringComparison.Ordinal);
        Assert.Equal(Path.Combine(server.WorkingDir, ".factorio-1.pid"), server.PidFile);
        Assert.EndsWith(Path.Combine("kgsm.slice", "kgsm-watchdog.service", "factorio-1"), server.CgroupPath, StringComparison.Ordinal);
        Assert.Empty(server.ComposeFile);
    }

    [Fact]
    public void An_empty_registry_watches_nothing()
    {
        Directory.CreateDirectory(_root);
        using var engine = new EngineReader(new EngineReaderOptions
        {
            InstallRoot = Path.Combine(_root, "install"),
            HomeFallback = Path.Combine(_root, "home"),
            Environment = new Dictionary<string, string?>
            {
                ["HOME"] = Path.Combine(_root, "home"),
                ["XDG_CONFIG_HOME"] = Path.Combine(_root, "config"),
                ["XDG_DATA_HOME"] = Path.Combine(_root, "data"),
                ["XDG_STATE_HOME"] = Path.Combine(_root, "state"),
            },
        });

        Assert.Empty(new EngineServerCatalog(engine).List());
    }
}
