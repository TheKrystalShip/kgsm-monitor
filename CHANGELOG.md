# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

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
