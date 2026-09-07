using System.Globalization;
using System.Text.RegularExpressions;
using HwScope.Core.Hardware.Inventory;
using HwScope.Core.Windows.Graphics;

namespace HwScope.Core.Hardware.Graphics;

internal static class GraphicsAdapterMerger
{
    public static IReadOnlyList<VideoControllerSnapshot> Merge(
        IReadOnlyList<VideoControllerSnapshot> controllers,
        DxgiEnumerationResult enumeration)
    {
        var adapters = enumeration.Adapters.Where(adapter => !adapter.IsSoftware).ToList();
        var result = new List<VideoControllerSnapshot>();
        var matched = new HashSet<int>();
        foreach (var controller in controllers)
        {
            var candidates = adapters.Select((adapter, index) => (adapter, index))
                .Where(item => Matches(controller, item.adapter)).ToList();
            // PCI IDs identify a model, not a physical instance. Never assign one card's memory to another.
            if (candidates.Count == 1
                && controllers.Count(other => Matches(other, candidates[0].adapter)) == 1)
            {
                var (adapter, index) = candidates[0];
                matched.Add(index);
                result.Add(WithMemory(controller, adapter, enumeration.Diagnostics));
            }
            else
            {
                var reason = candidates.Count > 0
                    ? "DXGI adapter identity is ambiguous; memory was not assigned by enumeration order."
                    : "No matching DXGI adapter; WMI AdapterRAM is a limited 32-bit report, not verified VRAM capacity.";
                result.Add(controller with
                {
                    MemorySource = controller.AdapterRam > 0 ? GraphicsMemorySource.Wmi : GraphicsMemorySource.Unknown,
                    IsEstimated = true,
                    MemoryDiagnostics = [.. enumeration.Diagnostics, reason]
                });
            }
        }

        if (controllers.Count == 0)
        {
            foreach (var adapter in adapters)
            {
                result.Add(WithMemory(new VideoControllerSnapshot(adapter.Description, 0, string.Empty),
                    adapter, [.. enumeration.Diagnostics, "DXGI adapter has no unique WMI identity association."]));
            }
        }
        else if (matched.Count != adapters.Count)
        {
            // WMI remains the authoritative device list when present. Keeping unmatched
            // DXGI records out of the list prevents duplicate GPUs in summary/CLI output.
            foreach (var controllerIndex in Enumerable.Range(0, result.Count))
            {
                var controller = result[controllerIndex];
                var diagnostics = controller.MemoryDiagnostics ?? [];
                if (!diagnostics.Any(note => note.Contains("DXGI adapter", StringComparison.OrdinalIgnoreCase)))
                {
                    result[controllerIndex] = controller with
                    {
                        MemoryDiagnostics = [.. diagnostics, "One or more DXGI adapters could not be uniquely associated with WMI records; unmatched adapters were omitted from the device list."]
                    };
                }
            }
        }
        return result;
    }

    private static VideoControllerSnapshot WithMemory(VideoControllerSnapshot controller, DxgiAdapterInfo adapter,
        IReadOnlyList<string> diagnostics) => controller with
    {
        DedicatedVideoMemoryBytes = adapter.DedicatedVideoMemoryBytes,
        DedicatedSystemMemoryBytes = adapter.DedicatedSystemMemoryBytes,
        SharedSystemMemoryBytes = adapter.SharedSystemMemoryBytes,
        MemorySource = GraphicsMemorySource.Dxgi,
        IsEstimated = false,
        AdapterLuid = adapter.Luid,
        MemoryDiagnostics = diagnostics
    };

    private static bool Matches(VideoControllerSnapshot controller, DxgiAdapterInfo adapter)
    {
        if (!controller.PnpDeviceId.StartsWith("PCI\\", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return PciValue(controller.PnpDeviceId, "VEN", 4) == adapter.VendorId
            && PciValue(controller.PnpDeviceId, "DEV", 4) == adapter.DeviceId
            && (PciValue(controller.PnpDeviceId, "SUBSYS", 8) is not { } subsystem || subsystem == adapter.SubSystemId)
            && (PciValue(controller.PnpDeviceId, "REV", 2) is not { } revision || revision == adapter.Revision);
    }

    private static uint? PciValue(string value, string key, int digits)
    {
        var match = Regex.Match(value, $@"(?:\\|&){key}_([0-9A-F]{{{digits}}})(?=&|\\|$)", RegexOptions.IgnoreCase);
        return match.Success ? uint.Parse(match.Groups[1].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture) : null;
    }
}
