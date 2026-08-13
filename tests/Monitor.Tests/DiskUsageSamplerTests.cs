using TheKrystalShip.KGSM.Core.Models;
using TheKrystalShip.KGSM.Monitor.Sampling;

namespace TheKrystalShip.KGSM.Monitor.Tests;

/// <summary>
/// The per-server on-disk footprint sampler. The directory-size walk is tested against a
/// real temp tree (sizes, nesting, the honest-null cases); <see cref="DiskUsageSampler.Refresh"/>
/// + <see cref="DiskUsageSampler.Get"/> are tested for the cache semantics the metrics tick
/// relies on (a measured value, and null — never 0 — for the not-measured cases).
/// </summary>
public class DiskUsageSamplerTests : IDisposable
{
    private readonly string _root;

    public DiskUsageSamplerTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "kgsm-disk-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort */ }
    }

    private string Write(string relPath, int bytes)
    {
        string full = Path.Combine(_root, relPath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllBytes(full, new byte[bytes]);
        return full;
    }

    [Fact]
    public void TryDirectorySize_sums_file_lengths_recursively()
    {
        Write("a.txt", 100);
        Write("sub/b.bin", 250);
        Write("sub/deep/c.dat", 7);

        long? size = DiskUsageSampler.TryDirectorySize(_root);

        Assert.Equal(357L, size); // 100 + 250 + 7, across nested dirs
    }

    [Fact]
    public void TryDirectorySize_is_null_for_a_missing_directory()
    {
        // Not-measured is null, never a fabricated 0 — the whole point of the honesty rule.
        Assert.Null(DiskUsageSampler.TryDirectorySize(Path.Combine(_root, "does-not-exist")));
        Assert.Null(DiskUsageSampler.TryDirectorySize(null));
        Assert.Null(DiskUsageSampler.TryDirectorySize(""));
    }

    [Fact]
    public void TryDirectorySize_is_zero_for_an_empty_directory()
    {
        // An empty (but readable) dir is genuinely measured at 0 bytes — distinct from a
        // missing/unreadable dir (null). 0-vs-null is the measured/not-measured distinction.
        string empty = Path.Combine(_root, "empty");
        Directory.CreateDirectory(empty);

        Assert.Equal(0L, DiskUsageSampler.TryDirectorySize(empty));
    }

    [Fact]
    public void TryDirectorySize_does_not_follow_symlinked_directories()
    {
        // A saves dir symlinked elsewhere must be counted once (here: not at all via the link),
        // never double-counted or looped. The link target's bytes live outside the tree.
        Write("real/big.bin", 1000);
        string outside = Path.Combine(Path.GetTempPath(), "kgsm-disk-out-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outside);
        File.WriteAllBytes(Path.Combine(outside, "elsewhere.bin"), new byte[5000]);
        try
        {
            Directory.CreateSymbolicLink(Path.Combine(_root, "link-to-outside"), outside);

            long? size = DiskUsageSampler.TryDirectorySize(_root);

            Assert.Equal(1000L, size); // only real/big.bin; the symlinked tree is not descended
        }
        finally
        {
            try { Directory.Delete(outside, recursive: true); } catch { }
        }
    }

    [Fact]
    public void Refresh_then_Get_returns_the_measured_footprint_per_instance()
    {
        Write("install/game", 4096);
        var sampler = new DiskUsageSampler();
        var watch = new Dictionary<string, Instance>
        {
            ["srv"] = new Instance { Name = "srv", WorkingDir = _root },
        };

        sampler.Refresh(watch);

        Assert.Equal(4096L, sampler.Get("srv"));
    }

    [Fact]
    public void Get_is_null_before_any_refresh_and_for_unknown_or_unreadable_instances()
    {
        var sampler = new DiskUsageSampler();
        Assert.Null(sampler.Get("srv")); // never refreshed yet

        sampler.Refresh(new Dictionary<string, Instance>
        {
            ["nodir"] = new Instance { Name = "nodir", WorkingDir = "/no/such/path" },
        });

        Assert.Null(sampler.Get("nodir"));   // unreadable working dir → absent, not 0
        Assert.Null(sampler.Get("unknown")); // never in the watch-list
    }

    // The whole cache is what reaches a consumer as Snapshot.ServerDisks, and it is built from the
    // WATCH-LIST, not from what is running — which is the entire reason a stopped instance can be
    // shown occupying disk. An unreadable working dir stays absent here too.
    [Fact]
    public void All_exposes_every_measured_instance_regardless_of_run_state()
    {
        Write("install/game", 4096);
        var sampler = new DiskUsageSampler();

        Assert.Empty(sampler.All); // never refreshed yet

        sampler.Refresh(new Dictionary<string, Instance>
        {
            ["stopped"] = new Instance { Name = "stopped", WorkingDir = _root },
            ["nodir"] = new Instance { Name = "nodir", WorkingDir = "/no/such/path" },
        });

        Assert.Equal(4096L, Assert.Contains("stopped", sampler.All));
        Assert.DoesNotContain("nodir", sampler.All);
    }
}
