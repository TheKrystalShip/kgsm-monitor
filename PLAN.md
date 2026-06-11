# KGSM Metrics Monitor — Plan & Design Record

> Living document. Captures the goal, the decisions and *why*, the KGSM facts we
> verified, and a slice-by-slice work tracker. Project name `kgsm-monitor` /
> namespace `TheKrystalShip.KGSM.Monitor` follows the `kgsm-*` convention.
>
> **Status:** Slice 1 (host-only) **complete** — sampler + env config + filters +
> 23 golden-file tests + measured self-cost under load, all proven on Native AOT
> (see §11 Validation log). Slices 2–3 tracked in §10.

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
  deploy/
    install.sh                # publish + install binary + unit (root; --enable to start)
  src/Monitor/
    Monitor.csproj            # Sdk.Web, net10, PublishAot, IsAotCompatible, AssemblyName=kgsm-monitor; InternalsVisibleTo tests
    Program.cs                # CreateSlimBuilder, DI, Kestrel unix socket, socket chmod, GET /metrics + /healthz
    MonitorOptions.cs         # env-var config (interval, socket path+mode, mount/iface deny) — AOT-safe
    Model/
      Snapshot.cs             # host DTO graph (records)
      MonitorJsonContext.cs   # [JsonSerializable(typeof(Snapshot))] source-gen, camelCase
    Sampling/                 # each source: pure Parse + ComputeRates helpers (golden-file testable)
      MetricsSampler.cs       # BackgroundService + PeriodicTimer(options.IntervalMs); volatile latest; conflation
      CpuSource.cs            # /proc/stat cpu+cpuN jiffies delta -> %
      MemorySource.cs         # /proc/meminfo (instant, MemAvailable)
      NetworkSource.cs        # /proc/net/dev delta -> bps/pps per iface (lo + deny-prefixes excluded)
      DiskSource.cs           # DriveInfo usage (pseudo-fs + deny filtered) + /sys/block/*/stat IO delta
      SystemSource.cs         # /proc/loadavg, /proc/uptime, hostname
    deploy/
      kgsm-monitor.service    # hardened systemd unit (root, Restart=always, RuntimeDirectory socket, group-share recipe)
  tests/Monitor.Tests/        # 23 golden-file tests over captured /proc fixtures + config tests
    Fixtures/                 # stat.a/b, netdev.a/b, meminfo, loadavg, uptime (live captures)
```

## 9. Snapshot shape (host)
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
- [x] `Snapshot` + source-gen JSON; `GET /metrics` (503 until first frame) + `/healthz` over unix socket
- [x] Native AOT publish: **0 IL warnings**, native ELF; live-validated (§11)
- [x] systemd unit (draft)
- [x] Validate under **sustained** CPU load + continuous scrape; **measured** self-cost ≈ nil (§11)
- [x] Refine filters: pseudo-fs always filtered + configurable extra mount/iface deny; default deny `veth`
- [x] Golden-file parse tests (captured `/proc` fixtures) in `tests/Monitor.Tests` — **23 tests, green**
- [x] Socket perms via `ApplicationStarted` chmod (default `0660`); group-sharing recipe in the unit
- [x] Config: interval, socket path, socket mode, iface/mount deny — all env vars (`MonitorOptions`)
- [x] Install/deploy: `deploy/install.sh` + hardened unit written & validated (enable on host = user action)

### Slice 2 — per-server via cgroups + embed kgsm-lib
- [ ] Add `kgsm-lib` ProjectReference (net9 lib in net10 AOT app)
- [ ] Confirm cgroup v2 (`stat -fc %T /sys/fs/cgroup` → `cgroup2fs`) and Docker cgroup driver
- [ ] `EventService` bound to `monitoring.sock`; handle `instance_started/stopped/removed`
- [ ] Periodic `InstanceService` resync = source of truth (events = delta)
- [ ] Resolver: `(LifecycleManager, isContainer)` → cgroup path | container scope | PID (see §6)
- [ ] `CgroupSampler`: `cpu.stat` usage_usec→rate, `memory.current`, `io.stat`, `pids.current`
- [ ] Extend snapshot: `servers: [{ id, name, kind, cpuPct, memBytes, ioReadBps, ioWriteBps, pids }]`

### Slice 3 — standalone-native fallback
- [ ] `ProcTreeSampler`: `.pid` → PID → walk `/proc/*/stat` `ppid` tree; sum `utime+stime`, RSS (`statm`), `/proc/[pid]/io`

### Later (out of build slices)
- [ ] **API relay**: KGSM API opens one scrape/subscription, re-fans-out over authed SSE, caches latest for instant first frame
- [ ] **React SPA** dashboards (per-process/per-server filtering client-side)
- [ ] **Per-server network** via eBPF/nftables (the deferred metric)

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
- **Correctness under load:** during an active 16-core burst the monitor reported peak `cpu.totalPct` **100 %**; 14/14 scrapes 200 OK across both bursts; `healthz` 200 after sustained load; frames stayed fresh (`ts` advanced continuously). (`stress-ng`/`iperf3`/`fio` absent on host → CPU via `dd`; net/disk throughput correctness covered by golden fixtures.)
  - The CPU-only self-cost figure is **representative, not partial**: the monitor reads the same fixed set of `/proc`+`/sys` files every tick regardless of load *type*, so its own cost is load-type-independent.
- **Array integrity post-refactor:** full-body scrape after the deny-list rewiring shows `disk.mounts` = `/` (ext4) + `/boot` (vfat), `net.ifaces` = `enp4s0`+`wlp5s0` (`lo`/pseudo-fs excluded), 16 per-core entries — the empty-array case a `totalPct`/`ts` grep can't catch.
- **Deploy:** `deploy/install.sh` `bash -n` + shellcheck clean; hardened unit passes `systemd-analyze verify` (only flags the not-yet-installed binary). Not enabled on the host (user action).

## 12. Open questions
- ~~Socket location/perms for the API consumer~~ → **resolved**: socket chmod `0660` (configurable), unit ships a `Group=kgsm` group-sharing recipe (commented). Final call (root-API vs shared group) is a deploy-time decision.
- Whether to bump `kgsm-lib` to net10 when Slice 2 lands (optional; net9 consumed fine).
- Docker cgroup driver on this host (decides `system.slice/docker-<id>.scope` path) — confirm in Slice 2.
- Live `iperf3`/`fio` throughput validation deferred (tools absent); net/disk rate math is covered by golden fixtures for now.
