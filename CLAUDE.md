# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

`kgsm-monitor` is one leaf of the `tks`/KGSM ecosystem. The umbrella `../CLAUDE.md` (loaded
in this repo) owns the cross-cutting invariants — read it for the dependency spine, the
never-fabricate-a-metric rule, the kgsm-lib chokepoint, and the SystemdConsole logging
convention. This file covers only what is specific to the monitor; where an ecosystem rule
has a monitor-specific manifestation, it's called out below.

## What this is

A Native-AOT daemon that samples **host + per-game-server** metrics straight from the Linux
kernel (`/proc`, `/sys`, cgroup v2 — never shelling `top`/`ps`), holds the latest frame in
memory, and serves it over a **unix-socket `GET /metrics`** (HTTP/1.1, unauthenticated,
pull-only). It is also the **single source of truth for metrics history**: a persist loop
writes the latest frame to a SQLite store (raw + rollup tiers) and `GET /metrics/history`
serves windowed queries (see `src/Monitor/History/`; kgsm-api relays this endpoint verbatim).
Authoritative docs:

- **`PLAN.md`** — full design, decisions, slice-by-slice tracker.
- **`docs/integration.md`** — the consumer contract (what kgsm-api / any scraper must handle).
- Wire shape lives in **`src/Monitor.Contracts/Snapshot.cs`** (not the stale
  `src/Monitor/Model/...` path `docs/integration.md` still cites).

## Commands

```bash
dotnet build                       # JIT build (kgsm-monitor.slnx)
dotnet test                        # golden-file suite (~60 tests)
dotnet test --filter "FullyQualifiedName~CpuSourceTests"   # one class/test

# AOT publish — this IS the lint gate. Expect 0 IL2026 / IL3050 / ILC warnings.
dotnet publish src/Monitor/Monitor.csproj -c Release -r linux-x64 -o artifacts/publish

# Run against a dev socket (no root / /run needed)
KGSM_MONITOR_SOCKET=/tmp/kgsm-monitor.sock ./artifacts/publish/kgsm-monitor &
curl --unix-socket /tmp/kgsm-monitor.sock http://localhost/metrics | jq

dotnet run -c Release --project bench/Monitor.Benchmarks -- --filter '*'   # perf (see bench/BASELINE.md)
./deploy/setup.sh                  # ONCE per host — asks for sudo; provisions the headless deploy grant
./deploy/deploy.sh                 # build + install AOT binary to /opt + systemd unit (no sudo, no prompts)
```

Bash under `deploy/` and `src/Monitor/bpf/` follows the ecosystem `shellcheck`-clean convention.

## Architecture

**Two self-ticking `BackgroundService`s, conflation/serve-latest.** Both compute a frame on
their own timer and publish via a single `volatile` reference swap — the HTTP scrape never
triggers a sample; it returns the precomputed `Latest` (503 until the first tick). Stale
frames are never queued.

- `MetricsSampler` (host, 1 Hz) — the always-on path. `Build()` assembles one `Snapshot` from
  the sampling sources. Stateful delta sources (`CpuSource`, `NetworkSource`, `DiskSource`) are
  primed once at start so the first published frame already carries rates.
- `ServerSampler` (per-server, **opt-in**: only wired when `KGSM_MONITOR_KGSM_PATH` is set;
  otherwise `servers` is always `[]`). Runs **two deliberately separate cadences**:
  - **Resync (slow, off the metrics tick):** lists KGSM instances via embedded kgsm-lib — a
    *process spawn*, the exact cost the metrics path avoids. A single-writer drain loop is the
    only writer of the `volatile` watch-list; KGSM lifecycle events on the event socket and the
    periodic floor both feed a coalescing `RequestResync()` (semaphore capped at 1), which is
    what keeps the swap lock-free. `diskBytes` (a directory walk) runs on its own slow loop too.
  - **Sample (fast, the host tick):** reads each watched server's counters from cheap kernel
    files. No spawn, no lock.

**Sampling-source pattern.** Each source splits live I/O from logic: a pure static `Parse` /
`ComputeRates` operating on a captured `/proc` string, kept golden-file-testable. Tests pin
expected values against fixtures in `tests/Monitor.Tests/Fixtures/`; the test project reaches
internals via `InternalsVisibleTo`. Add a metric → follow this split, add a fixture, pin a value.

**Three server `kind`s.** `systemd` / `container` are read from cgroup v2 files
(`CgroupSampler`); `native` (standalone, no cgroup) is summed from the `/proc` process tree
rooted at the instance `.pid` (`ProcTreeSampler`). The resolver partitions the watch-list on a
single liveness check so the two samplers' outputs are disjoint and concatenate.

**Per-server network = eBPF.** cgroup v2 has no network controller, so `rxBps`/`txBps` come from
a passive `cgroup/skb` byte counter (`src/Monitor/bpf/net_meter.bpf.c`) attached once to
`kgsm.slice`, read from a pinned BPF map. The pin path, map type, and key/value layout are a
**fixed contract** between `NetworkCgroupSource` and `deploy/net-meter-setup.sh` — change one,
change both. Setup is privileged + one-time (sudo); until then these fields read `null`.

## Monitor-specific gotchas

- **AOT cleanliness is non-negotiable.** Reflection-free by design. Config is read manually in
  `MonitorOptions.FromEnvironment()` (no config-binding source-gen); JSON goes through the
  source-generated `MonitorJsonContext`. A new serialized type must be registered there or it
  throws at runtime — the AOT publish (above) is how you catch it.
- **`null` ≠ `0` is a hard wire contract** (the ecosystem never-fabricate rule, concretely).
  `null` means "not measured": io without `IOAccounting=yes`, `diskBytes` before the first walk,
  `rxBps`/`txBps` when un-metered or the cgroup is outside `kgsm.slice`. Never substitute 0.
- **`Monitor.Contracts` is a separately-packed NuGet** consumed by kgsm-api. **Bump its
  `<Version>` on ANY change to `Snapshot.cs` or the JSON context** — NuGet caches by id+version,
  so a same-version repack serves a stale dll to kgsm-api. Compatibility is additive-only
  (consumers ignore unknown fields/`kind`s); see `docs/integration.md §7`.
- **kgsm-lib version is pinned and load-bearing** (`1.28.0` in `Monitor.csproj`). It resolves
  from the local feed in `nuget.config` (`/home/heisen/local-nuget`) before publish. The pin
  matters: `1.5.0` modelled `Instance.ports` as a string, but kgsm now emits a structured array,
  so an old pin throws on the detailed instance-list JSON and leaves `servers` permanently `[]`.
- **Two distinct sockets — don't conflate them.** `KGSM_MONITOR_SOCKET`
  (`metrics.sock`, default `/run/kgsm-monitor/`) is outbound: consumers scrape it.
  `KGSM_MONITOR_KGSM_SOCKET` (`monitoring.sock`) is inbound-only: KGSM pushes lifecycle events
  to it (best-effort; the resync floor is the source of truth).
- **Config file is `kgsm-monitor.settings.json`, not `appsettings.json`**, loaded explicitly
  from `AppContext.BaseDirectory` in `Program.cs` (the slim builder under systemd has no working
  dir, so default discovery finds nothing). It's logging-only; the monitor's own knobs are env vars.

## Version tracking

- **Version source:** `<Version>` in `src/Monitor/Monitor.csproj` (daemon) and `src/Monitor.Contracts/Monitor.Contracts.csproj` (NuGet contracts package — versioned independently)
- Bump the version whenever you make a user-facing change (new feature, bug fix, behaviour change). Patch for fixes, minor for new features, major for breaking changes.
- Update `CHANGELOG.md` under `## [Unreleased]` with a brief entry for every meaningful change.
- A git tag matching the new version should be created on release: `git tag v<version>`.
