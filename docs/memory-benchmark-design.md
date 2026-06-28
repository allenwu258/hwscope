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
  C# models, runner abstraction, process orchestration, CSV parsing

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

Minimum memory buffer size is 16 MiB. CSV output is used by HwScope:

```text
size_mib,read_mib_s,write_mib_s,copy_mib_s,latency_ns
```

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
- Add result metadata: worker version, options, executable path, elapsed time.
- Add cancellation and timeout handling in `MemoryBenchmarkProcessRunner`.
- Record executable path, arguments, stdout, and stderr for timeout, cancellation, non-zero exit, and parse failures.

### Stage 2: Multi-Threaded Memory Bandwidth

This is the biggest expected improvement toward AIDA64-like memory bandwidth.

- Add `--threads`.
- Allocate independent buffers per worker thread.
- Pin each thread to a distinct physical core.
- Avoid SMT sibling placement until physical cores are exhausted.
- Aggregate per-thread bytes and report total throughput.
- Keep single-thread mode available for diagnostic comparison.

### Stage 3: SIMD Kernels

Replace generic scalar/CRT paths with explicit kernels.

- Add CPU feature detection.
- Dispatch SSE2, AVX2, and AVX-512 paths where available.
- Implement read kernels with vector loads and reduction into a sink.
- Implement write kernels with vector stores.
- Implement copy kernels with load/store loops.
- Evaluate non-temporal stores for large memory working sets.

Non-temporal stores should be optional because they can improve large streaming writes but distort cache-resident tests.

### Stage 4: Cache Rows

Add cache-aware working set selection.

- Detect L1/L2/L3 sizes and sharing topology.
- Run benchmark rows for L1, L2, L3, and Memory.
- Keep cache tests single-threaded by default because cache-local throughput is usually a per-core property.
- Run memory tests with both single-thread and multi-thread modes.

### Stage 5: Better Timing

The current worker uses `std::chrono::steady_clock`, which is good enough for the prototype.

Future options:

- Use `QueryPerformanceCounter` directly on Windows.
- Consider `RDTSCP` with serialization for short cache tests.
- Calibrate invariant TSC before relying on cycle-based timing.
- Increase inner-loop duration for small cache working sets to reduce timer noise.

### Stage 6: Topology And Environment Awareness

Benchmark scores are strongly affected by topology.

- Detect CPU groups on high-core-count Windows systems.
- Detect NUMA nodes and memory locality.
- Choose test thread placement explicitly.
- Identify hypervisor presence and show it in the benchmark window.
- Record power plan, process priority, and whether the system is on AC power.

### Stage 7: Result Quality

Improve repeatability and diagnostics.

- Use warmup rounds separate from measured rounds.
- Keep median as the default aggregation.
- Also record min, max, mean, and standard deviation.
- Mark results unstable when variance is high.
- Save benchmark history with hardware metadata and options.

## Near-Term Tasks For HwScope

Recommended next steps:

1. Add `--threads` to the native worker and expose it through `MemoryBenchmarkOptions`.
2. Add `--json` output to the worker so C# does not rely only on CSV column order.
3. Add result metadata: worker version, options, executable path, elapsed time, and result quality flags.
4. Add L1/L2/L3 cache rows using detected cache sizes.
5. Add a compact benchmark report export from `MemoryBenchmarkWindow`.

