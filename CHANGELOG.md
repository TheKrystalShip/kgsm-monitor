# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Changed — configuration is bound from `kgsm-monitor.settings.json`, which is now the source of truth

- **Every knob is declared in the settings file and bound to `MonitorSettings`.** The file ships all
  22 keys with their defaults under a `Monitor` section, and an environment variable overrides one
  of them by spelling its path with `__` (`Monitor__IntervalMs`, `Monitor__HistoryDbPath`). The
  hand-rolled `MonitorOptions.FromEnvironment()` and its flat `KGSM_MONITOR_*` names are gone; what
  remains is `MonitorOptions.FromSettings`, which normalizes bound values rather than reading the
  environment itself.

  The point is that a variable naming a key the file does not declare now binds to nothing and
  changes nothing — there is no longer a way to configure the daemon that is invisible in the file.
  Binding is source-generated (the binder generator is on under `PublishAot`), so the AOT publish
  stays at zero IL warnings and nothing here costs reflection.

- **Environment variables are re-registered after the settings file so they win.** Configuration
  resolves by source order, and the explicitly-loaded file is appended after everything the slim
  builder installed — including its own environment provider. Without the re-registration the file
  outranks every `Monitor__*` and `Logging__*` variable. This was already true of the
  logging-only file that preceded this change: `Logging__LogLevel__Default` could not in fact
  override the level the file set, despite being documented as able to.

- **A cadence below its floor is raised to the floor** instead of reverting to the coded default.
  The floor is the nearest legal value to what was asked for; reverting meant a mistyped interval
  ran at a cadence nobody named. The floors themselves are unchanged, and they are the same bounds
  the Control Panel already rejects against before restarting anything.

- **Boolean knobs accept `true`/`false` only.** The hand-rolled parser also took `1/0`, `yes/no` and
  `on/off`; standard binding does not. The Control Panel writes `true`/`false`, so nothing on the
  writing side is affected.

- `LeafDescriptorTests` pins the surface in four directions instead of scanning the source for
  string literals — there are none left to scan. A knob must be a `MonitorSettings` property, a key
  in the settings file, and a descriptor entry; any one missing fails the build naming which. A
  property with no key has an invisible default, a key with no property binds to nothing, and either
  without a descriptor entry is invisible to the panel.

- Descriptor `env` values move to the new names while every `key` stays exactly as it was — stored
  overrides are keyed by `key`, so renaming one would orphan a live override and silently revert a
  leaf to its floor.

### Changed — kgsm-lib 2.0.0 (the socket event transport is gone)
- **Pinned to `TheKrystalShip.KGSM.Lib` 2.0.0**, which removes `UnixSocketClient`,
  `KgsmEventTransport` and `KgsmOptions.SocketPath`/`EventTransport`. This service already read the
  journal, so the only change here is dropping the now-nonexistent `EventTransport = Journal` line —
  there is no transport left to select. No behaviour change.

### Added — `events.db` is a declared index, rebuildable from the journal
- **`POST /events/rebuild` reconstructs the event index from the engine's journal.** The record is
  the engine's append-only NDJSON; `events.db` is derived from it, exists so a query like "the last
  50 events for this instance, paged" answers in bounded time, and is now recoverable when it is
  lost, corrupted, or was never written because the monitor was down while the engine kept emitting.
  It replays every surviving segment through the same `AppendAsync` the live reader uses, so a
  rebuilt index and a streamed one are the same rows by construction.

  Three properties make it safe to run against a live daemon, and each is pinned by a test:
  it is **additive** — it inserts what is missing and never clears the table, because the journal is
  pruned on age while the index is not, so a wipe would destroy rows whose segments are already gone;
  it **never moves the live cursor**, which is where streaming resumes and not something a recovery
  action should silently skip past; and it **never clears a recorded gap**, since replaying the
  segments that survived does not bring back the events that did not.

  The response is measured, not derived: segments read, the oldest and newest that still exist,
  lines, inserted, already-present, and unparseable. `no_journal` (directory absent) and a
  zero-row `ok` are different answers to different questions. A second concurrent call gets `409`
  rather than a competing full scan.

### Added — retention layering is checked, not assumed
- **The monitor reports at startup whether the journal still reaches back as far as the index claims
  to**, reading `event_journal_retention_days` from the engine and comparing it against
  `KGSM_MONITOR_EVENT_RETENTION_DAYS`. Configured the wrong way round, the index keeps serving rows
  whose segments have been pruned — correct until something rebuilds, at which point history
  silently shortens to the journal's window. That is now visible as an error log naming both numbers
  instead of a surprise during recovery.

  It reports and does not correct: retention lives in the engine's config, the engine prunes on age
  alone and never consults a consumer, and a leaf quietly rewriting the engine's configuration to
  suit itself would invert that ownership. A value it cannot read is logged as unverified, never
  assumed to be fine.

### Changed — engine events come from the journal, not a socket
- **The monitor tails KGSM's append-only event journal** (`KGSM_MONITOR_KGSM_JOURNAL`, default
  `/var/lib/kgsm/events`) instead of binding a socket for the engine to push to.
  `KGSM_MONITOR_KGSM_SOCKET` is gone, and with it the monitor's claim on a socket path the engine
  had to be configured with. The four lifecycle handlers driving watch-list resync are unchanged —
  the transport swap happens entirely below `IEventService`.

  Engine events stop being live-only. A monitor that was down now catches up from its stored
  position rather than losing everything it slept through, which matters because this daemon is
  the ecosystem's engine-event history: what it misses, nothing else records. Its position lives
  in `events.db` beside the events derived from it (`EventJournalCursorStore`), so the index and
  the position it was built from cannot end up in different places. Delivery is at-least-once, and
  that is safe here precisely because `AppendAsync` is already idempotent on the deterministic
  `AuditId` — a replayed event is an `INSERT OR IGNORE` no-op.

  The monitor starts at `CursorOrOldest`: it is the index, so with no stored position it replays
  the surviving journal rather than starting blind.

### Added — gaps are recorded and reported
- **`GET /events` carries a `gaps` array**, and `events.db` gains a `gap` table to back it. When
  journal retention has deleted the segment the monitor's cursor named, events occurred that this
  store will never contain — so it says so, instead of returning a partial history that reads
  exactly like a complete one. An empty array is a positive claim of unbroken coverage. The socket
  transport could not express this at all: a missed event was indistinguishable from an event that
  never happened. The field is additive, so a consumer that ignores it is unaffected.

### Added — leaf config descriptor
- **`deploy/kgsm-monitor.leaf.json` declares the monitor's full configurable surface** — all 21
  `KGSM_MONITOR_*` variables plus the standard `Logging__LogLevel__Default`, each with a label, a
  description written for an operator, its type, its coded default, its bounds, and a `risk` tag.
  `deploy/setup.sh` creates the shared discovery directory `/var/lib/kgsm/leaves/` (the one
  privileged step); `deploy/deploy.sh` installs the descriptor there unprivileged before the binary
  swap, so what kgsm-api reads can never lag the binary implementing it. The daemon does not read
  its own descriptor and remains unaware of the API. Format: `tks/leaf-config-descriptor.md`.
- Five fields are tagged `wiring` (the sockets, the KGSM path, the host id) and five `destructive`
  (the retention cutoffs and the two database paths). `hostId` and `socketPath` name their
  `pairedApiKey`, so a consumer moving either side moves both in one transaction rather than
  severing the link.
- **`LeafDescriptorTests` is the anti-drift guard.** It scans `src/Monitor` for `KGSM_MONITOR_*`
  and fails the build when a variable the daemon reads has no descriptor entry, or when a
  descriptor entry names a variable the daemon does not read — an override for one of those would
  be reported as applied while changing nothing. Scanning the source rather than a constants table
  is deliberate: a table only proves the table and the descriptor agree, and a knob read through a
  raw literal would bypass both.

### Changed — headless deploys (`setup.sh` once, `deploy.sh` forever after)
- **`deploy/setup.sh` provisions the host once** (asks for sudo; idempotent): chowns
  `/opt/kgsm-monitor` to the deploying user, puts the real unit in `/etc/kgsm-monitor/systemd/` with
  `/etc/systemd/system/kgsm-monitor.service` symlinked to it, installs a polkit grant scoped to this
  project's units, enables the unit, and verifies the grant with the same unprivileged `systemctl`
  calls `deploy.sh` makes.
- **`deploy/deploy.sh` runs with no `sudo` and no prompts**, and refuses up-front (before building)
  with "run `deploy/setup.sh`" when the host is not provisioned. The AOT publish, the native-library
  prune/install, and the unit refresh are otherwise unchanged.
- `deploy/deploy-common.sh` carries the project block plus the shared helpers, sourced by both entry
  points so they cannot drift. Canonical template and contract:
  `tks/scripts/deploy-template/README.md`.

## [1.5.1] - 2026-07-28

### Added
- **Blueprint-event attribution** — the `event` table carries a `blueprint TEXT` column (additive,
  NULL by default) that blueprint-scoped engine events (Phase 2 of `blueprint-editor-plan.md`)
  populate with the event's `BlueprintName`. Unlike `instance`, which an instance-scoped event
  fills, a blueprint event's subject is a blueprint file — forcing it through `instance` would
  invent a server relationship that does not exist. The two columns are orthogonal: an
  instance-scoped row leaves `blueprint` null, a blueprint-scoped row leaves `instance` null, and
  the `?blueprint=<name>` query filter (mirroring `?instance=`) returns blueprint rows only.
  Surface: the existing `GET /events` route now also accepts `?blueprint=`. Downstream readers
  (kgsm-api `MonitorEventShaping`) carry no Server audit target when `instance` is null, so
  blueprint rows never appear as server rows in `GET /audit` — the columns stay disjoint there
  too.

## [1.5.0] - 2026-07-18

### Added
- **Event history** — the monitor is the single source of truth for KGSM *engine* events (Phase B of
  `event-history-plan.md`). A raw handler (`IEventService.RegisterRawHandler`, kgsm-lib 1.36.0) fires
  on every deserialized event envelope, known or unknown type, and persists it to a dedicated
  `events.db` (own WAL, own single-writer gate — no contention with the metrics flusher) via
  `INSERT OR IGNORE` on the deterministic `evt_<hash>` id (`Events.AuditId.ForEvent`), so a
  redelivered event never double-inserts. `GET /events?instance=&type=&since=&until=&before_ts=&
  before_id=&limit=` serves ts-DESC windowed/filtered queries with a composite `(ts, id)` keyset
  cursor. Rows are stored raw and neutral (no domain shaping — that stays kgsm-api's read-time
  concern); `instance`/`actor`/`origin` are `NULL` when the emitter supplied none, never fabricated.
  No rollup tier (discrete facts, not a sampled series) — retention is a straight prune.
- Config knobs: `KGSM_MONITOR_EVENTS_DB_PATH` (`/var/lib/kgsm-monitor/events.db`),
  `KGSM_MONITOR_EVENT_HISTORY_DISABLED` (default enabled, independent of the metrics-history flag),
  `KGSM_MONITOR_EVENT_RETENTION_DAYS` (default 30).
- The shared rollup/prune/vacuum maintenance loop now also prunes `events.db` in the same pass when
  event history is enabled; metrics and event history are independently toggleable, so the loop runs
  whenever either is on and degrades correctly if only one store is wired.

### Notes
- Event-history persistence is gated on `KgsmEnabled` (needs the KGSM event socket) *and*
  `EventHistoryEnabled`. `Monitor.Contracts` is unchanged — the event-history DTOs are daemon-local
  (`src/Monitor/History/EventHistoryDto.cs`), same pattern as metrics history.
- Bumps the `TheKrystalShip.KGSM.Lib` pin to 1.36.0 (`RegisterRawHandler` + `AuditId.ForEvent`).

## [1.4.0] - 2026-07-18

### Added
- **Metrics history** — the monitor is now the single source of truth for metrics history. A persist
  loop flushes the latest frame to a SQLite store (`sample` raw tier ~15s step / 24h; `rollup` 5-min
  buckets / 30d) every `KGSM_MONITOR_PERSIST_MS` (default 15s); a maintenance loop rolls up closed
  buckets, prunes both tiers, and incremental-vacuums. `GET /metrics/history?kind=&id=&range=` serves
  windowed queries with automatic raw↔rollup tier selection by range. Honest gaps: a null metric field
  or a missing frame writes no row (never a fabricated 0 or carry-forward).
- Config knobs: `KGSM_MONITOR_HOST_ID` (host-row identity, defaults to the machine name),
  `KGSM_MONITOR_DB_PATH` (`/var/lib/kgsm-monitor/metrics.db`), `KGSM_MONITOR_PERSIST_MS`,
  `KGSM_MONITOR_RAW_RETENTION_HOURS`, `KGSM_MONITOR_ROLLUP_STEP_MIN`,
  `KGSM_MONITOR_ROLLUP_RETENTION_DAYS`, `KGSM_MONITOR_MAINT_MS`, `KGSM_MONITOR_HISTORY_DISABLED`.
- Deploy unit gains `StateDirectory=kgsm-monitor` (`/var/lib/kgsm-monitor`) for the persistent DB.

### Notes
- Raw ADO `Microsoft.Data.Sqlite` (not EF Core, which is not AOT-safe); the AOT publish stays clean
  and emits `libe_sqlite3.so`. The shared `Monitor.Contracts` (`Snapshot`) is unchanged — the history
  DTO is daemon-local.

## [1.3.0] - 2026-06-30

### Added
- Initial versioned release.
