# kgsm-monitor — Performance Baseline

> Captured **2026-06-11** with BenchmarkDotNet 0.15.8 on an otherwise-quiet host.
> Reproduce: `dotnet run -c Release --project bench/Monitor.Benchmarks -- --filter '*'`
> (job: 5 warmup + 10 iterations; `[MemoryDiagnoser]` on). Sources read live `/proc`+`/sys`.

**Host:** AMD Ryzen 7 3800X (8C/16T), Arch Linux, .NET SDK 10.0.108, RyuJIT x86-64-v3.

## Headline

| What | Time | Alloc | vs 1000 ms tick |
|------|-----:|------:|----------------:|
| **Full diagnostic frame** (`BuildFrame`) | **1.61 ms** | 398 KB | **0.16 % of budget** |
| Serialize frame (source-gen JSON) | 1.85 µs | 1.33 KB | — (this is the scrape cost) |
| Build + serialize (end-to-end per fresh scrape) | 1.61 ms | 399 KB | 0.16 % |

A full frame costs **1.61 ms of CPU once per second** — ~**0.16 % of one core**. (Consistent with
the live load-test self-cost of ~0.3 % of a core, which also includes scrape-serving + scheduling.)
Theoretical ceiling at the current cost: **~620 frames/sec** before the sampler can't keep up — i.e.
**~620× headroom** at 1 Hz.

> ⚠️ Captured on an **idle host (24 mounts)**. ~97 % of the frame is Disk, and that cost **scales with
> mount count** — which grows as containerized game servers come up. Treat 1.61 ms as a clean-host
> *floor*, not a fixed property; re-measure under representative container load (see Judgment §3).

## Frame
```
| Method            | Mean         | StdDev    | Allocated |
|------------------ |-------------:|----------:|----------:|
| BuildFrame        | 1,611.18 µs  | 6.09 µs   | 397.98 KB |
| SerializeFrame    |     1.85 µs  | 0.03 µs   |   1.33 KB |
| BuildAndSerialize | 1,614.92 µs  | 2.15 µs   | 399.31 KB |
```

## Per-source decomposition (where the time goes)
```
| Source     | Mean        | Allocated | % of frame |
|----------- |------------:|----------:|-----------:|
| Disk       | 1,561.90 µs | 306.78 KB |   96.9 %   |  <-- DriveInfo.GetDrives() statvfs + /sys/block
| Network    |    58.20 µs |  16.41 KB |    3.6 %   |
| Cpu        |    43.99 µs |  38.88 KB |    2.7 %   |
| SystemInfo |    17.26 µs |  15.88 KB |    1.1 %   |
| Memory     |    16.34 µs |  19.90 KB |    1.0 %   |
```
(Percentages sum to >100 % because frame ≈ Σ sources and the per-source runs have independent noise.)

## Pure parse / rate math (in-memory, no IO)
```
| Method           | Mean       | Allocated |
|----------------- |-----------:|----------:|
| Cpu_Parse        | 6,202 ns   | 16,728 B  |
| Mem_Parse        | 2,617 ns   |  5,176 B  |
| Net_Parse        | 2,246 ns   |  6,432 B  |
| Load_Parse       |   211 ns   |    272 B  |
| Uptime_Parse     |   103 ns   |    128 B  |
| Net_ComputeRates |    85 ns   |    264 B  |
| Cpu_ComputeRates |    41 ns   |    312 B  |
```
Total pure-parse ≈ **11.5 µs = 0.7 % of a frame.** The frame is **syscall-bound**, not CPU-bound.

## Validity cross-checks
- **Latency:** frame 1611 µs ≈ Σ(sources) 1697 µs (within Disk's ±62 µs variance) + serialize 1.85 µs.
- **Allocation:** frame 397.98 KB ≈ Σ(source allocs) 397.85 KB — near-exact. No DCE, no hidden caching.

## Judgment

1. **Viable, with enormous margin.** 1.61 ms / 1000 ms = 0.16 %. We could sample at 10 Hz (1.6 %)
   or higher and still be invisible next to the game servers.
2. **AOT baseline = JIT baseline (for the frame).** Codegen only affects the pure-parse tier, which
   is 0.7 % of the frame; the rest is kernel-transition cost identical under JIT and AOT. So these
   JIT numbers represent the shipped Native-AOT artifact within ~1 %. (Worth re-running under the
   NativeAOT toolchain only *after* Disk is optimized away — then parse becomes a meaningful fraction.)
3. **Disk is the entire story (96.9 %) — and it _scales with mount count_.** `DriveInfo.GetDrives()`
   enumerates **every** mount and the code touches `d.IsReady`/`d.DriveFormat` (per-mount `statvfs`)
   on all of them *before* `IncludeMount` filters — so pseudo/overlay mounts still pay. This box was
   **idle with 24 mounts → 1.56 ms**. ⚠️ **The production host runs containerized game servers, and each
   Docker container adds overlay/shm/secret mounts** — so this cost grows with the very workload the
   daemon exists to watch. The 1.61 ms frame is **workload-dependent, not a fixed property**;
   **re-measure `SourceBenchmarks.Disk` once containers are present (Slice 2).** Even a 5–10× blow-up is
   ~8–16 ms = ~1 % of the tick, so viability holds — but the headroom claim shrinks accordingly.
   Levers, in order of impact (note now, act when Disk becomes the constraint — not yet):
   - **Skip `DriveInfo`: read `/proc/self/mountinfo` directly, filter by fstype string, `statvfs` only
     the survivors.** Cuts per-tick `statvfs` from ~24 → ~2 and *kills the scaling at its root*. (Verify
     DriveInfo's exact syscall behavior before committing.)
   - **Decouple disk-usage cadence** — usage moves slowly; sample it every N seconds while CPU/mem/net
     stay at 1 Hz. Drops the steady frame to **~50 µs** but doesn't fix the per-sample scaling.
4. **Allocation is fine at 1 Hz, and is the lever for high rates.** 398 KB/frame → ~0.4 MB/s of Gen0
   garbage at 1 Hz = a sub-ms Gen0 collection roughly every ~20 s — irrelevant to game servers. If we
   push to 10 Hz+, span-based splitting (drop the per-line `string.Split`) is the cut.
5. **Slice 2 headroom is huge.** A per-server cgroup read (`cpu.stat`, `memory.current`, `io.stat`,
   `pids.current` — tiny files) costs on the order of the Memory source (~16 µs) or less. Even **100
   servers × ~30 µs = 3 ms**, still 0.3 % of the 1 Hz budget. The host runs out of RAM/CPU for actual
   game servers long before the monitor's sampling cost matters. **Disk (1.56 ms) remains the single
   biggest cost — bigger than per-server sampling will be at any realistic server count.**
