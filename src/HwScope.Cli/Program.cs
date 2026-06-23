using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using HwScope.Core.Benchmark;
using HwScope.Core.Hardware;

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
    if (options.MemoryBenchmark)
    {
        var result = await new MemoryBenchmarkProcessRunner().RunAsync(new MemoryBenchmarkOptions());
        Console.WriteLine("Memory Benchmark");
        Console.WriteLine("----------------");
        Console.WriteLine($"Read    : {result.ReadMBS:F0} MB/s");
        Console.WriteLine($"Write   : {result.WriteMBS:F0} MB/s");
        Console.WriteLine($"Copy    : {result.CopyMBS:F0} MB/s");
        Console.WriteLine($"Latency : {result.LatencyNs:F1} ns");
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

internal sealed record CliOptions(bool Json, bool Copy, bool MemoryBenchmark, bool ShowHelp)
{
    public static CliOptions Parse(string[] args)
    {
        var normalized = args.Select(a => a.Trim().ToLowerInvariant()).ToHashSet();
        return new CliOptions(
            Json: normalized.Contains("--json"),
            Copy: normalized.Contains("--copy"),
            MemoryBenchmark: normalized.Contains("benchmark") && normalized.Contains("memory"),
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
      -h, --help   显示帮助

    示例：
      dotnet run --project src/HwScope.Cli
      dotnet run --project src/HwScope.Cli -- --json
      dotnet run --project src/HwScope.Cli -- --copy
      dotnet run --project src/HwScope.Cli -- benchmark memory
    """;
}
