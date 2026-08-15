# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [2.6.0] - 2026-08-16

### Changed — being a journal producer is derived, not wired here

`AddKgsmJournal("kgsm-monitor", …)` replaces this daemon's hand-built writer registration, and
`MonitorJournal` derives from `JournalRecorder` (kgsm-lib 4.27.0 / Journal 1.1.0). What this repo
still owns is the part that is its own: the two threshold event types and the payload each carries.
What it no longer answers for itself is where its journal lives, which version it stamps, what its
actor is, and what happens when a write fails.

Three of those had drifted across the ecosystem, and two mattered here:

- **`ProducerVersion` is the informational version.** This daemon stamped
  `Assembly.GetName().Version`, so its events carried `2.3.0.0` — a four-part form no release of it
  is ever numbered with, and not comparable with the semver every other producer's version is
  eventually meant to be. Events written from now on carry `2.6.0`. ⚠ Lines already on disk keep the
  old spelling; the field is free text, so nothing breaks, but a reader comparing across the change
  sees both.
- **The journal directory no longer follows the metrics database.** It was derived from
  `Path.GetDirectoryName(HistoryDbPath)`, so pointing the history store elsewhere would have moved
  the journal somewhere no reader scans — where a producer is not reported as unreadable, it is
  simply absent. It now comes from the producer id, which is what a reader derives it from.
  Unchanged on a default host, where both are `/var/lib/kgsm-monitor/events`.
- The journal directory is still created at startup rather than on the first episode, for the reason
  this daemon was alone in having worked out: a leaf that has breached nothing yet must not be
  indistinguishable from one that writes no journal. That behaviour moved into the shared
  registration, so every producer now has it.

`system:monitor` is derived from the producer id rather than held as a constant, and is byte-for-byte
what it was. Authority: `../event-conformance-plan.md` Phase 2.

## [2.5.3] - 2026-08-16

### Fixed — the net meter reports success when it loads a prebuilt eBPF object

`net-meter-setup.sh` staged a freshly compiled object in a tempfile and removed it from an EXIT
trap whose last command was the guard itself. An EXIT trap's status is the script's exit status,
so on the prebuilt-object path — where the tempfile variable is empty and the guard is false —
a fully successful setup handed systemd a `1`. The unit reported `failed` with every step of
its own output showing the meter attached and ready.

## [2.5.2] - 2026-08-14

### Fixed — kgsm-monitor and kgsm-monitor-net-meter no longer claim the same file

The monitor package copied its whole build stage, which carries the eBPF object that
kgsm-monitor-net-meter installs to the same prefix. pacman refuses a file owned by two packages, and
because net-meter depends on the monitor that made net-meter impossible to install at all.

## [2.5.1] - 2026-08-14

### Added — GPL-3.0-or-later

This project now carries a `LICENSE`. Its package declares `GPL-3.0-or-later` and installs the text
to `/usr/share/licenses/`, so a distributed binary travels with the terms it is under.

### Changed — package license metadata is GPL-3.0-or-later

`PackageLicenseExpression` now matches the repo's own `LICENSE` on every published package. Already
published versions keep the metadata they were built with, since a published version is immutable —
the correction reaches consumers on the next version bump.

### Added — an Arch package, built from the tested binaries

`packaging/PKGBUILD` builds this project into a pacman package. It compiles nothing: CI publishes
first and the recipe places that output, so the packaged bytes are the tested bytes. `pkgver()`
reads `deploy/version.sh`, so the package never restates a version.

The install prefix stays `/opt/<project>` — the same path `deploy.sh` uses — which is what lets the
committed systemd unit ship verbatim instead of being rewritten at packaging time.

Config files are listed in `backup=()`, so an upgrade writes `.pacnew` beside a file you edited
rather than over it. The unit, the sysusers fragment and the leaf descriptor are packaged files, so
the descriptor can never lag the binary it describes. Nothing is enabled by a scriptlet: pacman's
own hooks handle the service account, the state directories and the daemon reload, and enabling a
unit is the administrator's decision.

It builds two packages: the daemon, and `kgsm-monitor-net-meter` carrying the eBPF meter's root-owned
script, unit and prebuilt object. Splitting them keeps the meter optional on a host without eBPF,
where the daemon still serves host metrics and reports per-server rx/tx as null.

### Added — a stopped instance's disk footprint reaches a consumer (contracts 1.6.0)

`Snapshot.serverDisks` carries one `{ id, diskBytes }` row per **watched** instance, whatever its run
state. Every other per-server figure is read from a live cgroup or process tree, so `servers[]` holds
running instances only — the slow directory walk already measured the whole watch-list, and a stopped
instance's footprint was computed each cadence and discarded for want of a row to hang it on. Disk is
a property of an instance's files, not of a run, so it is published as its own array.

An instance whose working dir isn't readable stays absent (the honest "not measured"), never a row of
0, and a running instance's `servers[].diskBytes` is the same value from the same cache.

### Added — one machine-readable version, read rather than restated

`deploy/version.sh` prints this project's version from the single file that declares it, and
`--pkgver` prints the form pacman accepts (a `pkgver` may not contain a hyphen; ordering survives it,
since `vercmp` puts `3.16.0rc3` before `3.16.0`). Packaging asks for a version instead of carrying a
copy that can fall behind the binary.

It reads `src/Monitor/Monitor.csproj` specifically: the package ships the daemon, and
`Monitor.Contracts` is a separate artifact on its own version.

### Added — the deploy contract is files, not install-time script output

`deploy/polkit/48-kgsm-monitor-deploy.rules.in` carries the headless-deploy grant as reviewable content, and
`setup.sh` renders the deploying user and unit list into it instead of embedding the rule in a
heredoc — what a host is granted can now be read without running anything.

`deploy/sysusers.d/kgsm-monitor.conf` declares the `kgsm` service account so a packaged install provisions it
declaratively rather than relying on an account that happens to exist.

`deploy/kgsm-monitor.requires.json` states every host command, peer service and kernel feature this project
needs — each with its Arch package name, a probe that proves it works, and, for anything optional,
what is lost without it.

### Changed — the committed unit names the service account, not a developer

`User=`/`Group=` read `kgsm`, the account `sysusers.d` declares. `render_unit()` still substitutes
the deploying user at install time, so a dev-host deploy is unchanged.

### Fixed — the network meter re-arms when the watchdog starts

`kgsm-net-meter.service` gains `WantedBy=kgsm-watchdog.service`. `PartOf=` propagates the watchdog's
stop and its restart but never its start, so a stop followed by a separate start left the oneshot
down until the next reboot — and a `kgsm.slice` torn down and recreated in between has no programs
attached to it.

Its `ExecStart` is the installed `/opt/kgsm-monitor/net-meter-setup.sh` instead of a path inside a
developer's checkout, and `setup_project_extras` now installs the script, the unit and any prebuilt
`net_meter.bpf.o` — root-owned, because the script loads BPF as root and a root-executed file an
unprivileged user can rewrite is an escalation path. It is skipped cleanly, with the daemon still
serving host metrics, when `bpftool` is absent or `KGSM_SKIP_NET_METER=1`.

`net-meter-setup.sh` defaults `MONITOR_USER` to `kgsm` rather than a developer's login.

### Changed — units live in `deploy/`

`kgsm-monitor.service`, `kgsm-net-meter.service` and `net-meter-setup.sh` move from
`src/Monitor/deploy/` to `deploy/`, so `render_unit()` uses the same path every other repo does.

### Fixed — the watch-list reacts to a native start again

`instance_started` for a native server is the **supervisor's** event, written to its own journal, so
the sampler's four lifecycle subscriptions were only half-served by a reader of the engine's journal
alone. `AddKgsmJournalFederation` now runs after `AddKgsmServices` and every producer's journal is
tailed. Nothing was broken by it — the resync floor re-derives the same watch-list — but the events
exist to react sooner, and half of them were landing somewhere this daemon was not looking.

⚠ The call must stay **after** `AddKgsmServices`: above it the single-journal registration wins,
silently. This daemon's own journal is discovered along with the rest, which costs nothing — the four
handlers are keyed by payload type and a threshold episode matches none of them.

The AOT publish stays 0-warn: discovery and the federated source are file I/O and reflection-free.

### Changed

- **kgsm-lib 4.23.1.** Picks up the journal writer's move into its own package
  (`TheKrystalShip.KGSM.Journal`), which this daemon resolves transitively — no source change, and the
  AOT publish stays 0-warn.

### Added — this daemon records the thresholds it measured

`MonitorJournal` writes `host_threshold_breached` / `host_threshold_cleared` to this daemon's own
event journal (`<history db directory>/events`) the moment an episode opens or closes. Nothing else on
this host takes these measurements, so nothing else can honestly say a value crossed a line — the fact
is now recorded where it happened, instead of another component polling it out of this database every
30 seconds and transcribing it into its own store. Authority:
`../event-journal-federation-plan.md` (Phase 5).

- **An opening and a closing are two events**, because the journal is append-only and they are two
  immutable facts. The mutable view of one condition over time is the alert feed, which answers a
  different question. Both carry `OpenedTs`, so a reader can place the breach without holding the pair.
- **Raw values only** — no summary sentence, no severity, no formatted number. Those are a domain-aware
  reader's business, and putting one consumer's wording in the record would force it on every other.
- ⚠ `CloseReason` travels on every close and must never be flattened into "recovered": a rule retuned,
  disabled or removed closes an episode without the value ever being observed to come down.
- The journal is written **before** the history database. A store failure then cannot leave a fact that
  happened unrecorded, which is the direction that matters.

### Added
- **Threshold episodes + `GET /thresholds/episodes`** — the durable record of what fired and for how long,
  as opposed to the live conditions on each frame. One row per continuous breach, carrying the peak reading
  across it and the leaf id of the daemon that established it, so the store is self-describing at rest.
  Closed episodes age off on the rollup retention window (so what fired outlives the samples behind it); an
  OPEN one is never pruned however old. Needs history on — with it off, alerts still work off the live
  frame and nothing is recorded, which is logged at startup rather than left to be discovered.
  An episode records WHY it ended: `recovered` (the value came back under its line and held), `unwatched`
  (its rule was retuned, disabled or removed while it was firing) or `interrupted` (the daemon stopped
  while it was firing). The last two are not recoveries — the value was never observed to come down — and
  a consumer that called them one would be reporting a measurement nobody took. Episodes left open by a
  previous run are closed as `interrupted` at startup, since dwell state does not survive a restart and
  nothing else would ever close them.
- **`GET|PUT|DELETE /thresholds`** — the rules this daemon watches its own numbers against, and the one place
  they change without a restart. A policy is applied whole, validated whole, and persisted before it is
  swapped in, so a refused one leaves the running rules untouched and one that could not be written is never
  reported as applied. Only the rules whose terms actually changed lose their dwell clocks. An override lives
  at `Monitor__ThresholdPolicyPath`; deleting it (or `DELETE`) returns the host to the built-in defaults.
  ⚠ This makes the metrics socket writable, which it previously was not. The boundary is unchanged — the
  socket's filesystem permissions, which already govern reading every metric this host produces.
- **Threshold evaluation** (`src/Monitor/Thresholds/`) — the daemon decides which metrics are over their
  line, at the sample cadence, and publishes the verdict on every frame. A breach must hold for the rule's
  fire dwell before a condition opens; clearing needs both a hysteresis margin and a clear dwell; the
  deadband between them is where a value parked on the line neither opens nor closes anything. Host rules
  ship enabled, per-server rules ship disabled (absolute thresholds depend on the game). Knobs:
  `Monitor__ThresholdsDisabled`, `Monitor__ThresholdPolicyPath`.
- **`Snapshot.Conditions` + `ConditionReading`** (`Monitor.Contracts` **1.5.0**, additive) — the wire shape
  for a threshold verdict: which rule is over its line, on which target, in which band, since when, and the
  highest reading seen since it opened. Breaching conditions only; a clear is an absence. Deliberately free
  of any consumer's vocabulary — no severities beyond the two bands, no display strings, no deep links.
- **`GET /stats`** — the daemon's report on itself, for the Control Panel's monitor page: nominal
  sample interval and the newest frame's timestamp, what that frame actually covered, and the history
  store's measured contents (row and entity counts per tier, the real span each tier holds, database
  size including its WAL) beside the retention it was configured with. The two are reported side by
  side and never reconciled — a measured span in excess of the window is the only externally visible
  sign that the maintenance pass has stopped completing.
- `HistoryStore.StatsAsync` and a `MaintenanceState` holder recording when the rollup/prune/vacuum pass
  last completed and whether it succeeded. A failed pass was previously logged and otherwise invisible.

### Removed — the engine-event index (**breaking**)

`GET /events`, `POST /events/rebuild` and `events.db` are gone, along with `EventHistoryStore`,
`EventPersistService`, `EventIndexRebuilder`, `EventJournalCursorStore` and the
`Monitor__EventHistoryDisabled` / `Monitor__EventsDbPath` / `Monitor__EventRetentionDays` knobs.

The index was only ever derived from kgsm's event journal — shorter-lived than the record it
copied, and rebuildable from it. What it cost was the coupling: reading audit history required
running a resource-metrics daemon, two things that share nothing but a SQLite idiom, so a host
without this leaf could hold every one of its engine events on disk and still be unable to answer
for them. Engine history is now read from the journal directly through kgsm-lib's
`IEventJournalHistory`, by kgsm-api and the assistant alike.

The `gap` table retires with it. It recorded a *consumer-liveness* failure — this daemon's cursor
pointing at a segment retention had deleted — which a reader holding no cursor cannot have. The
honest replacement is the reader's `CoverageFrom`: the oldest event the journal still holds,
derived rather than recorded.

**Before deploying, run `tools/backfill-journal.py`** if this host's `events.db` predates its
journal. The index was written while the socket transport was live and may hold events the journal
never received; those rows exist nowhere else. Delete `events.db*` only after the export.

### Changed — the monitor tails events at the tail, and stores none

`ServerSampler` still learns from the journal that the instance list has moved. That is the whole
remaining interest: the start position moves from `CursorOrOldest` to `Tail` with no cursor store,
because a replayed event would trigger a resync the periodic floor was going to do anyway.

### Changed — kgsm-lib 3.0.0

Up from 2.0.0, so this build also picks up the player-moderation verbs and the raw-handler position
that landed between. `MetricsMaintenanceService` loses its optional event-store parameter and is
metrics-only.

### Added — `tools/backfill-journal.py`

Exports engine events held only in `events.db` into kgsm's event journal, so the record covers
the full window and the index can be retired (`tks/audit-journal-reader-plan.md`). The index was
written while the socket transport was live and therefore holds events from before the journal
existed; those rows exist nowhere else.

Rows are matched against the journal by event identity — type, timestamp, payload, host — and
not by date, so the boundary day where the two overlap deduplicates correctly. `Actor`/`Origin`
are deliberately outside the match: the two paths enriched the same action differently across
the cutover, and treating that as two events would write a second row claiming someone else did
the same thing in the same second. Where both hold an event the journal's copy stands.

`KGSMVersion` does not survive — the index has no such column, so a backfilled line omits it and
it reads as null, honestly absent rather than invented. Every other envelope field round-trips.

Reports by default; `--apply` writes. Segments are written whole to a temporary file and renamed
into place, and it refuses to rewrite the segment the engine is currently appending to.

### Added — per-leaf resource metrics (Contracts 1.4.0)

`Snapshot.leaves` carries one `LeafMetrics` per running KGSM leaf — `cpuPctCore`, `memBytes`,
nullable `ioReadBps`/`ioWriteBps`, `pids` — from a new `LeafSampler`, so the ecosystem's own daemons
are measured the same way the game servers are. Persisted under the `leaf` entity kind, which the
history store's `(entity_kind, entity_id, metric, ts)` schema already carried, and served by
`GET /metrics/history?kind=leaf&id=<leafId>`; the metric names are deliberately the server
vocabulary, so one chart renders either.

- **The watch-list is the shared descriptor directory** (`Monitor__LeafDescriptorDir`, default
  `/var/lib/kgsm/leaves`), the same files kgsm-api scans — each declares one leaf's id and unit. A
  leaf that joins the ecosystem later is measured with nothing rebuilt here; one never deployed on
  this host is simply absent. Parsed with `JsonDocument`, so it costs the AOT publish nothing.
- **The cgroup sampled is the one the leaf's main process lives in, not its unit's.** cgroup v2
  counters are recursive, so sampling the unit cgroup charges a supervisor for everything it
  supervises: `kgsm-watchdog` runs itself in a `supervisor` child and spawns each game server into a
  sibling, and its unit cgroup reads ~8.2 GB against the daemon's ~103 MB. Resolution goes
  `systemctl show --property=Id,MainPID` → `/proc/<pid>/cgroup`, on a slow cadence
  (`Monitor__LeafResolveMs`, default 30s) off the metrics tick — the one process spawn here, mirroring
  the instance resync. A cgroup that vanishes mid-window nudges an immediate re-resolve, so a leaf
  that restarts is picked up without waiting out the period.
- **Independent of KGSM and of every other leaf**: no privilege (`systemctl show` is an unprivileged
  read, the kernel files are world-readable), no engine, no sibling. A host running leaves but no game
  servers still gets this. Off via `Monitor__LeafMetricsDisabled`.
- **No network and no disk footprint per leaf, deliberately.** The eBPF `cgroup/skb` meter is attached
  to `kgsm.slice` and never sees a leaf in `system.slice`; a leaf's on-disk size is its install prefix,
  static and not worth a recurring walk. A leaf that is not running produces no row at all rather than
  a zero — a socket-activated one sitting idle is absent, not idle-at-zero.

### Added — the leaf config descriptor is generated from the settings type
- **`deploy/kgsm-monitor.leaf.json` is written by `TheKrystalShip.KGSM.LeafConfig` on every build**, from
  `[LeafField]` attributes and `<panel>` doc tags on `MonitorSettings`. A knob now lives in two
  places — the property and the settings-file key — instead of three, and the descriptor cannot
  describe a variable the daemon does not read: the `env` name is derived from the property's
  position under its bound section, and the default from the settings file itself.
- **A field's operator-facing prose comes from a `<panel>` tag**, falling back to `<summary>` with a
  build message naming the field. The two are separate because they answer different questions: the
  summary tells a developer what the value means to the code, the panel tells whoever runs the host
  what changing it does.
- **The generator validates and fails the build**: a settings key no field describes, a described key
  the settings file does not declare, a field with no description, an unknown group or `dependsOn`,
  an enum with no values or a default outside them, bounds on a non-numeric field, a floor-source
  order that does not put the settings file first. `LeafDescriptorTests` is gone — every check it
  made now runs at the point the file is produced, one build step earlier.
- **The cadence floors are declared once.** `MonitorSettings.Floors` is read by both
  `MonitorOptions.FromSettings`, which raises anything lower, and the descriptor's `min`, which is
  what the Control Panel rejects against — so the panel can no longer accept a value the daemon
  would silently move.
- The mechanism is a **build-only package** shared across the ecosystem (`kgsm-leafconfig`): the
  attributes arrive as source, the generator runs in its own process against this assembly's
  metadata, and the package declares no dependencies. The daemon gains no reflection and nothing
  reaches its publish output — the AOT pass stays at zero ILC warnings, there is no
  `System.Reflection.MetadataLoadContext.dll` beside the binary, and the descriptor prose is absent
  from the native binary.

### Fixed
- **Six malformed XML doc comments**, surfaced by turning the documentation file on: an unclosed
  `<para>` in `ServerCgroupResolver`, three `&le;` undefined entities in the history store and DTO,
  and an ambiguous `IEventService.Initialize` cref. Each one dropped its member from the generated
  documentation.
- **`ServerSampler`'s event-delta comment described a socket the monitor no longer opens** (KGSM
  connecting via `socat`) and referenced a `MonitorOptions.KgsmSocketPath` that does not exist.
  Engine events are read from the journal directory.

### Changed
- **`pairedApiKey` names the Control Panel API's renamed setting.** kgsm-api's environment
  variables are now spelled `Api__<Property>`, and this value is what the API resolves to warn that
  a change here has moved this leaf out of its reach. Naming the old key would have made that check
  silently find nothing and report the change as clean.

### Fixed — a knob written blank no longer takes the daemon down
- **Every number and flag in the settings type is nullable, so "written blank" means unset.** Binding
  a blank value to a non-nullable `int` throws, which made a single stray `Monitor__IntervalMs=` line
  in an env file a startup crash; a null one binds to `0`/`false`, silently discarding the coded
  default — a 0ms sampling cadence nobody asked for. Null now means unset and the coded default
  applies. A value that is present but is not a number still fails loudly, which is the point of
  typing it.

### Changed — configuration is bound from `kgsm-monitor.settings.json`, which is now the source of truth

- **Every knob is declared in the settings file and bound to `MonitorSettings`.** The file ships all
  22 keys with their defaults under a `Monitor` section, and an environment variable overrides one
  of them by spelling its path with `__` (`Monitor__IntervalMs`, `Monitor__HistoryDbPath`). The
  hand-rolled `MonitorOptions.FromEnvironment()` and its flat `KGSM_MONITOR_*` names are gone; what
  remains is `MonitorOptions.FromSettings`, which normalizes bound values rather than reading the
  environment itself.

  The point is that a variable naming a key the file does not declare now binds to nothing and
  changes nothing — there is no longer a way to configure the daemon that is invisible in the file.
  Binding is source-generated (the binder generator is on under `PublishAot`), so the AOT publish
  stays at zero IL warnings and nothing here costs reflection.

- **Environment variables are re-registered after the settings file so they win.** Configuration
  resolves by source order, and the explicitly-loaded file is appended after everything the slim
  builder installed — including its own environment provider. Without the re-registration the file
  outranks every `Monitor__*` and `Logging__*` variable. This was already true of the
  logging-only file that preceded this change: `Logging__LogLevel__Default` could not in fact
  override the level the file set, despite being documented as able to.

- **A cadence below its floor is raised to the floor** instead of reverting to the coded default.
  The floor is the nearest legal value to what was asked for; reverting meant a mistyped interval
  ran at a cadence nobody named. The floors themselves are unchanged, and they are the same bounds
  the Control Panel already rejects against before restarting anything.

- **Boolean knobs accept `true`/`false` only.** The hand-rolled parser also took `1/0`, `yes/no` and
  `on/off`; standard binding does not. The Control Panel writes `true`/`false`, so nothing on the
  writing side is affected.

- `LeafDescriptorTests` pins the surface in four directions instead of scanning the source for
  string literals — there are none left to scan. A knob must be a `MonitorSettings` property, a key
  in the settings file, and a descriptor entry; any one missing fails the build naming which. A
  property with no key has an invisible default, a key with no property binds to nothing, and either
  without a descriptor entry is invisible to the panel.

- Descriptor `env` values move to the new names while every `key` stays exactly as it was — stored
  overrides are keyed by `key`, so renaming one would orphan a live override and silently revert a
  leaf to its floor.

- **`floorSources` declares the settings file, first.** The list is lowest-precedence-first, and the
  settings file is the base the environment overrides, so it belongs at the bottom. Omitted entirely,
  the Control Panel could not see where a knob's value came from once the file started carrying one;
  listed last, it would outrank the unit and report the file's defaults as the deployed values. A
  test pins the ordering, because nothing else catches it: the wrong order builds and runs fine, and
  only shows up as a wrong value on the Control Panel after a deploy.

### Changed — kgsm-lib 2.0.0 (the socket event transport is gone)
- **Pinned to `TheKrystalShip.KGSM.Lib` 2.0.0**, which removes `UnixSocketClient`,
  `KgsmEventTransport` and `KgsmOptions.SocketPath`/`EventTransport`. This service already read the
  journal, so the only change here is dropping the now-nonexistent `EventTransport = Journal` line —
  there is no transport left to select. No behaviour change.

### Added — `events.db` is a declared index, rebuildable from the journal
- **`POST /events/rebuild` reconstructs the event index from the engine's journal.** The record is
  the engine's append-only NDJSON; `events.db` is derived from it, exists so a query like "the last
  50 events for this instance, paged" answers in bounded time, and is now recoverable when it is
  lost, corrupted, or was never written because the monitor was down while the engine kept emitting.
  It replays every surviving segment through the same `AppendAsync` the live reader uses, so a
  rebuilt index and a streamed one are the same rows by construction.

  Three properties make it safe to run against a live daemon, and each is pinned by a test:
  it is **additive** — it inserts what is missing and never clears the table, because the journal is
  pruned on age while the index is not, so a wipe would destroy rows whose segments are already gone;
  it **never moves the live cursor**, which is where streaming resumes and not something a recovery
  action should silently skip past; and it **never clears a recorded gap**, since replaying the
  segments that survived does not bring back the events that did not.

  The response is measured, not derived: segments read, the oldest and newest that still exist,
  lines, inserted, already-present, and unparseable. `no_journal` (directory absent) and a
  zero-row `ok` are different answers to different questions. A second concurrent call gets `409`
  rather than a competing full scan.

### Added — retention layering is checked, not assumed
- **The monitor reports at startup whether the journal still reaches back as far as the index claims
  to**, reading `event_journal_retention_days` from the engine and comparing it against
  `KGSM_MONITOR_EVENT_RETENTION_DAYS`. Configured the wrong way round, the index keeps serving rows
  whose segments have been pruned — correct until something rebuilds, at which point history
  silently shortens to the journal's window. That is now visible as an error log naming both numbers
  instead of a surprise during recovery.

  It reports and does not correct: retention lives in the engine's config, the engine prunes on age
  alone and never consults a consumer, and a leaf quietly rewriting the engine's configuration to
  suit itself would invert that ownership. A value it cannot read is logged as unverified, never
  assumed to be fine.

### Changed — engine events come from the journal, not a socket
- **The monitor tails KGSM's append-only event journal** (`KGSM_MONITOR_KGSM_JOURNAL`, default
  `/var/lib/kgsm/events`) instead of binding a socket for the engine to push to.
  `KGSM_MONITOR_KGSM_SOCKET` is gone, and with it the monitor's claim on a socket path the engine
  had to be configured with. The four lifecycle handlers driving watch-list resync are unchanged —
  the transport swap happens entirely below `IEventService`.

  Engine events stop being live-only. A monitor that was down now catches up from its stored
  position rather than losing everything it slept through, which matters because this daemon is
  the ecosystem's engine-event history: what it misses, nothing else records. Its position lives
  in `events.db` beside the events derived from it (`EventJournalCursorStore`), so the index and
  the position it was built from cannot end up in different places. Delivery is at-least-once, and
  that is safe here precisely because `AppendAsync` is already idempotent on the deterministic
  `AuditId` — a replayed event is an `INSERT OR IGNORE` no-op.

  The monitor starts at `CursorOrOldest`: it is the index, so with no stored position it replays
  the surviving journal rather than starting blind.

### Added — gaps are recorded and reported
- **`GET /events` carries a `gaps` array**, and `events.db` gains a `gap` table to back it. When
  journal retention has deleted the segment the monitor's cursor named, events occurred that this
  store will never contain — so it says so, instead of returning a partial history that reads
  exactly like a complete one. An empty array is a positive claim of unbroken coverage. The socket
  transport could not express this at all: a missed event was indistinguishable from an event that
  never happened. The field is additive, so a consumer that ignores it is unaffected.

### Added — leaf config descriptor
- **`deploy/kgsm-monitor.leaf.json` declares the monitor's full configurable surface** — all 21
  `KGSM_MONITOR_*` variables plus the standard `Logging__LogLevel__Default`, each with a label, a
  description written for an operator, its type, its coded default, its bounds, and a `risk` tag.
  `deploy/setup.sh` creates the shared discovery directory `/var/lib/kgsm/leaves/` (the one
  privileged step); `deploy/deploy.sh` installs the descriptor there unprivileged before the binary
  swap, so what kgsm-api reads can never lag the binary implementing it. The daemon does not read
  its own descriptor and remains unaware of the API. Format: `tks/leaf-config-descriptor.md`.
- Five fields are tagged `wiring` (the sockets, the KGSM path, the host id) and five `destructive`
  (the retention cutoffs and the two database paths). `hostId` and `socketPath` name their
  `pairedApiKey`, so a consumer moving either side moves both in one transaction rather than
  severing the link.
- **`LeafDescriptorTests` is the anti-drift guard.** It scans `src/Monitor` for `KGSM_MONITOR_*`
  and fails the build when a variable the daemon reads has no descriptor entry, or when a
  descriptor entry names a variable the daemon does not read — an override for one of those would
  be reported as applied while changing nothing. Scanning the source rather than a constants table
  is deliberate: a table only proves the table and the descriptor agree, and a knob read through a
  raw literal would bypass both.

### Changed — headless deploys (`setup.sh` once, `deploy.sh` forever after)
- **`deploy/setup.sh` provisions the host once** (asks for sudo; idempotent): chowns
  `/opt/kgsm-monitor` to the deploying user, puts the real unit in `/etc/kgsm-monitor/systemd/` with
  `/etc/systemd/system/kgsm-monitor.service` symlinked to it, installs a polkit grant scoped to this
  project's units, enables the unit, and verifies the grant with the same unprivileged `systemctl`
  calls `deploy.sh` makes.
- **`deploy/deploy.sh` runs with no `sudo` and no prompts**, and refuses up-front (before building)
  with "run `deploy/setup.sh`" when the host is not provisioned. The AOT publish, the native-library
  prune/install, and the unit refresh are otherwise unchanged.
- `deploy/deploy-common.sh` carries the project block plus the shared helpers, sourced by both entry
  points so they cannot drift. Canonical template and contract:
  `tks/scripts/deploy-template/README.md`.

## [1.5.1] - 2026-07-28

### Added
- **Blueprint-event attribution** — the `event` table carries a `blueprint TEXT` column (additive,
  NULL by default) that blueprint-scoped engine events (Phase 2 of `blueprint-editor-plan.md`)
  populate with the event's `BlueprintName`. Unlike `instance`, which an instance-scoped event
  fills, a blueprint event's subject is a blueprint file — forcing it through `instance` would
  invent a server relationship that does not exist. The two columns are orthogonal: an
  instance-scoped row leaves `blueprint` null, a blueprint-scoped row leaves `instance` null, and
  the `?blueprint=<name>` query filter (mirroring `?instance=`) returns blueprint rows only.
  Surface: the existing `GET /events` route now also accepts `?blueprint=`. Downstream readers
  (kgsm-api `MonitorEventShaping`) carry no Server audit target when `instance` is null, so
  blueprint rows never appear as server rows in `GET /audit` — the columns stay disjoint there
  too.

## [1.5.0] - 2026-07-18

### Added
- **Event history** — the monitor is the single source of truth for KGSM *engine* events (Phase B of
  `event-history-plan.md`). A raw handler (`IEventService.RegisterRawHandler`, kgsm-lib 1.36.0) fires
  on every deserialized event envelope, known or unknown type, and persists it to a dedicated
  `events.db` (own WAL, own single-writer gate — no contention with the metrics flusher) via
  `INSERT OR IGNORE` on the deterministic `evt_<hash>` id (`Events.AuditId.ForEvent`), so a
  redelivered event never double-inserts. `GET /events?instance=&type=&since=&until=&before_ts=&
  before_id=&limit=` serves ts-DESC windowed/filtered queries with a composite `(ts, id)` keyset
  cursor. Rows are stored raw and neutral (no domain shaping — that stays kgsm-api's read-time
  concern); `instance`/`actor`/`origin` are `NULL` when the emitter supplied none, never fabricated.
  No rollup tier (discrete facts, not a sampled series) — retention is a straight prune.
- Config knobs: `KGSM_MONITOR_EVENTS_DB_PATH` (`/var/lib/kgsm-monitor/events.db`),
  `KGSM_MONITOR_EVENT_HISTORY_DISABLED` (default enabled, independent of the metrics-history flag),
  `KGSM_MONITOR_EVENT_RETENTION_DAYS` (default 30).
- The shared rollup/prune/vacuum maintenance loop now also prunes `events.db` in the same pass when
  event history is enabled; metrics and event history are independently toggleable, so the loop runs
  whenever either is on and degrades correctly if only one store is wired.

### Notes
- Event-history persistence is gated on `KgsmEnabled` (needs the KGSM event socket) *and*
  `EventHistoryEnabled`. `Monitor.Contracts` is unchanged — the event-history DTOs are daemon-local
  (`src/Monitor/History/EventHistoryDto.cs`), same pattern as metrics history.
- Bumps the `TheKrystalShip.KGSM.Lib` pin to 1.36.0 (`RegisterRawHandler` + `AuditId.ForEvent`).

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
