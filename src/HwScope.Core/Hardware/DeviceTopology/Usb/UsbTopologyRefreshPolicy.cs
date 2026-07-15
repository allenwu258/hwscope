namespace HwScope.Core.Hardware.DeviceTopology.Usb;

public sealed record UsbTopologyRefreshResult(
    UsbTopologySnapshot Snapshot,
    bool IsStale,
    bool CollectionFailed,
    DeviceTopologyDiagnostics AttemptDiagnostics);

public static class UsbTopologyRefreshPolicy
{
    public static UsbTopologyRefreshResult Resolve(
        UsbTopologySnapshot? current,
        UsbTopologySnapshot attempted)
    {
        var collectionFailed = attempted.Diagnostics.Entries.Any(entry =>
            string.Equals(entry.Code, "usb.enumeration-failed", StringComparison.Ordinal));
        if (collectionFailed && current is { Nodes.Count: > 0 })
        {
            return new UsbTopologyRefreshResult(current, true, true, attempted.Diagnostics);
        }

        return new UsbTopologyRefreshResult(attempted, false, collectionFailed, attempted.Diagnostics);
    }
}
