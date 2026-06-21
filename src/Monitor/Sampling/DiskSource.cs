using System.Text.RegularExpressions;
using TheKrystalShip.KGSM.Monitor.Contracts;

namespace TheKrystalShip.KGSM.Monitor.Sampling;

/// <summary>
/// Disk usage (per mount, via <see cref="DriveInfo"/>) and aggregate I/O
/// throughput. Throughput sums whole-disk counters from <c>/sys/block/*/stat</c>
/// — enumerating <c>/sys/block</c> yields only whole disks, never partitions, so
/// there is no double-counting. Sectors are 512 bytes by convention. Pseudo
/// filesystems are always filtered; <paramref name="mountFsDeny"/> hides additional
/// fs types.
///
/// Each reported mount also carries its backing-disk <c>model</c> string, resolved via
/// <c>/proc/self/mountinfo</c> (mount-point → <c>/dev</c> node) → partition-to-disk
/// normalization → <c>/sys/block/&lt;disk&gt;/device/model</c>. That join is <em>static</em>
/// (the model never changes), so it is built once and cached. The mountinfo + <c>/sys/block</c>
/// roots are injectable so the join is golden-file testable.
/// </summary>
public sealed partial class DiskSource(
    IReadOnlySet<string> mountFsDeny,
    string mountInfoPath = "/proc/self/mountinfo",
    string sysBlockRoot = "/sys/block")
{
    private readonly IReadOnlySet<string> _mountFsDeny = mountFsDeny;
    private readonly string _mountInfoPath = mountInfoPath;
    private readonly string _sysBlockRoot = sysBlockRoot;
    private long _prevReadSectors, _prevWriteSectors, _prevTicks;

    // mount-point -> backing-disk model string. Static (model doesn't change), built once.
    private Dictionary<string, string?>? _deviceModels;

    public DiskMetrics Sample()
    {
        return new DiskMetrics(ReadMounts(_mountFsDeny), ReadIo());
    }

    private MountUsage[] ReadMounts(IReadOnlySet<string> deny)
    {
        // Build the (cached) mount-point -> disk-model map once.
        _deviceModels ??= BuildDeviceModels(_mountInfoPath, _sysBlockRoot);

        var mounts = new List<MountUsage>();
        foreach (var d in DriveInfo.GetDrives())
        {
            try
            {
                if (!d.IsReady || !IncludeMount(d.DriveFormat, deny))
                    continue;
                long total = d.TotalSize;
                if (total <= 0)
                    continue;
                long used = total - d.TotalFreeSpace;
                string? model = _deviceModels.GetValueOrDefault(d.Name);
                mounts.Add(new MountUsage(
                    d.Name, d.DriveFormat, total, used,
                    Math.Round(100.0 * used / total, 1), model));
            }
            catch
            {
                // a mount may vanish or be unreadable between enumeration and stat
            }
        }
        return [.. mounts];
    }

    /// <summary>
    /// Build the mount-point → backing-disk-model map by joining <c>/proc/self/mountinfo</c>
    /// (mount-point → <c>/dev</c> node) onto <c>/sys/block/&lt;disk&gt;/device/model</c>. A mount
    /// with no resolvable model (e.g. LVM/device-mapper) maps to <c>null</c> — never invented.
    /// </summary>
    internal static Dictionary<string, string?> BuildDeviceModels(string mountInfoPath, string sysBlockRoot)
    {
        var map = new Dictionary<string, string?>(StringComparer.Ordinal);

        string? content;
        try { content = File.Exists(mountInfoPath) ? File.ReadAllText(mountInfoPath) : null; }
        catch { content = null; }
        if (content is null)
            return map;

        foreach (var (mountPoint, devNode) in ParseMountInfo(content))
        {
            string? disk = NormalizePartitionToDisk(devNode, sysBlockRoot);
            map[mountPoint] = disk is null ? null : ReadDiskModel(sysBlockRoot, disk);
        }

        return map;
    }

    /// <summary>
    /// Parse <c>/proc/self/mountinfo</c> into <c>(mountPoint, /dev node)</c> pairs, keeping only
    /// real block-backed mounts (source starts with <c>/dev/</c>). The mountinfo line has an
    /// optional variable-length field set terminated by a literal <c>-</c>; the fs-type is the
    /// first token after it and the mount source (<c>/dev/...</c>) is the second.
    /// </summary>
    internal static IEnumerable<(string MountPoint, string DevNode)> ParseMountInfo(string content)
    {
        foreach (var line in content.Split('\n'))
        {
            if (line.Length == 0)
                continue;

            // Pre-separator fields: 5th (index 4) is the mount point.
            int dash = line.IndexOf(" - ", StringComparison.Ordinal);
            if (dash < 0)
                continue;

            var pre = line.AsSpan(0, dash).ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (pre.Length < 5)
                continue;
            string mountPoint = pre[4];

            // Post-separator: [fstype, source, super-opts]. Source is the /dev node.
            var post = line[(dash + 3)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (post.Length < 2)
                continue;
            string source = post[1];

            if (source.StartsWith("/dev/", StringComparison.Ordinal))
                yield return (mountPoint, source);
        }
    }

    /// <summary>
    /// Normalize a <c>/dev</c> partition node to its whole-disk name under
    /// <c>/sys/block</c>. If the node already names a whole disk (its <c>/sys/block</c> dir
    /// exists) it is returned as-is. Otherwise: nvme/mmcblk use a <c>p&lt;N&gt;</c> partition
    /// <em>infix</em> (<c>nvme0n1p2</c> → <c>nvme0n1</c>); sd/vd/hd just append digits
    /// (<c>sda1</c> → <c>sda</c>). Returns <c>null</c> when no <c>/sys/block</c> disk resolves
    /// (e.g. <c>/dev/mapper/*</c> LVM).
    /// </summary>
    internal static string? NormalizePartitionToDisk(string devNode, string sysBlockRoot)
    {
        string name = Path.GetFileName(devNode);
        if (name.Length == 0)
            return null;

        // Already a whole disk?
        if (Directory.Exists(Path.Combine(sysBlockRoot, name)))
            return name;

        // nvme0n1p2 / mmcblk0p1 -> strip the p<N> partition infix.
        var pInfix = NvmePartitionRegex().Match(name);
        if (pInfix.Success)
        {
            string disk = pInfix.Groups[1].Value;
            return Directory.Exists(Path.Combine(sysBlockRoot, disk)) ? disk : null;
        }

        // sda1 / vdb3 / hda2 -> strip trailing digits.
        var trailing = TrailingDigitRegex().Match(name);
        if (trailing.Success)
        {
            string disk = trailing.Groups[1].Value;
            return Directory.Exists(Path.Combine(sysBlockRoot, disk)) ? disk : null;
        }

        return null;
    }

    /// <summary>Read <c>/sys/block/&lt;disk&gt;/device/model</c>, trimmed; <c>null</c> when absent.</summary>
    internal static string? ReadDiskModel(string sysBlockRoot, string disk)
    {
        string path = Path.Combine(sysBlockRoot, disk, "device", "model");
        try
        {
            if (!File.Exists(path))
                return null;
            string model = File.ReadAllText(path).Trim();
            return model.Length == 0 ? null : model;
        }
        catch
        {
            return null;
        }
    }

    // nvme<X>n<Y>p<Z> / mmcblk<X>p<Z> — capture the disk (everything before the p<N> infix).
    [GeneratedRegex(@"^((?:nvme\d+n\d+|mmcblk\d+))p\d+$")]
    private static partial Regex NvmePartitionRegex();

    // sda1 / vdb3 — capture the alpha disk before trailing partition digits.
    [GeneratedRegex(@"^([a-z]+)\d+$")]
    private static partial Regex TrailingDigitRegex();

    /// <summary>True if a mount of this fs type should be reported (not pseudo, not denied).</summary>
    internal static bool IncludeMount(string fmt, IReadOnlySet<string> extraDeny)
        => !IsPseudoFs(fmt) && !extraDeny.Contains(fmt);

    private DiskIo ReadIo()
    {
        long readSectors = 0, writeSectors = 0;
        foreach (var dir in Directory.GetDirectories("/sys/block"))
        {
            string name = Path.GetFileName(dir);
            if (name.StartsWith("loop", StringComparison.Ordinal) ||
                name.StartsWith("ram", StringComparison.Ordinal))
                continue;

            string statPath = Path.Combine(dir, "stat");
            if (!File.Exists(statPath))
                continue;

            var (r, w) = ParseBlockStat(File.ReadAllText(statPath));
            readSectors += r;
            writeSectors += w;
        }

        long now = Environment.TickCount64;
        double dt = _prevTicks == 0 ? 1.0 : Math.Max(1, now - _prevTicks) / 1000.0;
        long readBps = _prevTicks == 0 ? 0 : (long)((readSectors - _prevReadSectors) * 512 / dt);
        long writeBps = _prevTicks == 0 ? 0 : (long)((writeSectors - _prevWriteSectors) * 512 / dt);

        _prevReadSectors = readSectors;
        _prevWriteSectors = writeSectors;
        _prevTicks = now;

        return new DiskIo(Math.Max(0, readBps), Math.Max(0, writeBps));
    }

    /// <summary>Sectors read/written from a single <c>/sys/block/&lt;dev&gt;/stat</c> line ([2]=read, [6]=written).</summary>
    internal static (long ReadSectors, long WriteSectors) ParseBlockStat(string statLine)
    {
        var f = statLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (f.Length < 7)
            return (0, 0);
        return (long.Parse(f[2]), long.Parse(f[6]));
    }

    private static bool IsPseudoFs(string fmt) => fmt is
        "tmpfs" or "devtmpfs" or "ramfs" or "overlay" or "squashfs" or "proc" or
        "sysfs" or "cgroup" or "cgroup2" or "autofs" or "mqueue" or "debugfs" or
        "tracefs" or "devpts" or "configfs" or "fusectl" or "bpf" or "pstore" or
        "securityfs" or "hugetlbfs" or "binfmt_misc" or "nsfs" or "fuse.portal" or
        "efivarfs";
}
