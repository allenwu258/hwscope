namespace HwScope.Core.Benchmark.Storage;

public interface IStorageBenchmarkRunner
{
    Task<StorageBenchmarkResult> RunAsync(
        StorageBenchmarkPlan plan,
        IProgress<StorageBenchmarkProgress>? progress = null,
        CancellationToken cancellationToken = default);
}
