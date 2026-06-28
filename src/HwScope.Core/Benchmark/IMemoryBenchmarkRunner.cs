namespace HwScope.Core.Benchmark;

public interface IMemoryBenchmarkRunner
{
    Task<MemoryBenchmarkResult> RunAsync(MemoryBenchmarkOptions options, CancellationToken cancellationToken = default);

    Task<MemoryBenchmarkResult> RunAsync(
        MemoryBenchmarkOptions options,
        IProgress<MemoryBenchmarkProgress>? progress,
        CancellationToken cancellationToken = default);
}

