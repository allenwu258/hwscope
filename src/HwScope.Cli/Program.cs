using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using HwScope.Core.Benchmark;
using HwScope.Core.Benchmark.Storage;
using HwScope.Core.Hardware;
using HwScope.Core.Hardware.DeviceTopology.Pci;
using HwScope.Core.Hardware.Inventory;
using HwScope.Core.Hardware.Storage;

if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
{
    Console.Error.WriteLine("HwScope 当前版本仅支持 Windows。");
    return 2;
}

CliOptions options;
try
{
    options = CliOptions.Parse(args);
}
catch (ArgumentException ex)
{
    Console.Error.WriteLine($"参数错误：{ex.Message}");
    Console.Error.WriteLine("使用 --help 查看命令说明。");
    return 2;
}

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
    if (options.PciMode)
    {
        var snapshot = new PciTopologyCollector().Collect();
        var output = options.Json
            ? JsonSerializer.Serialize(
                options.IncludeSensitiveIds ? snapshot : PciTopologyRedactor.RedactSensitiveIds(snapshot),
                jsonOptions)
            : PciTopologyReportFormatter.Format(snapshot);
        Console.WriteLine(output);
        if (options.Copy)
        {
            Clipboard.Copy(output);
            Console.WriteLine();
            Console.WriteLine("已复制到剪贴板。");
        }

        return snapshot.Diagnostics.HasErrors ? 3 : 0;
    }

    if (options.StorageBenchmark)
    {
        var targets = new StorageBenchmarkTargetDiscovery().Discover();
        var target = targets.FirstOrDefault(candidate => string.Equals(
            candidate.DriveLetter,
            NormalizeDrive(options.BenchmarkDrive!),
            StringComparison.OrdinalIgnoreCase));
        if (target is null)
        {
            Console.Error.WriteLine("未找到可用的本地存储跑分目标卷。");
            return 3;
        }

        var benchmarkOptions = options.QuickStorageBenchmark
            ? new StorageBenchmarkOptions { Runs = 1, FileSizeBytes = 256L * 1024 * 1024 }
            : new StorageBenchmarkOptions();
        benchmarkOptions = benchmarkOptions with
        {
            Runs = options.BenchmarkRuns ?? benchmarkOptions.Runs,
            FileSizeBytes = checked((long)(options.BenchmarkSizeMiB ?? (benchmarkOptions.FileSizeBytes / 1024 / 1024)) * 1024 * 1024)
        };
        var selectedWorkloads = string.IsNullOrWhiteSpace(options.BenchmarkWorkload)
            ? null
            : new[] { options.BenchmarkWorkload };
        var plan = StorageBenchmarkPlanner.CreatePlan(target, benchmarkOptions, selectedWorkloads);
        Console.Error.WriteLine($"Target: {target.DisplayName}");
        Console.Error.WriteLine($"Plan  : {StorageBenchmarkFormatting.FormatBytes(plan.Options.FileSizeBytes)}, maximum writes {StorageBenchmarkFormatting.FormatBytes(plan.MaximumWriteBytes)}");
        using var benchmarkCancellation = options.CancelAfterMs is { } cancelAfterMs
            ? new CancellationTokenSource(TimeSpan.FromMilliseconds(cancelAfterMs))
            : new CancellationTokenSource();
        var storageBenchmarkResult = await new StorageBenchmarkProcessRunner().RunAsync(
            plan,
            progress: null,
            benchmarkCancellation.Token);
        Console.WriteLine(options.Json
            ? JsonSerializer.Serialize(storageBenchmarkResult, jsonOptions)
            : StorageBenchmarkResultFormatter.Format(storageBenchmarkResult));
        return storageBenchmarkResult.Cleanup.Deleted ? 0 : 5;
    }

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
catch (OperationCanceledException)
{
    Console.Error.WriteLine("存储跑分已取消，临时文件清理已完成或记录到诊断日志。");
    return 130;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"HwScope 命令执行失败：{ex.Message}");
    return 1;
}

static string NormalizeDrive(string value)
{
    var text = value.Trim().TrimEnd('\\');
    return text.Length == 1 && char.IsLetter(text[0])
        ? $"{char.ToUpperInvariant(text[0])}:"
        : text.Length >= 2 && text[1] == ':'
            ? $"{char.ToUpperInvariant(text[0])}:"
            : text;
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

internal sealed record CliOptions(
    bool Json,
    bool Copy,
    bool MemoryBenchmark,
    bool StorageBenchmark,
    bool QuickStorageBenchmark,
    string? BenchmarkDrive,
    int? BenchmarkSizeMiB,
    int? BenchmarkRuns,
    string? BenchmarkWorkload,
    int? CancelAfterMs,
    bool StorageMode,
    int? StorageDisk,
    bool PciMode,
    bool IncludeSensitiveIds,
    bool ShowHelp)
{
    public static CliOptions Parse(string[] args)
    {
        var normalized = args.Select(a => a.Trim().ToLowerInvariant()).ToHashSet();
        var command = args
            .Select(argument => argument.Trim())
            .FirstOrDefault(argument => !argument.Equals("--json", StringComparison.OrdinalIgnoreCase)
                && !argument.Equals("--copy", StringComparison.OrdinalIgnoreCase)
                && !argument.Equals("--include-sensitive-ids", StringComparison.OrdinalIgnoreCase));
        var optionsRequiringValue = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "--disk", "--drive", "--size-mib", "--runs", "--workload", "--cancel-after-ms"
        };
        for (var index = 0; index < args.Length; index++)
        {
            if (optionsRequiringValue.Contains(args[index]) && index + 1 >= args.Length)
            {
                throw new ArgumentException($"{args[index]} 缺少参数值。");
            }
        }

        var storageBenchmark = normalized.Contains("benchmark") && normalized.Contains("storage");
        var storageMode = normalized.Contains("storage") && !storageBenchmark;
        int? storageDisk = null;
        string? benchmarkDrive = null;
        int? benchmarkSizeMiB = null;
        int? benchmarkRuns = null;
        string? benchmarkWorkload = null;
        int? cancelAfterMs = null;
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
        for (var index = 0; index < args.Length - 1; index++)
        {
            if (args[index].Equals("--drive", StringComparison.OrdinalIgnoreCase))
            {
                benchmarkDrive = args[index + 1];
            }
            if (args[index].Equals("--size-mib", StringComparison.OrdinalIgnoreCase))
            {
                if (!int.TryParse(args[index + 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var sizeMiB))
                {
                    throw new ArgumentException("--size-mib 必须是整数。");
                }
                benchmarkSizeMiB = sizeMiB;
            }
            if (args[index].Equals("--runs", StringComparison.OrdinalIgnoreCase))
            {
                if (!int.TryParse(args[index + 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var runs))
                {
                    throw new ArgumentException("--runs 必须是整数。");
                }
                benchmarkRuns = runs;
            }
            if (args[index].Equals("--workload", StringComparison.OrdinalIgnoreCase))
            {
                benchmarkWorkload = args[index + 1];
            }
            if (args[index].Equals("--cancel-after-ms", StringComparison.OrdinalIgnoreCase))
            {
                if (!int.TryParse(args[index + 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var milliseconds)
                    || milliseconds <= 0)
                {
                    throw new ArgumentException("--cancel-after-ms 必须是大于 0 的整数。");
                }
                cancelAfterMs = milliseconds;
            }
        }

        var showHelp = normalized.Contains("-h") || normalized.Contains("--help") || normalized.Contains("/?");
        if (storageBenchmark && !showHelp && string.IsNullOrWhiteSpace(benchmarkDrive))
        {
            throw new ArgumentException("存储跑分必须显式指定 --drive，例如 --drive C:。");
        }

        return new CliOptions(
            Json: normalized.Contains("--json"),
            Copy: normalized.Contains("--copy"),
            MemoryBenchmark: normalized.Contains("benchmark") && normalized.Contains("memory"),
            StorageBenchmark: storageBenchmark,
            QuickStorageBenchmark: normalized.Contains("--quick"),
            BenchmarkDrive: benchmarkDrive,
            BenchmarkSizeMiB: benchmarkSizeMiB,
            BenchmarkRuns: benchmarkRuns,
            BenchmarkWorkload: benchmarkWorkload,
            CancelAfterMs: cancelAfterMs,
            StorageMode: storageMode,
            StorageDisk: storageDisk,
            PciMode: command is not null && (command.Equals("pcie", StringComparison.OrdinalIgnoreCase) || command.Equals("pci", StringComparison.OrdinalIgnoreCase)),
            IncludeSensitiveIds: normalized.Contains("--include-sensitive-ids"),
            ShowHelp: showHelp);
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
      benchmark storage --drive C: [--quick] [--size-mib N] [--runs N]
                        [--workload ID] [--cancel-after-ms N] [--json]
                   运行文件级存储跑分；必须显式指定目标卷；--quick 使用 1 run / 256 MiB
      storage list
                   列出物理存储设备
      storage --disk N [--json]
                   读取指定物理磁盘的详情和健康数据
      pcie [--json] [--copy] [--include-sensitive-ids]
                   枚举当前 PCI/PCIe 拓扑、BDF、链路属性和诊断
                   JSON 默认移除稳定设备标识；仅本机诊断时可显式保留
      -h, --help   显示帮助

    示例：
      dotnet run --project src/HwScope.Cli
      dotnet run --project src/HwScope.Cli -- --json
      dotnet run --project src/HwScope.Cli -- --copy
      dotnet run --project src/HwScope.Cli -- benchmark memory
      dotnet run --project src/HwScope.Cli -- benchmark storage --drive C: --quick
      dotnet run --project src/HwScope.Cli -- storage list
      dotnet run --project src/HwScope.Cli -- storage --disk 0
      dotnet run --project src/HwScope.Cli -- pcie
    """;
}
