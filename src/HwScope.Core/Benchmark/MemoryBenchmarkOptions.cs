namespace HwScope.Core.Benchmark;

public sealed record MemoryBenchmarkOptions(
    int SizeMiB = 512,
    int Iterations = 7,
    long LatencySteps = 20_000_000,
    int Threads = 1,
    string WorkingSetKind = "memory",
    TimeSpan? Timeout = null);

