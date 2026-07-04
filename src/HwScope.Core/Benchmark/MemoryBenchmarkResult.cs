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
    MemoryBenchmarkTimer? Timer = null,
    MemoryBenchmarkPlacement? Placement = null,
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
    int WarmupRuns,
    int MinSamples,
    int MaxSamples,
    double TargetSampleMs,
    double MaxCv,
    int Threads,
    string ThreadMode,
    string NumaMode,
    string Kernel,
    string StorePolicy,
    bool UsePreferredCore,
    string WorkingSetKind);

public sealed record MemoryBenchmarkMetricSet(
    MemoryBenchmarkMetricResult Read,
    MemoryBenchmarkMetricResult Write,
    MemoryBenchmarkMetricResult Copy,
    MemoryBenchmarkMetricResult Latency);

public record MemoryBenchmarkMetricResult(
    string Unit,
    IReadOnlyList<double> Samples,
    IReadOnlyList<long> InnerIterations,
    bool Converged,
    MemoryBenchmarkAggregate Aggregate);

public sealed record MemoryBenchmarkCopyMetricResult(
    string Unit,
    IReadOnlyList<double> Samples,
    IReadOnlyList<long> InnerIterations,
    bool Converged,
    MemoryBenchmarkAggregate Aggregate,
    IReadOnlyList<double> TrafficSamples,
    MemoryBenchmarkAggregate? TrafficAggregate)
    : MemoryBenchmarkMetricResult(Unit, Samples, InnerIterations, Converged, Aggregate);

public sealed record MemoryBenchmarkTimer(
    string Name,
    long FrequencyHz);

public sealed record MemoryBenchmarkPlacement(
    string Mode,
    string Source,
    string Confidence,
    string Reason,
    bool? AffinityApplied,
    MemoryBenchmarkProcessorPlacement? Requested,
    MemoryBenchmarkProcessorPlacement? Actual,
    IReadOnlyList<MemoryBenchmarkProcessorPlacement> RequestedWorkers,
    IReadOnlyList<MemoryBenchmarkProcessorPlacement> ActualWorkers,
    IReadOnlyList<MemoryBenchmarkProcessorPlacement> Candidates);

public sealed record MemoryBenchmarkProcessorPlacement(
    ushort Group,
    int ProcessorNumber,
    int? CoreIndex,
    int? PackageIndex,
    uint? NumaNodeNumber,
    int? SmtIndex,
    int? EfficiencyClass,
    bool? HasSmt);

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

