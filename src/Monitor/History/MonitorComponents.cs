namespace TheKrystalShip.KGSM.Monitor.History;

/// <summary>
/// The parts of this daemon's job that can stop working while it keeps serving.
/// </summary>
/// <remarks>
/// <para>
/// Each is a <c>leaf_degraded</c> component. ⚠ Every one of these was previously a log line and
/// nothing else — the <c>/health</c> endpoint answers a literal <c>ok</c>, so a monitor with a frozen
/// frame, no per-server network numbers, or a dead event listener reports itself operational to every
/// surface on this host.
/// </para>
/// <para>
/// They are held here rather than as strings at the call sites because the id is the dedup key: two
/// spellings of one component would report the same fault twice and recover from neither.
/// </para>
/// </remarks>
internal static class MonitorComponents
{
    /// <summary>
    /// The host sample itself.
    /// </summary>
    /// <remarks>
    /// ⚠ The worst of these to lose silently. A failed sample keeps the previous frame rather than
    /// publishing nothing, which is right — a gap would read as a host with no metrics — but it means a
    /// monitor whose sampling has broken serves a plausible, frozen snapshot indefinitely.
    /// </remarks>
    public const string Sampling = "sampling";

    /// <summary>
    /// The eBPF per-server network meter.
    /// </summary>
    /// <remarks>
    /// Recovers on its own: the pin is re-probed on every tick, so a meter attached after the monitor
    /// started is picked up without a restart. Without it <c>RxBps</c>/<c>TxBps</c> are honestly null
    /// rather than wrong, which is why this degrades one component instead of the whole leaf.
    /// </remarks>
    public const string NetworkMeter = "net-meter";

    /// <summary>
    /// The KGSM event listener that keeps the per-server watch-list current.
    /// </summary>
    /// <remarks>
    /// Its loss is not visible in a frame. Sampling falls back to the periodic resync, so servers still
    /// appear — just up to one resync interval late, which looks like nothing at all until somebody
    /// wonders why a server they just started has no metrics yet.
    /// </remarks>
    public const string EventListener = "event-listener";
}
