namespace HwScope.Core.Benchmark;

public sealed record MemoryBenchmarkResult(
    int SizeMiB,
    double ReadMiBS,
    double WriteMiBS,
    double CopyMiBS,
    double LatencyNs,
    DateTimeOffset CompletedAt,
    MemoryBenchmarkOptionsSnapshot? Options = null,
    string? WorkerVersion = null,
    int? ProtocolVersion = null,
    string? ExecutablePath = null,
    double? ElapsedMs = null,
    MemoryBenchmarkMetricSet? Metrics = null,
    MemoryBenchmarkQuality? Quality = null,
    MemoryBenchmarkEnvironment? Environment = null)
{
    public double ReadMBS => ReadMiBS * 1024.0 * 1024.0 / 1_000_000.0;

    public double WriteMBS => WriteMiBS * 1024.0 * 1024.0 / 1_000_000.0;

    public double CopyMBS => CopyMiBS * 1024.0 * 1024.0 / 1_000_000.0;
}

public sealed record MemoryBenchmarkOptionsSnapshot(
    int SizeMiB,
    int Iterations,
    long LatencySteps,
    int Threads,
    string WorkingSetKind);

public sealed record MemoryBenchmarkMetricSet(
    MemoryBenchmarkMetricResult Read,
    MemoryBenchmarkMetricResult Write,
    MemoryBenchmarkMetricResult Copy,
    MemoryBenchmarkMetricResult Latency);

public sealed record MemoryBenchmarkMetricResult(
    string Unit,
    IReadOnlyList<double> Samples,
    MemoryBenchmarkAggregate Aggregate);

public sealed record MemoryBenchmarkAggregate(
    double Median,
    double Min,
    double Max,
    double Mean,
    double StdDev,
    double Cv);

public sealed record MemoryBenchmarkQuality(
    bool HighVariance,
    bool ThermalSuspected,
    bool ShortDuration,
    bool BackgroundNoiseSuspected,
    IReadOnlyList<string> Flags);

public sealed record MemoryBenchmarkEnvironment(
    string CpuName,
    int PhysicalCoreCount,
    int LogicalProcessorCount,
    int NumaNodeCount,
    string PowerPlan,
    bool? OnAcPower,
    string ProcessPriority);

