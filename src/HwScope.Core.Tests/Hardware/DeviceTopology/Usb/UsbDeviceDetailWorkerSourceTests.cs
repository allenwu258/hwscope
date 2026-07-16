using HwScope.Core.Hardware.DeviceTopology.Usb;

namespace HwScope.Core.Tests.Hardware.DeviceTopology.Usb;

public sealed class UsbDeviceDetailWorkerSourceTests
{
    [Fact]
    public void WorkerRoundTripReturnsStructuredDiagnosticForUnavailableHub()
    {
        var workerPath = Path.Combine(AppContext.BaseDirectory, "HwScope.UsbWorker.exe");
        var source = new UsbDeviceDetailWorkerSource(workerPath);
        var target = new UsbDeviceDetailTarget(
            "port-test",
            "device-test",
            "HWSCOPE-NONEXISTENT-HUB",
            1,
            new UsbDeviceDescriptorInfo(
                18, 1, 0x0200, 0, 0, 0, 64, 0x1234, 0x5678, 0x0100, 1, 2, 3, 1));

        var detail = source.Collect(target);

        Assert.Equal(target.AttachmentId, detail.AttachmentId);
        Assert.Equal(target.DeviceNodeId, detail.DeviceNodeId);
        Assert.Equal("usb.detail.open-failed", Assert.Single(detail.Diagnostics.Entries).Code);
    }

    [Fact]
    public void WorkerHardTimeoutTerminatesAStuckChildProcess()
    {
        var commandPrompt = Path.Combine(Environment.SystemDirectory, "cmd.exe");
        var source = new UsbDeviceDetailWorkerSource(
            commandPrompt,
            TimeSpan.FromMilliseconds(200),
            "/d /c \"ping 127.0.0.1 -n 10 > nul\"");
        var target = new UsbDeviceDetailTarget(
            "port-test",
            "device-test",
            "unused",
            1,
            new UsbDeviceDescriptorInfo(
                18, 1, 0x0200, 0, 0, 0, 64, 0x1234, 0x5678, 0x0100, 0, 0, 0, 1));

        Assert.Throws<TimeoutException>(() => source.Collect(target));
    }
}
