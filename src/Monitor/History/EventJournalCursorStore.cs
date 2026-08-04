using TheKrystalShip.KGSM.Core.Interfaces;
using TheKrystalShip.KGSM.Core.Models;

namespace TheKrystalShip.KGSM.Monitor.History;

/// <summary>
/// Keeps the monitor's journal position in <c>events.db</c>, beside the events derived from it.
/// </summary>
/// <remarks>
/// The library's default is a standalone cursor file; the monitor deliberately does not use it.
/// The position and the index built from it belong together — two files could disagree after a
/// crash or a manual delete, leaving the store either replaying events it already holds or
/// skipping ones it does not. One database has one answer.
/// </remarks>
public sealed class EventJournalCursorStore : IEventCursorStore
{
    private readonly EventHistoryStore _store;

    public EventJournalCursorStore(EventHistoryStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    public async ValueTask<EventCursor?> LoadAsync(CancellationToken token = default)
        => await _store.LoadCursorAsync(token).ConfigureAwait(false);

    public async ValueTask SaveAsync(EventCursor cursor, CancellationToken token = default)
        => await _store.SaveCursorAsync(cursor, token).ConfigureAwait(false);
}
