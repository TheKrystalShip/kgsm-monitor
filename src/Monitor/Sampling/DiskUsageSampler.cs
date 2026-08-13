using TheKrystalShip.KGSM.Core.Models;

namespace TheKrystalShip.KGSM.Monitor.Sampling;

/// <summary>
/// Computes each server's on-disk footprint — the apparent total size of its working
/// directory (install + saves + backups + logs + temp). This is a <em>filesystem</em>
/// figure, not a cgroup counter: cgroups account I/O throughput (<see cref="CgroupSampler"/>)
/// but not space occupied, so this is the one per-server metric that needs a directory walk.
/// <para>
/// A walk stats every file under the tree, which is far too expensive for the 1&#160;Hz
/// metrics tick — so it runs on its own slow cadence (<see cref="MonitorOptions.DiskUsageMs"/>,
/// default 60&#160;s) in <see cref="ServerSampler"/>'s background loop. Results are cached in
/// an immutable dictionary swapped wholesale (reference assignment is atomic; the same
/// lock-free conflation as the watch-list), so the fast tick reads a precomputed value via
/// <see cref="Get"/> and never blocks on I/O.
/// </para>
/// <para>
/// Honest by construction: a directory that can't be read (perms / vanished mid-walk)
/// contributes nothing and an instance with no readable working dir is simply absent from
/// the cache → <see cref="Get"/> returns <c>null</c> ("not measured"), never a fabricated 0.
/// Symlinks are not traversed, so a save dir symlinked elsewhere is counted once, not looped.
/// The figure is <em>apparent</em> size (sum of file lengths), which can exceed on-disk
/// blocks for sparse files — an upper bound, consistent with the "measured, clearly caveated"
/// rule rather than a precise <c>du</c>.
/// </para>
/// </summary>
internal sealed class DiskUsageSampler
{
    private volatile IReadOnlyDictionary<string, long> _usage =
        new Dictionary<string, long>(StringComparer.Ordinal);

    /// <summary>Cached footprint (bytes) for an instance id, or <c>null</c> if not measured.</summary>
    public long? Get(string id) => _usage.TryGetValue(id, out long bytes) ? bytes : null;

    /// <summary>
    /// Every measured footprint, whatever each instance's run state — the whole cache, which the
    /// walk fills from the watch-list rather than from what is running. An instance with no readable
    /// working dir is absent (never a 0). The returned map is the immutable one currently published,
    /// so a caller can enumerate it without holding anything.
    /// </summary>
    public IReadOnlyDictionary<string, long> All => _usage;

    /// <summary>
    /// Walk every instance in the current watch-list and rebuild the footprint cache,
    /// then swap it in atomically. Called only by the slow disk-usage loop (single writer),
    /// so the swap needs no lock. An instance whose working dir is empty/unreadable is left
    /// out of the new map (→ <see cref="Get"/> null), rather than carried stale or zeroed.
    /// </summary>
    public void Refresh(IReadOnlyDictionary<string, Instance> instances)
    {
        var next = new Dictionary<string, long>(instances.Count, StringComparer.Ordinal);
        foreach ((string id, Instance instance) in instances)
        {
            if (TryDirectorySize(instance.WorkingDir) is long bytes)
                next[id] = bytes;
        }
        _usage = next;
    }

    /// <summary>
    /// Apparent total size (sum of file lengths) under <paramref name="root"/>, recursively,
    /// not following symlinked directories. Returns <c>null</c> when the directory is
    /// empty/missing or can't be opened at all; per-file errors (a file removed mid-walk)
    /// are swallowed so a live tree doesn't blank the whole figure.
    /// </summary>
    internal static long? TryDirectorySize(string? root)
    {
        if (string.IsNullOrEmpty(root) || !Directory.Exists(root))
            return null;

        // RecurseSubdirectories: one flat enumeration of the whole tree. AttributesToSkip
        // ReparsePoint: don't descend into (or count) symlinks — avoids double-counting a
        // bind/symlinked saves dir and avoids cycles. IgnoreInaccessible: skip a subdir we
        // can't read rather than aborting the walk.
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint,
        };

        try
        {
            long total = 0;
            foreach (string file in Directory.EnumerateFiles(root, "*", options))
            {
                try { total += new FileInfo(file).Length; }
                catch { /* vanished/locked mid-walk — skip this file */ }
            }
            return total;
        }
        catch
        {
            // The root became unreadable between the Exists check and the walk.
            return null;
        }
    }
}
