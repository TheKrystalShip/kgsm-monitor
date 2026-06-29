using System.Runtime.InteropServices;
using System.Text;

namespace TheKrystalShip.KGSM.Monitor.Interop;

/// <summary>
/// Minimal, Native-AOT-safe wrapper over the <c>bpf()</c> syscall — just the two ops the
/// monitor needs to READ a pinned map: <see cref="ObjGet"/> (get a map fd from a bpffs pin
/// path) and <see cref="MapLookupElem"/> (read a value by key). No libbpf dependency, no
/// reflection: the call goes through <c>[LibraryImport]</c> on libc's <c>syscall</c>, so the
/// source-generated marshalling stub is trim/AOT-clean (no IL2026/IL3050).
/// <para>
/// <b>x86-64 only.</b> The syscall number (<c>__NR_bpf</c> = 321) and the <c>bpf_attr</c> field
/// offsets are the x86-64 ABI; the monitor publishes <c>linux-x64</c>, so this is sound. The
/// <c>bpf_attr</c> union is passed as a per-op struct sized to exactly the fields that op reads:
/// the kernel zero-fills the rest and verifies trailing bytes are zero, so a per-op layout is
/// the canonical raw-bpf() pattern (what libbpf does internally).
/// </para>
/// <para>
/// <b>Honest failure, never throws on the hot path.</b> Every op returns a status the caller
/// turns into "unavailable" (a <c>null</c> metric). A missing pin (<c>ENOENT</c>) or a missing
/// capability (<c>EPERM</c>, since this host sets <c>unprivileged_bpf_disabled=2</c>) is a normal
/// "meter not set up" condition, not an error to surface to a user.
/// </para>
/// </summary>
internal static partial class Bpf
{
    // x86-64 syscall number for bpf(). (linux-x64 publish — see class remarks.)
    private const long SYS_bpf = 321;

    // bpf command codes (uapi/linux/bpf.h, stable ABI).
    private const int BPF_MAP_LOOKUP_ELEM = 1;
    private const int BPF_OBJ_GET = 7;

    /// <summary>Size of the map value the monitor reads: { rx_bytes, tx_bytes, rx_pkts, tx_pkts } = 4 × u64.</summary>
    internal const int ValueSize = 32;

    // glibc's variadic `long syscall(long number, ...)`. On x86-64 the first 6 integer/pointer
    // arguments are passed in registers regardless of the variadic prototype, and glibc's
    // assembly wrapper ignores AL (vector-arg count), so a fixed 4-arg prototype matches the
    // ABI: number->rax, then cmd/attr/size become the kernel's (cmd, uattr, size). Pointer args
    // keep [LibraryImport] blittable (no marshalling) → AOT-clean.
    [LibraryImport("libc", EntryPoint = "syscall", SetLastError = true)]
    private static unsafe partial long syscall(long number, int cmd, void* attr, uint size);

    // ── bpf_attr sub-layouts (per op). Explicit offsets mirror uapi/linux/bpf.h with
    //    __aligned_u64 (8-byte alignment); the kernel zero-fills the rest of the union. ──

    // BPF_OBJ_* : { __aligned_u64 pathname; __u32 bpf_fd; __u32 file_flags; }
    [StructLayout(LayoutKind.Explicit, Size = 16)]
    private struct ObjAttr
    {
        [FieldOffset(0)] public ulong pathname;    // user pointer to the NUL-terminated pin path
        [FieldOffset(8)] public uint bpf_fd;       // unused for OBJ_GET
        [FieldOffset(12)] public uint file_flags;  // 0 = default (RW) access
    }

    // BPF_MAP_*_ELEM : { __u32 map_fd; __aligned_u64 key; __aligned_u64 value; __u64 flags; }
    [StructLayout(LayoutKind.Explicit, Size = 32)]
    private struct MapElemAttr
    {
        [FieldOffset(0)] public uint map_fd;
        [FieldOffset(8)] public ulong key;         // user pointer to the key
        [FieldOffset(16)] public ulong value;      // user pointer to the value buffer (out)
        [FieldOffset(24)] public ulong flags;
    }

    /// <summary>
    /// Get a file descriptor for the map (or program) pinned at <paramref name="pinPath"/> in
    /// bpffs. Returns the new fd (≥ 0) on success, or <c>-1</c> on failure (the pin is absent,
    /// the path isn't traversable, or the process lacks <c>cap_bpf</c>); inspect
    /// <paramref name="error"/> for the errno. Never throws.
    /// </summary>
    internal static unsafe int ObjGet(string pinPath, out int error)
    {
        error = 0;
        // NUL-terminated UTF-8 path in a pinned managed buffer.
        int byteCount = Encoding.UTF8.GetByteCount(pinPath);
        Span<byte> path = byteCount < 256 ? stackalloc byte[byteCount + 1] : new byte[byteCount + 1];
        Encoding.UTF8.GetBytes(pinPath, path);
        path[byteCount] = 0;

        fixed (byte* p = path)
        {
            ObjAttr attr = default;
            attr.pathname = (ulong)(nuint)p;
            long fd = syscall(SYS_bpf, BPF_OBJ_GET, &attr, (uint)sizeof(ObjAttr));
            if (fd < 0)
            {
                error = Marshal.GetLastPInvokeError();
                return -1;
            }
            return (int)fd;
        }
    }

    /// <summary>
    /// Look up the value for <paramref name="key"/> in the map referenced by
    /// <paramref name="mapFd"/>, writing it into <paramref name="value"/> (must be
    /// <see cref="ValueSize"/> bytes). Returns <c>true</c> on a hit; <c>false</c> when the key is
    /// absent (<c>ENOENT</c> — e.g. that cgroup has no counted traffic yet) or on any error.
    /// Never throws.
    /// </summary>
    internal static unsafe bool MapLookupElem(int mapFd, ulong key, Span<byte> value)
    {
        if (mapFd < 0 || value.Length < ValueSize)
            return false;

        ulong k = key;
        fixed (byte* v = value)
        {
            MapElemAttr attr = default;
            attr.map_fd = (uint)mapFd;
            attr.key = (ulong)(nuint)(&k);
            attr.value = (ulong)(nuint)v;
            attr.flags = 0;
            return syscall(SYS_bpf, BPF_MAP_LOOKUP_ELEM, &attr, (uint)sizeof(MapElemAttr)) == 0;
        }
    }
}
