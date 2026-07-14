namespace HwScope.Core.Benchmark.Storage;

public static class StorageBenchmarkPlanner
{
    public const long MinimumFileSizeBytes = 64L * 1024 * 1024;
    public const long MaximumFileSizeBytes = 8L * 1024 * 1024 * 1024;
    public const long MaximumWriteBudgetBytes = 512L * 1024 * 1024 * 1024;

    public static StorageBenchmarkPlan CreatePlan(
        StorageBenchmarkTarget target,
        StorageBenchmarkOptions options,
        IReadOnlyList<string>? workloads = null,
        string? sessionId = null)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(options);
        ValidateTarget(target);
        ValidateOptions(options);

        var selectedWorkloads = (workloads ?? options.Workloads).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (selectedWorkloads.Count == 0)
        {
            throw new ArgumentException("至少需要选择一个存储跑分 workload。", nameof(workloads));
        }

        var operations = GetOperations(options.Columns);
        var plans = new List<StorageBenchmarkWorkloadPlan>();
        long plannedRead = 0;
        long maximumWrite = options.FileSizeBytes;
        var seed = 0x485753434F5045UL;

        foreach (var workloadId in selectedWorkloads)
        {
            var definition = StorageBenchmarkWorkloads.GetDefinition(workloadId);
            if (options.FileSizeBytes % definition.BlockSizeBytes != 0)
            {
                throw new ArgumentException($"文件大小必须是 {definition.DisplayName} block size 的整数倍。", nameof(options));
            }

            if (options.CacheMode == StorageBenchmarkCacheMode.Device
                && definition.BlockSizeBytes % target.RequiredAlignmentBytes != 0)
            {
                throw new NotSupportedException(
                    $"{definition.DisplayName} 的 {StorageBenchmarkFormatting.FormatBytes(definition.BlockSizeBytes)} block 不满足目标卷 {target.RequiredAlignmentBytes} B 对齐要求。");
            }

            foreach (var operation in operations)
            {
                var passes = checked(options.Runs + options.WarmupPasses);
                var operationsPerPass = options.FileSizeBytes / definition.BlockSizeBytes;
                var totalOperations = checked(operationsPerPass * passes);
                var totalBytes = checked(totalOperations * definition.BlockSizeBytes);
                var mixReadOperationsPerPass = checked(operationsPerPass * options.MixReadPercent / 100);
                var mixReadBytes = checked(mixReadOperationsPerPass * definition.BlockSizeBytes * passes);
                var readBytes = operation switch
                {
                    StorageBenchmarkOperation.Read => totalBytes,
                    StorageBenchmarkOperation.Mix => mixReadBytes,
                    _ => 0
                };
                var writeBytes = operation switch
                {
                    StorageBenchmarkOperation.Write => totalBytes,
                    StorageBenchmarkOperation.Mix => checked(totalBytes - readBytes),
                    _ => 0
                };

                plannedRead = checked(plannedRead + readBytes);
                maximumWrite = checked(maximumWrite + writeBytes);
                plans.Add(new StorageBenchmarkWorkloadPlan(
                    definition.Id,
                    definition.DisplayName,
                    operation,
                    definition.BlockSizeBytes,
                    definition.QueueDepth,
                    definition.Threads,
                    definition.RandomAccess,
                    options.FileSizeBytes,
                    options.Runs,
                    options.WarmupPasses,
                    seed++,
                    readBytes,
                    writeBytes));
            }
        }

        if (maximumWrite > options.WriteBudgetBytes)
        {
            throw new InvalidOperationException(
                $"计划最多写入 {StorageBenchmarkFormatting.FormatBytes(maximumWrite)}，超过当前 {StorageBenchmarkFormatting.FormatBytes(options.WriteBudgetBytes)} 写入预算。");
        }

        var reserve = Math.Max(2L * 1024 * 1024 * 1024, target.TotalSizeBytes / 20);
        if (options.FileSizeBytes > target.FreeSpaceBytes / 10 || target.FreeSpaceBytes - options.FileSizeBytes < reserve)
        {
            throw new InvalidOperationException(
                $"目标卷空间不足。需要 {StorageBenchmarkFormatting.FormatBytes(options.FileSizeBytes)} 测试文件，并保留至少 {StorageBenchmarkFormatting.FormatBytes(reserve)} 可用空间。");
        }

        var id = string.IsNullOrWhiteSpace(sessionId) ? Guid.NewGuid().ToString("N") : NormalizeSessionId(sessionId);
        var testFilePath = Path.Combine(target.TestDirectory, $"hwscope-storagebench-{id}.tmp");
        return new StorageBenchmarkPlan(
            id,
            target,
            options,
            plans,
            plannedRead,
            maximumWrite,
            testFilePath,
            DateTimeOffset.Now);
    }

    private static IReadOnlyList<StorageBenchmarkOperation> GetOperations(StorageBenchmarkColumnMode columns)
    {
        return columns switch
        {
            StorageBenchmarkColumnMode.ReadOnly => [StorageBenchmarkOperation.Read],
            StorageBenchmarkColumnMode.ReadWrite => [StorageBenchmarkOperation.Read, StorageBenchmarkOperation.Write],
            StorageBenchmarkColumnMode.ReadWriteMix => [StorageBenchmarkOperation.Read, StorageBenchmarkOperation.Write, StorageBenchmarkOperation.Mix],
            _ => throw new ArgumentOutOfRangeException(nameof(columns))
        };
    }

    private static void ValidateTarget(StorageBenchmarkTarget target)
    {
        if (string.IsNullOrWhiteSpace(target.RootPath) || !Path.IsPathFullyQualified(target.RootPath))
        {
            throw new ArgumentException("目标卷 root path 必须是绝对路径。", nameof(target));
        }

        if (string.IsNullOrWhiteSpace(target.TestDirectory) || !Path.IsPathFullyQualified(target.TestDirectory))
        {
            throw new ArgumentException("测试目录必须是绝对路径。", nameof(target));
        }

        if (!string.Equals(
                Path.GetPathRoot(Path.GetFullPath(target.RootPath)),
                Path.GetPathRoot(Path.GetFullPath(target.TestDirectory)),
                StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("测试目录必须位于目标卷。", nameof(target));
        }

        if (target.IsVirtual || target.IsMultiExtent)
        {
            throw new NotSupportedException("首版存储跑分不支持虚拟卷或跨多个 physical extent 的卷。");
        }

        if (target.RequiredAlignmentBytes <= 0 || (target.RequiredAlignmentBytes & (target.RequiredAlignmentBytes - 1)) != 0)
        {
            throw new ArgumentException("目标卷 alignment 必须是正的 2 次幂。", nameof(target));
        }
    }

    private static void ValidateOptions(StorageBenchmarkOptions options)
    {
        if (options.Runs is < 1 or > 9)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Runs 必须在 1 到 9 之间。");
        }

        if (options.WarmupPasses is < 0 or > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Warmup passes 必须是 0 或 1。");
        }

        if (options.FileSizeBytes is < MinimumFileSizeBytes or > MaximumFileSizeBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "测试文件大小必须在 64 MiB 到 8 GiB 之间。");
        }

        if (options.WriteBudgetBytes <= 0 || options.WriteBudgetBytes > MaximumWriteBudgetBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "写入预算必须大于 0 且不超过 512 GiB。");
        }

        if (options.MixReadPercent is < 1 or > 99)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Mix read ratio 必须在 1% 到 99% 之间。");
        }

        if (options.Timeout <= TimeSpan.Zero || options.Timeout > TimeSpan.FromHours(1))
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Timeout 必须大于 0 且不超过 1 小时。");
        }
    }

    private static string NormalizeSessionId(string sessionId)
    {
        return Guid.TryParseExact(sessionId, "N", out var parsed)
            ? parsed.ToString("N")
            : throw new ArgumentException("Session ID 必须是 32 位 GUID。", nameof(sessionId));
    }
}
