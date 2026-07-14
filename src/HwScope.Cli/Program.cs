using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using HwScope.Core.Benchmark;
using HwScope.Core.Hardware;
using HwScope.Core.Hardware.Inventory;
using HwScope.Core.Hardware.Storage;

if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
{
    Console.Error.WriteLine("HwScope 当前版本仅支持 Windows。");
    return 2;
}

var options = CliOptions.Parse(args);
if (options.ShowHelp)
{
    Console.WriteLine(CliHelp.Text);
    return 0;
}

var jsonOptions = new JsonSerializerOptions
{
    WriteIndented = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase
};

try
{
    if (options.StorageMode)
    {
        var inventory = new HardwareInventoryCollector().Collect();
        var devices = inventory.DiskDrives.Select(StorageDeviceDescriptor.FromSnapshot).OrderBy(device => device.PhysicalDriveNumber ?? int.MaxValue).ToList();
        if (options.StorageDisk is null)
        {
            Console.WriteLine("Storage Devices");
            Console.WriteLine("---------------");
            foreach (var device in devices)
            {
                var bus = StorageDeviceBusProbe.Query(device);
                Console.WriteLine($"Disk {device.PhysicalDriveNumber?.ToString() ?? "?"}: {device.Model} · {StorageField.FormatDecimalBytes(device.CapacityBytes)} · {bus.DisplayText}");
            }

            if (devices.Count == 0)
            {
                Console.WriteLine("No physical storage devices were returned by Windows.");
            }
            return 0;
        }

        var selected = devices.FirstOrDefault(device => device.PhysicalDriveNumber == options.StorageDisk);
        if (selected is null)
        {
            Console.Error.WriteLine($"未找到物理磁盘 {options.StorageDisk}。");
            return 3;
        }

        var storageReport = await new StorageDetailCollector().CollectAsync(selected);
        Console.WriteLine(options.Json
            ? JsonSerializer.Serialize(storageReport, jsonOptions)
            : StorageDetailReportFormatter.Format(storageReport));
        return storageReport.Health.Status == StorageHealthStatus.Critical ? 4 : 0;
    }

    if (options.MemoryBenchmark)
    {
        var result = await new MemoryBenchmarkProcessRunner().RunAsync(new MemoryBenchmarkOptions());
        Console.WriteLine("Memory Benchmark");
        Console.WriteLine("----------------");
        Console.WriteLine($"{"",-10} {"Read",10} {"Write",10} {"Copy",10} {"Latency",12}");
        foreach (var rowKey in MemoryBenchmarkRows.DisplayOrder)
        {
            Console.WriteLine(FormatRow(result, rowKey));
        }
        Console.WriteLine();
        Console.WriteLine($"Worker  : {result.WorkerVersion ?? "unknown"} (protocol {result.ProtocolVersion?.ToString() ?? "unknown"})");
        Console.WriteLine($"Timer   : {FormatTimer(result)}");
        Console.WriteLine($"Core    : {FormatPlacement(result)}");
        Console.WriteLine($"Options : {FormatOptions(result)}");
        Console.WriteLine($"Elapsed : {(result.ElapsedMs is { } elapsedMs ? $"{elapsedMs:F0} ms" : "unknown")}");
        Console.WriteLine($"Quality : {FormatQuality(result)}");
        return 0;
    }

    var collector = new HardwareCollector();
    var report = collector.CollectSummary();

    if (options.Json)
    {
        Console.WriteLine(JsonSerializer.Serialize(report, jsonOptions));
        return 0;
    }

    var text = HardwareReportFormatter.FormatSummary(report);
    Console.WriteLine(text);

    if (options.Copy)
    {
        Clipboard.Copy(text);
        Console.WriteLine();
        Console.WriteLine("已复制到剪贴板。");
    }

    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"采集硬件信息失败：{ex.Message}");
    return 1;
}

static string FormatRow(MemoryBenchmarkResult result, string rowKey)
{
    var rows = result.Rows ?? new Dictionary<string, MemoryBenchmarkRowResult>(StringComparer.OrdinalIgnoreCase);
    if (!rows.TryGetValue(rowKey, out var row) || !row.Available || row.Metrics is not { } metrics)
    {
        return $"{FormatRowName(rowKey),-10} {"N/A",10} {"N/A",10} {"N/A",10} {"N/A",12}";
    }

    return string.Join(' ',
        $"{FormatRowName(rowKey),-10}",
        $"{FormatThroughput(metrics.Read),10}",
        $"{FormatThroughput(metrics.Write),10}",
        $"{FormatThroughput(metrics.Copy),10}",
        $"{metrics.Latency.Aggregate.Median.ToString("F1", CultureInfo.InvariantCulture) + " ns",12}");
}

static string FormatRowName(string rowKey)
{
    return rowKey switch
    {
        MemoryBenchmarkRows.L1 => "L1 Cache",
        MemoryBenchmarkRows.L2 => "L2 Cache",
        MemoryBenchmarkRows.L3 => "L3 Cache",
        _ => "Memory"
    };
}

static string FormatThroughput(MemoryBenchmarkMetricResult metric)
{
    return $"{metric.Aggregate.Median * 1024.0 * 1024.0 / 1_000_000.0:F0}";
}

static string FormatQuality(MemoryBenchmarkResult result)
{
    if (result.Quality is null)
    {
        return "unknown";
    }

    if (result.Quality.Flags.Count == 0)
    {
        return "stable";
    }

    return string.Join(", ", result.Quality.Flags);
}

static string FormatTimer(MemoryBenchmarkResult result)
{
    if (result.Timer is null)
    {
        return "unknown";
    }

    return $"{result.Timer.Name} ({result.Timer.FrequencyHz.ToString(CultureInfo.InvariantCulture)} Hz)";
}

static string FormatOptions(MemoryBenchmarkResult result)
{
    if (result.Options is not { } options)
    {
        return $"{result.SizeMiB} MiB";
    }

    return string.Join(", ",
        $"{options.SizeMiB} MiB",
        $"samples {options.MinSamples}-{options.MaxSamples}",
        $"warmup {options.WarmupRuns}",
        $"target {options.TargetSampleMs.ToString("F0", CultureInfo.InvariantCulture)} ms",
        $"max CV {options.MaxCv.ToString("P0", CultureInfo.InvariantCulture)}",
        $"latency steps {options.LatencySteps}",
        $"threads {options.Threads}",
        options.ThreadMode,
        options.NumaMode,
        options.WorkingSetKind);
}

static string FormatPlacement(MemoryBenchmarkResult result)
{
    if (result.Placement is not { } placement)
    {
        return "unknown";
    }

    var requested = FormatProcessor(placement.Requested);
    var actual = FormatProcessor(placement.Actual);
    var affinity = placement.AffinityApplied switch
    {
        true => "affinity applied",
        false => "affinity failed",
        _ => "affinity unknown"
    };
    var workers = placement.RequestedWorkers.Count > 0 ? $", workers {placement.RequestedWorkers.Count}" : string.Empty;
    return $"{placement.Mode}, requested {requested}, actual {actual}, {placement.Confidence}, {affinity}{workers}";
}

static string FormatProcessor(MemoryBenchmarkProcessorPlacement? processor)
{
    if (processor is null)
    {
        return "none";
    }

    var core = processor.CoreIndex is { } coreIndex ? $", core {coreIndex}" : string.Empty;
    var numa = processor.NumaNodeNumber is { } numaNode ? $", numa {numaNode}" : string.Empty;
    var efficiency = processor.EfficiencyClass is { } efficiencyClass ? $", eff {efficiencyClass}" : string.Empty;
    return $"group {processor.Group}/cpu {processor.ProcessorNumber}{core}{numa}{efficiency}";
}

internal sealed record CliOptions(bool Json, bool Copy, bool MemoryBenchmark, bool StorageMode, int? StorageDisk, bool ShowHelp)
{
    public static CliOptions Parse(string[] args)
    {
        var normalized = args.Select(a => a.Trim().ToLowerInvariant()).ToHashSet();
        var storageMode = normalized.Contains("storage");
        int? storageDisk = null;
        for (var index = 0; index < args.Length - 1; index++)
        {
            if (args[index].Equals("--disk", StringComparison.OrdinalIgnoreCase)
                && int.TryParse(args[index + 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
                && parsed >= 0)
            {
                storageDisk = parsed;
                break;
            }
        }

        return new CliOptions(
            Json: normalized.Contains("--json"),
            Copy: normalized.Contains("--copy"),
            MemoryBenchmark: normalized.Contains("benchmark") && normalized.Contains("memory"),
            StorageMode: storageMode,
            StorageDisk: storageDisk,
            ShowHelp: normalized.Contains("-h") || normalized.Contains("--help") || normalized.Contains("/?"));
    }
}

internal static class Clipboard
{
    public static void Copy(string text)
    {
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = "clip.exe",
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardInputEncoding = Encoding.UTF8
        };

        process.Start();
        process.StandardInput.Write(text);
        process.StandardInput.Close();
        process.WaitForExit();

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException("clip.exe 返回非零退出码。");
        }
    }
}

internal static class CliHelp
{
    public const string Text = """
    HwScope CLI - Windows 本机硬件配置摘要

    用法：
      HwScope.Cli [选项]

    选项：
      --json       输出 JSON，方便后续接 GUI 或本地 API
      --copy       将默认文本摘要复制到剪贴板
      benchmark memory
                   运行内存跑分
      storage list
                   列出物理存储设备
      storage --disk N [--json]
                   读取指定物理磁盘的详情和健康数据
      -h, --help   显示帮助

    示例：
      dotnet run --project src/HwScope.Cli
      dotnet run --project src/HwScope.Cli -- --json
      dotnet run --project src/HwScope.Cli -- --copy
      dotnet run --project src/HwScope.Cli -- benchmark memory
      dotnet run --project src/HwScope.Cli -- storage list
      dotnet run --project src/HwScope.Cli -- storage --disk 0
    """;
}
