namespace TheKrystalShip.KGSM.Monitor.History;

/// <summary>
/// When the history maintenance pass last completed, and whether it worked.
/// <para>
/// A singleton the maintenance service writes and <c>GET /stats</c> reads. It exists because maintenance
/// failing is silent by design — a failed tick is logged and the timer carries on — so from the outside a
/// store whose rollups stopped running looks exactly like one that is healthy, right up until the disk
/// fills or a 30-day query returns a week. This is the one place that difference is observable.
/// </para>
/// </summary>
/// <remarks>
/// Written on one timer thread and read on request threads, so the fields are <c>volatile</c>-equivalent
/// via <see cref="Interlocked"/>-free reference/long reads on 64-bit. Both are set together, failure
/// first, so a reader can never see a fresh timestamp attached to a stale verdict.
/// </remarks>
public sealed class MaintenanceState
{
    private long _lastRunMs;      // 0 = never
    private int _lastOk = -1;     // -1 = unknown, 0 = failed, 1 = ok

    /// <summary>When the last pass completed (unix ms), or null before the first one.</summary>
    public long? LastRunMs => Volatile.Read(ref _lastRunMs) is var ms && ms > 0 ? ms : null;

    /// <summary>Whether the last pass completed without throwing; null before the first one — never an
    /// optimistic true for a pass that has not happened.</summary>
    public bool? LastOk => Volatile.Read(ref _lastOk) switch { 1 => true, 0 => false, _ => null };

    /// <summary>Record the outcome of a completed pass.</summary>
    public void Record(bool ok)
    {
        // Verdict first: a reader that interleaves sees the new timestamp only once the verdict behind
        // it is already current.
        Volatile.Write(ref _lastOk, ok ? 1 : 0);
        Volatile.Write(ref _lastRunMs, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
    }
}
