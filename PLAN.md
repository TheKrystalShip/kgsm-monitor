# KGSM Metrics Monitor — Plan & Design Record

> Living document. Captures the goal, the decisions and *why*, the KGSM facts we
> verified, and a slice-by-slice work tracker. Project name `kgsm-monitor` /
> namespace `TheKrystalShip.KGSM.Monitor` follows the `kgsm-*` convention.
>
> **Status:** Slice 1 (host-only) **complete**; **Slice 2a (per-server cgroups +
> embedded kgsm-lib) complete** — resolver + `CgroupSampler` (stat-and-skip) +
> off-tick resync, AOT-proven live (48 µs/server). **Slice 2b (event-driven delta)
> complete** — `EventService` on `monitoring.sock` nudges a coalesced authoritative
> resync (single-writer drain loop), 44 tests incl. a real-socket round-trip,
> AOT-proven live (`socat` push → resync). **Slice 3 (native-standalone fallback)
> complete** — `ProcTreeSampler` walks the `/proc` ppid tree for servers with no
> cgroup, summing CPU/RSS/IO; gated single scan, PID-recycle guard,
> AOT-proven live (busy native server read `cpuPctCore≈100`). **Inc 4 (2026-06-14)** —
> natives are now **cgroup-first**: kgsm-lib 1.5.0 surfaces `Instance.CgroupPath`
> (`kgsm.slice/<inst>`, the path kgsm-watchdog creates), so a native whose cgroup is
> live is sampled by `CgroupSampler` and the `/proc` tree is the fallback only for
> natives with no live cgroup (partition arbiter = `ServerCgroupResolver.FirstExisting`,
> so each server is sampled by exactly one path). See §11 Validation log.
>
> **Inc 5 (2026-06-23) — per-server disk footprint + lib re-pin (Contracts 1.2.0).**
> `ServerMetrics.diskBytes` — the on-disk size of an instance's working dir — is now on
> the frame, from a new `DiskUsageSampler`: a directory walk on its own slow cadence
> (`KGSM_MONITOR_DISK_USAGE_MS`, default 60 s, volatile-swap cache), merged onto each
> running server in `ServerSampler.Sample()`. The walk skips symlinks (no double-count),
> is apparent-size, and is `null` (never 0) until walked / when unreadable. **Also re-pinned
> kgsm-lib 1.5.0 → 1.22.0:** 1.5.0 modelled `Instance.ports` as a string, but kgsm now emits
> the structured `ports` array (canonical-port-format, lib 1.10.0) — on the old pin the
> per-server resync threw on the instance-list JSON and `servers[]` was permanently empty.
> **Per-server network stays deferred** (D8) — confirmed 2026-06-23: native servers share
> the host netns and UDP has no per-socket byte counters, so it needs continuous root-level
> packet accounting, which neither the read-only monitor nor the **socket-activated**
> `kgsm-firewall` (not a resident daemon) can provide. Live-validated: factorio-test
> `diskBytes` byte-exact vs `du -b`; AOT publish 0-warning. See §11 Validation log.
>
> **Logging** follows the ecosystem convention (`../logging-convention.md`):
> `Microsoft.Extensions.Logging` → `AddSystemdConsole()` (journald `<N>` priority prefix),
> levels from `appsettings.json` `Logging` + env (`Logging__LogLevel__Default`, default
> `Information`). The `CreateSlimBuilder` host binds the section explicitly via `AddConfiguration`.

---

## 1. Goal

Stream real-time host **and per-game-server** metrics (CPU / memory / disk /
network) from a host machine to live React dashboards, at ~1 Hz, **without
nuking the host** — the box also runs game servers whose CPU/RAM are spoken for.
Think `htop`/`btop` efficiency: read what the kernel already maintains, cheaply.

```
[monitor daemon]  --scrape GET /metrics-->  [KGSM API: auth + fan-out]  --SSE-->  [React SPA]
  reads /proc, /sys, cgroup v2            relays, computes nothing       dashboards
  computes all rates, holds latest        enforces Discord-OAuth         (filter client-side)
  unix socket, unauthenticated, root
```

## 2. Why this shape (the context)

- **htop/btop are cheap because they read `/proc` + `/sys`** — virtual files backed
  by counters the kernel already keeps. Reading them is microseconds and a few KB.
  The expensive thing is **spawning `top`/`ps`** every second. So: read `/proc`
  directly, never shell out.
- **Rates force a stateful sampler.** CPU%, network throughput, and disk I/O are
  *derivatives* — `/proc` gives cumulative counters, so a value needs `(s₂−s₁)/Δt`,
  i.e. two samples over time. A stateless REST call can't compute a rate without
  either blocking ~1 s or holding prior state anyway. So the monitor **self-ticks
  at 1 Hz**, holds `previous`+`current`, and publishes the latest snapshot.
- **Scrape ≠ sample.** The HTTP handler returns the last precomputed frame; it never
  triggers a sample. That keeps the monitor **consumer-agnostic** (it knows nothing
  about the API or clients) *and* correct (a per-request sample would make rates
  depend on caller timing and multiply cost per consumer).
- **Fan-out lives in the API, not the monitor.** The API opens one scrape and
  re-broadcasts to N browser clients over its existing authed SSE → sampling stays
  O(1) in client count system-wide.

## 3. Decisions (resolved)

| # | Decision | Why |
|---|----------|-----|
| D1 | Read `/proc` `/sys` cgroup v2 directly; never spawn `top`/`ps` | Process-spawn is the real cost; kernel counters are ~free |
| D2 | Self-tick 1 Hz, serve latest precomputed snapshot (conflation, latest-wins) | Rates need two samples; conflation never serves stale |
| D3 | Consumer-agnostic monitor; **API** does auth + fan-out | Separation of concerns; O(1) sampling vs clients |
| D4 | Scrape (API pulls) over push | Monitor shouldn't know its consumers; pull is simpler here |
| D5 | **.NET 10**, Native AOT | Latest tier; small footprint/instant start fits "don't steal RAM"; AOT discipline = the fast path |
| D6 | Transport = **unix domain socket**, unauthenticated, root-only reachable | No exposed port; FS perms are the boundary; API enforces OAuth |
| D7 | Runs as **root** | Needs `/proc/<pid>/io` + all cgroups; KGSM systemd units are **system-wide** |
| D8 | **Host-level network only**; per-server net deferred | cgroups don't account network without eBPF/netns — conscious scope cut |
| D9 | **Embed `kgsm-lib`** for KGSM integration (Slice 2), not re-implement socket/CLI | Single KGSM chokepoint; lib is now AOT-safe (see §7) |
| D10 | source-gen JSON + source-gen logging + span `/proc` parsing | Keeps the binary reflection-free / trim-AOT-clean |

## 4. Architecture principles
- Sample, don't poll. · Self-tick, serve-latest. · Consumer-agnostic. · Reflection-free (AOT).

## 5. Tech baseline
- **.NET 10**, `Microsoft.NET.Sdk.Web`, `PublishAot`, `IsAotCompatible`, `InvariantGlobalization`.
- HTTP via `WebApplication.CreateSlimBuilder` + minimal API (Request Delegate Generator is AOT-safe); Kestrel `ListenUnixSocket`.
- `System.Text.Json` **source generator** (`MonitorJsonContext`); span-based `/proc` parsing.
- **Deps: zero** for Slice 1 (host metrics need nothing from KGSM). `kgsm-lib` (net9, now AOT-safe) is added in Slice 2 — a net10 app consuming the net9 lib under AOT is fine (verified pattern).

## 6. KGSM integration facts (verified from source — for Slice 2)
These were read out of the KGSM repo / `kgsm-lib`, not assumed:
- **`LifecycleManager` ∈ {`systemd`, `standalone`}** only (`commands/handlers/lifecycle.sh`). Containers run under **`standalone`** too.
- **The `.pid` file is overloaded** (`instance_pid_file = ${instance_working_dir}/.${instance_name}.pid`): a **real PID** for native processes, a **Docker container id** for container instances. So the sampler key is **`(LifecycleManager, isContainer)`**, where `isContainer` = instance has a `compose_file` — *not* `LifecycleManager` alone.
- **systemd unit name is deterministic**: `${instance_name%.ini}` (no prefix) → cgroup at `/sys/fs/cgroup/system.slice/<unit>.service` (system-wide, root-readable).
- **Events** (`docs/events.md`): consumer owns the socket, KGSM connects via `socat`. Supports a dedicated `monitoring.sock` alongside the bot's `kgsm.sock` (`event_socket_filenames=kgsm.sock,monitoring.sock`). Payload carries `InstanceName` + `LifecycleManager`, **not** PID/cgroup → a resolve step is always needed. Events are **best-effort, only fire via `kgsm.sh`** → can't be the source of truth.
- Therefore watch-list = **list + watch**: periodic `InstanceService.GetAll()` resync is truth; socket events are the low-latency delta. The resync also supplies the resolve inputs (kind, working-dir, compose-file).

## 7. Prerequisite — kgsm-lib AOT compliance ✅ DONE
`kgsm-lib` was converted to System.Text.Json source generation and marked
`IsAotCompatible` (merged to `main`, commit `feat(json): source-generate JSON…`).
Proven: 0 IL warnings, no test regressions, and a Native AOT consumer publishes
0-warning and deserializes correctly. This is what makes **D9 (embed the lib)**
viable. Details in the memory note `system-metrics-monitor`.

## 8. Project layout (as built)
```
kgsm-monitor/
  kgsm-monitor.slnx
  README.md · PLAN.md · .gitignore
  bench/
    BASELINE.md               # committed perf baseline (frame 1.61ms, Disk = 96.9%, judgment)
    Monitor.Benchmarks/       # BenchmarkDotNet: Frame / per-Source / pure-Parse tiers
  deploy/
    install.sh                # publish + install binary + unit (root; --enable to start)
  src/Monitor.Contracts/      # SHARED GET /metrics wire contract (its own packable project)
    Monitor.Contracts.csproj  # Sdk classlib, net10, IsAotCompatible; PackageId TheKrystalShip.KGSM.Monitor.Contracts
    Snapshot.cs               # host DTO graph (records) — namespace TheKrystalShip.KGSM.Monitor.Contracts
    MonitorJsonContext.cs     # public [JsonSerializable(typeof(Snapshot))] source-gen, camelCase — shipped with the contract
  src/Monitor/
    Monitor.csproj            # Sdk.Web, net10, PublishAot, IsAotCompatible, AssemblyName=kgsm-monitor; ProjectReference -> Monitor.Contracts; InternalsVisibleTo tests
    Program.cs                # CreateSlimBuilder, DI, Kestrel unix socket, socket chmod, GET /metrics + /health
    MonitorOptions.cs         # env-var config (interval, socket path+mode, mount/iface deny) — AOT-safe
    Sampling/                 # each source: pure Parse + ComputeRates helpers (golden-file testable)
      MetricsSampler.cs       # BackgroundService + PeriodicTimer(options.IntervalMs); volatile latest; conflation; optional ServerSampler
      CpuSource.cs            # /proc/stat cpu+cpuN jiffies delta -> %
      MemorySource.cs         # /proc/meminfo (instant, MemAvailable)
      NetworkSource.cs        # /proc/net/dev delta -> bps/pps per iface (lo + deny-prefixes excluded)
      DiskSource.cs           # DriveInfo usage (pseudo-fs + deny filtered) + /sys/block/*/stat IO delta
      SystemSource.cs         # /proc/loadavg, /proc/uptime, hostname
      ServerSampler.cs        # [Slice 2] hosted; volatile watch-list, off-tick KGSM resync; exposes Sample(). [2b] subscribes to KGSM lifecycle events → coalesced resync nudge (single-writer drain loop)
      ServerCgroupResolver.cs # [Slice 2] (lifecycle, is-container) -> candidate cgroup paths; never fails
      CgroupSampler.cs        # [Slice 2] stat-and-skip; cpu.stat/memory.current/pids.current/io.stat(opt); pure Parse/ComputeRates
      ProcTreeSampler.cs      # [Slice 3] native-standalone fallback: gated /proc stat scan -> ppid tree -> sum utime+stime/RSS/io; sysconf(_SC_CLK_TCK) p/invoke; PID-recycle guard
    deploy/
      kgsm-monitor.service    # hardened systemd unit (root, Restart=always, RuntimeDirectory socket, group-share recipe)
  tests/Monitor.Tests/        # 60 tests: golden-file /proc + cgroup parse, resolver, config, [2b] event wiring + real-socket integration, [3] proc-tree
    ServerEventTests.cs       # [2b] fake IEventService wiring + real UnixSocketClient/EventService envelope round-trip
    ProcTreeSamplerTests.cs   # [3] stat/statm/io parse + tree build/walk + cpu rate (pure) + synthetic-/proc Sample (membership, recycle guard, cost gate)
    Fixtures/                 # stat.a/b, netdev.a/b, meminfo, loadavg, uptime, cgroup.* (live captures)
```

## 9. Snapshot shape (host)
> **Shared as a package (the contract, build-time-solid).** The `Snapshot` graph + its
> source-gen camelCase `MonitorJsonContext` live in **`src/Monitor.Contracts/`** and ship as
> **`TheKrystalShip.KGSM.Monitor.Contracts`** (packed to the local feed). The monitor
> references it by project (so a shape change is a compile break here) and serializes
> `GET /metrics` with its context; **kgsm-api consumes the same package and deserializes with
> the same context** — the wire shape and naming cannot drift between producer and consumer.
> ⚠ **Drift rule:** NuGet caches by `id+version`, so any contract change MUST bump the package
> `Version` (and every consumer's `<PackageReference>`); a same-version repack is silently
> served stale. Loop: edit → bump `Version` → `dotnet pack -c Release -o /home/heisen/local-nuget`.
```jsonc
{
  "ts": 1781184473162, "intervalMs": 1000, "hostname": "...", "uptimeSec": 182285,
  "cpu":  { "totalPct": 2.4, "perCore": [..], "load": { "one": .49, "five": .34, "fifteen": .2 } },
  "mem":  { "totalKb": .., "availableKb": .., "usedKb": .., "usedPct": 25.1, "swapTotalKb": .., "swapUsedKb": .. },
  "disk": { "mounts": [ { "mount": "/", "fs": "ext4", "totalBytes": .., "usedBytes": .., "usedPct": 24 } ],
            "io": { "readBps": .., "writeBps": .. } },
  "net":  { "ifaces": [ { "name": "enp4s0", "rxBps": .., "txBps": .., "rxPps": .., "txPps": .. } ] }
}
```

## 10. Work tracker

### Slice 1 — host-only (the vertical slice)
- [x] Scaffold: sln + AOT web project (net10, `Sdk.Web`, `PublishAot`)
- [x] `MetricsSampler` BackgroundService — PeriodicTimer(1s), volatile latest, conflation
- [x] CPU (`/proc/stat`, aggregate + per-core), Memory (`/proc/meminfo`)
- [x] Network (`/proc/net/dev` rates), Disk usage (`DriveInfo`) + IO (`/sys/block/*/stat`)
- [x] Load / uptime / hostname
- [x] `Snapshot` + source-gen JSON; `GET /metrics` (503 until first frame) + `/health` over unix socket
      (the unified ecosystem liveness/availability path — renamed from `/healthz` 2026-06-15)
- [x] Native AOT publish: **0 IL warnings**, native ELF; live-validated (§11)
- [x] systemd unit (draft)
- [x] Validate under **sustained** CPU load + continuous scrape; **measured** self-cost ≈ nil (§11)
- [x] Refine filters: pseudo-fs always filtered + configurable extra mount/iface deny; default deny `veth`
- [x] Golden-file parse tests (captured `/proc` fixtures) in `tests/Monitor.Tests` — **23 tests, green**
- [x] Socket perms via `ApplicationStarted` chmod (default `0660`); group-sharing recipe in the unit
- [x] Config: interval, socket path, socket mode, iface/mount deny — all env vars (`MonitorOptions`)
- [x] Install/deploy: `deploy/install.sh` + hardened unit written & validated (enable on host = user action)

### Slice 2a — per-server via cgroups + embed kgsm-lib ✅ COMPLETE
- [x] Add `kgsm-lib` ProjectReference (net9 lib in net10 AOT app) — builds 0-warning; AOT publish 0 ILC, 11 MB ELF, 0 `libcoreclr`
- [x] Confirm cgroup v2 (`stat -fc %T /sys/fs/cgroup` → `cgroup2fs`) ✓. **Docker cgroup driver deferred** (Docker not running this session — container path built but unmeasured; stat-and-skip makes it safe)
- [x] Periodic `InstanceService.GetAll()` resync = source of truth — own slow timer (`KGSM_MONITOR_RESYNC_MS`, default 15 s), off the metrics tick. **Proven live under AOT** (`server resync: 1 instance(s) known`)
- [x] Resolver `ServerCgroupResolver`: `(isContainer, CgroupPath)` → candidate cgroup paths; **never fails** (liveness = stat-and-skip at sample time). isContainer = non-empty `compose_file` (§6). container→`docker-<id>.scope`|`docker/<id>`; native→`Instance.CgroupPath` (`kgsm.slice/<inst>`) when KGSM supplies it (Inc 4), else none (Slice 3 `/proc` fallback). `Kind` stays `"native"` either way.
- [x] `CgroupSampler`: `cpu.stat` usage_usec→`cpuPctCore` rate, `memory.current`, `pids.current`, `io.stat` (**opt-in: null when absent**, needs `IOAccounting=yes`). Pure `Parse`/`ComputeRates` helpers
- [x] Extend snapshot: `servers: [{ id, name, kind, cpuPctCore, memBytes, ioReadBps?, ioWriteBps?, pids }]` (always present, empty when none)
- [x] Wired DI conditionally (`KGSM_MONITOR_KGSM_PATH` set ⇒ per-server on; else host-only). 17 new golden/resolver tests (**40 total, green**); per-server cost measured **48 µs/server** (`bench/BASELINE.md` Slice 2 addendum)

### Slice 2b — event-driven watch-list delta ✅ COMPLETE
- [x] `EventService` bound to `monitoring.sock` (monitor owns the socket; KGSM connects via `socat`); `Initialize()` + handlers for `instance_started/stopped/removed/uninstalled`. **Design: event = *nudge*, not a partial delta** — the payload carries only `InstanceName` (+`LifecycleManager`), not the cgroup-resolution inputs (compose-file/pid-file/unit), so a true "add" needs a lookup anyway. Each handler instead signals an immediate authoritative `GetAll()` resync, which keeps `_watch` **single-writer** (lock-free volatile swap) and self-heals on the best-effort channel. Latency drops from "≤`ServerResyncMs`" to "sub-second"; the periodic resync stays the floor.
- [x] Coalescing: `SemaphoreSlim(0,1)` + a single drain loop is the sole `_watch` writer; both the periodic floor and events feed `RequestResync()`. A burst (or event mid-resync) collapses to ≤1 extra resync (a pending `Release` throws `SemaphoreFullException` → swallowed = the coalesce). `EventsEnabled` (`KGSM_MONITOR_EVENTS`, default on) is the kill-switch → resync-only fallback.
- [x] Tests: 4 new (**44 total, green**) — wiring (fake `IEventService` asserts the four handler types + `Initialize`), event→resync nudge, events-disabled, and a **real-socket integration** test (real `UnixSocketClient`+`EventService` on a temp socket; a hand-written KGSM envelope round-trips through `KgsmJsonContext` → resync). AOT publish still **0 ILC warnings**, 11 MB ELF, 0 `libcoreclr`. Live socket smoke proven (§11).
- [ ] Re-measure container path + `SourceBenchmarks.Disk` once Docker + a running fleet are present

### Slice 3 — standalone-native fallback ✅ COMPLETE
- [x] `ProcTreeSampler`: `.pid` → root PID → invert `/proc/*/stat` `ppid` links → BFS the subtree → sum `utime+stime` (→ `cpuPctCore`), RSS (`statm` resident × page size), `/proc/[pid]/io` `read_bytes`/`write_bytes`. Emitted as `kind:"native"` alongside the cgroup servers (the two samplers cover disjoint sets, so `ServerSampler.Sample()` concatenates). Routed by the resolver's `Kind=="native"` **and** no live cgroup dir (Inc 4): a broken container isn't misrouted here, and a native whose `kgsm.slice/<inst>` cgroup exists is sampled by `CgroupSampler` instead — claiming it here too would double-count.
- [x] **One gated global scan.** `ppid` lives only in `/proc/<pid>/stat`, so inverting the tree reads `stat` for *every* host process — cost scales with **host process count, not native-server count** (one scan serves all natives). Gated: skipped entirely when the watch-list has no native server. `statm`/`io` read for tree members only. `sysconf(_SC_CLK_TCK)` p/invoke (`[LibraryImport]`, AOT-clean; `AllowUnsafeBlocks` for the generated stub) with a `100` USER_HZ fallback so a wrong constant/failed call degrades correctly rather than fabricating CPU%.
- [x] **PID-recycle guard:** the `.pid` file holds a kernel-reused number, so the root PID's `starttime` (stat field 22) is pinned at first observation; a changed `starttime` means the file now points at a foreign process → skip that tick + drop state → re-prime next tick (stat-and-skip parity with the cgroup path). `Instance` carries no runtime status (verified — pure config), so this structural guard, not a running-flag filter, is the mechanism.
- [x] Tests: 16 new (**60 total, green**) — pure helpers (comm-with-parens `stat` parse, `statm`, `io`, tree build/walk incl. a `ppid`-cycle, cpu rate math) + synthetic-`/proc` `Sample` (tree membership excludes unrelated procs, first-tick zero rates, recycle drop-then-re-prime, native-less cost gate). AOT publish **0 ILC warnings**, 11 MB ELF, 0 `libcoreclr`; **live-proven** (§11). Bench `ServerNative` = **3.4 ms** on a 288-proc host.
- [ ] Re-measure container path + `SourceBenchmarks.Disk` once Docker + a running fleet are present (carried from 2b)

### Later (out of build slices)
- [ ] **API relay**: KGSM API opens one scrape/subscription, re-fans-out over authed SSE, caches latest for instant first frame
- [ ] **React SPA** dashboards (per-process/per-server filtering client-side)
- [ ] **Per-server network** via eBPF/nftables/conntrack (the deferred metric) — **confirmed deferred 2026-06-23**: needs a *resident, privileged* packet-accounting source. The read-only monitor won't escalate, and `kgsm-firewall` is socket-activated (invoked on demand, not a daemon) so it can't count continuously. No honest source today; field omitted, not faked (see `docs/integration.md §3.6`).
- [x] **Per-server disk footprint** (`diskBytes`) — Inc 5, 2026-06-23 (working-dir walk, slow cadence)

## 11. Validation log
**2026-06-11 — Slice 1 host sampler, Native AOT, live:**
- `dotnet publish -r linux-x64` (PublishAot) → 0 IL warnings; 9.7 MB native ELF, **0 `libcoreclr` links**.
- `curl --unix-socket` `GET /metrics` returned a full snapshot: 16 per-core CPU, load, memory 25.1%, mounts (`/` ext4, `/boot` vfat; pseudo-fs incl. `efivarfs` filtered), per-iface net rates.
- **Liveness:** `ts` advanced exactly 2000 ms across two reads; `cpu.totalPct` 2.4 → 7.9 under a 2 s single-core burn.
- **Correctness:** monitor `load` `[0.62,0.37,0.21]` == `/proc/loadavg`. 503 before first tick, 200 after. Responses 0.1–0.7 ms.

**2026-06-11 — Slice 1 polish (config, filters, tests, perms, deploy, load test):**
- Sources split into pure `Parse` + `ComputeRates` helpers; **23 golden-file tests** (`tests/Monitor.Tests`) pin the values from the live run above (CPU 8.2% agg / core1 72.5%, mem 15.6%, net rates at dt=1s, load/uptime, mount filters, octal/csv config). All green.
- Refactor + `MonitorOptions` re-published Native AOT: still **0 ILC warnings**, 9.7 MB ELF, 0 `libcoreclr` links.
- **Socket perms:** with `KGSM_MONITOR_SOCKET_MODE=660`, the chmod (in `ApplicationStarted`, after the socket exists) produced `srw-rw---- (0660)`.
- **Self-cost under load (the claim, measured):** all 16 cores burned with `dd` + continuous scraping. From `/proc/<pid>/stat`: monitor used **8 jiffies = 0.080 CPU-s over a 26 s window = 0.31 % of one core / 0.019 % of the host**. RSS ~25 MB.
- **Correctness under load:** during an active 16-core burst the monitor reported peak `cpu.totalPct` **100 %**; 14/14 scrapes 200 OK across both bursts; `/health` 200 after sustained load; frames stayed fresh (`ts` advanced continuously). (`stress-ng`/`iperf3`/`fio` absent on host → CPU via `dd`; net/disk throughput correctness covered by golden fixtures.)
  - The CPU-only self-cost figure is **representative, not partial**: the monitor reads the same fixed set of `/proc`+`/sys` files every tick regardless of load *type*, so its own cost is load-type-independent.
- **Array integrity post-refactor:** full-body scrape after the deny-list rewiring shows `disk.mounts` = `/` (ext4) + `/boot` (vfat), `net.ifaces` = `enp4s0`+`wlp5s0` (`lo`/pseudo-fs excluded), 16 per-core entries — the empty-array case a `totalPct`/`ts` grep can't catch.
- **Deploy:** `deploy/install.sh` `bash -n` + shellcheck clean; hardened unit passes `systemd-analyze verify` (only flags the not-yet-installed binary). Not enabled on the host (user action).

**2026-06-11 — Performance baseline (BenchmarkDotNet, full results in `bench/BASELINE.md`):**
- **Full diagnostic frame = 1.61 ms** (398 KB alloc) → **0.16 % of the 1000 ms tick budget**; ceiling ~620 frames/s (~620× headroom at 1 Hz). Serialize (source-gen JSON) = **1.85 µs** — the scrape is effectively free.
- **Disk = 96.9 % of the frame** (`DriveInfo.GetDrives()` statvfs + `/sys/block`); every other source 16–58 µs. Pure parse = 0.7 % → frame is **syscall-bound**, so this JIT baseline ≈ the AOT artifact within ~1 % (AOT toolchain run not needed yet).
- Validity: frame ≈ Σ(sources) on both latency *and* allocation (397.98 KB ≈ 397.85 KB). Levers noted for pushing rate (read `/proc/self/mountinfo` + `statvfs` survivors instead of `DriveInfo` → kills scaling; decouple disk-usage cadence → ~50 µs frame; span-split to cut allocs).
- ⚠️ **Caveat:** captured on an **idle host (24 mounts)**; Disk cost scales with mount count, which grows with containerized servers — so 1.61 ms is a clean-host floor. **Re-measure `SourceBenchmarks.Disk` in Slice 2** when containers are present. Even 5–10× is ~1 % of the tick, so viability holds; the per-server cgroup reads (~tens of µs each) still leave huge headroom.

**2026-06-11 — Slice 2a per-server cgroups, embedded kgsm-lib, Native AOT, live:**
- **Embed + AOT:** monitor (net10 AOT) references `kgsm-lib` (net9, `IsAotCompatible`). `dotnet publish -r linux-x64 -p:PublishAot=true` → **0 ILC warnings**, 11 MB native ELF (was 9.7 MB), **0 `libcoreclr` links**.
- **Runtime smoke (the AOT proof that matters):** ran the published binary with `KGSM_MONITOR_KGSM_PATH` set. It spawned `kgsm.sh instances list --detailed --json`, the embedded lib **deserialized the instance list under AOT** (`server resync: 1 instance(s) known` — the `7dtd` instance), and `GET /metrics` served a frame carrying the new `servers` array. A 0-warning ILC count alone wouldn't have caught a reflection fallback in the DI graph / `ProcessRunner` — running it did.
- **Stat-and-skip validated:** `7dtd` is **standalone-native + stopped** → not cgroup-addressable → correctly absent from `servers` (no crash, no zero-row). The resolver returns a candidate path that simply doesn't exist; the sampler skips it.
- **Live cgroup read proven** (throwaway probe, since no game-servers run): `CgroupSampler.Sample` against the real `ollama.service` cgroup returned `kind=systemd, memBytes≈547 MB, pids=45, io=null` — matching a hand `cat` of the same files. `io=null` confirms the opt-in path (services default `IOAccounting=no`).
- **Env facts (verified on host):** `/sys/fs/cgroup` = `cgroup2fs`; every `system.slice/*.service` exposes `cpu.stat`(`usage_usec`)/`memory.current`/`pids.current`, but **`io.stat` is absent** (root `subtree_control` = `cpu memory pids`, `DefaultIOAccounting=no`). Per-server **disk-IO is therefore opt-in**, a conscious scope parallel to the host-only/no-per-server-network cut.
- **Tests:** 17 new (cgroup parse against real captures + deterministic `ComputeCpuPctCore` + resolver path construction) → **40 total, green**. Committed suite stays deterministic (the live probe was throwaway).
- **Perf:** host frame **unchanged at 1,610 µs** (server-less path = null-check; no regression). Per-server cgroup read **= 48 µs** against a live cgroup → 100 servers ≈ 4.8 ms ≈ 0.5 % of the 1 s tick. See `bench/BASELINE.md` Slice 2 addendum.
- **Host-only path (no KGSM) re-verified after the DI change:** `MetricsSampler` now takes `ServerSampler? = null`; with `KGSM_MONITOR_KGSM_PATH` unset, `ServerSampler` is never registered. Ran the AOT binary with no `KGSM_*` vars → started clean (no `Unable to resolve service`), served `servers: []` + 16 cores / 2 mounts / 2 ifaces. The built-in DI container honours the optional-parameter default on the real resolve call-site, not just in the benchmark's direct `new`.

**2026-06-11 — Slice 2b event-driven watch-list delta, Native AOT, live:**
- **Design:** events *nudge* an authoritative resync rather than apply a partial delta — the payload lacks the cgroup-resolution inputs, so re-listing via the proven `GetAll()` keeps `_watch` single-writer (lock-free swap) and self-heals on the best-effort channel. One `SemaphoreSlim(0,1)` + a single drain loop serialize the periodic floor and event nudges; bursts coalesce to ≤1 extra resync.
- **Tests:** 4 new (**44 total, green**). The headline is a **real-socket integration test**: a real `UnixSocketClient`+`EventService` on a temp socket, a hand-written `instance_started` envelope pushed over the wire, asserted to round-trip through the source-generated `KgsmJsonContext` and bump an authoritative resync (1→2). Plus socket-free fakes for handler-set wiring, the event→resync nudge, and the events-disabled fallback.
- **AOT:** `dotnet publish -r linux-x64` → **0 ILC warnings**, 11 MB ELF, **0 `libcoreclr`** — the generic `RegisterHandler<T>` + event deserialization is exactly where a reflection fallback would have hidden; ILC stayed clean.
- **Live socket smoke (the AOT proof that matters):** ran the published binary with the event socket enabled and `KGSM_MONITOR_KGSM_PATH` pointed at a stub that logs each call + returns `{}`. Both sockets bound; all four handlers registered; the prime resync spawned the stub once. Pushing the envelope via `socat … UNIX-CONNECT:` produced the full chain in the log — `Received event message: 167 bytes` → `Processing event of type instance_started` → `KGSM event for 7dtd; requesting resync` → a **second** `server resync` (stub spawned exactly twice). The `EventWrapper`+`InstanceStartedData` deserialized **under AOT**; a reflection fallback would have thrown `NotSupportedException` at that line.
- **Validation scope (honest):** this proves the **monitor side** end-to-end (binds the socket, receives a real envelope, deserializes under AOT, nudges a resync). It does **not** exercise the real KGSM→`socat`→monitor path — that needs KGSM's event socket configured (`event_socket_filenames=…,monitoring.sock`) and a real instance operation, since events only fire via `kgsm.sh` (§6). The envelope used is byte-for-byte the documented KGSM wire format (`docs/events.md`).

**2026-06-11 — Slice 3 standalone-native process-tree fallback, Native AOT, live:**
- **Design:** native-standalone servers have no cgroup, so `ProcTreeSampler` reads `/proc` directly: one **gated** global `stat` scan inverts the `ppid` links (the only place `ppid` lives), then each native server's root PID (its `.pid` file) seeds a BFS over its subtree; per-process `utime+stime`/RSS/`read_bytes`+`write_bytes` are summed. The two samplers own disjoint server kinds, so `ServerSampler.Sample()` just concatenates cgroup + native results.
- **Accuracy limits (documented, not fabricated):** a live-tree CPU sum **cannot** recover CPU from children that exited between ticks (a cgroup's cumulative counter can) — a negative delta from a vanished child clamps to 0, biasing a churn-heavy server slightly low; `cutime/cstime` were rejected (reaped-only, double-counting). Summed RSS double-counts shared pages (overcount vs `memory.current`). Both are in the `ProcTreeSampler` class remarks and §12.
- **AOT:** `dotnet publish -r linux-x64` → **0 ILC warnings**, 11 MB ELF, **0 `libcoreclr`**. The new surface is the `sysconf(_SC_CLK_TCK)` `[LibraryImport]` p/invoke (needed `AllowUnsafeBlocks` for the generated marshalling stub); ILC stayed clean and `libc.so.6` is dynamically linked.
- **Live smoke (the AOT proof that matters):** ran the published binary with `KGSM_MONITOR_KGSM_PATH` pointed at a stub returning one native-standalone instance whose `.pid` points at a **core-pinning** process, `KGSM_MONITOR_EVENTS=0`. `GET /metrics` served `servers:[{id:survival, kind:"native", cpuPctCore:100, memBytes≈13.6 MB, pids:2, ioReadBps:0, ioWriteBps:0}]`. `cpuPctCore≈100` (exactly one core) is the decisive check: it only reads right if the `sysconf` p/invoke **resolved under AOT and returned 100** — a failed/garbage call would skew it by orders of magnitude (the `100` fallback would also land it right). `pids:2` confirms the tree walk caught the spinner's child; an idle first-tick probe (killed too early) had correctly shown `cpuPctCore:0` (rate needs two samples).
- **Perf (measured, `bench/Monitor.Benchmarks` `ServerNative`):** **3.4 ms / 3.17 MB** for one native server on a **288-process** host (≈12 µs/process). This is the heaviest single source (vs 1.61 ms whole host frame, 48 µs/server cgroup) **but**: (a) it's **flat in native-server count** — one shared scan serves all natives, so 1 or 50 native servers cost the same ~3.4 ms; (b) it's **gated to zero** when no native servers exist (the common systemd/container fleet); (c) even unconditional it's 0.34 % of the 1 s tick. If a host ever runs many natives *and* the tick must tighten, decouple the proc scan onto its own sub-cadence (the scan is the cost, not the per-server summation).
- **Validation scope (honest):** monitor side proven end-to-end under AOT (real `/proc`, real busy process, real `sysconf`, correct rate). Not exercised: a real KGSM-managed native instance (no fleet this session) — but the `.pid`→PID contract and the `/proc` reads are kernel-standard, and the synthetic-`/proc` unit tests pin the parse/tree/guard logic deterministically.

## 12. Open questions
- ~~Socket location/perms for the API consumer~~ → **resolved**: socket chmod `0660` (configurable), unit ships a `Group=kgsm` group-sharing recipe (commented). Final call (root-API vs shared group) is a deploy-time decision.
- Whether to bump `kgsm-lib` to net10 when Slice 2 lands (optional; net9 consumed fine under AOT — confirmed).
- ~~Docker cgroup driver on this host~~ → **still open, deferred to 2b**: Docker wasn't running this session. The container resolver emits **both** candidates (`system.slice/docker-<id>.scope` for the systemd driver, `docker/<id>` for cgroupfs); stat-and-skip picks whichever exists. Confirm the driver + container-id length (short vs full 64-hex) against a live container.
- Per-server **disk-IO is opt-in** (`io.stat` needs `IOAccounting=yes`); the unit should ship an `IOAccounting=yes` recipe (parallel to the `Group=kgsm` recipe) for operators who want it. `memory.current` includes reclaimable page cache (reads higher than RSS); switch to `memory.current − inactive_file` later if users read it as RSS.
- **Deploy-time (hardened unit) — `ProtectHome=true` vs KGSM's reads:** the spawned `kgsm.sh` inherits the unit's sandbox and reads its *own* config (`~/.config/kgsm` / `$KGSM_ROOT`) + instance files. If any of those resolve under `/home`, every `GetAll()` throws → `Resync` catches → `_watch` stays empty → `servers[]` is **silently empty forever** (a warning logs, but the feature is dead). When enabling per-server on the hardened unit, ensure KGSM's root + instances live outside `/home` (or relax `ProtectHome`). Host-only path is unaffected.
- **systemd unit-name escaping:** the resolver uses the literal unit name; systemd escapes special chars (`@`, certain `-`) in cgroup dir names. Fine for `7dtd`/`factorio`-style names; an escaped mismatch is a silent stat-and-skip (no crash), but exotic instance names would need `systemd-escape` parity to be sampled.
- **Slice 2b — a dead event socket is silent.** `EventService.Initialize()` binds on a fire-and-forget `Task.Run` inside the lib, so a bind failure (perms/path/already-in-use) surfaces only as a background `LogError` in `UnixSocketClient`, never to the monitor — the `WireEvents` try/catch only covers the disposal-race paths `Initialize()` itself can throw. Metrics keep working via the resync floor, so a non-binding event socket is indistinguishable from "KGSM isn't pushing events" without reading the lib's logs. The monitor logs `server events: listening on <socket>` on the happy path; absence of new resyncs after an instance op is the only operator-visible symptom. If event reliability ever needs to be observable, the fix belongs in `kgsm-lib` (surface the bind result from `StartListeningAsync`), not here. Set `KGSM_MONITOR_EVENTS=0` to disable the listener entirely and run resync-only.
- **Slice 2b coalescing is rate-, not count-, bounded:** events that arrive spaced *just longer* than a `GetAll()` spawn takes each trigger their own resync (the signal only coalesces concurrent/overlapping requests). For a fleet-wide restart that's a handful of sub-second spawns — acceptable, and the periodic floor would have done the same work. If a pathological burst ever matters, add a short debounce before the drain-loop `Resync()`.
- **Slice 3 native CPU is biased low for child-churn; RSS is an overcount.** The process-tree path has no kernel aggregator, so two accuracy gaps are structural (not bugs): (1) **CPU** — a helper process exiting between ticks drops its `utime+stime` from the live sum, making the delta negative (clamped to 0), so a server that churns short-lived children reads lumpy and slightly low; the equivalent cgroup counter is cumulative and loses nothing. `cutime/cstime` were rejected — they count only *reaped* children and invite double-counting. (2) **RSS** is summed per-process and double-counts pages shared across the tree, so it reads higher than a cgroup's `memory.current`. Both are measured-and-labeled. **The structural fix has landed (Inc 4, 2026-06-14):** KGSM/kgsm-watchdog place every native instance in its own cgroup (`kgsm.slice/<inst>`) and kgsm-lib 1.5.0 surfaces it as `Instance.CgroupPath`, so a *running, cgroup-placed* native is now sampled via its cgroup's cumulative counters (no child-churn CPU bias, no shared-RSS overcount). The `/proc` tree remains **only** as the fallback for natives with no live cgroup (cgroups disabled, pre-1.5.0 KGSM, or an instance not yet placed) — so these two limitations apply only on that fallback path now, not to the common case.
- **Slice 3 scan cost scales with host process count, not server count.** The gated `/proc` `stat` scan (≈3.4 ms on a 288-proc host) is one shared pass regardless of how many native servers exist, and zero when none do. On a host with both many native servers *and* a high process count *and* a need to tighten the tick, decouple the scan onto its own sub-cadence (it, not the per-server summation, is the cost). Not worth doing until a real fleet shows the need.
- **Slice 3 trusts KGSM's `.pid` as server identity (first-observation mis-attribution window).** The native sampler reads the root PID from the instance `.pid` file and trusts it — exactly as KGSM's own native status handler does (`manage.native.d/11-status.sh` `cat`s the file and `ps`-es that pid with no identity check). **Verified KGSM behavior:** the native lifecycle handler `rm -f`s the `.pid` on a clean stop (`_stop_server`, `_kill_all_processes`, and an exit trap), so a stopped native server has no `.pid` → `ReadPid` returns −1 → correctly **absent** (no metrics, not fabricated). The residual window is a crash/SIGKILL/OOM/power-loss that **bypasses** that cleanup, leaving a stale `.pid` whose number the kernel later **reuses** for an unrelated process: on the *first* observation there is no prior `starttime` to compare against, so the monitor would attribute the reusing process's (real, measured) CPU/RSS/IO to the stopped server. This is mis-attribution, not invention — the numbers are real and the monitor reports exactly what `kgsm instances status` would for the same stale pid — but it is a known boundary, captured by a characterization test (`First_observation_trusts_the_pid_file_identity`). The `starttime` pin closes the *continuous*-observation case (a change across ticks drops the server for one tick rather than emitting a cross-process rate), not the cold-adopt case. We deliberately avoid a stronger heuristic (e.g. matching `/proc/<pid>/cwd` to `launch_dir`): a game that `chdir`s internally would be wrongly hidden, and inventing an identity model stronger than the instance authority's would let the monitor disagree with KGSM. The proper fix belongs in KGSM — record process identity (e.g. `starttime`) alongside the pid, or have the monitor consume a future authoritative `instances status` — mirroring how the "dead event socket is silent" caveat puts its fix in `kgsm-lib`.
- Live `iperf3`/`fio` throughput validation deferred (tools absent); net/disk rate math is covered by golden fixtures for now.
