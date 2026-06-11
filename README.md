# kgsm-monitor

A lightweight **Native-AOT** daemon that samples host (and, from Slice 2, per-game-server)
metrics straight from the Linux kernel — `/proc`, `/sys`, cgroup v2 — and serves the
latest snapshot over a unix-socket `GET /metrics`. Designed to feed live dashboards in
the KGSM ecosystem without nuking host resources (it never shells out to `top`/`ps`).

See **[PLAN.md](PLAN.md)** for the full design, decisions, KGSM-integration facts, and
the slice-by-slice work tracker.

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
| `KGSM_MONITOR_SOCKET` | `/run/kgsm-monitor.sock` | Unix socket path to listen on |
| `KGSM_MONITOR_SOCKET_MODE` | `660` | Octal perms applied to the socket (group read = API can scrape) |
| `KGSM_MONITOR_INTERVAL_MS` | `1000` | Sampling cadence (floor 100 ms) |
| `KGSM_MONITOR_IFACE_DENY` | `veth` | Comma-separated interface-name prefixes to exclude |
| `KGSM_MONITOR_MOUNT_FS_DENY` | *(empty)* | Extra fs types to hide (pseudo-fs already filtered) |

## Deploy

`sudo ./deploy/install.sh` publishes the AOT binary to `/opt/kgsm-monitor` and installs the
hardened systemd unit (it does **not** start the daemon; pass `--enable` to
`systemctl enable --now`). To let a non-root API read the socket, see the `Group=` recipe in
`src/Monitor/deploy/kgsm-monitor.service`.

## Status

Slice 1 (host-only CPU/MEM/DISK/NET over the unix socket) — **complete & AOT-proven**:
self-ticking sampler, env-configurable filters, 23 golden-file tests, measured self-cost
≈ 0.02 % of host under full load. **Perf baseline:** a full diagnostic frame is **1.61 ms**
(0.16 % of the 1 s tick; ~620× headroom) — see **[bench/BASELINE.md](bench/BASELINE.md)**.
Per-server metrics via cgroups + embedded `kgsm-lib` land in Slice 2. See **[PLAN.md](PLAN.md)**.
