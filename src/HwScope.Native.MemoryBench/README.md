# HwScope.Native.MemoryBench

Native C++ worker for HwScope memory benchmarking.

This worker currently measures main-memory read, write, copy throughput and random-access latency. It is invoked by `HwScope.Core.Benchmark.MemoryBenchmarkProcessRunner` with `--csv`.

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

