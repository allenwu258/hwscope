using System.Diagnostics;
using System.Globalization;

namespace HwScope.Core.Benchmark;

public sealed class MemoryBenchmarkProcessRunner : IMemoryBenchmarkRunner
{
    private readonly string? _executablePath;

    public MemoryBenchmarkProcessRunner(string? executablePath = null)
    {
        _executablePath = executablePath;
    }

    public async Task<MemoryBenchmarkResult> RunAsync(MemoryBenchmarkOptions options, CancellationToken cancellationToken = default)
    {
        var executable = ResolveExecutablePath();
        if (executable is null)
        {
            throw new FileNotFoundException("未找到 membench.exe。请先构建 src/HwScope.Native.MemoryBench，或确认外部 memory-bench-cpp 构建产物存在。");
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            Arguments = $"--size-mib {options.SizeMiB} --iterations {options.Iterations} --latency-steps {options.LatencySteps} --csv",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("无法启动内存跑分进程。");
        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);

        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

        var output = await outputTask.ConfigureAwait(false);
        var error = await errorTask.ConfigureAwait(false);
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"内存跑分失败：{error.Trim()}");
        }

        return ParseCsv(output);
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
        candidates.Add(Path.GetFullPath(Path.Combine(baseDirectory, "..", "..", "..", "..", "HwScope.Native.MemoryBench", "build", "Release", "membench.exe")));
        candidates.Add(@"C:\Users\Trivedi\memory-bench-cpp\build\Release\membench.exe");

        return candidates.FirstOrDefault(File.Exists);
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
}

