# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

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
