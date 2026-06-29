namespace HwScope.Core.Benchmark;

public sealed record MemoryBenchmarkOptions(
    int SizeMiB = 512,
    int Iterations = 7,
    long LatencySteps = 20_000_000,
    int WarmupRuns = 1,
    int MaxSamples = 11,
    double TargetSampleMs = 120.0,
    double MaxCv = 0.03,
    int Threads = 1,
    bool UsePreferredCore = true,
    string WorkingSetKind = "memory",
    TimeSpan? Timeout = null);

