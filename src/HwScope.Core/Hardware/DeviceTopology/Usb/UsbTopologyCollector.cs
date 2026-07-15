namespace HwScope.Core.Hardware.DeviceTopology.Usb;

public sealed class UsbTopologyCollector
{
    private readonly IUsbDeviceSource _source;

    public UsbTopologyCollector()
        : this(new UsbWindowsDeviceSource())
    {
    }

    internal UsbTopologyCollector(IUsbDeviceSource source)
    {
        _source = source;
    }

    public UsbTopologySnapshot Collect()
    {
        var generatedAt = DateTimeOffset.Now;
        try
        {
            var result = _source.ReadPresentTopology();
            return UsbTopologyBuilder.Build(result.Controllers, result.Diagnostics, generatedAt);
        }
        catch (Exception ex)
        {
            var cause = ex.GetBaseException();
            var diagnostic = new DeviceTopologyDiagnostic(
                DeviceTopologyDiagnosticSeverity.Error,
                "usb.enumeration-failed",
                $"USB topology enumeration failed ({cause.GetType().Name}): {cause.Message}");
            return new UsbTopologySnapshot([], [], new DeviceTopologyDiagnostics([diagnostic]), generatedAt);
        }
    }
}
