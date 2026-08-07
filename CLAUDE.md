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

It reads **engine events** but stores none: the journal tells `ServerSampler` when the instance
list has changed, and that is the whole of its interest in them. Audit history is read from the
journal itself through kgsm-lib (`IEventJournalHistory`), by whoever wants it. Authoritative docs:

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
Monitor__SocketPath=/tmp/kgsm-monitor.sock ./artifacts/publish/kgsm-monitor &
curl --unix-socket /tmp/kgsm-monitor.sock http://localhost/metrics | jq

dotnet run -c Release --project bench/Monitor.Benchmarks -- --filter '*'   # perf (see bench/BASELINE.md)
./deploy/setup.sh                  # ONCE per host — asks for sudo; provisions the headless deploy grant
./deploy/deploy.sh                 # build + install AOT binary to /opt + systemd unit (no sudo, no prompts)
```

Bash under `deploy/` and `src/Monitor/bpf/` follows the ecosystem `shellcheck`-clean convention.

## Deploying

`deploy/setup.sh` runs **once per host** and is the only part that asks for sudo: it chowns
`/opt/kgsm-monitor` to you, puts the real unit in **user-owned** `/etc/kgsm-monitor/systemd/` with
`/etc/systemd/system/kgsm-monitor.service` symlinked to it, installs a polkit rule scoped to this
project's units, enables the unit, then verifies the grant by making the same unprivileged
`systemctl` calls the deploy will. It is idempotent — re-run it after changing what the host needs.

`deploy/deploy.sh` is then **fully headless: no sudo, no prompts.** The prefix is yours so
installing the AOT binary is a plain file write, a changed unit is a plain file write into the
user-owned directory, and every `systemctl` verb goes through the polkit grant. It refuses
**before building**, with *"run `deploy/setup.sh`"*, on an unprovisioned host. `deploy-common.sh`
holds the paths/units/helpers both scripts share; the three files are self-contained, so a
standalone clone deploys with no other repo checked out. Every `kgsm-*` repo carries this same
pattern.

If some *other* operation seems to need root, stop and ask — don't reintroduce `sudo` into
`deploy.sh`. The one genuinely privileged thing here is unrelated to delivering code: the eBPF
per-server network meter has its own one-time `deploy/net-meter-setup.sh`.

`deploy.sh` also installs **`deploy/kgsm-monitor.leaf.json`** — the leaf config descriptor — into
`/var/lib/kgsm/leaves/monitor.json`, unprivileged, before the binary swap. It declares every
`Monitor__*` knob so the Control Panel can render and edit them; the daemon never reads it.

**That file is generated, not written.** `TheKrystalShip.KGSM.LeafConfig` (the `kgsm-leafconfig`
repo, consumed as a build-only `PackageReference`) reads the `[LeafField]` attributes and `<panel>`
doc tags off `MonitorSettings` in the built assembly and rewrites it on every build — so **edit the
settings class, not the JSON**, and commit what the build produces. It also validates: a settings key
no field describes, a described key the settings file does not declare, an undocumented field, a bad
group or `dependsOn` reference all fail the build naming the key. `Monitor.csproj` configures it with
two properties, `LeafSettingsFile` and `LeafDescriptorFile`. Format and rules:
`tks/leaf-config-descriptor.md`; the mechanism: `kgsm-leafconfig/README.md`.

**Describing the daemon costs it no reflection and no dependency.** The attributes are compiled in as
source, the generator reads the assembly's metadata in its own process, and the package declares no
dependencies — so nothing reaches the AOT publish. Verified here: zero ILC warnings, no
`System.Reflection.MetadataLoadContext.dll` in `artifacts/publish/`, and none of the descriptor's
strings in the native binary.

## Architecture

**Two self-ticking `BackgroundService`s, conflation/serve-latest.** Both compute a frame on
their own timer and publish via a single `volatile` reference swap — the HTTP scrape never
triggers a sample; it returns the precomputed `Latest` (503 until the first tick). Stale
frames are never queued.

- `MetricsSampler` (host, 1 Hz) — the always-on path. `Build()` assembles one `Snapshot` from
  the sampling sources. Stateful delta sources (`CpuSource`, `NetworkSource`, `DiskSource`) are
  primed once at start so the first published frame already carries rates.
- `ServerSampler` (per-server, **opt-in**: only wired when `Monitor__KgsmPath` is set;
  otherwise `servers` is always `[]`). Runs **two deliberately separate cadences**:
  - **Resync (slow, off the metrics tick):** lists KGSM instances via embedded kgsm-lib — a
    *process spawn*, the exact cost the metrics path avoids. A single-writer drain loop is the
    only writer of the `volatile` watch-list; KGSM lifecycle events from the journal and the
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

- **AOT cleanliness is non-negotiable.** Reflection-free by design. Configuration binds through
  the config-binding source generator (on by default under `PublishAot`, so `Get<T>()` costs no
  reflection); JSON goes through the source-generated `MonitorJsonContext`. A new serialized type
  must be registered there or it throws at runtime — the AOT publish (above) is how you catch it.
- **A knob lives in two places**: a `MonitorSettings` property carrying `[LeafField]` and a
  `<panel>` doc tag, and a key in `kgsm-monitor.settings.json`. The descriptor is generated from the
  first and the defaults from the second, so there is no third place to keep in step. Miss either
  and the build fails naming the key — a property with no key has an invisible default, a key with
  no property binds to nothing.
- **`null` ≠ `0` is a hard wire contract** (the ecosystem never-fabricate rule, concretely).
  `null` means "not measured": io without `IOAccounting=yes`, `diskBytes` before the first walk,
  `rxBps`/`txBps` when un-metered or the cgroup is outside `kgsm.slice`. Never substitute 0.
- **`Monitor.Contracts` is a separately-packed NuGet** consumed by kgsm-api. **Bump its
  `<Version>` on ANY change to `Snapshot.cs` or the JSON context** — NuGet caches by id+version,
  so a same-version repack serves a stale dll to kgsm-api. Compatibility is additive-only
  (consumers ignore unknown fields/`kind`s); see `docs/integration.md §7`.
- **kgsm-lib version is pinned and load-bearing** (`3.0.0` in `Monitor.csproj`). It resolves
  from the local feed in `nuget.config` (`/home/heisen/local-nuget`) before publish. The pin
  matters: `1.5.0` modelled `Instance.ports` as a string, but kgsm now emits a structured array,
  so an old pin throws on the detailed instance-list JSON and leaves `servers` permanently `[]`.
- **The monitor owns exactly one socket.** `Monitor__SocketPath` (`metrics.sock`, default
  `/run/kgsm-monitor/`) is outbound: consumers scrape it. Engine events arrive the other way,
  from a **file** — `Monitor__KgsmJournalDir` (default `/var/lib/kgsm/events`), read-only,
  with the engine as sole writer and no reservation of any kind. The resync floor is the
  watch-list's source of truth; events only make it react sooner.
- **The monitor stores no engine events.** It tails the journal only so `ServerSampler` learns that
  the instance list moved, and it starts at the **tail with no cursor**: a replayed event would
  trigger a resync the periodic floor was going to do anyway. Don't give this consumer a cursor or a
  store — persisting what it reads is what coupled audit history to a metrics daemon.
- **`kgsm-monitor.settings.json` is the source of truth for every knob**, not `appsettings.json`
  (the ecosystem names these `kgsm-<leaf>.settings.json`). It is loaded explicitly from
  `AppContext.BaseDirectory` in `Program.cs`, because the slim builder under systemd has no working
  directory and default discovery finds nothing. An environment variable overrides one key of it
  by spelling the path with `__` (`Monitor__IntervalMs`); a variable naming a key the file does not
  declare binds to nothing.
- **`AddEnvironmentVariables()` is re-registered after the settings file, and the order is
  load-bearing.** Configuration resolves by source order and the file is appended after everything
  the builder installed — including its own environment provider. Drop that line and the file
  outranks every `Monitor__*` and `Logging__*` variable, so an override reads as applied while
  changing nothing.

## Version tracking

- **Version source:** `<Version>` in `src/Monitor/Monitor.csproj` (daemon) and `src/Monitor.Contracts/Monitor.Contracts.csproj` (NuGet contracts package — versioned independently)
- Bump the version whenever you make a user-facing change (new feature, bug fix, behaviour change). Patch for fixes, minor for new features, major for breaking changes.
- Update `CHANGELOG.md` under `## [Unreleased]` with a brief entry for every meaningful change.
- A git tag matching the new version should be created on release: `git tag v<version>`.
