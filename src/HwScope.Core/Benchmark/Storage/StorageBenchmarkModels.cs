using System.Text.Json.Serialization;

namespace HwScope.Core.Benchmark.Storage;

public enum StorageBenchmarkCacheMode
{
    Device,
    Buffered
}

public enum StorageBenchmarkColumnMode
{
    ReadOnly,
    ReadWrite,
    ReadWriteMix
}

public enum StorageBenchmarkOperation
{
    Read,
    Write,
    Mix
}

public static class StorageBenchmarkWorkloads
{
    public const string Sequential1MiBQ8T1 = "seq1m-q8t1";
    public const string Sequential1MiBQ1T1 = "seq1m-q1t1";
    public const string Random4KiBQ32T1 = "rnd4k-q32t1";
    public const string Random4KiBQ1T1 = "rnd4k-q1t1";

    public static IReadOnlyList<string> DisplayOrder { get; } =
    [
        Sequential1MiBQ8T1,
        Sequential1MiBQ1T1,
        Random4KiBQ32T1,
        Random4KiBQ1T1
    ];

    public static StorageBenchmarkWorkloadDefinition GetDefinition(string id)
    {
        return id.ToLowerInvariant() switch
        {
            Sequential1MiBQ8T1 => new(id, "SEQ1M Q8T1", 1024 * 1024, 8, 1, false),
            Sequential1MiBQ1T1 => new(id, "SEQ1M Q1T1", 1024 * 1024, 1, 1, false),
            Random4KiBQ32T1 => new(id, "RND4K Q32T1", 4 * 1024, 32, 1, true),
            Random4KiBQ1T1 => new(id, "RND4K Q1T1", 4 * 1024, 1, 1, true),
            _ => throw new ArgumentOutOfRangeException(nameof(id), $"未知的存储跑分 workload：{id}。")
        };
    }
}

public sealed record StorageBenchmarkWorkloadDefinition(
    string Id,
    string DisplayName,
    int BlockSizeBytes,
    int QueueDepth,
    int Threads,
    bool RandomAccess);

public sealed record StorageBenchmarkOptions
{
    public int Runs { get; init; } = 5;
    public long FileSizeBytes { get; init; } = 1024L * 1024 * 1024;
    public int WarmupPasses { get; init; }
    public StorageBenchmarkCacheMode CacheMode { get; init; } = StorageBenchmarkCacheMode.Device;
    public StorageBenchmarkColumnMode Columns { get; init; } = StorageBenchmarkColumnMode.ReadWrite;
    public int MixReadPercent { get; init; } = 70;
    public long WriteBudgetBytes { get; init; } = 64L * 1024 * 1024 * 1024;
    public IReadOnlyList<string> Workloads { get; init; } = StorageBenchmarkWorkloads.DisplayOrder;
    public TimeSpan Timeout { get; init; } = TimeSpan.FromMinutes(10);
}

public sealed record StorageBenchmarkTarget(
    string Id,
    string RootPath,
    string TestDirectory,
    string DriveLetter,
    string Label,
    string FileSystem,
    long TotalSizeBytes,
    long FreeSpaceBytes,
    string DeviceStableId,
    int? PhysicalDriveNumber,
    string Model,
    string Bus,
    string MediaType,
    int RequiredAlignmentBytes,
    bool IsSystem,
    bool IsRemovable,
    bool IsVirtual = false,
    bool IsMultiExtent = false,
    string VolumeGuidPath = "",
    uint? VolumeSerialNumber = null)
{
    public string DisplayName
    {
        get
        {
            var drive = string.IsNullOrWhiteSpace(DriveLetter) ? RootPath : DriveLetter;
            var model = string.IsNullOrWhiteSpace(Model) ? "本地卷" : Model;
            var role = IsSystem ? " · 系统盘" : IsRemovable ? " · 可移动" : string.Empty;
            return $"{drive}  {model} · {FileSystem} · 可用 {StorageBenchmarkFormatting.FormatBytes(FreeSpaceBytes)}{role}";
        }
    }
}

public sealed record StorageBenchmarkWorkloadPlan(
    string Id,
    string DisplayName,
    StorageBenchmarkOperation Operation,
    int BlockSizeBytes,
    int QueueDepth,
    int Threads,
    bool RandomAccess,
    long BytesPerSample,
    int Samples,
    int WarmupPasses,
    ulong Seed,
    long PlannedReadBytes,
    long MaximumWriteBytes);

public sealed record StorageBenchmarkPlan(
    string SessionId,
    StorageBenchmarkTarget Target,
    StorageBenchmarkOptions Options,
    IReadOnlyList<StorageBenchmarkWorkloadPlan> Workloads,
    long PlannedReadBytes,
    long MaximumWriteBytes,
    string TestFilePath,
    DateTimeOffset CreatedAt);

public sealed record StorageBenchmarkProgress(
    string Type,
    string SessionId,
    long Sequence,
    string Phase,
    string? WorkloadId = null,
    StorageBenchmarkOperation? Operation = null,
    int? SampleIndex = null,
    int? SampleCount = null,
    long? CompletedBytes = null,
    long? PlannedBytes = null,
    double? ThroughputMBs = null,
    double? Iops = null,
    double? P95Microseconds = null,
    string? Message = null)
{
    public double? Fraction => CompletedBytes is { } completed && PlannedBytes is > 0
        ? Math.Clamp((double)completed / PlannedBytes.Value, 0, 1)
        : null;
}

public sealed record StorageBenchmarkAggregate(
    double Median,
    double Min,
    double Max,
    double Mean,
    double StdDev,
    double Cv);

public sealed record StorageBenchmarkLatency(
    double MeanMicroseconds,
    double P50Microseconds,
    double P95Microseconds,
    double P99Microseconds,
    double MaximumMicroseconds);

public sealed record StorageBenchmarkSampleResult(
    int Index,
    [property: JsonPropertyName("throughput_mb_s")] double ThroughputMBs,
    double Iops,
    StorageBenchmarkLatency Latency,
    long BytesRead,
    long BytesWritten,
    double ElapsedMs);

public sealed record StorageBenchmarkMetricResult(
    string Unit,
    IReadOnlyList<StorageBenchmarkSampleResult> Samples,
    StorageBenchmarkAggregate Throughput,
    StorageBenchmarkAggregate Iops,
    StorageBenchmarkLatency Latency,
    long LogicalBytesRead,
    long LogicalBytesWritten);

public sealed record StorageBenchmarkRowResult(
    string Id,
    string DisplayName,
    int BlockSizeBytes,
    int QueueDepth,
    int Threads,
    StorageBenchmarkMetricResult? Read,
    StorageBenchmarkMetricResult? Write,
    StorageBenchmarkMetricResult? Mix);

public sealed record StorageBenchmarkCleanupResult(
    bool Attempted,
    bool Deleted,
    string Status,
    int? NativeErrorCode = null);

public sealed record StorageBenchmarkQuality(
    bool HighVariance,
    bool ShortSample,
    IReadOnlyList<string> Flags);

public sealed record StorageBenchmarkResult(
    string SessionId,
    string WorkerVersion,
    int ProtocolVersion,
    double ElapsedMs,
    long FileSizeBytes,
    long LogicalBytesRead,
    long LogicalBytesWritten,
    string CacheMode,
    IReadOnlyDictionary<string, StorageBenchmarkRowResult> Rows,
    StorageBenchmarkCleanupResult Cleanup,
    DateTimeOffset CompletedAt,
    StorageBenchmarkPlan? Plan = null,
    StorageBenchmarkQuality? Quality = null,
    string? ExecutablePath = null,
    long? FreeSpaceBeforeBytes = null,
    long? FreeSpaceAfterBytes = null,
    double? TemperatureBeforeCelsius = null,
    double? TemperatureAfterCelsius = null);

internal sealed record StorageWorkerResult(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("session_id")] string SessionId,
    [property: JsonPropertyName("worker_version")] string WorkerVersion,
    [property: JsonPropertyName("protocol_version")] int ProtocolVersion,
    [property: JsonPropertyName("elapsed_ms")] double ElapsedMs,
    [property: JsonPropertyName("file_size_bytes")] long FileSizeBytes,
    [property: JsonPropertyName("logical_bytes_read")] long LogicalBytesRead,
    [property: JsonPropertyName("logical_bytes_written")] long LogicalBytesWritten,
    [property: JsonPropertyName("cache_mode")] string CacheMode,
    [property: JsonPropertyName("rows")] Dictionary<string, StorageBenchmarkRowResult> Rows,
    [property: JsonPropertyName("cleanup")] StorageBenchmarkCleanupResult Cleanup);

public static class StorageBenchmarkFormatting
{
    public static string FormatBytes(long bytes)
    {
        if (bytes < 0)
        {
            return "未知";
        }

        string[] units = ["B", "KiB", "MiB", "GiB", "TiB"];
        var value = (double)bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return $"{value:0.##} {units[unit]}";
    }
}
