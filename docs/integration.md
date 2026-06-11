# Integrating with kgsm-monitor

A guide for **building a service that consumes the monitor** — the KGSM API relay, a
future exporter, a CLI, anything. It is the contract: the transport, the endpoints, the
exact data shape, what you can and cannot configure, and the failure modes you must
handle. If you are reading this cold six months from now, this file is the only thing you
need to write a correct consumer.

> **TL;DR.** The monitor is a root-owned daemon that, once per second, reads kernel
> counters and holds the **latest** host+per-server metrics frame in memory. You read that
> frame by sending `GET /metrics` over a **unix domain socket** (default
> `/run/kgsm-monitor.sock`). It is HTTP/1.1, unauthenticated, JSON, pull-only. It never
> pushes, never streams, keeps no history, and computes every rate for you. Your job is to
> scrape it and re-expose it however your service needs.

---

## 1. Mental model (read this first — it explains every other section)

Four properties drive the whole contract. Internalise them and the rest is mechanical.

1. **Self-ticking, serve-latest (conflation).** The monitor samples on its *own* 1 Hz
   timer and overwrites a single in-memory `latest` frame. `GET /metrics` returns that
   frame as-is — **the scrape never triggers a sample.** Consequence: scraping faster than
   the tick returns the *same* frame repeatedly (dedupe on `ts`); scraping slower just
   skips frames (fine — it's a gauge, not a log). There is **no buffer and no history** —
   you only ever get the most recent frame. If you need history, *you* store it.

2. **Rates are already computed.** CPU %, network bps/pps, and disk/io bps are
   *derivatives* the monitor computes from two consecutive samples. **Do not diff frames
   yourself** — the numbers are already rates. The only counters left for you to interpret
   over time are gauges (memory, mount usage, pid counts).

3. **Consumer-agnostic, pull-only.** The monitor knows nothing about you. It does **not**
   authenticate, fan out, push, or stream. If your service serves many downstream clients
   (browsers, etc.), run **one** scrape loop and re-broadcast — do not multiply scrapes.
   (Auth + SSE fan-out is the job of the API layer *above* the monitor, not the monitor.)

4. **Measured or explicitly unknown — never invented.** A value you can't trust isn't
   faked. A missing metric is `null` (io without accounting) or an absent array entry
   (a stopped server), never a zero standing in for "don't know." Treat `null` as
   "not measured," which is **not** the same as `0`.

---

## 2. Transport: connecting to the socket

The monitor listens on a **unix domain socket** speaking ordinary **HTTP/1.1**. There is
**no TCP port** — the socket's filesystem permissions are the entire security boundary.

- **Default path:** `/run/kgsm-monitor.sock` (override with `KGSM_MONITOR_SOCKET`).
- **Default perms:** `0660`, owner+group read/write. To scrape it your process must run as
  the socket's **owner or be in its group** (typically root runs the monitor; you add the
  API's user to a shared group — see the `Group=` recipe in
  `src/Monitor/deploy/kgsm-monitor.service`).
- **Do not confuse it with the event socket.** `KGSM_MONITOR_KGSM_SOCKET`
  (`/run/kgsm-monitoring.sock`) is **inbound, KGSM→monitor only** — it is where KGSM
  *pushes* lifecycle events. Consumers never touch it. Always scrape
  `KGSM_MONITOR_SOCKET`.

### curl (smoke test)

```bash
curl --unix-socket /run/kgsm-monitor.sock http://localhost/metrics | jq
curl --unix-socket /run/kgsm-monitor.sock http://localhost/healthz   # -> ok (text/plain, "ok\n")
```

The hostname in the URL is ignored — only the path matters. Any host works.

### .NET (the realistic consumer — the KGSM API is .NET/AOT)

Use `SocketsHttpHandler.ConnectCallback` to dial the unix endpoint, then a normal
`HttpClient`. Deserialize with a **source-generated** `JsonSerializerContext` so an AOT
consumer stays trim-clean:

```csharp
using System.Net.Sockets;
using System.Net.Http.Json;

var handler = new SocketsHttpHandler
{
    ConnectCallback = async (_, ct) =>
    {
        var sock = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        await sock.ConnectAsync(new UnixDomainSocketEndPoint("/run/kgsm-monitor.sock"), ct);
        return new NetworkStream(sock, ownsSocket: true);
    }
};
using var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost") };

// 503 until the first tick lands — see §4. Snapshot is your own DTO graph (or copy the
// monitor's Model records) wired into a source-gen context for AOT.
using var resp = await http.GetAsync("/metrics", ct);
if (resp.StatusCode == System.Net.HttpStatusCode.ServiceUnavailable) { /* retry */ }
var snapshot = await resp.Content.ReadFromJsonAsync(MyJsonContext.Default.Snapshot, ct);
```

**DTOs:** you can copy `src/Monitor/Model/Snapshot.cs`'s record graph verbatim (it's pure
records, camelCase on the wire) or generate your own from the schema in §3. Keeping your
own copy decouples you from the monitor's internal refactors — recommended. Either way,
**ignore unknown fields** (STJ does by default) so an additive monitor change can't break
you (see §7).

### Anything else (Node, Python, …)

Any HTTP client that can dial a unix socket works — Node `http.request({ socketPath })`,
Python `requests-unixsocket`, `socat`/`nc` for raw bytes. It's vanilla HTTP/1.1; nothing
about the protocol is special.

---

## 3. The data: `GET /metrics`

One JSON object per scrape — the latest frame. `Content-Type: application/json`,
**camelCase** keys, **nulls are emitted** (an absent metric shows as `"key": null`, not a
missing key). Below is a fully-annotated example with every field; types and units follow.

```jsonc
{
  "ts": 1781184473162,        // unix epoch MILLISECONDS this frame was sampled (freshness/dedupe key)
  "intervalMs": 1000,         // the monitor's nominal tick — your natural poll cadence (§5)
  "hostname": "gameserver-01",
  "uptimeSec": 182285,        // host uptime, seconds

  "cpu": {
    "totalPct": 2.4,          // host CPU across ALL cores, 0..100 (NOT the same unit as a server's cpuPctCore!)
    "perCore": [1.0, 3.1, …], // per-core %, 0..100 each; array length = core count
    "load": { "one": 0.49, "five": 0.34, "fifteen": 0.20 }   // /proc/loadavg
  },

  "mem": {
    "totalKb": 32710144,
    "availableKb": 24503020,  // MemAvailable (the honest "free" — accounts for reclaimable cache)
    "usedKb": 8207124,        // total - available
    "usedPct": 25.1,
    "swapTotalKb": 8388604,
    "swapUsedKb": 0
  },

  "disk": {
    "mounts": [               // real filesystems only; pseudo-fs always filtered, plus KGSM_MONITOR_MOUNT_FS_DENY
      { "mount": "/", "fs": "ext4", "totalBytes": 500107862016, "usedBytes": 120000000000, "usedPct": 24.0 }
    ],
    "io": { "readBps": 10240, "writeBps": 4096 }   // HOST aggregate block IO, bytes/sec
  },

  "net": {
    "ifaces": [               // loopback + KGSM_MONITOR_IFACE_DENY prefixes (default "veth") excluded
      { "name": "enp4s0", "rxBps": 2048, "txBps": 1024, "rxPps": 12, "txPps": 8 }
    ]
  },

  "servers": [                // per-KGSM-server; EMPTY when host-only mode OR no servers running (§6)
    {
      "id": "factorio",       // KGSM instance name — STABLE key across restarts; use this to correlate
      "name": "factorio",     // display name (== id today; reserved for future alias/blueprint labels)
      "kind": "systemd",      // "systemd" | "container" | "native" — how the server was measured (§3.2)
      "cpuPctCore": 92.4,     // % of ONE core (htop per-process convention) — CAN exceed 100 on multi-core
      "memBytes": 1734967296, // systemd/container: cgroup memory.current (incl. page cache). native: summed RSS
      "ioReadBps": null,      // bytes/sec, or NULL when not accounted (§3.3) — null ≠ zero
      "ioWriteBps": null,
      "pids": 12              // live process/thread count
    }
  ]
}
```

### 3.1 Field reference

| Path | Type | Unit / meaning | Notes |
|---|---|---|---|
| `ts` | `long` | unix epoch **ms** | Freshness + dedupe key. Strictly increasing while the daemon ticks. |
| `intervalMs` | `int` | ms | Nominal tick; your poll cadence. |
| `hostname` | `string` | — | |
| `uptimeSec` | `long` | seconds | Host uptime. |
| `cpu.totalPct` | `double` | **0–100 across all cores** | Host-wide. |
| `cpu.perCore[]` | `double[]` | 0–100 each | Length = logical core count. |
| `cpu.load.{one,five,fifteen}` | `double` | load average | `/proc/loadavg`. |
| `mem.*Kb` | `long` | **kibibytes** | `usedKb = totalKb − availableKb`. |
| `mem.usedPct` | `double` | percent | |
| `disk.mounts[].{totalBytes,usedBytes}` | `long` | **bytes** | Real filesystems only. |
| `disk.mounts[].usedPct` | `double` | percent | |
| `disk.io.{readBps,writeBps}` | `long` | **bytes/sec** | **Host aggregate**, not per-server. |
| `net.ifaces[].{rxBps,txBps}` | `long` | bytes/sec | Per interface. |
| `net.ifaces[].{rxPps,txPps}` | `long` | packets/sec | |
| `servers[].id` | `string` | — | **Stable correlation key.** |
| `servers[].name` | `string` | — | Display label (== id today). |
| `servers[].kind` | `string` | `systemd`\|`container`\|`native` | Measurement path (§3.2). |
| `servers[].cpuPctCore` | `double` | **% of one core** | Different unit from `cpu.totalPct` — see the trap below. |
| `servers[].memBytes` | `long` | **bytes** | cgroup `memory.current` (systemd/container) or summed RSS (native). |
| `servers[].ioReadBps` | `long?` | bytes/sec or **null** | `null` = not accounted (§3.3). |
| `servers[].ioWriteBps` | `long?` | bytes/sec or **null** | |
| `servers[].pids` | `int` | count | Live processes/threads. |

> **⚠ Unit trap — host CPU vs server CPU are different scales.** `cpu.totalPct` is
> **0–100 across all cores** (the whole host). `servers[].cpuPctCore` is **percent of one
> core** (htop's per-process convention) and **can exceed 100** — a server pinning 3 cores
> reads ~300. They are deliberately not the same unit. To put a server on a host-relative
> 0–100 scale, divide by `cpu.perCore.length`.

> **⚠ Memory includes cache, and `Kb` vs `bytes` differ by block.** `mem.*` is in
> **kibibytes**; `servers[].memBytes` is in **bytes**. `memBytes` for systemd/container
> servers is cgroup `memory.current`, which **includes reclaimable page cache**, so it
> reads higher than process RSS. For native servers it's summed RSS (and double-counts
> shared pages). Both are honest, just not interchangeable with a `ps`-style RSS.

### 3.2 `kind` — what it tells you about the measurement

| `kind` | Source | What KGSM lifecycle it is |
|---|---|---|
| `systemd` | cgroup `system.slice/<unit>.service` | a systemd-managed instance |
| `container` | cgroup `docker-<id>.scope` / `docker/<id>` | a Docker/compose instance |
| `native` | `/proc` process-tree walk from the instance `.pid` | a standalone process (no cgroup) |

`kind` is informational for a consumer — the fields mean the same thing regardless — but it
signals the **accuracy profile** (§3.4) and which knobs affect io accounting (§3.3).

### 3.3 Why `ioReadBps`/`ioWriteBps` can be `null`

- **systemd / container (cgroup):** io rates require the cgroup's `io.stat`, which only
  exists when the unit sets **`IOAccounting=yes`**. Most units don't by default → `null`.
  This is opt-in at the *unit* level, not something a consumer can turn on. `null` here
  means "the kernel isn't counting it," **not** "no I/O happened."
- **native (`/proc`):** io is read straight from `/proc/<pid>/io` (the monitor runs as
  root), so native servers report real numbers, `0` on a quiet first tick, never `null`
  from lack of accounting.

So: `null` io ⇒ a cgroup server without `IOAccounting=yes`. Render it as "—"/"n/a", not 0.

### 3.4 Per-`kind` accuracy notes (so you can label confidently)

- **`native` CPU is biased slightly low under child-churn.** The process-tree sum can't
  recover CPU from helper processes that exited between ticks (a cgroup counter can), so a
  server that spawns many short-lived children reads a touch low and lumpy. Cgroup kinds
  (`systemd`/`container`) don't have this gap.
- **`native` memory double-counts shared pages.** Summed RSS overcounts vs a cgroup's
  `memory.current`. Treat it as an upper bound.
- **Identity for `native` is the `.pid` file** — the monitor trusts it exactly as KGSM's
  own status command does. In the rare residual case (a crash that bypasses `.pid`
  cleanup *and* the kernel reuses that PID number) a freshly-observed native server could
  briefly report a different process's real metrics. It's mis-attribution, not fabrication,
  and it matches what `kgsm instances status` would show. Don't build alerting that
  assumes native identity is cryptographically certain.

---

## 4. Endpoints

| Method + path | Success | Body | Notes |
|---|---|---|---|
| `GET /metrics` | `200` | the JSON frame (§3) | `503` until the first tick lands — see below. |
| `GET /healthz` | `200` | `ok\n` (text/plain) | Liveness only. Returns `200` even before the first frame. |

- **Only `GET` is mapped.** Other methods → `405`; other paths → `404`.
- **`503 Service Unavailable` on `/metrics`** means the daemon is up but hasn't completed
  its first sample yet (a sub-second window right after start). **Handle it:** retry after
  ~`intervalMs`. Don't treat it as fatal. `/healthz` is already `200` in this window, so
  use `/metrics` 503-vs-200 — not `/healthz` — to gate "metrics ready."
- **No auth, no query params, no request body.** Anything you send beyond the path+method
  is ignored. The endpoint is a pure read.

---

## 5. Polling guidance

- **Match the tick.** Poll about once per `intervalMs` (default 1000 ms). The frame carries
  its own `intervalMs`, so read it and adapt rather than hard-coding.
- **`ts` is your freshness signal.** If `ts` hasn't advanced since your last scrape you got
  the same frame — the daemon may be mid-tick, or (if `ts` is stale by many intervals) it
  may be wedged. A simple staleness rule: `now − ts > 3 × intervalMs` ⇒ flag stale.
- **Don't over-poll.** Scraping at 10 Hz gives you the same 1 Hz frame ten times. It's
  cheap on the monitor (it returns a precomputed object) but pointless. One loop at the
  tick rate is right.
- **One scrape, many downstreams.** If your service feeds N clients, scrape once and
  fan out yourself. The monitor is O(1) by design; keep it that way.

---

## 6. The two ways `servers` can be empty (you must distinguish them)

An empty `servers: []` is **ambiguous on its own**:

1. **Host-only mode** — the monitor was started **without** `KGSM_MONITOR_KGSM_PATH`, so
   per-server sampling is off entirely. `servers` will be `[]` *forever*.
2. **Per-server mode, nothing running** — KGSM is wired up but no instances are currently
   up (stopped servers are simply absent; only running, addressable ones appear).

The `/metrics` payload **does not currently expose which mode it's in** — there's no
`kgsmEnabled` flag in the frame. If your consumer needs to tell "monitoring is off" from
"monitoring on, zero servers," you must know it out-of-band (from the deployment / the
monitor's env), **or** request that a mode flag be added to the snapshot (a small,
additive change — see §7). Until then, treat persistent-empty as "ask the operator how the
monitor was launched."

Also note: **servers appear and disappear between frames** as instances start/stop. Key on
`id`, and don't assume a server present last frame is present this frame.

---

## 7. Stability & forward-compatibility contract

There is **no version field** in the payload today. The compatibility model is therefore
**additive-only by convention**:

- **Stable:** existing field names (camelCase), their types, and their units. Treat them as
  the contract.
- **Allowed to change without notice:** *new* fields may be added to any object, and new
  `kind` values may appear. **Your consumer must ignore unknown fields and tolerate unknown
  `kind` strings** (STJ ignores unknowns by default; don't `switch` on `kind` with no
  default branch). This is the single most important forward-compat rule.
- **Nullability is part of the contract:** `ioReadBps`/`ioWriteBps` are nullable; everything
  else is non-null. Don't assume io is present.
- **If you need a breaking signal** (a real schema version, a `kgsmEnabled`/mode flag, a
  per-server cgroup-vs-proc accuracy tag), the right move is to **add it to
  `Model/Snapshot.cs` + `MonitorJsonContext`** as an additive field rather than working
  around its absence. Both are tiny, AOT-safe changes.

---

## 8. Failure modes a correct consumer handles

| Symptom | Cause | What to do |
|---|---|---|
| `connect: No such file or directory` | Monitor not started, or wrong `KGSM_MONITOR_SOCKET` path | Confirm the daemon is up and the path matches its env. |
| `connect: Permission denied` | Your user isn't owner/group of the `0660` socket | Add your user to the socket's group (`Group=` recipe in the unit). |
| `GET /metrics` → `503` | Up, but pre-first-tick | Retry after ~`intervalMs`; not fatal. |
| `404` / `405` | Wrong path or method | Only `GET /metrics` and `GET /healthz` exist. |
| `servers: []` forever | Host-only mode (no `KGSM_MONITOR_KGSM_PATH`) | Expected; see §6. |
| `servers: []` intermittently | No instances currently running | Expected; servers come and go. |
| `ioReadBps`/`ioWriteBps` = `null` | cgroup server without `IOAccounting=yes` | Render "n/a", not 0. |
| `ts` not advancing | Daemon wedged, or you're polling faster than the tick | Compare `now − ts` against `intervalMs` (§5). |
| `cpuPctCore` > 100 | Multi-core server — **not a bug** | It's per-one-core; normalise by core count if you want 0–100. |
| Connection drops mid-stream | Daemon restarted (`Restart=always`) | Reconnect; the socket is recreated on start. |

---

## 9. Configuration — what's tunable, what's fixed

All monitor configuration is **environment variables** set by whoever runs the daemon
(systemd `Environment=`). A **consumer cannot change any of it at scrape time** — there are
no query params, headers, or control endpoints. If you need different behaviour, it's a
*deployment* change to the monitor's unit, not something your code can request.

### Operator-tunable (set on the monitor process)

| Variable | Default | Affects the consumer how |
|---|---|---|
| `KGSM_MONITOR_SOCKET` | `/run/kgsm-monitor.sock` | **The path you connect to.** |
| `KGSM_MONITOR_SOCKET_MODE` | `660` (octal) | Whether your user can open the socket. |
| `KGSM_MONITOR_INTERVAL_MS` | `1000` (floor 100) | Frame cadence = your poll rate; mirrored in `intervalMs`. |
| `KGSM_MONITOR_IFACE_DENY` | `veth` | Which interfaces appear in `net.ifaces`. |
| `KGSM_MONITOR_MOUNT_FS_DENY` | *(empty)* | Extra fs types hidden from `disk.mounts`. |
| `KGSM_MONITOR_KGSM_PATH` | *(empty)* | **Unset ⇒ `servers` always `[]`** (host-only). Set ⇒ per-server on. |
| `KGSM_MONITOR_KGSM_SOCKET` | `/run/kgsm-monitoring.sock` | KGSM→monitor event socket — **not yours**; don't connect to it. |
| `KGSM_MONITOR_RESYNC_MS` | `15000` (floor 1000) | How fast a started/stopped server's presence catches up (worst case, absent events). |
| `KGSM_MONITOR_EVENTS` | `on` | When on, lifecycle events make new servers appear sub-second instead of after `RESYNC_MS`. |

### Fixed by design — **not** configurable, and not worth fighting

- **Units.** CPU per-core %, memory cgroup-current/RSS, host-aggregate disk io — see §3.
  You normalise/convert on your side.
- **Per-request sampling.** The scrape always returns the precomputed frame; you can't ask
  for a fresh sample or a custom interval per request.
- **Server-side filtering / selection.** No "give me just server X" query. Fetch the whole
  frame and filter client-side.
- **History / time ranges.** Latest frame only; no `?since=`. You store history if you need
  it.
- **Push / streaming / SSE.** The monitor is pull-only. Streaming + auth + fan-out is the
  job of the API layer above it (the planned aggregator), not the monitor.
- **Per-server network.** Not measured (cgroups don't account network without eBPF) — a
  deliberate scope cut. There is no per-server net field and won't be without that work.
- **Auth.** None — the socket's filesystem perms are the boundary. If you need authn/authz,
  you add it in front; don't expect the monitor to.

---

## 10. Quick-start checklist

1. Ensure the monitor is running and your user can read its socket (`0660` ⇒ shared group).
2. Open an HTTP/1.1 client over the **unix socket** `KGSM_MONITOR_SOCKET`
   (default `/run/kgsm-monitor.sock`). Not the `…-monitoring.sock` event socket.
3. `GET /metrics`. On `503`, retry after ~`intervalMs`.
4. Parse the JSON (§3), **ignoring unknown fields** and tolerating unknown `kind` values.
5. Loop at ~`intervalMs`; use `ts` for freshness/dedupe; fan out from a single loop.
6. Handle `null` io, empty `servers`, `cpuPctCore > 100`, and reconnect-on-drop (§8).

That's the whole integration surface. Everything else — auth, fan-out, history, dashboards
— lives in *your* service, by design.

---

*Authoritative source for shapes: `src/Monitor/Model/Snapshot.cs`. For config:
`src/Monitor/MonitorOptions.cs`. For endpoints: `src/Monitor/Program.cs`. For the design
rationale behind these choices: [PLAN.md](../PLAN.md).*
