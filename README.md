# kgsm-monitor

A lightweight **Native-AOT** daemon that samples host (and, from Slice 2, per-game-server)
metrics straight from the Linux kernel — `/proc`, `/sys`, cgroup v2 — and serves the
latest snapshot over a unix-socket `GET /metrics`. Designed to feed live dashboards in
the KGSM ecosystem without nuking host resources (it never shells out to `top`/`ps`).

See **[PLAN.md](PLAN.md)** for the full design, decisions, KGSM-integration facts, and
the slice-by-slice work tracker.

## Build & run (local)

```bash
# JIT build
dotnet build

# Native AOT publish
dotnet publish src/Monitor/Monitor.csproj -c Release -r linux-x64

# Run against a dev socket (no root / /run needed)
KGSM_MONITOR_SOCKET=/tmp/kgsm-monitor.sock \
  ./src/Monitor/bin/Release/net10.0/linux-x64/publish/kgsm-monitor &

curl --unix-socket /tmp/kgsm-monitor.sock http://localhost/metrics | jq
```

## Status

Slice 1 (host-only CPU/MEM/DISK/NET over the unix socket) — in progress. Per-server
metrics via cgroups + embedded `kgsm-lib` land in Slice 2. See PLAN.md.
