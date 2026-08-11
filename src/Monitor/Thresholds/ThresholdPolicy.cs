using System.Text.Json.Serialization;
using TheKrystalShip.KGSM.Monitor.Contracts;

namespace TheKrystalShip.KGSM.Monitor.Thresholds;

/// <summary>
/// The closed set of <see cref="Snapshot"/> fields a threshold rule may watch. Closed deliberately:
/// thresholding a field this enum does not name is a compile error, never a runtime guess at what the
/// daemon honestly carries. <c>Host*</c> members are host-scope; <c>Server*</c> members yield one
/// observation per <see cref="Snapshot.Servers"/> row — see <see cref="ThresholdMetrics.IsHostScope"/>.
/// <para>
/// Serialized by NAME, so a policy file and a <c>PUT</c> body both say <c>"HostTempC"</c>. An unknown name
/// fails deserialization, which is what turns "this build has never heard of that metric" into a rejected
/// request rather than a rule that silently watches whatever member happens to be zero.
/// </para>
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<ThresholdMetric>))]
public enum ThresholdMetric
{
    /// <summary>Host RAM, percent (<c>Snapshot.Mem.UsedPct</c>).</summary>
    HostMemUsedPct,

    /// <summary>Host swap, percent (<c>100 * Mem.SwapUsedKb / Mem.SwapTotalKb</c>) — not evaluable
    /// (no swap configured on this host) when <c>SwapTotalKb == 0</c>.</summary>
    HostSwapUsedPct,

    /// <summary>Per-mount disk usage, percent (<c>Snapshot.Disk.Mounts[].UsedPct</c>) — fans out: one
    /// observation per mount, <see cref="MetricObservation.RefKey"/> = the mount path.</summary>
    HostDiskUsedPct,

    /// <summary>5-minute load average per core (<c>Cpu.Load.Five / Cpu.Info.Cores</c>) — not evaluable
    /// when <c>Snapshot.Cpu.Info</c> (or its <c>Cores</c>) is null.</summary>
    HostLoadPerCore,

    /// <summary>hwmon sensor temperature, °C (<c>Snapshot.Sensors[].ValueC</c>) — fans out: one
    /// observation per sensor (none when the array is empty), <see cref="MetricObservation.RefKey"/> =
    /// the chip/label.</summary>
    HostTempC,

    /// <summary>Per-server resident memory, bytes (<c>ServerMetrics.MemBytes</c>).</summary>
    ServerMemBytes,

    /// <summary>Per-server CPU, percent of ONE core (<c>ServerMetrics.CpuPctCore</c>) — can exceed 100
    /// on a multi-threaded server.</summary>
    ServerCpuPctCore,

    /// <summary>Per-server live process/thread count (<c>ServerMetrics.Pids</c>).</summary>
    ServerPids,
}

/// <summary>
/// One "&gt;=" comparison against one <see cref="ThresholdMetric"/> ("too high" is the only direction the
/// default policy needs), with the dwells and deadband that stop a passing spike from being reported as a
/// condition.
/// </summary>
/// <param name="Key">Stable rule key, carried on every condition this rule produces and used in the
/// episode id. It is what an operator recognises the rule by, so it survives edits to everything else.</param>
/// <param name="Metric">Which snapshot field this rule watches.</param>
/// <param name="Warn">Value &gt;= <see cref="Warn"/> is in the <c>warn</c> band.</param>
/// <param name="Danger">Value &gt;= <see cref="Danger"/> is in the <c>danger</c> band; <see langword="null"/>
/// = warn-only, this rule never escalates.</param>
/// <param name="FireForSec">Dwell-to-fire: the value must stay at/above <see cref="Warn"/> this long before
/// a condition opens. Zero means the first reading over the line opens one.</param>
/// <param name="ClearForSec">Dwell-to-clear: the value must stay below the clear threshold
/// (<see cref="Warn"/> − <see cref="ClearMargin"/>) this long before an open condition closes.</param>
/// <param name="ClearMargin">Hysteresis deadband: the value must drop this far below <see cref="Warn"/>
/// before the clear dwell even starts, so a value hovering right at the line never flaps.</param>
/// <param name="Enabled">Whether this rule is evaluated. A disabled rule is kept rather than dropped, so it
/// can be inspected and switched on without being retyped.</param>
public sealed record ThresholdRule(
    string Key,
    ThresholdMetric Metric,
    double Warn,
    double? Danger,
    int FireForSec,
    int ClearForSec,
    double ClearMargin,
    bool Enabled);

/// <summary>
/// The rule set this daemon evaluates. The baseline is <see cref="Default"/>, overridden wholesale by
/// whatever an operator has applied (see <c>PolicyStore</c>).
/// </summary>
/// <param name="Rules">The rule set, in no particular order.</param>
public sealed record MetricsThresholdPolicy(IReadOnlyList<ThresholdRule> Rules)
{
    /// <summary>Whether any rule is enabled — whether there is anything to evaluate at all.</summary>
    public bool AnyEnabled
    {
        get
        {
            for (int i = 0; i < Rules.Count; i++)
                if (Rules[i].Enabled) return true;
            return false;
        }
    }

    /// <summary>
    /// The baseline policy. Host rules (universal, percent-based) ship enabled; per-server rules (absolute
    /// thresholds, which depend entirely on the game) ship disabled with a placeholder warn, inert until an
    /// operator opts in and tunes them. <c>host-disk</c> leads because a full disk actually stops servers.
    /// </summary>
    public static readonly MetricsThresholdPolicy Default = new(
    [
        new(Key: "host-disk", Metric: ThresholdMetric.HostDiskUsedPct, Warn: 90, Danger: 95,
            FireForSec: 60, ClearForSec: 300, ClearMargin: 3, Enabled: true),
        new(Key: "host-mem", Metric: ThresholdMetric.HostMemUsedPct, Warn: 90, Danger: 97,
            FireForSec: 120, ClearForSec: 120, ClearMargin: 5, Enabled: true),
        new(Key: "host-swap", Metric: ThresholdMetric.HostSwapUsedPct, Warn: 50, Danger: 90,
            FireForSec: 120, ClearForSec: 120, ClearMargin: 10, Enabled: true),
        new(Key: "host-load", Metric: ThresholdMetric.HostLoadPerCore, Warn: 1.5, Danger: 4.0,
            FireForSec: 120, ClearForSec: 120, ClearMargin: 0.3, Enabled: true),
        new(Key: "host-temp", Metric: ThresholdMetric.HostTempC, Warn: 85, Danger: 95,
            FireForSec: 30, ClearForSec: 60, ClearMargin: 5, Enabled: true),
        new(Key: "srv-pids", Metric: ThresholdMetric.ServerPids, Warn: 1000, Danger: null,
            FireForSec: 120, ClearForSec: 120, ClearMargin: 50, Enabled: false),
        new(Key: "srv-mem", Metric: ThresholdMetric.ServerMemBytes, Warn: 0, Danger: null,
            FireForSec: 120, ClearForSec: 120, ClearMargin: 0, Enabled: false),
        new(Key: "srv-cpu", Metric: ThresholdMetric.ServerCpuPctCore, Warn: 0, Danger: null,
            FireForSec: 120, ClearForSec: 120, ClearMargin: 0, Enabled: false),
    ]);
}

/// <summary>
/// One measured target a <see cref="ThresholdRule"/> is compared against, yielded by
/// <see cref="ThresholdMetrics.Observe"/>. A target that is not evaluable this tick (a null field, no swap,
/// no cpu-info, no sensors) yields nothing — never a fabricated value.
/// </summary>
/// <param name="RefKey">The mount path / sensor chip-label for a metric that fans out; <see langword="null"/>
/// for a singleton host metric and for server-scope metrics (keyed by <see cref="ServerId"/> instead).</param>
/// <param name="ServerId">The reporting server's instance id for a server-scope metric, else null.</param>
/// <param name="Value">The measured value, compared against the rule's bands.</param>
public readonly record struct MetricObservation(string? RefKey, string? ServerId, double Value);

/// <summary>
/// All the <see cref="Snapshot"/>-field knowledge the threshold source needs, in one place. The evaluator
/// never reads a snapshot field directly — it calls <see cref="Observe"/> and works only with
/// <see cref="MetricObservation"/>.
/// </summary>
public static class ThresholdMetrics
{
    /// <summary>Whether <paramref name="metric"/> is host-scope (a singleton, or a fan-out over
    /// mounts/sensors) as opposed to per-server. Derived from the metric rather than stored on the rule, so
    /// there is one source of truth.</summary>
    public static bool IsHostScope(ThresholdMetric metric) => metric switch
    {
        ThresholdMetric.HostMemUsedPct => true,
        ThresholdMetric.HostSwapUsedPct => true,
        ThresholdMetric.HostDiskUsedPct => true,
        ThresholdMetric.HostLoadPerCore => true,
        ThresholdMetric.HostTempC => true,
        _ => false,
    };

    /// <summary>
    /// The metric's name on the wire. A hand-written switch rather than <c>ToString()</c>: the wire name is
    /// a contract with every consumer, and renaming an enum member must not silently rename it.
    /// </summary>
    public static string WireName(ThresholdMetric metric) => metric switch
    {
        ThresholdMetric.HostMemUsedPct => "HostMemUsedPct",
        ThresholdMetric.HostSwapUsedPct => "HostSwapUsedPct",
        ThresholdMetric.HostDiskUsedPct => "HostDiskUsedPct",
        ThresholdMetric.HostLoadPerCore => "HostLoadPerCore",
        ThresholdMetric.HostTempC => "HostTempC",
        ThresholdMetric.ServerMemBytes => "ServerMemBytes",
        ThresholdMetric.ServerCpuPctCore => "ServerCpuPctCore",
        ThresholdMetric.ServerPids => "ServerPids",
        _ => "Unknown",
    };

    /// <summary>The scope name on the wire (<c>host</c> / <c>server</c>).</summary>
    public static string ScopeName(ThresholdMetric metric) => IsHostScope(metric) ? "host" : "server";

    /// <summary>
    /// Whether <paramref name="metric"/> measures a <em>set</em> of targets (one per mount, sensor or
    /// server) rather than a single one. This is what separates the two reasons a target can produce no
    /// observation, which need opposite handling:
    /// <list type="bullet">
    /// <item>A fan-out metric's target that stops appearing has <b>gone</b> — the server stopped, the mount
    /// was unmounted. That is a real transition, so an open condition clears on the normal dwell.</item>
    /// <item>A singleton metric's target always exists; no observation means the value could not be
    /// <b>established</b> (swap is off, cpu-info is unreadable). That is not recovery, so an open condition
    /// holds — reporting it as cleared would be inventing a measurement nobody took.</item>
    /// </list>
    /// </summary>
    public static bool FansOut(ThresholdMetric metric) => metric switch
    {
        ThresholdMetric.HostDiskUsedPct => true,
        ThresholdMetric.HostTempC => true,
        ThresholdMetric.ServerMemBytes => true,
        ThresholdMetric.ServerCpuPctCore => true,
        ThresholdMetric.ServerPids => true,
        _ => false,
    };

    /// <summary>
    /// Yields one <see cref="MetricObservation"/> per evaluable target of <paramref name="rule"/> against
    /// <paramref name="snap"/>. Skips — never throws on — a target that is not honestly evaluable this tick:
    /// a null field, a null <c>Cpu.Info</c>, <c>SwapTotalKb == 0</c>, or an empty sensors/servers array.
    /// The caller is responsible for only invoking this with an enabled rule.
    /// </summary>
    public static IEnumerable<MetricObservation> Observe(ThresholdRule rule, Snapshot snap)
    {
        switch (rule.Metric)
        {
            case ThresholdMetric.HostMemUsedPct:
                yield return new MetricObservation(null, null, snap.Mem.UsedPct);
                break;

            case ThresholdMetric.HostSwapUsedPct:
            {
                if (snap.Mem.SwapTotalKb == 0) yield break; // not evaluable: no swap configured
                yield return new MetricObservation(null, null, 100.0 * snap.Mem.SwapUsedKb / snap.Mem.SwapTotalKb);
                break;
            }

            case ThresholdMetric.HostDiskUsedPct:
            {
                foreach (MountUsage mount in snap.Disk?.Mounts ?? [])
                    yield return new MetricObservation(mount.Mount, null, mount.UsedPct);
                break;
            }

            case ThresholdMetric.HostLoadPerCore:
            {
                int? cores = snap.Cpu?.Info?.Cores;
                if (cores is null or 0) yield break; // not evaluable: no cpu-info / unknown core count
                yield return new MetricObservation(null, null, snap.Cpu!.Load.Five / cores.Value);
                break;
            }

            case ThresholdMetric.HostTempC:
            {
                foreach (SensorReading sensor in snap.Sensors ?? [])
                    yield return new MetricObservation(SensorRef(sensor), null, sensor.ValueC);
                break;
            }

            case ThresholdMetric.ServerMemBytes:
            {
                foreach (ServerMetrics srv in snap.Servers ?? [])
                    yield return new MetricObservation(null, srv.Id, srv.MemBytes);
                break;
            }

            case ThresholdMetric.ServerCpuPctCore:
            {
                foreach (ServerMetrics srv in snap.Servers ?? [])
                    yield return new MetricObservation(null, srv.Id, srv.CpuPctCore);
                break;
            }

            case ThresholdMetric.ServerPids:
            {
                foreach (ServerMetrics srv in snap.Servers ?? [])
                    yield return new MetricObservation(null, srv.Id, srv.Pids);
                break;
            }

            default:
                yield break; // closed enum — an unmapped member is a no-op, never a guess
        }
    }

    /// <summary>How a sensor names itself as a target: <c>chip/label</c>, or just the chip when it carries
    /// no label. Two chips can share a name, which this does not resolve — a host with two identically
    /// named unlabelled chips reports them as one target, which is the honest limit of what hwmon gives.</summary>
    public static string SensorRef(SensorReading sensor) =>
        string.IsNullOrEmpty(sensor.Label) ? sensor.Chip : $"{sensor.Chip}/{sensor.Label}";
}
