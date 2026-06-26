# HwScope.Native.MemoryBench

Native C++ worker for HwScope memory benchmarking.

This worker currently measures main-memory read, write, copy throughput and random-access latency. It is invoked by `HwScope.Core.Benchmark.MemoryBenchmarkProcessRunner` with `--csv`.

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

During development, HwScope can also use the external prototype executable at:

```text
C:\Users\Trivedi\memory-bench-cpp\build\Release\membench.exe
```

## Invocation

HwScope invokes the worker with:

```text
membench --size-mib 512 --iterations 7 --latency-steps 20000000 --csv
```

CSV output format:

```text
size_mib,read_mib_s,write_mib_s,copy_mib_s,latency_ns
```

See [`../../docs/memory-benchmark-design.md`](../../docs/memory-benchmark-design.md) for algorithm notes and the evolution plan.

