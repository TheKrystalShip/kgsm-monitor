namespace TheKrystalShip.KGSM.Monitor.History;

/// <summary>
/// What one instance has been measured to hold, over its whole observed life.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is an observation, and the field names say so.</b> It is not a requirement and must not be
/// rendered as one: it reports what an instance used given the allowance it had, which differs from
/// what it needs whenever that allowance binds — a JVM heap flag, a configured cache size. A consumer
/// deciding those are the same thing is the failure this shape is arranged to make awkward.
/// </para>
/// <para>
/// <see cref="ObservedHours"/> and <see cref="Runs"/> are part of the answer, not metadata about it. Twenty
/// minutes of one run says nothing about an instance no matter how confident the figures beside it look,
/// and a reader that cannot see how much was measured cannot tell the two apart.
/// </para>
/// </remarks>
/// <param name="Instance">The instance name — the same join key every other per-server figure uses.</param>
/// <param name="WorkingSetPeakBytes">The largest working set observed (anonymous memory plus swap).</param>
/// <param name="WorkingSetAvgBytes">The mean working set across every observation.</param>
/// <param name="PeakBytes">
/// The highest the kernel's own high-water mark has reached in any run. Higher than
/// <paramref name="WorkingSetPeakBytes"/> whenever a spike fell between two samples, and it includes the
/// page cache the working-set figures exclude.
/// </param>
/// <param name="OomKills">
/// Processes the kernel has killed in this instance's cgroup for want of memory. Non-zero means this
/// instance needs <em>more</em> than it was given — the one figure here that is a bound rather than a
/// description.
/// </param>
/// <param name="MaxEvents">How many times allocation hit the instance's memory ceiling without dying.</param>
/// <param name="StallSeconds">Cumulative time every task in the cgroup spent stalled waiting on memory.</param>
/// <param name="Runs">How many times this instance has been observed to start.</param>
/// <param name="ObservedHours">Cumulative time this instance has been observed running.</param>
/// <param name="Samples">Observations behind the figures above.</param>
/// <param name="FirstSeen">When this record started, ISO-8601 UTC.</param>
/// <param name="LastSeen">The most recent observation, ISO-8601 UTC.</param>
public sealed record FootprintDto(
    string Instance,
    double? WorkingSetPeakBytes,
    double? WorkingSetAvgBytes,
    double? PeakBytes,
    long OomKills,
    long MaxEvents,
    double StallSeconds,
    long Runs,
    double ObservedHours,
    long Samples,
    string FirstSeen,
    string LastSeen)
{
    /// <summary>
    /// The stored row rendered in the units a reader thinks in.
    /// </summary>
    /// <remarks>
    /// The accumulator's own bookkeeping — the counter baselines it uses to avoid double-banking a
    /// value across a restart — is deliberately not carried through. It describes how the figures were
    /// arrived at, not the instance, and a consumer that could see it would eventually reason about it.
    /// <para>
    /// A mean over zero samples is null, never 0: an instance whose cgroup exposes no working set has
    /// not been measured to hold nothing.
    /// </para>
    /// </remarks>
    public static FootprintDto From(FootprintRow row) => new(
        Instance: row.InstanceId,
        WorkingSetPeakBytes: row.AnonMax,
        WorkingSetAvgBytes: row.Samples > 0 ? row.AnonSum / row.Samples : null,
        PeakBytes: row.PeakBytes,
        OomKills: row.OomKills,
        MaxEvents: row.MaxEvents,
        StallSeconds: Math.Round(row.StallTotalUsec / 1_000_000.0, 3),
        Runs: row.Runs,
        ObservedHours: Math.Round(row.UptimeMs / 3_600_000.0, 2),
        Samples: row.Samples,
        FirstSeen: DateTimeOffset.FromUnixTimeMilliseconds(row.FirstSeen).UtcDateTime.ToString("O"),
        LastSeen: DateTimeOffset.FromUnixTimeMilliseconds(row.LastSeen).UtcDateTime.ToString("O"));
}

/// <summary>Every instance this host holds a footprint for.</summary>
/// <param name="Footprints">One row per instance, ordered by name.</param>
public sealed record FootprintResponse(IReadOnlyList<FootprintDto> Footprints);
