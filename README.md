# kgsm-monitor

A lightweight **Native-AOT** daemon that samples host **and per-game-server** metrics
straight from the Linux kernel — `/proc`, `/sys`, cgroup v2 — and serves the latest
snapshot over a unix-socket `GET /metrics`. Designed to feed live dashboards in the KGSM
ecosystem without nuking host resources (it never shells out to `top`/`ps`).

See **[PLAN.md](PLAN.md)** for the full design, decisions, KGSM-integration facts, and
the slice-by-slice work tracker. **Building a service that consumes the monitor?** Start
with **[docs/integration.md](docs/integration.md)** — the consumer contract: transport,
endpoints, the exact JSON shape, what's configurable, and the failure modes to handle.

## Build & run (local)

```bash
# JIT build + run the golden-file test suite
dotnet build
dotnet test

# Performance baseline (BenchmarkDotNet) — see bench/BASELINE.md for committed results
dotnet run -c Release --project bench/Monitor.Benchmarks -- --filter '*'

# Native AOT publish
dotnet publish src/Monitor/Monitor.csproj -c Release -r linux-x64 -o artifacts/publish

# Run against a dev socket (no root / /run needed)
KGSM_MONITOR_SOCKET=/tmp/kgsm-monitor.sock \
  ./artifacts/publish/kgsm-monitor &

curl --unix-socket /tmp/kgsm-monitor.sock http://localhost/metrics | jq
```

## Configuration (environment variables)

| Variable | Default | Meaning |
|----------|---------|---------|
| `KGSM_MONITOR_SOCKET` | `/run/kgsm-monitor/metrics.sock` | Unix socket path to listen on |
| `KGSM_MONITOR_SOCKET_MODE` | `660` | Octal perms applied to the socket (group read = API can scrape) |
| `KGSM_MONITOR_INTERVAL_MS` | `1000` | Sampling cadence (floor 100 ms) |
| `KGSM_MONITOR_IFACE_DENY` | `veth` | Comma-separated interface-name prefixes to exclude |
| `KGSM_MONITOR_MOUNT_FS_DENY` | *(empty)* | Extra fs types to hide (pseudo-fs already filtered) |
| `KGSM_MONITOR_KGSM_PATH` | *(empty)* | Path to `kgsm.sh`. **Unset ⇒ host-only**; set ⇒ per-server cgroup sampling on |
| `KGSM_MONITOR_KGSM_JOURNAL` | `/var/lib/kgsm/events` | The engine's event journal the monitor tails for KGSM lifecycle events |
| `KGSM_MONITOR_RESYNC_MS` | `15000` | How often to re-list KGSM instances (floor 1 s; off the metrics tick) |
| `KGSM_MONITOR_EVENTS` | `on` | Listen for KGSM events on the socket above (`1/0`, `true/false`, `on/off`). Off ⇒ resync-only |

### Per-server metrics (Slice 2)

When `KGSM_MONITOR_KGSM_PATH` is set, each frame gains a `servers[]` array — one entry per
**running, cgroup-addressable** KGSM instance (systemd units and Docker containers):

```jsonc
"servers": [
  { "id": "factorio", "name": "factorio", "kind": "systemd",
    "cpuPctCore": 92.4,        // % of ONE core (htop convention) — can exceed 100
    "memBytes": 1734967296,    // memory.current (includes page cache)
    "ioReadBps": null,         // null unless the unit sets IOAccounting=yes
    "ioWriteBps": null,
    "pids": 12 }
]
```

Stopped instances are simply absent — a systemd/container server appears only when its
cgroup exists. **Standalone-native** servers (no cgroup) are covered by the **Slice 3**
process-tree fallback: their `kind` is `"native"`, and CPU/memory/IO are summed from the
`/proc` tree rooted at the instance `.pid`. Per-server disk-IO for cgroup servers requires
`IOAccounting=yes` on the unit (otherwise the io fields are `null` — not measured ≠ no I/O);
native servers read io straight from `/proc` as root, so they need no such flag.

> **Accuracy note (native only):** a process-tree CPU sum can't recover CPU from children
> that exited between samples (a cgroup counter can), so a server that churns short-lived
> helpers reads slightly low; summed RSS double-counts pages shared across the tree
> (overcount vs a cgroup's `memory.current`). Measured-and-labeled, never fabricated.

The watch-list refreshes on a slow timer (`KGSM_MONITOR_RESYNC_MS`, off the metrics tick
since it spawns `kgsm.sh`). **Slice 2b** adds the low-latency half: KGSM pushes lifecycle
events (`instance_started/stopped/removed/uninstalled`) to the monitor-owned event socket,
and each one *nudges* an immediate resync — so a freshly-started server shows up in
sub-second rather than after up to `RESYNC_MS`. Events are best-effort (the periodic resync
stays the source of truth); set `KGSM_MONITOR_EVENTS=0` to bind nothing and run resync-only.
KGSM connects via `socat` — wire the emitter by adding `monitoring.sock` to KGSM's
`event_socket_filenames` (see `src/Monitor/deploy/kgsm-monitor.service` for the recipe).

## Deploy

```bash
./deploy/setup.sh    # ONCE per host. Asks for sudo. Idempotent, re-runnable.
./deploy/deploy.sh   # every deploy. NO sudo, NO prompts.
```

`setup.sh` provisions the host — `/opt/kgsm-monitor` chowned to you, the hardened unit installed
into user-owned `/etc/kgsm-monitor/systemd/` with `/etc/systemd/system/kgsm-monitor.service`
symlinked to it, a polkit rule scoped to this project's units, the unit enabled — then verifies the
grant by making the same unprivileged `systemctl` calls the deploy will.

`deploy.sh` publishes the AOT binary, refreshes the unit if it changed, restarts the daemon and
confirms a real `GET /health` over the metrics socket — all **without sudo or a prompt**, because
the prefix and the unit directory are yours and the `systemctl` verbs go through the polkit grant.
On an unprovisioned host it stops before building and tells you to run `setup.sh`. Run both as the
service user, never as root.

To let a non-root API read the socket, see the `Group=` recipe in
`src/Monitor/deploy/kgsm-monitor.service`.

## Status

Slice 1 (host-only CPU/MEM/DISK/NET over the unix socket) — **complete & AOT-proven**:
self-ticking sampler, env-configurable filters, golden-file tests, measured self-cost
≈ 0.02 % of host under full load. **Perf baseline:** a full diagnostic frame is **1.61 ms**
(0.16 % of the 1 s tick; ~620× headroom) — see **[bench/BASELINE.md](bench/BASELINE.md)**.

Slice 2a (**per-server cgroups + embedded `kgsm-lib`**) — **complete & AOT-proven**: a
never-failing resolver + stat-and-skip `CgroupSampler` (cpu/mem/pids, opt-in io) driven by
an off-tick KGSM instance resync; per-server read **= 48 µs** (100 servers ≈ 0.5 % of the tick).

Slice 2b (**event-driven watch-list delta**) — **complete & AOT-proven**: KGSM lifecycle
events on `monitoring.sock` nudge a coalesced, authoritative resync (single-writer drain
loop, lock-free swap); a real-socket envelope round-trip through the source-generated JSON
context; live-proven under AOT (`socat` push → resync).

Slice 3 (**standalone-native process-tree fallback**) — **complete & AOT-proven**: servers
with no cgroup are read from the `/proc` ppid tree (one gated scan, summed CPU/RSS/IO, a
`starttime` PID-recycle guard); **60 tests** total; live-proven under AOT (a busy native
server read `cpuPctCore≈100`, confirming the `sysconf(_SC_CLK_TCK)` p/invoke under Native
AOT). Per-native scan ≈ 3.4 ms, flat in native-server count and gated to zero when none
exist. See **[PLAN.md](PLAN.md)**.
