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

    [Fact]
    public void AcceptsResultThatExactlyMatchesPlan()
    {
        var plan = CreatePlan();

        StorageBenchmarkProcessRunner.ValidateWorkerResult(CreateValidResult(plan), plan);
    }

    [Fact]
    public void RejectsMismatchedCacheModeAndRowDefinition()
    {
        var plan = CreatePlan();
        var result = CreateValidResult(plan) with { CacheMode = "buffered" };

        Assert.Throws<FormatException>(() => StorageBenchmarkProcessRunner.ValidateWorkerResult(result, plan));

        result = CreateValidResult(plan);
        var row = Assert.Single(result.Rows).Value with { QueueDepth = 999 };
        result.Rows[row.Id] = row;
        Assert.Throws<FormatException>(() => StorageBenchmarkProcessRunner.ValidateWorkerResult(result, plan));
    }

    [Fact]
    public void RejectsMissingOrUnexpectedOperationMetric()
    {
        var plan = CreatePlan();
        var result = CreateValidResult(plan);
        var row = Assert.Single(result.Rows).Value with { Mix = null };
        result.Rows[row.Id] = row;

        Assert.Throws<FormatException>(() => StorageBenchmarkProcessRunner.ValidateWorkerResult(result, plan));

        var readOnlyPlan = CreatePlan(StorageBenchmarkColumnMode.ReadOnly);
        result = CreateValidResult(readOnlyPlan);
        row = Assert.Single(result.Rows).Value;
        result.Rows[row.Id] = row with { Write = CreateMetric(plan.Workloads.Single(item => item.Operation == StorageBenchmarkOperation.Write), plan.Options.MixReadPercent) };
        Assert.Throws<FormatException>(() => StorageBenchmarkProcessRunner.ValidateWorkerResult(result, readOnlyPlan));
    }

    [Fact]
    public void RejectsInvalidSamplesAndMetricBytes()
    {
        var plan = CreatePlan();
        var result = CreateValidResult(plan);
        var row = Assert.Single(result.Rows).Value;
        var read = row.Read! with
        {
            Samples = row.Read!.Samples.Select((sample, index) => index == 0
                ? sample with { Index = 2, ThroughputMBs = double.NaN }
                : sample).ToList()
        };
        result.Rows[row.Id] = row with { Read = read };

        Assert.Throws<FormatException>(() => StorageBenchmarkProcessRunner.ValidateWorkerResult(result, plan));

        result = CreateValidResult(plan);
        row = Assert.Single(result.Rows).Value;
        result.Rows[row.Id] = row with { Write = row.Write! with { LogicalBytesWritten = row.Write.LogicalBytesWritten - 1 } };
        Assert.Throws<FormatException>(() => StorageBenchmarkProcessRunner.ValidateWorkerResult(result, plan));
    }

    [Fact]
    public void RejectsMismatchedTopLevelBytesAndCleanupState()
    {
        var plan = CreatePlan();
        var result = CreateValidResult(plan) with { LogicalBytesWritten = plan.MaximumWriteBytes - 1 };

        Assert.Throws<FormatException>(() => StorageBenchmarkProcessRunner.ValidateWorkerResult(result, plan));

        result = CreateValidResult(plan) with { Cleanup = new StorageBenchmarkCleanupResult(true, false, "deleted") };
        Assert.Throws<FormatException>(() => StorageBenchmarkProcessRunner.ValidateWorkerResult(result, plan));
    }

    private static StorageBenchmarkPlan CreatePlan(StorageBenchmarkColumnMode columns = StorageBenchmarkColumnMode.ReadWriteMix)
    {
        var options = new StorageBenchmarkOptions
        {
            Runs = 2,
            WarmupPasses = 1,
            FileSizeBytes = StorageBenchmarkPlanner.MinimumFileSizeBytes,
            Columns = columns,
            Workloads = [StorageBenchmarkWorkloads.Random4KiBQ1T1]
        };
        return StorageBenchmarkPlanner.CreatePlan(CreateTarget(), options, sessionId: Guid.Empty.ToString("N"));
    }

    private static StorageWorkerResult CreateValidResult(StorageBenchmarkPlan plan)
    {
        var workload = plan.Workloads[0];
        var row = new StorageBenchmarkRowResult(
            workload.Id,
            workload.DisplayName,
            workload.BlockSizeBytes,
            workload.QueueDepth,
            workload.Threads,
            CreateMetric(plan.Workloads.SingleOrDefault(item => item.Operation == StorageBenchmarkOperation.Read), plan.Options.MixReadPercent),
            CreateMetric(plan.Workloads.SingleOrDefault(item => item.Operation == StorageBenchmarkOperation.Write), plan.Options.MixReadPercent),
            CreateMetric(plan.Workloads.SingleOrDefault(item => item.Operation == StorageBenchmarkOperation.Mix), plan.Options.MixReadPercent));
        return new StorageWorkerResult(
            "result",
            plan.SessionId,
            "test-worker",
            StorageBenchmarkProcessRunner.ExpectedProtocolVersion,
            100,
            plan.Options.FileSizeBytes,
            plan.PlannedReadBytes,
            plan.MaximumWriteBytes,
            plan.Options.CacheMode == StorageBenchmarkCacheMode.Device ? "device" : "buffered",
            new Dictionary<string, StorageBenchmarkRowResult>(StringComparer.OrdinalIgnoreCase) { [row.Id] = row },
            new StorageBenchmarkCleanupResult(true, true, "deleted"));
    }

    private static StorageBenchmarkMetricResult? CreateMetric(StorageBenchmarkWorkloadPlan? workload, int mixReadPercent)
    {
        if (workload is null)
        {
            return null;
        }

        var operations = workload.BytesPerSample / workload.BlockSizeBytes;
        var readOperations = workload.Operation == StorageBenchmarkOperation.Mix
            ? operations * mixReadPercent / 100
            : workload.Operation == StorageBenchmarkOperation.Read ? operations : 0;
        var sampleReadBytes = readOperations * workload.BlockSizeBytes;
        var sampleWriteBytes = workload.BytesPerSample - sampleReadBytes;
        var samples = Enumerable.Range(1, workload.Samples)
            .Select(index => new StorageBenchmarkSampleResult(index, 100, 1000, ValidLatency, sampleReadBytes, sampleWriteBytes, 10))
            .ToList();
        return new StorageBenchmarkMetricResult(
            "mb_s",
            samples,
            ValidAggregate,
            ValidAggregate,
            ValidLatency,
            sampleReadBytes * workload.Samples,
            sampleWriteBytes * workload.Samples);
    }

    private static StorageBenchmarkAggregate ValidAggregate { get; } = new(100, 90, 110, 100, 5, 0.05);
    private static StorageBenchmarkLatency ValidLatency { get; } = new(10, 9, 12, 14, 16);

    private static StorageBenchmarkTarget CreateTarget()
    {
        const long gib = 1024L * 1024 * 1024;
        return new StorageBenchmarkTarget(
            "c", @"C:\", @"C:\Temp\HwScope", "C:", "System", "NTFS",
            100 * gib, 80 * gib, "disk", 0, "Test SSD", "NVMe", "SSD", 4096, true, false);
    }
}
