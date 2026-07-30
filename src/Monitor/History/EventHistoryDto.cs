using System.Text.Json;

namespace TheKrystalShip.KGSM.Monitor.History;

/// <summary>
/// The event-history response shape served from <c>GET /events</c> — the monitor is the single
/// source of truth for KGSM <em>engine</em> events (server-triggered lifecycle/config/etc, delivered
/// on the KGSM event socket). Rows are stored and served <b>raw and neutral</b>: no domain shaping
/// (dotted action vocabulary, severity, human summary) — that is kgsm-api's read-time concern, applied
/// on top of these rows via its existing <c>AuditMapping</c>. ts-DESC ordering; pagination is a
/// composite <c>(ts, id)</c> keyset cursor (a single rowid cursor can't span kgsm-api's own audit
/// table once the two feeds are merged there). <see cref="Count"/> is the number of items in this
/// page, not a total; an honest empty page is <c>{ count: 0, nextCursorTs: null, nextCursorId: null,
/// events: [] }</c>, never a fabricated "nothing happened".
/// </summary>
public sealed record EventHistoryResponse(
    int Count,
    string? NextCursorTs,
    string? NextCursorId,
    List<EventHistoryItem> Events);

/// <summary>
/// One persisted engine event row, as it arrived on the event socket. <see cref="Instance"/> is
/// <see langword="null"/> for host/global events and for blueprint-scoped events (whose subject is a
/// <see cref="Blueprint"/>, not an instance — Phase 2 of <c>blueprint-editor-plan.md</c>); conversely
/// <see cref="Blueprint"/> is <see langword="null"/> for every instance-scoped event — the two columns
/// are orthogonal, never one backing the other. <see cref="Actor"/>/<see cref="Origin"/> are
/// <see langword="null"/> when the emitter supplied no enrichment (e.g. a bare CLI call) — never
/// fabricated. <see cref="Data"/> is the event-specific payload, relayed verbatim and opaque to the
/// monitor (a per-event shape only the domain-aware reader — kgsm-api's <c>AuditMapping</c>, or the
/// assistant — interprets).
/// </summary>
public sealed record EventHistoryItem(
    string Id,
    DateTimeOffset Ts,
    string Type,
    string? Instance,
    string? Blueprint,
    string? Actor,
    string? Origin,
    JsonElement? Data);
