# HwScope.Native.MemoryBench

Native C++ worker for HwScope memory benchmarking.

This worker currently measures main-memory read, write, copy throughput and random-access latency. `HwScope.Core.Benchmark.MemoryBenchmarkProcessRunner` invokes it with structured JSON by default, or `--progress-json` for live GUI updates.

It does not yet measure L1, L2, or L3 cache rows. Those rows are currently UI placeholders in `HwScope.App`.

## Build

From this directory:

```powershell
.\scripts\build-msvc.ps1
```

The output is:

```text
build\Release\membench.exe
```

After the native Release artifact exists, `HwScope.App` and `HwScope.Cli` copy it to their output directories during `dotnet build`:

```text
bin\Debug\net8.0-windows\native\membench.exe
```

The C# runner also keeps a source-tree `build\Release` developer fallback so App/CLI can run directly after a manual native build.

## Invocation

HwScope invokes the worker with structured final JSON by default and passes the expected protocol version:

```text
membench --size-mib 512 --iterations 7 --latency-steps 20000000 --expected-protocol-version 5 --json
```

`--iterations` is a legacy alias for the minimum measured sample count. The adaptive run policy can also be controlled explicitly:

```text
membench --size-mib 512 --min-samples 7 --max-samples 11 --warmup-runs 1 --target-sample-ms 120 --max-cv 0.03 --expected-protocol-version 5 --json
```

On Windows, the worker times samples with `QueryPerformanceCounter` and records the timer frequency. Each metric warms up, increases its inner loop until the sample reaches the target duration, then stops after variance converges or `max-samples` is reached.

When invoked by HwScope Core, the worker also receives logical processors selected from Windows topology. Single-core diagnostic mode passes one preferred logical processor:

```text
membench --preferred-group 0 --preferred-processor 0 --preferred-core 0 --preferred-smt-index 0 --expected-protocol-version 5 --json
```

The worker applies this with `SetThreadGroupAffinity` and records both requested and actual placement. Direct CLI runs without preferred placement are still allowed, but their JSON is marked as native fallback placement.

Multi-thread read/write/copy is available through Core-selected worker processors. The default App/CLI memory benchmark uses `ThreadMode=PhysicalCores`, which selects one logical processor per physical core before SMT siblings:

```text
membench --threads 4 --thread-mode PhysicalCores --worker-processor 0:0:0:-1:0:0:0:0 --worker-processor 0:2:1:-1:0:0:0:0 --expected-protocol-version 5 --json
```

Each worker has independent source/destination buffers and the timed window starts after all workers are ready. `size_mib` is treated as the total requested working set and split across workers; configurations below 16 MiB per worker are rejected. Latency remains single-threaded. Copy reports payload throughput as `samples`/`aggregate` and estimated read+write traffic as `traffic_samples`/`traffic_aggregate`.

If Core topology collection fails while the thread count is automatic, Core intentionally falls back to one native worker rather than guessing from logical processor count. This preserves the previous single-thread baseline and avoids uncontrolled SMT/NUMA placement.

JSON output includes worker/protocol metadata, options, timer metadata, placement metadata, elapsed time, raw samples, inner-loop counts, convergence state, aggregate statistics, and copy traffic diagnostics.

CSV remains available for manual compatibility:

```text
membench --size-mib 512 --iterations 7 --latency-steps 20000000 --expected-protocol-version 5 --csv
```

CSV output format:

```text
size_mib,read_mib_s,write_mib_s,copy_mib_s,latency_ns
```

The GUI uses newline-delimited progress JSON:

```text
membench --size-mib 512 --iterations 7 --latency-steps 20000000 --expected-protocol-version 5 --progress-json
```

Each completed metric is flushed as one event:

```json
{"type":"metric","metric":"read","value":36000.00,"unit":"mib_s"}
```

The progress stream also emits a final `type=result` JSON event before `type=completed`. The C# runner treats live metric updates as best-effort, then strictly validates the full stream after process exit and writes stdout/stderr diagnostics for parse failures.

Useful quick validation command:

```powershell
.\build\Release\membench.exe --size-mib 16 --min-samples 3 --max-samples 5 --target-sample-ms 20 --latency-steps 1000 --expected-protocol-version 5 --json
```

Useful multi-thread validation command:

```powershell
.\build\Release\membench.exe --size-mib 32 --threads 2 --min-samples 2 --max-samples 3 --target-sample-ms 10 --latency-steps 1000 --expected-protocol-version 5 --json
```

See [`../../docs/memory-benchmark-design.md`](../../docs/memory-benchmark-design.md) for algorithm notes and the evolution plan.

