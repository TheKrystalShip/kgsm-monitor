# Internals

How the daemon is put together. The design rationale is `../PLAN.md`; the consumer contract is
`integration.md`; the cross-repo contract is `../../kgsm-docs/reference/monitor-socket.md`.

## Samplers

Two self-ticking `BackgroundService`s compute a frame on their own timer and publish it with a single
`volatile` reference swap. The HTTP scrape never triggers a sample; it returns the precomputed `Latest`
(`503` until the first tick). Stale frames are never queued.

- `MetricsSampler` (host, 1 Hz) is the always-on path. `Build()` assembles one `Snapshot` from the
  sampling sources. The stateful delta sources (`CpuSource`, `NetworkSource`, `DiskSource`) are primed
  once at start so the first published frame already carries rates.
- `ServerSampler` (per-server) is opt-in: it is wired only when `Monitor__KgsmPath` is set; otherwise
  `servers` is always `[]`. It runs two separate cadences.
  - **Resync (slow, off the metrics tick)** reads the engine's registry in-process through the engine core, which opens every
    instance's config and is the cost the metrics path avoids. A single-writer drain loop is the only writer of
    the `volatile` watch-list. KGSM lifecycle events from the journal and the periodic floor both feed a
    coalescing `RequestResync()` (a semaphore capped at 1), which keeps the swap lock-free. `diskBytes`
    (a directory walk) runs on its own slow loop.
  - **Sample (fast, the host tick)** reads each watched server's counters from cheap kernel files, with
    no spawn and no lock.

## Sampling sources

Each source splits live I/O from logic: a pure static `Parse` / `ComputeRates` operating on a captured
`/proc` string, so it stays golden-file-testable. Tests pin expected values against fixtures in
`tests/Monitor.Tests/Fixtures/`; the test project reaches internals through `InternalsVisibleTo`. A new
metric follows the split, adds a fixture and pins a value.

## Server kinds

`systemd` and `container` servers are read from cgroup v2 files (`CgroupSampler`). A `native` server is
read from its cgroup under `kgsm.slice` when the watchdog has created one (`Instance.CgroupPath`), and
otherwise summed from the `/proc` process tree rooted at the instance `.pid` (`ProcTreeSampler`). The
resolver partitions the watch-list on a single liveness check so the samplers' outputs are disjoint and
concatenate.

## Per-server network

cgroup v2 has no network controller, so `rxBps` and `txBps` come from a passive `cgroup/skb` byte counter
(`src/Monitor/bpf/net_meter.bpf.c`) attached once to `kgsm.slice` and read from a pinned BPF map. The pin
path, map type and key/value layout are a fixed contract between `NetworkCgroupSource` and
`deploy/net-meter-setup.sh`: change one, change both. Setup is privileged and one-time; until it has run
these fields read `null`.

## Journals

The monitor owns exactly one outbound socket, `Monitor__SocketPath`. Events arrive from files: every
producer's journal, read-only, with no reservation of any kind. The resync floor is the watch-list's
source of truth; events only make it react sooner.

`Monitor__KgsmJournalDir` names the engine's journal, not the only one read. `AddKgsmJournalFederation`
tails every producer and stays registered after `AddKgsmServices`: above it the single-journal
registration wins silently, and a native `server.started` (the supervisor's event, in its own journal)
reaches nothing. The setting exists because the engine's location is configurable; the other journals
are found at their own state directories. This daemon's own journal is among them, which costs nothing:
the four handlers are keyed by payload type and a threshold episode matches none of them.

The monitor stores no engine events. It tails the journal at the tail with no cursor, only so
`ServerSampler` learns that the instance list moved: a replayed event would trigger a resync the periodic
floor was going to do anyway.
