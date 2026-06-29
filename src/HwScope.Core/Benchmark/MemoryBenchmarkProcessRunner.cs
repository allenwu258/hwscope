using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using HwScope.Core.Hardware.Cpu;
using HwScope.Core.Windows;

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

        var placementPlan = options.UsePreferredCore
            ? MemoryBenchmarkPlacementPlanner.CreatePreferredSingleThreadPlan()
            : MemoryBenchmarkPlacementPlan.Fallback("Preferred core placement is disabled by options.");
        var arguments = BuildArguments(options, placementPlan, useProgressJson: progress is not null);
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
            var result = progress is null ? ParseJsonResult(output) : ParseProgressJson(output);
            return EnrichResult(result, executable, options, placementPlan);
        }
        catch (Exception ex) when (ex is FormatException or OverflowException or JsonException or InvalidOperationException or KeyNotFoundException)
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

    private static string[] BuildArguments(MemoryBenchmarkOptions options, MemoryBenchmarkPlacementPlan placementPlan, bool useProgressJson)
    {
        var arguments = new List<string>
        {
            "--size-mib",
            options.SizeMiB.ToString(CultureInfo.InvariantCulture),
            "--iterations",
            options.Iterations.ToString(CultureInfo.InvariantCulture),
            "--latency-steps",
            options.LatencySteps.ToString(CultureInfo.InvariantCulture),
            "--warmup-runs",
            options.WarmupRuns.ToString(CultureInfo.InvariantCulture),
            "--min-samples",
            options.Iterations.ToString(CultureInfo.InvariantCulture),
            "--max-samples",
            Math.Max(options.MaxSamples, options.Iterations).ToString(CultureInfo.InvariantCulture),
            "--target-sample-ms",
            options.TargetSampleMs.ToString(CultureInfo.InvariantCulture),
            "--max-cv",
            options.MaxCv.ToString(CultureInfo.InvariantCulture)
        };

        if (placementPlan.Requested is { } requested)
        {
            arguments.Add("--preferred-group");
            arguments.Add(requested.Group.ToString(CultureInfo.InvariantCulture));
            arguments.Add("--preferred-processor");
            arguments.Add(requested.ProcessorNumber.ToString(CultureInfo.InvariantCulture));
            arguments.Add("--preferred-core");
            arguments.Add(requested.CoreIndex.ToString(CultureInfo.InvariantCulture));
            if (requested.PackageIndex is { } packageIndex)
            {
                arguments.Add("--preferred-package");
                arguments.Add(packageIndex.ToString(CultureInfo.InvariantCulture));
            }

            if (requested.NumaNodeNumber is { } numaNode)
            {
                arguments.Add("--preferred-numa-node");
                arguments.Add(numaNode.ToString(CultureInfo.InvariantCulture));
            }

            arguments.Add("--preferred-smt-index");
            arguments.Add(requested.SmtIndex.ToString(CultureInfo.InvariantCulture));
            arguments.Add("--preferred-efficiency-class");
            arguments.Add(requested.EfficiencyClass.ToString(CultureInfo.InvariantCulture));
            arguments.Add("--preferred-has-smt");
            arguments.Add(requested.HasSmt ? "1" : "0");
        }

        arguments.Add(useProgressJson ? "--progress-json" : "--json");
        return [.. arguments];
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

        if (options.Iterations > 1000)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "迭代次数不能超过 1000。");
        }

        if (options.LatencySteps <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "延迟测试步数必须大于 0。");
        }

        if (options.WarmupRuns < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "预热次数不能小于 0。");
        }

        if (options.WarmupRuns > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "预热次数不能超过 100。");
        }

        if (options.MaxSamples <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "最大样本数必须大于 0。");
        }

        if (options.MaxSamples > 1000)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "最大样本数不能超过 1000。");
        }

        if (!double.IsFinite(options.TargetSampleMs) || options.TargetSampleMs <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "目标样本时长必须大于 0。");
        }

        if (options.TargetSampleMs > 60_000)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "目标样本时长不能超过 60000 ms。");
        }

        if (!double.IsFinite(options.MaxCv) || options.MaxCv is < 0 or > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "最大变异系数必须在 [0, 1]。");
        }

        if (options.Threads <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "线程数必须大于 0。");
        }

        if (options.Threads != 1)
        {
            throw new NotSupportedException("当前内存跑分阶段仅支持单线程，--threads 将在后续阶段接入。");
        }

        if (!string.Equals(options.WorkingSetKind, "memory", StringComparison.OrdinalIgnoreCase))
        {
            throw new NotSupportedException("当前内存跑分阶段仅支持 memory working set，缓存行将在后续阶段接入。");
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
        catch (Exception ex) when (ex is FormatException or JsonException or InvalidOperationException or KeyNotFoundException)
        {
            return false;
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
        MemoryBenchmarkResult? finalResult = null;
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
                case "result":
                    finalResult = ParseJsonResult(root);
                    break;
                case "completed":
                    completed = true;
                    break;
            }
        }

        if (!completed)
        {
            throw new FormatException("内存跑分进度输出缺少完整结果。");
        }

        if (finalResult is not null)
        {
            return finalResult;
        }

        if (sizeMiB is null || readMiBS is null || writeMiBS is null || copyMiBS is null || latencyNs is null)
        {
            throw new FormatException("内存跑分进度输出缺少完整指标。");
        }

        return new MemoryBenchmarkResult(
            SizeMiB: sizeMiB.Value,
            ReadMiBS: readMiBS.Value,
            WriteMiBS: writeMiBS.Value,
            CopyMiBS: copyMiBS.Value,
            LatencyNs: latencyNs.Value,
            CompletedAt: DateTimeOffset.Now);
    }

    private static MemoryBenchmarkResult ParseJsonResult(string output)
    {
        var lines = output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (lines.Length == 0)
        {
            throw new FormatException("内存跑分 JSON 输出为空。");
        }

        using var document = JsonDocument.Parse(lines[^1]);
        return ParseJsonResult(document.RootElement);
    }

    private static MemoryBenchmarkResult ParseJsonResult(JsonElement root)
    {
        if (!root.TryGetProperty("type", out var typeElement)
            || !string.Equals(typeElement.GetString(), "result", StringComparison.OrdinalIgnoreCase))
        {
            throw new FormatException("内存跑分 JSON 输出缺少 result 事件。");
        }

        var optionsElement = root.GetProperty("options");
        var metricsElement = root.GetProperty("metrics");
        var read = ParseMetricResult(metricsElement.GetProperty("read"));
        var write = ParseMetricResult(metricsElement.GetProperty("write"));
        var copy = ParseMetricResult(metricsElement.GetProperty("copy"));
        var latency = ParseMetricResult(metricsElement.GetProperty("latency"));
        var timer = root.TryGetProperty("timer", out var timerElement)
            ? new MemoryBenchmarkTimer(
                Name: timerElement.TryGetProperty("name", out var timerName) ? timerName.GetString() ?? string.Empty : string.Empty,
                FrequencyHz: timerElement.TryGetProperty("frequency_hz", out var frequency) ? frequency.GetInt64() : 0)
            : null;
        var options = new MemoryBenchmarkOptionsSnapshot(
            SizeMiB: optionsElement.GetProperty("size_mib").GetInt32(),
            Iterations: optionsElement.GetProperty("iterations").GetInt32(),
            LatencySteps: optionsElement.GetProperty("latency_steps").GetInt64(),
            WarmupRuns: optionsElement.TryGetProperty("warmup_runs", out var warmupRuns) ? warmupRuns.GetInt32() : 0,
            MinSamples: optionsElement.TryGetProperty("min_samples", out var minSamples) ? minSamples.GetInt32() : optionsElement.GetProperty("iterations").GetInt32(),
            MaxSamples: optionsElement.TryGetProperty("max_samples", out var maxSamples) ? maxSamples.GetInt32() : optionsElement.GetProperty("iterations").GetInt32(),
            TargetSampleMs: optionsElement.TryGetProperty("target_sample_ms", out var targetSampleMs) ? targetSampleMs.GetDouble() : 0.0,
            MaxCv: optionsElement.TryGetProperty("max_cv", out var maxCv) ? maxCv.GetDouble() : 0.0,
            Threads: optionsElement.GetProperty("threads").GetInt32(),
            UsePreferredCore: optionsElement.TryGetProperty("use_preferred_core", out var usePreferredCore) && usePreferredCore.GetBoolean(),
            WorkingSetKind: optionsElement.GetProperty("working_set_kind").GetString() ?? "memory");
        var placement = root.TryGetProperty("placement", out var placementElement)
            ? ParsePlacement(placementElement)
            : null;

        return new MemoryBenchmarkResult(
            SizeMiB: options.SizeMiB,
            ReadMiBS: read.Aggregate.Median,
            WriteMiBS: write.Aggregate.Median,
            CopyMiBS: copy.Aggregate.Median,
            LatencyNs: latency.Aggregate.Median,
            CompletedAt: DateTimeOffset.Now,
            Options: options,
            WorkerVersion: root.TryGetProperty("worker_version", out var workerVersion) ? workerVersion.GetString() : null,
            ProtocolVersion: root.TryGetProperty("protocol_version", out var protocolVersion) ? protocolVersion.GetInt32() : null,
            ElapsedMs: root.TryGetProperty("elapsed_ms", out var elapsedMs) ? elapsedMs.GetDouble() : null,
            Timer: timer,
            Placement: placement,
            Metrics: new MemoryBenchmarkMetricSet(read, write, copy, latency));
    }

    private static MemoryBenchmarkPlacement ParsePlacement(JsonElement element)
    {
        return new MemoryBenchmarkPlacement(
            Mode: element.TryGetProperty("mode", out var mode) ? mode.GetString() ?? string.Empty : string.Empty,
            Source: element.TryGetProperty("source", out var source) ? source.GetString() ?? string.Empty : string.Empty,
            Confidence: element.TryGetProperty("confidence", out var confidence) ? confidence.GetString() ?? string.Empty : string.Empty,
            Reason: element.TryGetProperty("reason", out var reason) ? reason.GetString() ?? string.Empty : string.Empty,
            AffinityApplied: element.TryGetProperty("affinity_applied", out var affinityApplied) ? affinityApplied.GetBoolean() : null,
            Requested: element.TryGetProperty("requested", out var requested) && requested.ValueKind == JsonValueKind.Object ? ParseProcessorPlacement(requested) : null,
            Actual: element.TryGetProperty("actual", out var actual) && actual.ValueKind == JsonValueKind.Object ? ParseProcessorPlacement(actual) : null,
            Candidates: element.TryGetProperty("candidates", out var candidates) && candidates.ValueKind == JsonValueKind.Array
                ? candidates.EnumerateArray().Select(ParseProcessorPlacement).ToList()
                : []);
    }

    private static MemoryBenchmarkProcessorPlacement ParseProcessorPlacement(JsonElement element)
    {
        return new MemoryBenchmarkProcessorPlacement(
            Group: element.TryGetProperty("group", out var group) ? group.GetUInt16() : (ushort)0,
            ProcessorNumber: element.TryGetProperty("processor", out var processor) ? processor.GetInt32() : 0,
            CoreIndex: element.TryGetProperty("core", out var core) ? core.GetInt32() : null,
            PackageIndex: element.TryGetProperty("package", out var package) ? package.GetInt32() : null,
            NumaNodeNumber: element.TryGetProperty("numa_node", out var numaNode) ? numaNode.GetUInt32() : null,
            SmtIndex: element.TryGetProperty("smt_index", out var smtIndex) ? smtIndex.GetInt32() : null,
            EfficiencyClass: element.TryGetProperty("efficiency_class", out var efficiencyClass) ? efficiencyClass.GetInt32() : null,
            HasSmt: element.TryGetProperty("has_smt", out var hasSmt) ? hasSmt.GetBoolean() : null);
    }

    private static MemoryBenchmarkMetricResult ParseMetricResult(JsonElement element)
    {
        var samples = element.GetProperty("samples")
            .EnumerateArray()
            .Select(sample => sample.GetDouble())
            .ToList();
        var innerIterations = element.TryGetProperty("inner_iterations", out var innerIterationsElement)
            ? innerIterationsElement.EnumerateArray().Select(sample => sample.GetInt64()).ToList()
            : [];
        var aggregate = element.GetProperty("aggregate");
        return new MemoryBenchmarkMetricResult(
            Unit: element.GetProperty("unit").GetString() ?? string.Empty,
            Samples: samples,
            InnerIterations: innerIterations,
            Converged: element.TryGetProperty("converged", out var converged) && converged.GetBoolean(),
            Aggregate: new MemoryBenchmarkAggregate(
                Median: aggregate.GetProperty("median").GetDouble(),
                Min: aggregate.GetProperty("min").GetDouble(),
                Max: aggregate.GetProperty("max").GetDouble(),
                Mean: aggregate.GetProperty("mean").GetDouble(),
                StdDev: aggregate.GetProperty("stddev").GetDouble(),
                Cv: aggregate.GetProperty("cv").GetDouble()));
    }

    private static MemoryBenchmarkResult EnrichResult(
        MemoryBenchmarkResult result,
        string executable,
        MemoryBenchmarkOptions options,
        MemoryBenchmarkPlacementPlan placementPlan)
    {
        var environmentCollectionFailed = false;
        var environment = CollectEnvironment();
        if (environment is null)
        {
            environmentCollectionFailed = true;
            environment = CreateUnknownEnvironment();
        }

        var quality = EvaluateQuality(result, environment, environmentCollectionFailed);
        var snapshot = result.Options ?? new MemoryBenchmarkOptionsSnapshot(
            options.SizeMiB,
            options.Iterations,
            options.LatencySteps,
            options.WarmupRuns,
            options.Iterations,
            Math.Max(options.MaxSamples, options.Iterations),
            options.TargetSampleMs,
            options.MaxCv,
            options.Threads,
            options.UsePreferredCore,
            options.WorkingSetKind);
        var placement = MergePlacement(result.Placement, placementPlan);

        return result with
        {
            Options = snapshot,
            ExecutablePath = executable,
            Environment = environment,
            Placement = placement,
            Quality = quality
        };
    }

    private static MemoryBenchmarkPlacement ToPlacement(
        MemoryBenchmarkPlacementPlan plan,
        MemoryBenchmarkProcessorPlacement? actual)
    {
        return new MemoryBenchmarkPlacement(
            Mode: plan.Mode,
            Source: plan.Source,
            Confidence: plan.Confidence,
            Reason: plan.Reason,
            AffinityApplied: null,
            Requested: plan.Requested is null ? null : ToProcessorPlacement(plan.Requested),
            Actual: actual,
            Candidates: plan.Candidates.Select(ToProcessorPlacement).ToList());
    }

    private static MemoryBenchmarkPlacement MergePlacement(
        MemoryBenchmarkPlacement? nativePlacement,
        MemoryBenchmarkPlacementPlan plan)
    {
        var planned = ToPlacement(plan, nativePlacement?.Actual);
        if (nativePlacement is null)
        {
            return planned;
        }

        return nativePlacement with
        {
            Source = plan.Requested is null || nativePlacement.AffinityApplied == false
                ? Coalesce(nativePlacement.Source, planned.Source)
                : planned.Source,
            Confidence = nativePlacement.AffinityApplied == false
                ? "fallback"
                : plan.Requested is null
                    ? Coalesce(nativePlacement.Confidence, planned.Confidence)
                    : planned.Confidence,
            Reason = nativePlacement.AffinityApplied == false
                ? $"{planned.Reason} Native affinity application failed; actual placement may not match requested placement."
                : plan.Requested is null
                    ? Coalesce(nativePlacement.Reason, planned.Reason)
                    : planned.Reason,
            Requested = nativePlacement.Requested ?? planned.Requested,
            Candidates = nativePlacement.Candidates.Count > 0 ? nativePlacement.Candidates : planned.Candidates
        };
    }

    private static string Coalesce(string value, string fallback)
    {
        return string.IsNullOrWhiteSpace(value) ? fallback : value;
    }

    private static MemoryBenchmarkProcessorPlacement ToProcessorPlacement(MemoryBenchmarkLogicalProcessor processor)
    {
        return new MemoryBenchmarkProcessorPlacement(
            Group: processor.Group,
            ProcessorNumber: processor.ProcessorNumber,
            CoreIndex: processor.CoreIndex,
            PackageIndex: processor.PackageIndex,
            NumaNodeNumber: processor.NumaNodeNumber,
            SmtIndex: processor.SmtIndex,
            EfficiencyClass: processor.EfficiencyClass,
            HasSmt: processor.HasSmt);
    }

    private static MemoryBenchmarkEnvironment? CollectEnvironment()
    {
        try
        {
            var cpu = Wmi.Query("SELECT Name, NumberOfCores, NumberOfLogicalProcessors FROM Win32_Processor").FirstOrDefault();
            var topology = CpuTopologyAnalyzer.TryAnalyze();
            return new MemoryBenchmarkEnvironment(
                CpuName: CleanName(Wmi.GetString(cpu, "Name")),
                PhysicalCoreCount: topology?.Topology.CoreCount.Value ?? (int)Wmi.GetUInt(cpu, "NumberOfCores"),
                LogicalProcessorCount: topology?.Topology.LogicalProcessorCount.Value ?? (int)Wmi.GetUInt(cpu, "NumberOfLogicalProcessors"),
                NumaNodeCount: topology?.Topology.NumaNodeCount.Value ?? 0,
                PowerPlan: GetActivePowerPlan(),
                OnAcPower: GetAcPowerStatus(),
                ProcessPriority: Process.GetCurrentProcess().PriorityClass.ToString());
        }
        catch
        {
            return null;
        }
    }

    private static MemoryBenchmarkEnvironment CreateUnknownEnvironment()
    {
        return new MemoryBenchmarkEnvironment(
            CpuName: string.Empty,
            PhysicalCoreCount: 0,
            LogicalProcessorCount: 0,
            NumaNodeCount: 0,
            PowerPlan: string.Empty,
            OnAcPower: null,
            ProcessPriority: string.Empty);
    }

    private static MemoryBenchmarkQuality EvaluateQuality(
        MemoryBenchmarkResult result,
        MemoryBenchmarkEnvironment environment,
        bool environmentCollectionFailed)
    {
        const double highVarianceCv = 0.08;
        const double backgroundNoiseCv = 0.15;
        const double shortDurationMs = 500.0;

        var flags = new List<string>();
        var metricCvs = new[]
        {
            result.Metrics?.Read.Aggregate.Cv,
            result.Metrics?.Write.Aggregate.Cv,
            result.Metrics?.Copy.Aggregate.Cv,
            result.Metrics?.Latency.Aggregate.Cv
        }.OfType<double>().ToList();

        var highVariance = metricCvs.Any(cv => cv >= highVarianceCv);
        if (highVariance)
        {
            flags.Add("highVariance");
        }

        var backgroundNoiseSuspected = metricCvs.Any(cv => cv >= backgroundNoiseCv);
        if (backgroundNoiseSuspected)
        {
            flags.Add("backgroundNoiseSuspected");
        }

        var shortDuration = result.ElapsedMs is > 0 and < shortDurationMs;
        if (shortDuration)
        {
            flags.Add("shortDuration");
        }

        var thermalSuspected = false;
        if (result.Metrics is not null
            && IsDownwardTrend(result.Metrics.Read.Samples)
            && IsDownwardTrend(result.Metrics.Write.Samples)
            && IsDownwardTrend(result.Metrics.Copy.Samples))
        {
            thermalSuspected = true;
            flags.Add("thermalSuspected");
        }

        if (environment.PhysicalCoreCount == 0 || environment.LogicalProcessorCount == 0)
        {
            flags.Add("incompleteTopologyData");
        }

        if (environmentCollectionFailed)
        {
            flags.Add("environmentCollectionFailed");
        }

        return new MemoryBenchmarkQuality(
            HighVariance: highVariance,
            ThermalSuspected: thermalSuspected,
            ShortDuration: shortDuration,
            BackgroundNoiseSuspected: backgroundNoiseSuspected,
            Flags: flags);
    }

    private static bool IsDownwardTrend(IReadOnlyList<double> samples)
    {
        if (samples.Count < 4)
        {
            return false;
        }

        var firstHalf = samples.Take(samples.Count / 2).Average();
        var secondHalf = samples.Skip(samples.Count / 2).Average();
        return firstHalf > 0 && secondHalf / firstHalf < 0.9;
    }

    private static string CleanName(string value)
    {
        return string.Join(' ', value.Split([' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries));
    }

    private static string GetActivePowerPlan()
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "powercfg.exe",
                ArgumentList = { "/getactivescheme" },
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            });

            if (process is null)
            {
                return string.Empty;
            }

            var outputTask = process.StandardOutput.ReadToEndAsync();
            var exitTask = process.WaitForExitAsync();
            if (Task.WhenAny(exitTask, Task.Delay(TimeSpan.FromSeconds(2))).GetAwaiter().GetResult() != exitTask)
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch
                {
                    // Best-effort environment metadata should never fail the benchmark result.
                }

                return string.Empty;
            }

            var output = outputTask.GetAwaiter().GetResult();
            if (process.ExitCode != 0)
            {
                return string.Empty;
            }

            return CleanName(output.Trim());
        }
        catch
        {
            return string.Empty;
        }
    }

    private static bool? GetAcPowerStatus()
    {
        var batteries = Wmi.Query("SELECT BatteryStatus FROM Win32_Battery").ToList();
        if (batteries.Count == 0)
        {
            return null;
        }

        var statuses = batteries.Select(battery => Wmi.GetUInt(battery, "BatteryStatus")).Where(status => status > 0).ToList();
        if (statuses.Count == 0)
        {
            return null;
        }

        return statuses.Any(status => status is 2 or 6 or 7 or 8 or 9 or 10 or 11);
    }
}

