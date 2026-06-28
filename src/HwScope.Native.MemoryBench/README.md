# HwScope.Native.MemoryBench

Native C++ worker for HwScope memory benchmarking.

This worker currently measures main-memory read, write, copy throughput and random-access latency. `HwScope.Core.Benchmark.MemoryBenchmarkProcessRunner` can invoke it with `--csv` for final-result parsing or `--progress-json` for live GUI updates.

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

HwScope invokes the worker with:

```text
membench --size-mib 512 --iterations 7 --latency-steps 20000000 --csv
```

CSV output format:

```text
size_mib,read_mib_s,write_mib_s,copy_mib_s,latency_ns
```

The GUI uses newline-delimited progress JSON:

```text
membench --size-mib 512 --iterations 7 --latency-steps 20000000 --progress-json
```

Each completed metric is flushed as one event:

```json
{"type":"metric","metric":"read","value":36000.00,"unit":"mib_s"}
```

See [`../../docs/memory-benchmark-design.md`](../../docs/memory-benchmark-design.md) for algorithm notes and the evolution plan.

