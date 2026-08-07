#!/usr/bin/env python3
"""Export engine events held only in the monitor's index into kgsm's event journal.

The journal is the record and `events.db` is a derived index over it — but the index was
written while the socket transport was live, so it holds events from before the journal
existed. Those rows exist nowhere else, and the index is being retired. This moves them into
the journal, in the engine's own envelope shape, so the record covers the full window and the
index can be deleted.

One field does not survive: the index has no `KGSMVersion` column, so a backfilled line omits
it and it reads as null — honestly absent rather than invented. Everything else round-trips
exactly.

Rows are matched against the journal by content, not by date, so a row the journal already
holds is never written twice — which matters on the boundary day, where the two overlap.

The match is on the event's identity — type, timestamp, payload, host — and deliberately not
on `Actor`/`Origin`. Across the cutover the same action was enriched differently on the two
paths (the journal attributing to the API caller, the index to the OS user), and treating that
as two events would write a second row claiming someone else did the same thing in the same
second. Where both hold an event, the journal's copy stands: it is the record, and the index
is derived from it and authoritative for nothing.

A segment is written whole to a temporary file and renamed into place. A reader holding a byte
offset into a segment being rewritten would be reading a different file than it thinks, so
this is only safe against segments the engine has closed (it appends only to today's) and with
consumers stopped. Both are checked below.
"""

import argparse
import json
import os
import sqlite3
import sys
import tempfile
from collections import Counter, defaultdict
from datetime import datetime, timezone
from pathlib import Path

DEFAULT_DB = "/var/lib/kgsm-monitor/events.db"
DEFAULT_JOURNAL = "/var/lib/kgsm/events"

# The engine's key order. A journal line is compared by content, never by bytes, but matching
# the writer's shape keeps the file uniform for anything that reads it with human eyes.
KEY_ORDER = ("EventType", "Data", "Timestamp", "Actor", "Origin", "Hostname", "KGSMVersion")


def identity(envelope):
    """A content key for one event, stable across the two representations.

    The index stores unix milliseconds while the journal writes an ISO-8601 string, and the
    engine's own timestamps carry one-second granularity — so both sides reduce to integer
    seconds. Two fields are excluded on purpose: `KGSMVersion`, which the index never stored,
    and `Actor`/`Origin`, which the two paths disagree about for events recorded across the
    cutover (see the module docstring).
    """
    ts = envelope.get("Timestamp")
    if isinstance(ts, str):
        seconds = int(datetime.fromisoformat(ts.replace("Z", "+00:00")).timestamp())
    else:
        seconds = int(ts)

    return json.dumps(
        [
            envelope.get("EventType"),
            seconds,
            envelope.get("Data"),
            envelope.get("Hostname"),
        ],
        sort_keys=True,
        separators=(",", ":"),
        ensure_ascii=False,
    )


def encode(envelope):
    """One journal line: compact, single-line, engine key order, absent fields omitted."""
    ordered = {k: envelope[k] for k in KEY_ORDER if k in envelope and envelope[k] is not None}
    return json.dumps(ordered, separators=(",", ":"), ensure_ascii=False)


def read_journal(journal_dir):
    """Every segment's lines, keyed by segment name, plus how many times each event is held.

    A count rather than a set: an event genuinely emitted twice must be backfilled twice if the
    journal holds it once, and dropping the surplus would lose a real occurrence.
    """
    segments = {}
    held = Counter()

    for path in sorted(journal_dir.glob("*.ndjson")):
        lines = []
        for lineno, raw in enumerate(path.read_text(encoding="utf-8").splitlines(), 1):
            raw = raw.strip()
            if not raw:
                continue
            try:
                held[identity(json.loads(raw))] += 1
            except (json.JSONDecodeError, ValueError, KeyError, TypeError) as exc:
                # A line the reader could not parse is reported, never silently dropped: it
                # stays in the segment verbatim, but it cannot be deduplicated against.
                print(f"  ! {path.name}:{lineno} unparseable, kept verbatim ({exc})", file=sys.stderr)
            lines.append(raw)
        segments[path.name] = lines

    return segments, held


def read_index(db_path):
    """The index's rows as engine envelopes, oldest first."""
    conn = sqlite3.connect(f"file:{db_path}?mode=ro", uri=True)
    try:
        rows = conn.execute(
            "SELECT ts, event_type, actor, origin, hostname, data FROM event ORDER BY ts ASC"
        ).fetchall()
    finally:
        conn.close()

    events = []
    for ts_ms, event_type, actor, origin, hostname, data in rows:
        when = datetime.fromtimestamp(ts_ms / 1000, tz=timezone.utc)
        envelope = {
            "EventType": event_type,
            "Data": json.loads(data) if data else {},
            "Timestamp": when.strftime("%Y-%m-%dT%H:%M:%SZ"),
            "Actor": actor,
            "Origin": origin,
            "Hostname": hostname,
        }
        events.append((when, envelope))

    return events


def segment_for(when):
    return f"{when:%Y-%m-%d}.ndjson"


def main():
    parser = argparse.ArgumentParser(description=__doc__,
                                     formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--db", default=DEFAULT_DB, help=f"monitor event index (default {DEFAULT_DB})")
    parser.add_argument("--journal", default=DEFAULT_JOURNAL, help=f"journal directory (default {DEFAULT_JOURNAL})")
    parser.add_argument("--apply", action="store_true", help="write the segments (default: report only)")
    args = parser.parse_args()

    db_path = Path(args.db)
    journal_dir = Path(args.journal)

    if not db_path.exists():
        sys.exit(f"no index at {db_path}")
    if not journal_dir.is_dir():
        sys.exit(f"no journal directory at {journal_dir}")

    segments, held = read_journal(journal_dir)
    events = read_index(db_path)

    print(f"journal:  {len(segments)} segments, {sum(len(v) for v in segments.values())} lines")
    print(f"index:    {len(events)} rows")

    # Each indexed event consumes one journal copy; only the surplus is missing. Events are
    # walked oldest-first, so where the counts differ it is the later occurrences that export.
    missing = defaultdict(list)
    covered = Counter()
    for when, envelope in events:
        key = identity(envelope)
        if covered[key] < held.get(key, 0):
            covered[key] += 1
            continue
        missing[segment_for(when)].append((when, envelope))

    total = sum(len(v) for v in missing.values())
    print(f"deduped:  {len(events) - total} rows the journal already holds")
    if total == 0:
        print("\nnothing to export — the journal already holds every indexed event")
        return

    # Today's segment is the one the engine appends to; rewriting it would race the writer.
    today = segment_for(datetime.now(timezone.utc))
    if today in missing:
        sys.exit(f"refusing to rewrite {today}: the engine is still appending to it")

    print(f"\nexport:   {total} rows into {len(missing)} segments")
    for name in sorted(missing):
        existing = len(segments.get(name, []))
        verb = "rewrite" if existing else "create "
        print(f"  {verb} {name}  +{len(missing[name]):4d} rows"
              + (f"  (merging with {existing} existing)" if existing else ""))

    if not args.apply:
        print("\nreport only — pass --apply to write")
        return

    print()
    for name in sorted(missing):
        rows = missing[name]
        # Existing lines keep their relative order; a stable sort by timestamp interleaves the
        # new rows without reordering anything the engine wrote.
        merged = [(None, line) for line in segments.get(name, [])]
        merged += [(when, encode(envelope)) for when, envelope in rows]

        if segments.get(name):
            def key(item, _rows=rows):
                when, line = item
                if when is not None:
                    return when
                return datetime.fromisoformat(
                    json.loads(line)["Timestamp"].replace("Z", "+00:00"))
            merged.sort(key=key)

        path = journal_dir / name
        fd, tmp = tempfile.mkstemp(dir=journal_dir, prefix=f".{name}.", suffix=".tmp")
        try:
            with os.fdopen(fd, "w", encoding="utf-8") as handle:
                for _, line in merged:
                    handle.write(line + "\n")
                handle.flush()
                os.fsync(handle.fileno())
            os.chmod(tmp, 0o644)
            os.replace(tmp, path)
            print(f"  wrote {name}  ({len(merged)} lines)")
        except BaseException:
            os.unlink(tmp)
            raise

    print(f"\ndone — {total} rows exported")


if __name__ == "__main__":
    main()
