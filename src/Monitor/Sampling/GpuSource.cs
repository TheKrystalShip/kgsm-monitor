using System.Runtime.InteropServices;
using System.Text;

using TheKrystalShip.KGSM.Monitor.Contracts;

namespace TheKrystalShip.KGSM.Monitor.Sampling;

/// <summary>
/// GPU devices and their compute contexts, read from NVML (<c>libnvidia-ml.so.1</c>) directly.
/// </summary>
/// <remarks>
/// <para>
/// <b>The library is optional and is bound lazily.</b> A host with no card, no driver, or no NVML
/// reports <see cref="Sample"/> as null and never tries again — a missing GPU is an ordinary state, not
/// a fault, and the daemon must start and run identically without one. .NET binds a p/invoke on first
/// call rather than at load, so the failure surfaces here where it can be caught and cached.
/// </para>
/// <para>
/// <b>Nothing here spawns a process.</b> <c>nvidia-smi</c> reads the same library; shelling out to it
/// would cost a fork per tick to obtain what these calls return directly. No CUDA runtime is involved
/// and no accounting mode is enabled (that is a separate, root-requiring API this does not need), so
/// the whole source runs unprivileged.
/// </para>
/// <para>
/// <b>Utilisation is sampled; memory is not.</b> Device memory is a figure the driver always has.
/// Compute utilisation comes from a windowed sampler, so a process that did no work in the window is
/// <em>absent from the result</em> rather than reported as zero — which is why every utilisation field
/// on the wire is nullable. Reporting an idle process as 0 % would be indistinguishable from measuring
/// it and finding it idle, and only one of those is true.
/// </para>
/// </remarks>
public sealed partial class GpuSource(ILogger<GpuSource>? logger = null) : IDisposable
{
    private const string Nvml = "libnvidia-ml.so.1";

    // nvmlReturn_t values this source distinguishes. Everything else is "could not read", which yields
    // null rather than a substituted value.
    private const int Success = 0;
    private const int NotFound = 6;          // no samples in the window — idleness, not an error
    private const int InsufficientSize = 7;  // the buffer was too small; the count tells us how big

    private const uint TemperatureGpu = 0;   // NVML_TEMPERATURE_GPU

    // nvmlTemperatureThresholds_t. The driver holds several lines; these are the two worth reporting —
    // the temperature the part is rated to run to, and the one at which the driver cuts power.
    // nvmlTemperatureThresholds_t, in the header's order. GPU_MAX is 3; 4 is ACOUSTIC_MIN, the bottom of
    // the fan curve, which is a plausible temperature and therefore passes every gate while meaning
    // something else entirely.
    private const uint ThresholdShutdown = 0;     // NVML_TEMPERATURE_THRESHOLD_SHUTDOWN
    private const uint ThresholdMaxOperating = 3; // NVML_TEMPERATURE_THRESHOLD_GPU_MAX

    // Buffer sizes chosen so the ordinary case is one call. Both grow on InsufficientSize, so these are
    // a starting guess and never a cap.
    private const int InitialProcessBuffer = 64;
    private const int InitialSampleBuffer = 256;

    private bool _bound;
    private bool _usable;
    private nint[] _devices = [];
    private GpuDevice[] _identity = [];
    private bool _disposed;

    // NVML reports per-process utilisation over a caller-supplied window. Tracking the previous tick's
    // wall clock keeps that window flush against the last sample: no gap that would drop a busy process,
    // no overlap that would count the same work twice.
    private long _lastSampleMicros;

    /// <summary>
    /// The devices and compute contexts on this host, or null when the host has no readable GPU.
    /// </summary>
    public GpuMetrics? Sample()
    {
        if (!Bind()) return null;

        var devices = new List<GpuDevice>(_devices.Length);
        var processes = new List<GpuProcess>();

        long nowMicros = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1000L;
        // First tick has no previous mark; a two-second reach-back gives the sampler something to answer
        // with instead of an empty window.
        long since = _lastSampleMicros > 0 ? _lastSampleMicros : nowMicros - 2_000_000L;

        for (int i = 0; i < _devices.Length; i++)
        {
            nint handle = _devices[i];
            GpuDevice identity = _identity[i];

            if (!TryMemory(handle, out long memTotal, out long memUsed))
                continue;   // a device that cannot report memory is not described at all

            devices.Add(identity with
            {
                MemTotalBytes = memTotal,
                MemUsedBytes = memUsed,
                SmPct = TryUtilization(handle),
                TempC = TryTemperature(handle),
                TempLimitC = TryThreshold(handle, ThresholdMaxOperating),
                TempShutdownC = TryThreshold(handle, ThresholdShutdown),
                PowerW = TryPower(handle),
                PowerCapW = TryPowerCap(handle),
            });

            CollectProcesses(handle, i, since, processes);
        }

        _lastSampleMicros = nowMicros;

        return devices.Count == 0 ? null : new GpuMetrics([.. devices], [.. processes]);
    }

    /// <summary>
    /// Merge every compute context on a device into the frame, joined to the unit that owns it.
    /// </summary>
    /// <remarks>
    /// Memory and utilisation come from two different NVML calls over two different populations: every
    /// live context has memory, only a context that did work in the window has utilisation. The memory
    /// call is therefore the roster, and utilisation is attached where it exists — a process present in
    /// the utilisation result but absent from the memory roster has already exited and is dropped.
    /// </remarks>
    private void CollectProcesses(nint handle, int deviceIndex, long since, List<GpuProcess> into)
    {
        Dictionary<int, double> utilisation = ReadUtilisation(handle, since);

        foreach ((int pid, long mem) in ReadComputeProcesses(handle))
        {
            into.Add(new GpuProcess(
                DeviceIndex: deviceIndex,
                Pid: pid,
                ProcessName: ProcessName(pid),
                Unit: UnitForPid(pid),
                MemBytes: mem,
                SmPct: utilisation.TryGetValue(pid, out double sm) ? sm : null));
        }
    }

    private List<(int Pid, long MemBytes)> ReadComputeProcesses(nint handle)
    {
        var found = new List<(int, long)>();
        var buffer = new NvmlProcessInfo[InitialProcessBuffer];

        for (int attempt = 0; attempt < 3; attempt++)
        {
            uint count = (uint)buffer.Length;
            int rc;
            try
            {
                rc = nvmlDeviceGetComputeRunningProcesses_v3(handle, ref count, ref buffer[0]);
            }
            catch (EntryPointNotFoundException)
            {
                // An older driver without the v3 entry point. Device metrics stay; per-process
                // attribution is simply absent rather than guessed at from a different shape.
                logger?.LogDebug("gpu: this driver exposes no v3 compute-process query");
                return found;
            }

            if (rc == InsufficientSize)
            {
                buffer = new NvmlProcessInfo[Math.Max(count, (uint)buffer.Length * 2)];
                continue;
            }
            if (rc != Success) return found;

            for (int i = 0; i < count; i++)
                found.Add(((int)buffer[i].Pid, (long)buffer[i].UsedGpuMemory));
            return found;
        }

        return found;
    }

    /// <summary>Per-process compute utilisation over the window, keyed by pid. Empty when none was busy.</summary>
    private Dictionary<int, double> ReadUtilisation(nint handle, long since)
    {
        var result = new Dictionary<int, double>();
        var buffer = new NvmlProcessUtilizationSample[InitialSampleBuffer];

        for (int attempt = 0; attempt < 3; attempt++)
        {
            uint count = (uint)buffer.Length;
            int rc = nvmlDeviceGetProcessUtilization(handle, ref buffer[0], ref count, (ulong)since);

            if (rc == InsufficientSize)
            {
                buffer = new NvmlProcessUtilizationSample[Math.Max(count, (uint)buffer.Length * 2)];
                continue;
            }
            // NotFound is the ordinary answer for an idle card: nothing was sampled, so nothing is known.
            if (rc != Success) return result;

            // The window can hold several samples per process. The most recent one describes the
            // interval the frame covers, so a later timestamp replaces an earlier one.
            var newest = new Dictionary<int, ulong>();
            for (int i = 0; i < count; i++)
            {
                NvmlProcessUtilizationSample s = buffer[i];
                if (s.Pid == 0) continue;
                int pid = (int)s.Pid;
                if (newest.TryGetValue(pid, out ulong seen) && seen >= s.TimeStamp) continue;
                newest[pid] = s.TimeStamp;
                result[pid] = s.SmUtil;
            }
            return result;
        }

        return result;
    }

    /// <summary>
    /// The systemd unit a pid belongs to, from its cgroup v2 line, or null when it is in none.
    /// </summary>
    /// <remarks>
    /// The unified line is <c>0::/system.slice/some.service</c>. The owning unit is the last path
    /// segment naming one, which keeps a process spawned into a child cgroup attributed to the unit that
    /// spawned it rather than to the anonymous leaf directory it happens to live in.
    /// </remarks>
    internal static string? UnitForPid(int pid)
    {
        string? line;
        try
        {
            line = File.ReadLines($"/proc/{pid}/cgroup").FirstOrDefault(l => l.StartsWith("0::", StringComparison.Ordinal));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;   // the process exited between enumeration and this read
        }

        return UnitFromCgroupLine(line);
    }

    /// <summary>Pure over the cgroup line, so the segment rules are testable without a process.</summary>
    internal static string? UnitFromCgroupLine(string? line)
    {
        if (string.IsNullOrEmpty(line)) return null;

        int sep = line.IndexOf("::", StringComparison.Ordinal);
        string path = sep >= 0 ? line[(sep + 2)..] : line;

        string? unit = null;
        foreach (string segment in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment.EndsWith(".service", StringComparison.Ordinal) ||
                segment.EndsWith(".scope", StringComparison.Ordinal))
                unit = segment;
        }
        return unit;
    }

    /// <summary>
    /// The process's name from <c>/proc/&lt;pid&gt;/comm</c>, or a placeholder when it has already exited.
    /// </summary>
    /// <remarks>
    /// <c>comm</c> is world-readable and needs no privilege, unlike resolving <c>exe</c>. It is truncated
    /// to 15 characters by the kernel, which is a limit of the source rather than something to paper over.
    /// </remarks>
    private static string ProcessName(int pid)
    {
        try
        {
            return File.ReadAllText($"/proc/{pid}/comm").Trim();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return "(exited)";
        }
    }

    // ── device fields ────────────────────────────────────────────────────────────────────────────
    // Each is independently nullable: a card that reports memory but no power draw describes what it
    // knows and leaves the rest unset.

    private static bool TryMemory(nint handle, out long total, out long used)
    {
        total = used = 0;
        if (nvmlDeviceGetMemoryInfo(handle, out NvmlMemory mem) != Success) return false;
        total = (long)mem.Total;
        used = (long)mem.Used;
        return true;
    }

    private static double? TryUtilization(nint handle) =>
        nvmlDeviceGetUtilizationRates(handle, out NvmlUtilization u) == Success ? u.Gpu : null;

    private static double? TryTemperature(nint handle) =>
        nvmlDeviceGetTemperature(handle, TemperatureGpu, out uint c) == Success ? c : null;

    // A threshold the driver does not implement for this part comes back as an error or as a figure no
    // silicon has — gated the same way a hwmon limit is, so one rule decides what counts as a setting.
    private static double? TryThreshold(nint handle, uint which) =>
        nvmlDeviceGetTemperatureThreshold(handle, which, out uint c) == Success
            ? HwmonCatalog.PlausibleLimit(c)
            : null;

    private static double? TryPower(nint handle) =>
        nvmlDeviceGetPowerUsage(handle, out uint mw) == Success ? mw / 1000.0 : null;

    private static double? TryPowerCap(nint handle) =>
        nvmlDeviceGetEnforcedPowerLimit(handle, out uint mw) == Success ? mw / 1000.0 : null;

    // ── binding ──────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Initialise NVML and enumerate devices once. A failure is remembered so a host without a GPU pays
    /// one failed bind for the process lifetime rather than one per tick.
    /// </summary>
    private bool Bind()
    {
        if (_bound) return _usable;
        _bound = true;

        try
        {
            if (nvmlInit_v2() != Success)
            {
                logger?.LogInformation("gpu: NVML declined to initialise; no GPU metrics on this host");
                return false;
            }
            if (nvmlDeviceGetCount_v2(out uint count) != Success || count == 0)
            {
                logger?.LogInformation("gpu: NVML reports no devices");
                return false;
            }

            var handles = new List<nint>((int)count);
            var identity = new List<GpuDevice>((int)count);
            for (uint i = 0; i < count; i++)
            {
                if (nvmlDeviceGetHandleByIndex_v2(i, out nint handle) != Success) continue;
                handles.Add(handle);
                identity.Add(new GpuDevice(
                    Index: (int)i,
                    Name: ReadString(handle, nvmlDeviceGetName) ?? "(unknown)",
                    Uuid: ReadString(handle, nvmlDeviceGetUUID) ?? $"index-{i}",
                    MemTotalBytes: 0, MemUsedBytes: 0,
                    SmPct: null, TempC: null, PowerW: null, PowerCapW: null,
                    TempLimitC: null, TempShutdownC: null));
            }

            _devices = [.. handles];
            _identity = [.. identity];
            _usable = _devices.Length > 0;

            if (_usable)
                logger?.LogInformation("gpu: {Count} device(s) — {Names}",
                    _devices.Length, string.Join(", ", _identity.Select(d => d.Name)));

            return _usable;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            // No driver on this host. Expected, and the reason every GPU field is optional.
            logger?.LogInformation("gpu: {Library} is not present; no GPU metrics on this host", Nvml);
            return false;
        }
    }

    private delegate int StringQuery(nint device, ref byte buffer, uint length);

    /// <summary>Read one of NVML's fixed-buffer string fields, trimmed at its NUL terminator.</summary>
    private static string? ReadString(nint handle, StringQuery query)
    {
        var buffer = new byte[96];
        if (query(handle, ref buffer[0], (uint)buffer.Length) != Success) return null;

        int end = Array.IndexOf(buffer, (byte)0);
        if (end < 0) end = buffer.Length;
        return end == 0 ? null : Encoding.UTF8.GetString(buffer, 0, end);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (!_usable) return;
        try { nvmlShutdown(); }
        catch (DllNotFoundException) { /* it was never bound */ }
    }

    // ── NVML ─────────────────────────────────────────────────────────────────────────────────────
    // Plain C ABI, so source-generated marshalling covers it with no reflection and nothing for the AOT
    // compiler to trim away. Array arguments are passed as a `ref` to the first element, which is what
    // the C signature wants and keeps this file free of hand-written unsafe code.

    [StructLayout(LayoutKind.Sequential)]
    private struct NvmlMemory
    {
        public ulong Total;
        public ulong Free;
        public ulong Used;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NvmlUtilization
    {
        public uint Gpu;
        public uint Memory;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NvmlProcessInfo
    {
        public uint Pid;
        public ulong UsedGpuMemory;
        public uint GpuInstanceId;
        public uint ComputeInstanceId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NvmlProcessUtilizationSample
    {
        public uint Pid;
        public ulong TimeStamp;
        public uint SmUtil;
        public uint MemUtil;
        public uint EncUtil;
        public uint DecUtil;
    }

    [LibraryImport(Nvml)] private static partial int nvmlInit_v2();
    [LibraryImport(Nvml)] private static partial int nvmlShutdown();
    [LibraryImport(Nvml)] private static partial int nvmlDeviceGetCount_v2(out uint count);
    [LibraryImport(Nvml)] private static partial int nvmlDeviceGetHandleByIndex_v2(uint index, out nint device);
    [LibraryImport(Nvml)] private static partial int nvmlDeviceGetName(nint device, ref byte name, uint length);
    [LibraryImport(Nvml)] private static partial int nvmlDeviceGetUUID(nint device, ref byte uuid, uint length);
    [LibraryImport(Nvml)] private static partial int nvmlDeviceGetMemoryInfo(nint device, out NvmlMemory memory);
    [LibraryImport(Nvml)] private static partial int nvmlDeviceGetUtilizationRates(nint device, out NvmlUtilization utilization);
    [LibraryImport(Nvml)] private static partial int nvmlDeviceGetTemperature(nint device, uint sensorType, out uint temp);
    [LibraryImport(Nvml)] private static partial int nvmlDeviceGetTemperatureThreshold(nint device, uint thresholdType, out uint temp);
    [LibraryImport(Nvml)] private static partial int nvmlDeviceGetPowerUsage(nint device, out uint milliwatts);
    [LibraryImport(Nvml)] private static partial int nvmlDeviceGetEnforcedPowerLimit(nint device, out uint milliwatts);

    [LibraryImport(Nvml)]
    private static partial int nvmlDeviceGetComputeRunningProcesses_v3(nint device, ref uint infoCount, ref NvmlProcessInfo infos);

    [LibraryImport(Nvml)]
    private static partial int nvmlDeviceGetProcessUtilization(nint device, ref NvmlProcessUtilizationSample utilization, ref uint count, ulong lastSeenTimeStamp);
}
