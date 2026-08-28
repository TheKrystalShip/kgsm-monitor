using System.Text.Json;
using TheKrystalShip.KGSM.Core.Interfaces;
using TheKrystalShip.KGSM.Monitor.Contracts;
using TheKrystalShip.KGSM.Monitor.Thresholds;
using TheKrystalShip.KGSM.Services;

using TheKrystalShip.KGSM.Events;

namespace TheKrystalShip.KGSM.Monitor.History;

/// <summary>
/// Records the facts this daemon's own measurements established — a value crossing a line it watches,
/// and a server the kernel refused memory to — in this daemon's own event journal.
/// </summary>
/// <remarks>
/// <para>
/// <b>A producer records what that producer established.</b> Nothing else on this host takes these
/// measurements, so nothing else can honestly say a value crossed a line. Writing them here means the
/// fact is recorded where it happened rather than being polled out of a database by another component
/// and transcribed into that component's own store — which is what a reader had to trust before, and
/// what left "did this leaf record nothing, or did nobody ask it?" unanswerable.
/// </para>
/// <para>
/// <b>An opening and a closing are two events.</b> The journal is append-only and these are two
/// immutable facts; the mutable view of one condition over time is the alert feed, which answers a
/// different question. Both carry <c>OpenedTs</c>, so a reader can place the breach without having to
/// hold the pair.
/// </para>
/// <para>
/// <b>Raw values only.</b> No summary sentence, no severity, no formatted number — those are a
/// domain-aware reader's business, and putting one consumer's wording in the record would force it on
/// every other. The Control Panel's phrasing and a chat surface's are allowed to differ.
/// </para>
/// <para>
/// Best-effort, and the base decides what that means: a failed write is logged and reported, never
/// thrown. The episode is already in the history database either way, and failing the drain because
/// recording it did not work would trade a missing line for a stalled recorder.
/// </para>
/// </remarks>
public sealed class MonitorJournal(IEventJournalWriter writer, ILogger<MonitorJournal> logger)
    : JournalRecorder(writer, logger)
{
    /// <summary>A measured value crossed a line this host watches.</summary>
    public const string BreachedEvent = "host.threshold.breached";

    /// <summary>A firing condition stopped firing — which is not always a recovery.</summary>
    public const string ClearedEvent = "host.threshold.cleared";

    /// <summary>The kernel killed a process in a game server's cgroup for want of memory.</summary>
    public const string ServerOomEvent = "server.memory.oom_killed";

    /// <summary>The three names, typed so the writer can take them.</summary>
    /// <remarks>
    /// Derived from the constants above rather than restated, so the name this daemon writes and the
    /// name a reader matches cannot become two different strings.
    /// </remarks>
    private static readonly EventName BreachedName = EventName.Parse(BreachedEvent);
    private static readonly EventName ClearedName = EventName.Parse(ClearedEvent);
    private static readonly EventName ServerOomName = EventName.Parse(ServerOomEvent);

    /// <summary>
    /// Records one episode transition — an opening or a closing, whichever it is.
    /// </summary>
    /// <remarks>
    /// The actor is this daemon's derived <c>system:monitor</c> and the origin is <c>system</c>: no
    /// product surface drove a measurement, which is a fact about how it arose rather than a gap in
    /// what is known about it.
    /// </remarks>
    /// <param name="t">The transition the evaluator produced.</param>
    public void Record(EpisodeTransition t)
    {
        bool closed = t.ClosedTs is not null;

        Record(
            closed ? ClearedName : BreachedName,
            w => WritePayload(w, t, closed),
            // The band the episode reached IS how much it matters, and this daemon is the only thing
            // that measured it — so it says so rather than leaving a reader to infer weight from the
            // fact that a row exists. A close is routine whatever band it reached: the value came back.
            severity: closed ? EventSeverity.Info : BandSeverity(t.Band),
            // A breach reports a reading, not an operation, so it neither succeeded nor failed. A close
            // is the condition ending, which is the good result.
            outcome: closed ? EventOutcome.Success : EventOutcome.Neutral);
    }

    /// <summary>
    /// The band an episode reached, as a severity.
    /// </summary>
    /// <remarks>
    /// The two vocabularies coincide today and this does not assume they always will. An unrecognised
    /// band reads as <see cref="EventSeverity.Warn"/> rather than <see cref="EventSeverity.Info"/>:
    /// an episode exists because something crossed a line, so the quiet reading is the wrong guess.
    /// </remarks>
    private static EventSeverity BandSeverity(string? band) =>
        EventSeverities.TryParse(band, out EventSeverity severity) ? severity : EventSeverity.Warn;

    /// <summary>
    /// Records that the kernel killed a process in one server's cgroup for want of memory.
    /// </summary>
    /// <remarks>
    /// <b>The one memory fact that is not an inference.</b> Every other reading here describes what a
    /// server was using; this one says it asked for memory and was refused, which establishes a lower
    /// bound on what it needs rather than describing a sample. It needs no window, no coverage and no
    /// judgment about load, which is why it is announced the moment it is counted instead of waiting for
    /// something to accumulate enough evidence to conclude it.
    /// <para>
    /// Not the same as an exit code of 137. That is a SIGKILL from any source; this is the kernel's own
    /// counter, in the cgroup it happened in.
    /// </para>
    /// </remarks>
    /// <param name="instance">The instance whose cgroup it happened in.</param>
    /// <param name="kills">Kills counted since this daemon last read the counter.</param>
    /// <param name="total">Kills this host has counted for this instance across every run.</param>
    /// <param name="memory">The reading the kill was counted in, so a reader has the state beside the fact.</param>
    public void RecordOom(string instance, long kills, long total, ServerMemory memory)
        => Record(ServerOomName, w =>
        {
            w.WriteString("Instance", instance);
            w.WriteNumber("Kills", kills);
            w.WriteNumber("TotalKills", total);
            if (memory.AnonBytes is { } anon)
                w.WriteNumber("AnonBytes", anon);
            if (memory.PeakBytes is { } peak)
                w.WriteNumber("PeakBytes", peak);
            if (memory.MaxEvents is { } max)
                w.WriteNumber("MaxEvents", max);
        },
        severity: EventSeverity.Danger,
        outcome: EventOutcome.Failure);

    /// <summary>Writes the payload both events share, plus the half that differs.</summary>
    private static void WritePayload(Utf8JsonWriter w, EpisodeTransition t, bool closed)
    {
        w.WriteString("EpisodeId", t.EpisodeId);
        w.WriteString("RuleKey", t.RuleKey);
        w.WriteString("Metric", t.Metric);
        w.WriteString("Scope", t.Scope);
        WriteNullable(w, "Ref", t.Ref);
        WriteNullable(w, "ServerId", t.ServerId);
        w.WriteNumber("Threshold", t.Threshold);
        w.WriteNumber("PeakValue", t.PeakValue);

        // The worst band the episode reached, which is what justifies how loudly a reader reports it —
        // not the band at either end, since a condition that touched danger and eased back to warn was
        // still a danger-band episode.
        w.WriteString("PeakBand", t.Band);
        w.WriteNumber("OpenedTs", t.OpenedTs);

        if (closed)
        {
            w.WriteNumber("ClosedTs", t.ClosedTs!.Value);
            w.WriteNumber("CloseValue", t.Value);

            // Why it ended, never flattened into "recovered": a rule retuned, disabled or removed closes
            // an episode without the value ever being seen to come down.
            WriteNullable(w, "CloseReason", t.EndReason);
        }
        else
        {
            w.WriteNumber("OpenValue", t.Value);
            w.WriteString("Band", t.Band);
        }
    }
}
