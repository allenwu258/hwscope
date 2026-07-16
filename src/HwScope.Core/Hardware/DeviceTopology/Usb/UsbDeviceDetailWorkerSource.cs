using System.Diagnostics;
using System.Text.Json;

namespace HwScope.Core.Hardware.DeviceTopology.Usb;

internal sealed class UsbDeviceDetailWorkerSource : IUsbDeviceDetailSource
{
    private static readonly TimeSpan DefaultWorkerTimeout = TimeSpan.FromSeconds(12);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };
    private readonly string _workerPath;
    private readonly TimeSpan _workerTimeout;
    private readonly string _arguments;

    public UsbDeviceDetailWorkerSource(
        string workerPath,
        TimeSpan? workerTimeout = null,
        string arguments = "")
    {
        _workerPath = workerPath;
        _workerTimeout = workerTimeout ?? DefaultWorkerTimeout;
        _arguments = arguments;
        if (_workerTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(workerTimeout));
        }
    }

    public UsbDeviceDetailSnapshot Collect(UsbDeviceDetailTarget target)
    {
        return CollectAsync(target).GetAwaiter().GetResult();
    }

    private async Task<UsbDeviceDetailSnapshot> CollectAsync(UsbDeviceDetailTarget target)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = _workerPath,
                Arguments = _arguments,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            }
        };
        if (!process.Start())
        {
            throw new InvalidOperationException("Unable to start the USB descriptor worker process.");
        }

        var standardOutput = process.StandardOutput.ReadToEndAsync();
        var standardError = process.StandardError.ReadToEndAsync();
        ObserveFailure(standardOutput);
        ObserveFailure(standardError);
        await process.StandardInput.WriteAsync(JsonSerializer.Serialize(target, JsonOptions)).ConfigureAwait(false);
        process.StandardInput.Close();

        using var timeout = new CancellationTokenSource(_workerTimeout);
        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            TryTerminate(process);
            throw new TimeoutException(
                $"USB descriptor worker exceeded the {_workerTimeout.TotalSeconds:0.###}-second hard timeout.");
        }

        var output = await standardOutput.ConfigureAwait(false);
        var error = await standardError.ConfigureAwait(false);
        if (process.ExitCode != 0)
        {
            throw new InvalidDataException(
                string.IsNullOrWhiteSpace(error)
                    ? $"USB descriptor worker exited with code {process.ExitCode}."
                    : $"USB descriptor worker failed: {error.Trim()}");
        }

        return JsonSerializer.Deserialize<UsbDeviceDetailSnapshot>(output, JsonOptions)
            ?? throw new InvalidDataException("USB descriptor worker returned an empty result.");
    }

    private static void TryTerminate(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(2_000);
            }
        }
        catch (InvalidOperationException)
        {
        }
        catch (System.ComponentModel.Win32Exception)
        {
        }
    }

    private static void ObserveFailure(Task task)
    {
        _ = task.ContinueWith(
            completed => _ = completed.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }
}
