using HwScope.Core.Benchmark.Storage;
using System.Text.Json;

namespace HwScope.Core.Tests.Benchmark;

public sealed class StorageBenchmarkProcessRunnerTests
{
    [Fact]
    public void BuildArgumentsDoesNotExposePhysicalDrive()
    {
        var plan = StorageBenchmarkPlanner.CreatePlan(CreateTarget(), new StorageBenchmarkOptions
        {
            Runs = 1,
            FileSizeBytes = 256L * 1024 * 1024
        });

        var arguments = StorageBenchmarkProcessRunner.BuildArguments(plan);

        Assert.Contains("--path", arguments);
        Assert.DoesNotContain(arguments, value => value.Contains("PhysicalDrive", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("--write-budget-bytes", arguments);
        Assert.Contains("--expected-protocol-version", arguments);
        var budgetIndex = arguments.ToList().IndexOf("--write-budget-bytes");
        Assert.Equal(plan.MaximumWriteBytes.ToString(), arguments[budgetIndex + 1]);
    }

    [Fact]
    public void ParsesSampleProgress()
    {
        const string line = """
            {"type":"sample_completed","protocol_version":1,"session_id":"abc","sequence":9,"phase":"running","workload_id":"rnd4k-q1t1","operation":"read","sample_index":2,"sample_count":5,"throughput_mb_s":88.5,"iops":22000,"p95_microseconds":51.2}
            """;

        var progress = StorageBenchmarkProcessRunner.ParseProgressLine(line);

        Assert.Equal("rnd4k-q1t1", progress.WorkloadId);
        Assert.Equal(StorageBenchmarkOperation.Read, progress.Operation);
        Assert.Equal(2, progress.SampleIndex);
        Assert.Equal(88.5, progress.ThroughputMBs);
        Assert.Equal(51.2, progress.P95Microseconds);
    }

    [Fact]
    public void ProgressFractionIsClamped()
    {
        var progress = new StorageBenchmarkProgress("progress", "session", 1, "running", CompletedBytes: 200, PlannedBytes: 100);

        Assert.Equal(1, progress.Fraction);
    }

    [Fact]
    public void SampleThroughputUsesWorkerJsonName()
    {
        const string json = """
            {"index":1,"throughput_mb_s":4123.5,"iops":3932,"latency":{"mean_microseconds":250,"p50_microseconds":240,"p95_microseconds":280,"p99_microseconds":350,"maximum_microseconds":400},"bytes_read":67108864,"bytes_written":0,"elapsed_ms":16.2}
            """;

        var sample = JsonSerializer.Deserialize<StorageBenchmarkSampleResult>(json, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
        });

        Assert.NotNull(sample);
        Assert.Equal(4123.5, sample.ThroughputMBs);
    }

    private static StorageBenchmarkTarget CreateTarget()
    {
        const long gib = 1024L * 1024 * 1024;
        return new StorageBenchmarkTarget(
            "c", @"C:\", @"C:\Temp\HwScope", "C:", "System", "NTFS",
            100 * gib, 80 * gib, "disk", 0, "Test SSD", "NVMe", "SSD", 4096, true, false);
    }
}
