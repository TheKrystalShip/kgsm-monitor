using TheKrystalShip.KGSM.Monitor.Contracts;

namespace TheKrystalShip.KGSM.Monitor.Sampling;

/// <summary>
/// Disk usage (per mount, via <see cref="DriveInfo"/>) and aggregate I/O
/// throughput. Throughput sums whole-disk counters from <c>/sys/block/*/stat</c>
/// — enumerating <c>/sys/block</c> yields only whole disks, never partitions, so
/// there is no double-counting. Sectors are 512 bytes by convention. Pseudo
/// filesystems are always filtered; <paramref name="mountFsDeny"/> hides additional
/// fs types.
/// </summary>
public sealed class DiskSource(IReadOnlySet<string> mountFsDeny)
{
    private readonly IReadOnlySet<string> _mountFsDeny = mountFsDeny;
    private long _prevReadSectors, _prevWriteSectors, _prevTicks;

    public DiskMetrics Sample()
    {
        return new DiskMetrics(ReadMounts(_mountFsDeny), ReadIo());
    }

    private static MountUsage[] ReadMounts(IReadOnlySet<string> deny)
    {
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
                mounts.Add(new MountUsage(
                    d.Name, d.DriveFormat, total, used,
                    Math.Round(100.0 * used / total, 1)));
            }
            catch
            {
                // a mount may vanish or be unreadable between enumeration and stat
            }
        }
        return [.. mounts];
    }

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
