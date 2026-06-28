using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace HwScope.Core.Benchmark;

public sealed class MemoryBenchmarkProcessRunner : IMemoryBenchmarkRunner
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(5);

    private readonly string? _executablePath;
    private readonly string _diagnosticLogPath;

    public MemoryBenchmarkProcessRunner(string? executablePath = null, string? diagnosticLogPath = null)
    {
        _executablePath = executablePath;
        _diagnosticLogPath = diagnosticLogPath ?? Path.Combine(Path.GetTempPath(), "HwScope-memory-benchmark.log");
    }

    public async Task<MemoryBenchmarkResult> RunAsync(MemoryBenchmarkOptions options, CancellationToken cancellationToken = default)
    {
        return await RunAsync(options, progress: null, cancellationToken).ConfigureAwait(false);
    }

    public async Task<MemoryBenchmarkResult> RunAsync(
        MemoryBenchmarkOptions options,
        IProgress<MemoryBenchmarkProgress>? progress,
        CancellationToken cancellationToken = default)
    {
        ValidateOptions(options);

        var executable = ResolveExecutablePath();
        if (executable is null)
        {
            throw new FileNotFoundException("未找到 membench.exe。请先构建 src/HwScope.Native.MemoryBench，或确认构建产物已复制到应用输出目录的 native 子目录。");
        }

        var arguments = BuildArguments(options, useProgressJson: progress is not null);
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("无法启动内存跑分进程。");
        var outputLines = new List<string>();
        var outputTask = progress is null
            ? process.StandardOutput.ReadToEndAsync()
            : ReadProgressOutputAsync(process.StandardOutput, outputLines, progress, cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync();

        using var timeoutCts = new CancellationTokenSource(options.Timeout ?? DefaultTimeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

        try
        {
            await process.WaitForExitAsync(linkedCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException ex) when (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            await KillProcessTreeAsync(process).ConfigureAwait(false);
            var timeoutOutput = await CompleteReadAsync(outputTask).ConfigureAwait(false);
            var timeoutError = await CompleteReadAsync(errorTask).ConfigureAwait(false);
            var logPath = WriteDiagnostics("Timeout", executable, arguments, null, timeoutOutput, timeoutError);
            throw new TimeoutException($"内存跑分超过 {(options.Timeout ?? DefaultTimeout).TotalSeconds:F0} 秒未完成，已终止 worker。诊断日志：{logPath}", ex);
        }
        catch (OperationCanceledException ex)
        {
            await KillProcessTreeAsync(process).ConfigureAwait(false);
            var canceledOutput = await CompleteReadAsync(outputTask).ConfigureAwait(false);
            var canceledError = await CompleteReadAsync(errorTask).ConfigureAwait(false);
            var logPath = WriteDiagnostics("Canceled", executable, arguments, null, canceledOutput, canceledError);
            throw new OperationCanceledException($"内存跑分已取消，已终止 worker。诊断日志：{logPath}", ex, cancellationToken);
        }

        var output = await CompleteReadAsync(outputTask).ConfigureAwait(false);
        var error = await CompleteReadAsync(errorTask).ConfigureAwait(false);
        if (process.ExitCode != 0)
        {
            var logPath = WriteDiagnostics("NonZeroExit", executable, arguments, process.ExitCode, output, error);
            throw new InvalidOperationException($"内存跑分失败，worker 退出码 {process.ExitCode}：{FormatBriefError(error)}。诊断日志：{logPath}");
        }

        try
        {
            return progress is null ? ParseCsv(output) : ParseProgressJson(output);
        }
        catch (Exception ex) when (ex is FormatException or OverflowException)
        {
            var logPath = WriteDiagnostics("ParseError", executable, arguments, process.ExitCode, output, error);
            throw new FormatException($"内存跑分输出解析失败。诊断日志：{logPath}", ex);
        }
    }

    private string? ResolveExecutablePath()
    {
        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(_executablePath))
        {
            candidates.Add(_executablePath);
        }

        var baseDirectory = AppContext.BaseDirectory;
        candidates.Add(Path.Combine(baseDirectory, "membench.exe"));
        candidates.Add(Path.Combine(baseDirectory, "native", "membench.exe"));
        // Developer fallback: allows running from source after building the native worker manually.
        candidates.Add(Path.GetFullPath(Path.Combine(baseDirectory, "..", "..", "..", "..", "HwScope.Native.MemoryBench", "build", "Release", "membench.exe")));

        return candidates.FirstOrDefault(File.Exists);
    }

    private static string[] BuildArguments(MemoryBenchmarkOptions options, bool useProgressJson)
    {
        return
        [
            "--size-mib",
            options.SizeMiB.ToString(CultureInfo.InvariantCulture),
            "--iterations",
            options.Iterations.ToString(CultureInfo.InvariantCulture),
            "--latency-steps",
            options.LatencySteps.ToString(CultureInfo.InvariantCulture),
            useProgressJson ? "--progress-json" : "--csv"
        ];
    }

    private static void ValidateOptions(MemoryBenchmarkOptions options)
    {
        if (options.SizeMiB < 16)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "测试缓冲区至少需要 16 MiB。");
        }

        if (options.Iterations <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "迭代次数必须大于 0。");
        }

        if (options.LatencySteps <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "延迟测试步数必须大于 0。");
        }

        if (options.Timeout is { } timeout && timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "超时时间必须大于 0。");
        }
    }

    private static async Task KillProcessTreeAsync(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
            return;
        }

        await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
    }

    private static async Task<string> CompleteReadAsync(Task<string> readTask)
    {
        try
        {
            return await readTask.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return $"<无法读取进程输出：{ex.Message}>";
        }
    }

    private string WriteDiagnostics(
        string reason,
        string executable,
        IReadOnlyList<string> arguments,
        int? exitCode,
        string output,
        string error)
    {
        try
        {
            var directory = Path.GetDirectoryName(_diagnosticLogPath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var text = string.Join(Environment.NewLine,
                $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}] {reason}",
                $"Executable: {executable}",
                $"Arguments : {string.Join(' ', arguments)}",
                $"ExitCode  : {(exitCode.HasValue ? exitCode.Value.ToString(CultureInfo.InvariantCulture) : "<none>")}",
                "Stdout:",
                string.IsNullOrWhiteSpace(output) ? "<empty>" : output.TrimEnd(),
                "Stderr:",
                string.IsNullOrWhiteSpace(error) ? "<empty>" : error.TrimEnd(),
                new string('-', 80),
                string.Empty);

            File.AppendAllText(_diagnosticLogPath, text);
            return _diagnosticLogPath;
        }
        catch (Exception ex)
        {
            return $"{_diagnosticLogPath}（写入失败：{ex.Message}）";
        }
    }

    private static string FormatBriefError(string error)
    {
        return string.IsNullOrWhiteSpace(error) ? "无 stderr 输出" : error.Trim();
    }

    private static async Task<string> ReadProgressOutputAsync(
        StreamReader reader,
        List<string> outputLines,
        IProgress<MemoryBenchmarkProgress> progress,
        CancellationToken cancellationToken)
    {
        while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
            outputLines.Add(line);
            if (TryParseMetricEvent(line, out var update))
            {
                progress.Report(update);
            }
        }

        return string.Join(Environment.NewLine, outputLines);
    }

    private static bool TryParseMetricEvent(string line, out MemoryBenchmarkProgress progress)
    {
        progress = default!;
        if (string.IsNullOrWhiteSpace(line))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (!root.TryGetProperty("type", out var typeElement)
                || !string.Equals(typeElement.GetString(), "metric", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var metricName = root.GetProperty("metric").GetString();
            var metric = metricName?.ToLowerInvariant() switch
            {
                "read" => MemoryBenchmarkMetric.Read,
                "write" => MemoryBenchmarkMetric.Write,
                "copy" => MemoryBenchmarkMetric.Copy,
                "latency" => MemoryBenchmarkMetric.Latency,
                _ => throw new FormatException($"未知内存跑分指标：{metricName}")
            };

            progress = new MemoryBenchmarkProgress(
                metric,
                root.GetProperty("value").GetDouble(),
                root.GetProperty("unit").GetString() ?? string.Empty,
                DateTimeOffset.Now);
            return true;
        }
        catch (JsonException ex)
        {
            throw new FormatException("内存跑分进度事件不是有效 JSON。", ex);
        }
    }

    private static MemoryBenchmarkResult ParseCsv(string output)
    {
        var lines = output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (lines.Length < 2)
        {
            throw new FormatException("内存跑分输出为空或格式不正确。");
        }

        var values = lines[1].Split(',', StringSplitOptions.TrimEntries);
        if (values.Length != 5)
        {
            throw new FormatException("内存跑分 CSV 列数不正确。");
        }

        return new MemoryBenchmarkResult(
            SizeMiB: int.Parse(values[0], CultureInfo.InvariantCulture),
            ReadMiBS: double.Parse(values[1], CultureInfo.InvariantCulture),
            WriteMiBS: double.Parse(values[2], CultureInfo.InvariantCulture),
            CopyMiBS: double.Parse(values[3], CultureInfo.InvariantCulture),
            LatencyNs: double.Parse(values[4], CultureInfo.InvariantCulture),
            CompletedAt: DateTimeOffset.Now);
    }

    private static MemoryBenchmarkResult ParseProgressJson(string output)
    {
        int? sizeMiB = null;
        double? readMiBS = null;
        double? writeMiBS = null;
        double? copyMiBS = null;
        double? latencyNs = null;
        var completed = false;

        var lines = output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var line in lines)
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (!root.TryGetProperty("type", out var typeElement))
            {
                continue;
            }

            switch (typeElement.GetString()?.ToLowerInvariant())
            {
                case "started":
                    if (root.TryGetProperty("size_mib", out var sizeElement))
                    {
                        sizeMiB = sizeElement.GetInt32();
                    }
                    break;
                case "metric":
                    var metric = root.GetProperty("metric").GetString()?.ToLowerInvariant();
                    var value = root.GetProperty("value").GetDouble();
                    switch (metric)
                    {
                        case "read":
                            readMiBS = value;
                            break;
                        case "write":
                            writeMiBS = value;
                            break;
                        case "copy":
                            copyMiBS = value;
                            break;
                        case "latency":
                            latencyNs = value;
                            break;
                    }
                    break;
                case "completed":
                    completed = true;
                    break;
            }
        }

        if (!completed || sizeMiB is null || readMiBS is null || writeMiBS is null || copyMiBS is null || latencyNs is null)
        {
            throw new FormatException("内存跑分进度输出缺少完整结果。");
        }

        return new MemoryBenchmarkResult(
            SizeMiB: sizeMiB.Value,
            ReadMiBS: readMiBS.Value,
            WriteMiBS: writeMiBS.Value,
            CopyMiBS: copyMiBS.Value,
            LatencyNs: latencyNs.Value,
            CompletedAt: DateTimeOffset.Now);
    }
}

