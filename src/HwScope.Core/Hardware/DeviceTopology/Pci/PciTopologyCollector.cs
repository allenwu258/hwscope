namespace HwScope.Core.Hardware.DeviceTopology.Pci;

public sealed class PciTopologyCollector
{
    private readonly IPciDeviceSource _source;

    public PciTopologyCollector()
        : this(new PciSetupApiDeviceSource())
    {
    }

    internal PciTopologyCollector(IPciDeviceSource source)
    {
        _source = source;
    }

    public PciTopologySnapshot Collect()
    {
        var generatedAt = DateTimeOffset.Now;
        try
        {
            var result = _source.ReadPresentDevices();
            return PciTopologyBuilder.Build(result.Devices, result.Diagnostics, generatedAt);
        }
        catch (Exception ex)
        {
            var cause = ex.GetBaseException();
            var diagnostic = new DeviceTopologyDiagnostic(
                DeviceTopologyDiagnosticSeverity.Error,
                "pci.enumeration-failed",
                $"PCI device enumeration failed ({cause.GetType().Name}): {cause.Message}");
            return new PciTopologySnapshot([], [], new DeviceTopologyDiagnostics([diagnostic]), generatedAt);
        }
    }
}
