using System.Buffers.Binary;
using System.Runtime.InteropServices;
using TheKrystalShip.KGSM.Monitor.Interop;

namespace TheKrystalShip.KGSM.Monitor.Sampling;

/// <summary>
/// Reads cumulative per-server network bytes from the pinned eBPF map produced by the passive
/// <c>cgroup/skb</c> meter (<c>bpf/net_meter.bpf.c</c>, attached to <c>kgsm.slice</c> by
/// <c>deploy/net-meter-setup.sh</c>). Given an instance's resolved cgroup directory it computes
/// the cgroup id (<c>stat().st_ino</c> in cgroup v2 — the same number the kernel reports to the
/// BPF program via <c>bpf_skb_cgroup_id</c>), looks it up in the map, and returns the cumulative
/// <c>{rxBytes, txBytes}</c>. <see cref="CgroupSampler"/> turns the cumulative totals into
/// <c>RxBps</c>/<c>TxBps</c> rates against the previous sample, exactly like the I/O counters.
/// <para>
/// <b>Honest null, never a fabricated 0.</b> The map fd is opened once (the pin path) and cached.
/// When the meter isn't set up — the pin is absent (<c>ENOENT</c>) or <c>cap_bpf</c> wasn't
/// granted (<c>EPERM</c>; this host sets <c>unprivileged_bpf_disabled=2</c>) — <see cref="TryRead"/>
/// returns <c>null</c> and the caller emits <c>null</c> (rendered "—"), logging the unavailability
/// once. A per-cgroup miss (the row doesn't exist yet because no traffic crossed, or because the
/// cgroup is outside <c>kgsm.slice</c> so the meter never saw it) also returns <c>null</c>: we
/// will not report 0 bytes for a cgroup the kernel has no counter for, since we can't distinguish
/// "metered but idle" from "not metered" — null is the honest answer for both. Once any packet is
/// attributed the row appears and real bytes flow.
/// </para>
/// <para>
/// State (the cached fd, the open-attempted/unavailable flags) is mutated only on the single host
/// sampling thread (<see cref="CgroupSampler.Sample"/>), so no lock is needed.
/// </para>
/// </summary>
internal sealed partial class NetworkCgroupSource
{
    /// <summary>The FIXED pin path (Contract A). The setup script pins the map here.</summary>
    internal const string DefaultPinPath = "/sys/fs/bpf/kgsm/net_metrics";

    private readonly string _pinPath;
    private readonly ILogger? _log;

    private int _mapFd = -1;            // cached map fd once opened; -1 = not open
    private bool _loggedUnavailable;    // log the "meter unavailable" line at most once

    internal NetworkCgroupSource(ILogger? log = null, string pinPath = DefaultPinPath)
    {
        _log = log;
        _pinPath = pinPath;
    }

    /// <summary>True once the pinned map has been opened — i.e. the meter is set up and readable.</summary>
    internal bool Available => _mapFd >= 0;

    /// <summary>
    /// Cumulative <c>{rxBytes, txBytes}</c> for the cgroup at <paramref name="cgroupDir"/>, or
    /// <c>null</c> when the meter is unavailable or has no row for that cgroup (see class remarks).
    /// </summary>
    internal (long RxBytes, long TxBytes)? TryRead(string cgroupDir)
    {
        int fd = EnsureMap();
        if (fd < 0)
            return null;

        if (!TryCgroupId(cgroupDir, out ulong id))
            return null;

        Span<byte> buf = stackalloc byte[Bpf.ValueSize];
        if (!Bpf.MapLookupElem(fd, id, buf))
            return null; // key absent (no counted traffic / cgroup outside kgsm.slice) — honest null

        long rx = (long)BinaryPrimitives.ReadUInt64LittleEndian(buf);          // value.rx_bytes (offset 0)
        long tx = (long)BinaryPrimitives.ReadUInt64LittleEndian(buf[8..]);     // value.tx_bytes (offset 8)
        return (rx, tx);                                                       // rx_pkts/tx_pkts reserved (offsets 16/24)
    }

    /// <summary>
    /// Open the pinned map once and cache its fd. Re-attempted each tick while closed (one cheap
    /// syscall) so the meter is picked up if its setup ran after the monitor started; the
    /// "unavailable" line logs only on the first failure to avoid 1 Hz spam.
    /// </summary>
    private int EnsureMap()
    {
        if (_mapFd >= 0)
            return _mapFd;

        int fd = Bpf.ObjGet(_pinPath, out int error);
        if (fd < 0)
        {
            if (!_loggedUnavailable)
            {
                _loggedUnavailable = true;
                _log?.LogInformation(
                    "per-server network meter unavailable (bpf pin {Pin}, errno {Errno}); RxBps/TxBps will be null. " +
                    "Run deploy/net-meter-setup.sh (needs the eBPF meter attached + cap_bpf).",
                    _pinPath, error);
            }
            return -1;
        }

        _mapFd = fd;
        _log?.LogInformation("per-server network meter ready (bpf map pin {Pin})", _pinPath);
        return _mapFd;
    }

    // ── cgroup id == cgroup dir inode (cgroup v2) ──────────────────────────────────────────────
    // No managed API exposes st_ino, so stat() via the raw x86-64 syscall (__NR_stat = 4) — same
    // linux-x64 assumption as Bpf.cs, and avoids any glibc symbol-versioning of the stat symbol.
    // We read st_ino at offset 8 (8 bytes) of struct stat; the kernel fills the full 144-byte
    // struct on x86-64, so we give it a buffer that large.
    private const long SYS_stat = 4;        // x86-64
    private const int StatBufSize = 144;    // sizeof(struct stat) on x86-64
    private const int StInoOffset = 8;      // st_dev (8) then st_ino (8)

    [LibraryImport("libc", EntryPoint = "syscall", SetLastError = true)]
    private static unsafe partial long syscall(long number, byte* path, byte* statbuf);

    private static unsafe bool TryCgroupId(string cgroupDir, out ulong id)
    {
        id = 0;
        if (string.IsNullOrEmpty(cgroupDir))
            return false;

        int byteCount = System.Text.Encoding.UTF8.GetByteCount(cgroupDir);
        Span<byte> path = byteCount < 256 ? stackalloc byte[byteCount + 1] : new byte[byteCount + 1];
        System.Text.Encoding.UTF8.GetBytes(cgroupDir, path);
        path[byteCount] = 0;

        Span<byte> statbuf = stackalloc byte[StatBufSize];
        fixed (byte* p = path)
        fixed (byte* s = statbuf)
        {
            if (syscall(SYS_stat, p, s) != 0)
                return false; // dir vanished mid-tick (teardown race) — caller treats as null
        }

        id = BinaryPrimitives.ReadUInt64LittleEndian(statbuf[StInoOffset..]);
        return true;
    }
}
