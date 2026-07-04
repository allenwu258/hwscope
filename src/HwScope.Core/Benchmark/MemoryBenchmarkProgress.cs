namespace HwScope.Core.Benchmark;

public enum MemoryBenchmarkMetric
{
    Read,
    Write,
    Copy,
    Latency
}

public sealed record MemoryBenchmarkProgress(
    string Row,
    MemoryBenchmarkMetric Metric,
    double Value,
    string Unit,
    DateTimeOffset ReceivedAt);
