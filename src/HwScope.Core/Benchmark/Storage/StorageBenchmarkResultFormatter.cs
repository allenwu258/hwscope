using System.Globalization;
using System.Text;

namespace HwScope.Core.Benchmark.Storage;

public static class StorageBenchmarkResultFormatter
{
    public static string Format(StorageBenchmarkResult result)
    {
        var builder = new StringBuilder();
        builder.AppendLine("HwScope Storage Benchmark");
        builder.AppendLine("=========================");
        builder.AppendLine($"Completed       : {result.CompletedAt:yyyy-MM-dd HH:mm:ss zzz}");
        builder.AppendLine($"Session         : {result.SessionId}");
        builder.AppendLine($"Worker          : {result.WorkerVersion} (protocol {result.ProtocolVersion})");
        builder.AppendLine($"Cache mode      : {result.CacheMode}");
        builder.AppendLine($"File size       : {StorageBenchmarkFormatting.FormatBytes(result.FileSizeBytes)}");
        builder.AppendLine($"Bytes read      : {StorageBenchmarkFormatting.FormatBytes(result.LogicalBytesRead)}");
        builder.AppendLine($"Bytes written   : {StorageBenchmarkFormatting.FormatBytes(result.LogicalBytesWritten)}");
        builder.AppendLine($"Elapsed         : {result.ElapsedMs / 1000.0:F2} s");
        builder.AppendLine($"Cleanup         : {result.Cleanup.Status} (deleted: {result.Cleanup.Deleted})");
        builder.AppendLine($"Temperature     : {FormatTemperature(result.TemperatureBeforeCelsius)} -> {FormatTemperature(result.TemperatureAfterCelsius)}");

        if (result.Plan is { } plan)
        {
            builder.AppendLine();
            builder.AppendLine("Target");
            builder.AppendLine($"Volume          : {plan.Target.DriveLetter} {plan.Target.Label}".TrimEnd());
            builder.AppendLine($"File system     : {plan.Target.FileSystem}");
            builder.AppendLine($"Device          : {plan.Target.Model}");
            builder.AppendLine($"Physical disk   : {plan.Target.PhysicalDriveNumber?.ToString(CultureInfo.InvariantCulture) ?? "unknown"}");
            builder.AppendLine($"Planned writes  : {StorageBenchmarkFormatting.FormatBytes(plan.MaximumWriteBytes)}");
        }

        builder.AppendLine();
        builder.AppendLine($"{"Workload",-16} {"Operation",-10} {"MB/s",12} {"IOPS",12} {"p95 us",12} {"CV",9}");
        foreach (var rowId in StorageBenchmarkWorkloads.DisplayOrder)
        {
            if (!result.Rows.TryGetValue(rowId, out var row))
            {
                continue;
            }

            AppendMetric(builder, row.DisplayName, "Read", row.Read);
            AppendMetric(builder, row.DisplayName, "Write", row.Write);
            AppendMetric(builder, row.DisplayName, "Mix", row.Mix);
        }

        builder.AppendLine();
        builder.AppendLine("Quality");
        builder.AppendLine(result.Quality is null || result.Quality.Flags.Count == 0
            ? "stable"
            : string.Join(", ", result.Quality.Flags));
        return builder.ToString().TrimEnd();
    }

    private static void AppendMetric(StringBuilder builder, string workload, string operation, StorageBenchmarkMetricResult? metric)
    {
        if (metric is null)
        {
            return;
        }

        builder.AppendLine($"{workload,-16} {operation,-10} {metric.Throughput.Median,12:F2} {metric.Iops.Median,12:F0} {metric.Latency.P95Microseconds,12:F2} {metric.Throughput.Cv,8:P1}");
        foreach (var sample in metric.Samples)
        {
            builder.AppendLine($"  run {sample.Index}: {sample.ThroughputMBs:F2} MB/s, {sample.Iops:F0} IOPS, p50 {sample.Latency.P50Microseconds:F2} us, p95 {sample.Latency.P95Microseconds:F2} us, p99 {sample.Latency.P99Microseconds:F2} us");
        }
    }

    private static string FormatTemperature(double? value) => value is { } temperature ? $"{temperature:F0} C" : "unknown";
}
