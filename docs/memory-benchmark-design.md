# Memory Benchmark Design Notes

This document summarizes the design discussion from Codex session `019ef287-5c2f-79d2-9034-c21763e9aafc` and maps it to the current HwScope implementation.

## Scope

The current HwScope memory benchmark is a first native prototype. It measures main-memory throughput and random-access latency only:

- `Read`: sequential read throughput.
- `Write`: sequential write throughput.
- `Copy`: sequential copy throughput.
- `Latency`: randomized pointer-chasing latency.

It does not yet measure L1, L2, or L3 cache rows. The UI keeps those rows as placeholders because the intended end state is an AIDA64-style `Cache & Memory Benchmark` table.

Relevant files:

- `src/HwScope.Native.MemoryBench/src/main.cpp`
- `src/HwScope.Core/Benchmark/MemoryBenchmarkProcessRunner.cs`
- `src/HwScope.App/MemoryBenchmarkWindow.xaml`

## Current GUI Integration

The WPF app opens the benchmark in a separate Fluent window, matching the AIDA64 pattern where the benchmark table is not embedded into the main dashboard.

The window is reachable from:

- Top toolbar: `跑分`
- Left navigation: `性能测试 -> 内存跑分`
- Main menu: `工具 -> 内存跑分`

The benchmark table intentionally keeps a white background and black text for now, while the window chrome and surrounding shell use the shared HwScope theme resources. This preserves the AIDA64-like benchmark table while still allowing the window to follow the app theme service.

Theme integration is handled through `ThemeService`. The benchmark window attaches after `Loaded` to avoid calling WPF-UI `SystemThemeWatcher` before the window handle exists.

## Intended Architecture

The long-term benchmark design has three layers:

```text
HwScope.App / HwScope.Cli
  UI, commands, result presentation

HwScope.Core.Benchmark
  C# models, runner abstraction, process orchestration, CSV / progress JSON parsing

HwScope.Native.MemoryBench
  C++ benchmark worker for low-level memory access patterns
```

The benchmark engine should stay native. Managed runtimes are useful for orchestration and UI, but low-level memory benchmarking is sensitive to runtime overhead, JIT behavior, GC, bounds checks, and scheduler noise.

The current implementation uses a native worker process instead of a DLL. This is deliberate for the first version: the existing C++ prototype was already a CLI program, worker crashes do not take down the WPF process, and this shape will also fit future long-running stability tests.

## Current Algorithm

### Options

Current defaults:

```text
--size-mib 512
--iterations 7
--latency-steps 20000000
```

Minimum memory buffer size is 16 MiB. CSV output remains available for manual compatibility:

```text
size_mib,read_mib_s,write_mib_s,copy_mib_s,latency_ns
```

The preferred final-result protocol is structured JSON:

```text
{"type":"result","worker_version":"0.2.0","protocol_version":2,"elapsed_ms":1234.56,"options":{"size_mib":512,"iterations":7,"latency_steps":20000000,"threads":1,"working_set_kind":"memory"},"metrics":{"read":{"unit":"mib_s","samples":[36000.00],"aggregate":{"median":36000.00,"min":36000.00,"max":36000.00,"mean":36000.00,"stddev":0.00,"cv":0.000000}}}}
```

The GUI uses newline-delimited progress JSON so each result can appear as soon as that metric finishes:

```text
{"type":"started","size_mib":512,"iterations":7,"latency_steps":20000000}
{"type":"metric","metric":"read","value":36000.00,"unit":"mib_s"}
{"type":"metric","metric":"write","value":51000.00,"unit":"mib_s"}
{"type":"metric","metric":"copy","value":22000.00,"unit":"mib_s"}
{"type":"metric","metric":"latency","value":138.50,"unit":"ns"}
{"type":"result",...}
{"type":"completed"}
```

The worker flushes each progress JSON line immediately. `MemoryBenchmarkProcessRunner` reads stdout line-by-line, reports `MemoryBenchmarkProgress` updates to the UI, and still returns a complete `MemoryBenchmarkResult` when the worker exits successfully.

Progress reporting is best-effort during the run: malformed or unknown progress lines are ignored for live UI updates so stdout collection can continue. After the worker exits with code 0, the runner strictly parses the accumulated output and requires `started`, all four metric events, and `completed`. Parse failures are recorded with executable path, arguments, stdout, and stderr in the benchmark diagnostic log before surfacing a managed `FormatException`.

`MemoryBenchmarkProcessRunner` enriches the native result with the resolved executable path, environment metadata, and quality flags. The first quality pass is intentionally conservative: it marks high variance, likely background noise, short runs, simple downward throughput trends, and incomplete topology data. These flags are diagnostic hints, not proof of a specific thermal or scheduling cause.

### Allocation And Warmup

The worker allocates 64-byte aligned buffers via `_aligned_malloc` on Windows. This matches common cache-line size and keeps future SIMD paths straightforward.

Before timed runs, it touches pages in 4 KiB steps. This reduces first-touch page fault noise during measured loops.

The worker also runs one untimed warmup pass of read, write, and copy before collecting official samples.

### Read Throughput

The read benchmark treats the buffer as `uint64_t[]`, walks it sequentially, and manually unrolls eight accumulators. The accumulators are XORed into a global `volatile` sink so the compiler cannot delete the loop.

Result calculation:

```text
read_mib_s = bytes_read / elapsed_seconds / 1024 / 1024
```

Samples are collected for `iterations` rounds, then the median sample is reported.

### Write Throughput

The write benchmark calls `std::memset` over the whole buffer with a changing byte value per round. One byte is mixed into the volatile sink after each round.

This currently measures the platform C runtime's memset path rather than a custom AVX/non-temporal store path.

### Copy Throughput

The copy benchmark calls `std::memcpy(dst, src, bytes)` and reports copied bytes per second.

Copy is naturally more demanding than read or write because it pressures both load and store paths. The current score is also dependent on the CRT memcpy implementation and does not use a HwScope-specific copy kernel yet.

### Latency

Latency uses randomized pointer chasing:

1. Create an array of `uint32_t` node indexes.
2. Shuffle the node order with a fixed seed.
3. Build a single cycle where each node points to the next shuffled node.
4. Follow the chain for a warmup subset.
5. Time `latency_steps` dependent loads.

Each load depends on the previous loaded index, so the CPU cannot freely parallelize the loop. This suppresses the benefit of sequential hardware prefetching and makes the result closer to true random-access latency.

Result calculation:

```text
latency_ns = elapsed_seconds * 1_000_000_000 / latency_steps
```

### Thread Pinning

The worker calls `GetCurrentProcessorNumber()` and then pins the current thread to that CPU with `SetThreadAffinityMask`.

This prevents scheduler migration during a single-thread run, but it is still a minimal policy. It does not yet choose a preferred physical core, avoid E-cores/P-cores, avoid SMT siblings, or handle CPU groups.

## Why Results Differ From AIDA64

Large differences from AIDA64 are expected at this stage.

The current benchmark is single-threaded. AIDA64's memory read/write/copy rows are highly optimized and commonly exercise enough cores to saturate the memory controller. Single-thread memory streaming can be much lower than whole-system memory bandwidth.

The current read path is scalar C++ with manual unrolling. Write and copy call CRT `memset` and `memcpy`. AIDA64 likely uses platform-specific assembly or intrinsics, dispatching different paths for SSE2, AVX2, AVX-512, non-temporal stores, and CPU-family-specific behavior.

The current copy result counts only the copied payload bytes. A copy operation also reads and writes memory, so different tools may use different accounting conventions or kernels.

Units can also differ. The native worker reports `MiB/s`. The UI currently converts to decimal `MB/s` for display. This accounts for only about a 4.86% difference and does not explain large gaps.

Latency is expected to be closer than bandwidth, but still differs due to thread placement, page size, TLB behavior, power state, background load, NUMA placement, and test buffer size.

## Accuracy Roadmap

Improving the memory benchmark should not only chase higher numbers. The goal is to make each score more accurate, more repeatable, and easier to compare with tools such as AIDA64.

HwScope should improve accuracy in this order:

1. Make results explainable: record metadata, samples, selected kernel, topology, timing source, and environment.
2. Make measurement windows stable: use explicit timing, warmup policy, adaptive sample duration, and variance checks.
3. Make thread placement deterministic: choose physical cores, handle SMT, CPU groups, NUMA locality, and hybrid-core hints.
4. Make memory bandwidth representative: add multi-threaded read/write/copy paths that can saturate the memory controller.
5. Make kernels comparable: add explicit SIMD and non-temporal variants while preserving scalar/CRT reference paths.
6. Make cache rows real: derive L1/L2/L3 working sets from detected cache topology instead of reusing the memory row.
7. Make the UI honest: surface result quality and warnings instead of showing a bare number without confidence context.

### Measurement Metadata

Each final result should include enough context to explain how it was produced:

- Benchmark options: size, iterations or sample policy, latency steps, thread mode, working set kind, and selected kernel.
- Worker metadata: worker version, protocol version, executable path, command line, and elapsed time.
- Environment metadata: CPU name, physical/logical core count, NUMA node, power plan, AC power state, process priority, and virtualization hints.
- Sample metadata: raw samples, median, min, max, mean, standard deviation, coefficient of variation, and any discarded warmup samples.
- Quality flags: high variance, short sample duration, suspected background noise, suspected thermal or power throttling, and incomplete topology data.

`MemoryBenchmarkResult` now carries these first-stage fields for the main memory row. Future phases should deepen the metadata rather than replace this shape. Without this evidence chain, a faster number is hard to trust.

### Timing And Sampling

The current worker uses fixed `iterations` and `std::chrono::steady_clock`, which is acceptable for a prototype but weak for cache-sized tests and noisy systems.

The next timing model should use a target-duration loop:

```text
for each metric:
  run separate warmup passes
  collect at least min_samples
  make each measured sample last at least target_sample_ms
  stop after variance converges or max_samples is reached
```

On Windows, `QueryPerformanceCounter` should be the primary wall-clock timer. `RDTSCP` can be evaluated later for short cache tests, but only after validating invariant TSC behavior and serialization costs.

Median should remain the default displayed aggregate. Mean, min, max, standard deviation, and coefficient of variation should be recorded for diagnostics and quality flags.

### Thread Placement

Thread placement has a direct effect on both bandwidth and latency.

Single-thread tests should bind to a selected physical core instead of pinning to whichever logical processor the worker starts on. Multi-threaded tests should:

- Prefer one logical processor per physical core before using SMT siblings.
- Keep memory tests NUMA-local by default, with explicit modes for per-node and interleaved tests.
- Handle Windows CPU groups on high-core-count systems.
- Record the chosen logical processors in the result.
- Keep single-thread mode available as a diagnostic baseline.

Hybrid CPUs need special care. If reliable P-core/E-core classification is unavailable, the benchmark should record that the placement is heuristic instead of implying full accuracy.

### Bandwidth Kernels

The highest-impact step for AIDA64-like memory bandwidth is multi-threading. Each worker thread should own independent buffers and synchronize through a barrier so all threads measure the same window.

Copy results should clearly define accounting. HwScope should record both:

```text
copy_payload_mib_s = copied_bytes / elapsed_seconds / MiB
copy_traffic_mib_s = copied_bytes * 2 / elapsed_seconds / MiB
```

The UI can choose one convention, but the protocol and documentation should preserve both so comparisons with external tools are not ambiguous.

SIMD kernels should be introduced after metadata, timing, and topology are in place:

- Keep scalar and CRT paths as reference kernels.
- Add CPU feature detection and dispatch for SSE2, AVX2, and AVX-512 where available.
- Add cached and non-temporal store variants.
- Restrict non-temporal stores to large memory working sets by default because they can distort cache-resident tests.
- Record the selected kernel and store policy in the result.

### Cache And Latency Rows

The target benchmark table includes Memory, L1 Cache, L2 Cache, and L3 Cache rows. Those rows should be backed by topology-aware working sets:

- Detect cache sizes, line size, and sharing topology through CPUID or `GetLogicalProcessorInformationEx`.
- Use a working set comfortably inside the target cache level rather than at the exact boundary.
- Treat shared L3 or CCD/CCX-local caches as topology domains, not global constants.
- Prefer single-thread cache bandwidth tests because cache-local throughput is usually a per-core property.
- Keep memory bandwidth tests available in both single-thread and multi-thread modes.

Latency should continue to use pointer chasing. Future latency work should produce a curve across L1, L2, L3, and Memory working sets, and should record page size, random seed, node count, warmup count, and whether huge pages are used.

## Cache Benchmark Direction

The original design target includes AIDA64-like rows:

```text
Memory
L1 Cache
L2 Cache
L3 Cache
```

The core idea is that working set size determines the level being measured:

```text
L1:     16 KiB to 64 KiB
L2:     256 KiB to 2 MiB
L3:     several MiB to LLC size
Memory: much larger than LLC, for example 512 MiB or 1 GiB
```

For a robust implementation, cache sizes should come from CPUID or `GetLogicalProcessorInformationEx`, not hardcoded constants. The benchmark matrix should derive sizes from detected topology, then run the same read/write/copy/latency kernels against each working set.

Cache latency should also use pointer chasing. Sequential latency tests are misleading because hardware prefetchers can hide much of the access cost.

## Evolution Plan

### Stage 1: Stabilize Current Worker

Keep the current worker process model, but make packaging reliable.

- Build `membench.exe` as part of the developer workflow.
- Copy the native executable into the WPF/CLI output directory under a `native/` subdirectory when the Release native artifact exists.
- Keep only a source-tree `build\Release` developer fallback; do not use personal external prototype paths.
- Add cancellation and timeout handling in `MemoryBenchmarkProcessRunner`.
- Record executable path, arguments, stdout, and stderr for timeout, cancellation, non-zero exit, and parse failures.
- Emit and parse progress JSON so the GUI can show read/write/copy/latency incrementally, while final parsing remains strict and diagnostic-friendly.
- Add result metadata: worker version, options, executable path, elapsed time.

### Stage 2: Result Metadata And Final JSON

Make the result protocol explainable before adding faster measurement paths.

- Add worker version, protocol version, options, executable path, arguments, elapsed time, and selected kernel to final output.
- Add raw samples and aggregate statistics for each metric.
- Add quality flags for variance, short sample duration, topology uncertainty, and likely environment interference.
- Keep CSV compatibility for existing CLI flows, but use structured JSON as the preferred C# parsing path.

### Stage 3: Better Timing And Adaptive Sampling

The current worker uses `std::chrono::steady_clock`, which is good enough for the prototype.

Future work:

- Use `QueryPerformanceCounter` directly on Windows.
- Consider `RDTSCP` with serialization for short cache tests.
- Calibrate invariant TSC before relying on cycle-based timing.
- Separate warmup rounds from measured rounds.
- Replace fixed iteration counts with target sample duration, minimum sample count, maximum sample count, and variance convergence.
- Increase inner-loop duration for small cache working sets to reduce timer noise.

### Stage 4: Topology And Environment Awareness

Benchmark scores are strongly affected by topology.

- Detect CPU groups on high-core-count Windows systems.
- Detect NUMA nodes and memory locality.
- Choose test thread placement explicitly.
- Prefer physical cores before SMT siblings for multi-thread runs.
- Record selected logical processors and topology confidence in the result.
- Identify hypervisor presence and show it in the benchmark window.
- Record power plan, process priority, and whether the system is on AC power.

### Stage 5: Multi-Threaded Memory Bandwidth

This is the biggest expected improvement toward AIDA64-like memory bandwidth.

- Add `--threads`.
- Allocate independent buffers per worker thread.
- Synchronize worker start with a barrier.
- Pin each thread to a distinct physical core by default.
- Avoid SMT sibling placement until physical cores are exhausted.
- Aggregate per-thread bytes over a shared measurement window and report total throughput.
- Keep single-thread mode available for diagnostic comparison.
- Report both copy payload throughput and estimated copy traffic throughput.

### Stage 6: SIMD Kernels

Replace generic scalar/CRT paths with explicit kernels after timing and topology are observable.

- Add CPU feature detection.
- Dispatch SSE2, AVX2, and AVX-512 paths where available.
- Implement read kernels with vector loads and reduction into a sink.
- Implement write kernels with vector stores.
- Implement copy kernels with load/store loops.
- Evaluate non-temporal stores for large memory working sets.
- Record the selected kernel and store policy in the result.

Non-temporal stores should be optional because they can improve large streaming writes but distort cache-resident tests.

### Stage 7: Cache Rows

Add cache-aware working set selection.

- Detect L1/L2/L3 sizes and sharing topology.
- Run benchmark rows for L1, L2, L3, and Memory.
- Keep cache tests single-threaded by default because cache-local throughput is usually a per-core property.
- Run memory tests with both single-thread and multi-thread modes.
- Generate latency rows from pointer-chasing working sets sized for each cache level.

### Stage 8: Result Quality And History

Improve repeatability, diagnostics, and user trust.

- Keep median as the default displayed aggregation.
- Also record min, max, mean, standard deviation, coefficient of variation, and sample count.
- Mark results unstable when variance is high.
- Surface environment and topology warnings in the benchmark window.
- Save benchmark history with hardware metadata and options.

## Near-Term Tasks For HwScope

Recommended next steps:

1. Add structured final JSON with worker version, options, elapsed time, raw samples, aggregate statistics, and quality flags.
2. Add adaptive timing: warmup samples, target sample duration, min/max sample count, and variance calculation.
3. Add topology-aware placement metadata before enabling multi-thread default behavior.
4. Add `--threads` to the native worker and expose it through `MemoryBenchmarkOptions`.
5. Add explicit kernel metadata and then introduce SIMD kernels behind feature detection.
6. Add L1/L2/L3 cache rows using detected cache sizes and sharing topology.
7. Add a compact benchmark report export from `MemoryBenchmarkWindow`.

