namespace HwScope.Core.Benchmark;

public sealed record MemoryBenchmarkResult(
    int SizeMiB,
    double ReadMiBS,
    double WriteMiBS,
    double CopyMiBS,
    double LatencyNs,
    DateTimeOffset CompletedAt)
{
    public double ReadMBS => ReadMiBS * 1024.0 * 1024.0 / 1_000_000.0;

    public double WriteMBS => WriteMiBS * 1024.0 * 1024.0 / 1_000_000.0;

    public double CopyMBS => CopyMiBS * 1024.0 * 1024.0 / 1_000_000.0;
}

