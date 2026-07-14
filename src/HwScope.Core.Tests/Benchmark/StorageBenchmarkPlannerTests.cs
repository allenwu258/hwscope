using HwScope.Core.Benchmark.Storage;

namespace HwScope.Core.Tests.Benchmark;

public sealed class StorageBenchmarkPlannerTests
{
    private const long GiB = 1024L * 1024 * 1024;

    [Fact]
    public void StandardPlanCalculatesTwentyOneGiBMaximumWrites()
    {
        var plan = StorageBenchmarkPlanner.CreatePlan(CreateTarget(), new StorageBenchmarkOptions(), sessionId: Guid.Empty.ToString("N"));

        Assert.Equal(21 * GiB, plan.MaximumWriteBytes);
        Assert.Equal(20 * GiB, plan.PlannedReadBytes);
        Assert.Equal(8, plan.Workloads.Count);
        Assert.EndsWith("hwscope-storagebench-00000000000000000000000000000000.tmp", plan.TestFilePath);
    }

    [Fact]
    public void PlanOrdersEveryReadThenEveryWriteThenEveryMix()
    {
        var options = new StorageBenchmarkOptions
        {
            Runs = 1,
            FileSizeBytes = 64L * 1024 * 1024,
            Columns = StorageBenchmarkColumnMode.ReadWriteMix
        };

        var plan = StorageBenchmarkPlanner.CreatePlan(CreateTarget(), options);
        var expected = new[]
        {
            StorageBenchmarkOperation.Read,
            StorageBenchmarkOperation.Write,
            StorageBenchmarkOperation.Mix
        }.SelectMany(operation => StorageBenchmarkWorkloads.DisplayOrder.Select(id => (id, operation)));

        Assert.Equal(expected, plan.Workloads.Select(item => (item.Id, item.Operation)));
    }

    [Fact]
    public void ReadOnlyStillBudgetsFileInitialization()
    {
        var options = new StorageBenchmarkOptions
        {
            Columns = StorageBenchmarkColumnMode.ReadOnly,
            Runs = 1,
            FileSizeBytes = 256L * 1024 * 1024
        };

        var plan = StorageBenchmarkPlanner.CreatePlan(CreateTarget(), options);

        Assert.Equal(options.FileSizeBytes, plan.MaximumWriteBytes);
        Assert.Equal(options.FileSizeBytes * 4, plan.PlannedReadBytes);
    }

    [Fact]
    public void MixWritesAreIncludedInBudget()
    {
        var options = new StorageBenchmarkOptions
        {
            Columns = StorageBenchmarkColumnMode.ReadWriteMix,
            Runs = 1,
            FileSizeBytes = GiB,
            MixReadPercent = 70
        };

        var plan = StorageBenchmarkPlanner.CreatePlan(CreateTarget(), options);

        var mixWrites = StorageBenchmarkWorkloads.DisplayOrder.Sum(id =>
        {
            var definition = StorageBenchmarkWorkloads.GetDefinition(id);
            var operations = GiB / definition.BlockSizeBytes;
            var readOperations = operations * 70 / 100;
            return (operations - readOperations) * definition.BlockSizeBytes;
        });
        Assert.Equal(GiB + 4 * GiB + mixWrites, plan.MaximumWriteBytes);
    }

    [Fact]
    public void MixBudgetUsesTheWorkersPerPassRounding()
    {
        const long mib = 1024L * 1024;
        var options = new StorageBenchmarkOptions
        {
            Columns = StorageBenchmarkColumnMode.ReadWriteMix,
            Runs = 2,
            WarmupPasses = 1,
            FileSizeBytes = 64 * mib,
            MixReadPercent = 70,
            Workloads = [StorageBenchmarkWorkloads.Sequential1MiBQ1T1]
        };

        var plan = StorageBenchmarkPlanner.CreatePlan(CreateTarget(), options);

        Assert.Equal(324 * mib, plan.PlannedReadBytes);
        Assert.Equal(316 * mib, plan.MaximumWriteBytes);
    }

    [Fact]
    public void PlanRejectsWriteBudgetOverflow()
    {
        var options = new StorageBenchmarkOptions
        {
            Runs = 5,
            FileSizeBytes = GiB,
            WriteBudgetBytes = 20 * GiB
        };

        var error = Assert.Throws<InvalidOperationException>(() => StorageBenchmarkPlanner.CreatePlan(CreateTarget(), options));

        Assert.Contains("写入预算", error.Message);
    }

    [Fact]
    public void PlanRejectsRandomFourKiBWhenAlignmentIsLarger()
    {
        var target = CreateTarget() with { RequiredAlignmentBytes = 8192 };

        var error = Assert.Throws<NotSupportedException>(() => StorageBenchmarkPlanner.CreatePlan(target, new StorageBenchmarkOptions()));

        Assert.Contains("RND4K", error.Message);
    }

    [Fact]
    public void PlanRejectsTestDirectoryOnAnotherVolume()
    {
        var target = CreateTarget() with { TestDirectory = @"D:\HwScope-Benchmark" };

        Assert.Throws<ArgumentException>(() => StorageBenchmarkPlanner.CreatePlan(target, new StorageBenchmarkOptions()));
    }

    private static StorageBenchmarkTarget CreateTarget()
    {
        return new StorageBenchmarkTarget(
            "c", @"C:\", @"C:\Temp\HwScope", "C:", "System", "NTFS",
            100 * GiB, 80 * GiB, "disk", 0, "Test SSD", "NVMe", "SSD", 4096, true, false);
    }
}
