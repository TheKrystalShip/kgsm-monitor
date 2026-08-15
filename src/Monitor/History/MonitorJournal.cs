using System.Text.Json;
using TheKrystalShip.KGSM.Core.Interfaces;
using TheKrystalShip.KGSM.Monitor.Thresholds;
using TheKrystalShip.KGSM.Services;

namespace TheKrystalShip.KGSM.Monitor.History;

/// <summary>
/// Records the threshold facts this daemon's own measurements established, in this daemon's own event
/// journal.
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
    public const string BreachedEvent = "host_threshold_breached";

    /// <summary>A firing condition stopped firing — which is not always a recovery.</summary>
    public const string ClearedEvent = "host_threshold_cleared";

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

        Record(closed ? ClearedEvent : BreachedEvent, w => WritePayload(w, t, closed));
    }

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
