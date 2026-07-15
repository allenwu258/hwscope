namespace HwScope.Core.Hardware.DeviceTopology.Pci;

public sealed record PciTopologyRefreshResult(
    PciTopologySnapshot Snapshot,
    bool IsStale,
    bool CollectionFailed,
    DeviceTopologyDiagnostics AttemptDiagnostics);

public static class PciTopologyRefreshPolicy
{
    public static PciTopologyRefreshResult Resolve(
        PciTopologySnapshot? current,
        PciTopologySnapshot attempted)
    {
        var collectionFailed = attempted.Diagnostics.Entries.Any(entry =>
            string.Equals(entry.Code, "pci.enumeration-failed", StringComparison.Ordinal));
        if (collectionFailed && current is not null)
        {
            return new PciTopologyRefreshResult(
                current,
                IsStale: true,
                CollectionFailed: true,
                attempted.Diagnostics);
        }

        return new PciTopologyRefreshResult(
            attempted,
            IsStale: false,
            collectionFailed,
            attempted.Diagnostics);
    }
}
