using System.Globalization;
using System.Text.RegularExpressions;

namespace HwScope.Core.Hardware.DeviceTopology.Pci;

internal static partial class PciTopologyBuilder
{
    public static PciTopologySnapshot Build(
        IReadOnlyList<PciDeviceRecord> source,
        IReadOnlyList<DeviceTopologyDiagnostic>? sourceDiagnostics = null,
        DateTimeOffset? generatedAt = null)
    {
        var diagnostics = sourceDiagnostics?.ToList() ?? [];
        var records = new Dictionary<string, PciDeviceRecord>(StringComparer.OrdinalIgnoreCase);
        foreach (var record in source.OrderBy(item => item.InstanceId, StringComparer.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(record.InstanceId))
            {
                diagnostics.Add(new DeviceTopologyDiagnostic(
                    DeviceTopologyDiagnosticSeverity.Warning,
                    "pci.empty-instance-id",
                    "Windows returned a PCI device without an instance ID."));
                continue;
            }

            if (!records.TryAdd(record.InstanceId, record))
            {
                diagnostics.Add(new DeviceTopologyDiagnostic(
                    DeviceTopologyDiagnosticSeverity.Warning,
                    "pci.duplicate-instance-id",
                    $"Duplicate PCI instance ID was ignored: {record.InstanceId}",
                    BuildNodeId(record.InstanceId)));
            }
        }

        AddSyntheticRoots(records);

        var parentByInstance = records.Values.ToDictionary(
            record => record.InstanceId,
            record => ResolveParent(record, records),
            StringComparer.OrdinalIgnoreCase);
        BreakCycles(parentByInstance, diagnostics);

        var childrenByNode = records.Keys.ToDictionary(
            BuildNodeId,
            _ => new List<string>(),
            StringComparer.OrdinalIgnoreCase);
        foreach (var (instanceId, parentInstanceId) in parentByInstance)
        {
            if (parentInstanceId is not null)
            {
                childrenByNode[BuildNodeId(parentInstanceId)].Add(BuildNodeId(instanceId));
            }
        }

        foreach (var children in childrenByNode.Values)
        {
            children.Sort((left, right) => CompareRecords(records[left[4..]], records[right[4..]]));
        }

        var nodes = records.Values
            .Select(record => BuildNode(record, parentByInstance[record.InstanceId], childrenByNode[BuildNodeId(record.InstanceId)], diagnostics))
            .OrderBy(node => node.Address?.Bus ?? byte.MaxValue)
            .ThenBy(node => node.Address?.Device ?? byte.MaxValue)
            .ThenBy(node => node.Address?.Function ?? byte.MaxValue)
            .ThenBy(node => node.Identity.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var roots = nodes.Where(node => node.ParentNodeId is null).Select(node => node.NodeId).ToList();

        return new PciTopologySnapshot(nodes, roots, new DeviceTopologyDiagnostics(diagnostics), generatedAt ?? DateTimeOffset.Now);
    }

    public static string BuildNodeId(string instanceId)
    {
        if (TryParseSyntheticRootInstanceId(instanceId, out var rootIndex))
        {
            return $"pci-root:{rootIndex:x}";
        }

        return $"pci:{instanceId.Trim().ToLowerInvariant()}";
    }

    private static PciTopologyNode BuildNode(
        PciDeviceRecord record,
        string? parentInstanceId,
        IReadOnlyList<string> childNodeIds,
        ICollection<DeviceTopologyDiagnostic> diagnostics)
    {
        var nodeId = BuildNodeId(record.InstanceId);
        var rawAddressValid = PciAddressParser.TryDecode(record.BusNumber, record.DeviceAddress, out var address);
        var locationAddressValid = PciAddressParser.TryParseLastLocationSegment(record.LocationPaths, out var locationDevice, out var locationFunction);
        if (rawAddressValid && locationAddressValid && address is not null
            && (address.Device != locationDevice || address.Function != locationFunction))
        {
            diagnostics.Add(new DeviceTopologyDiagnostic(
                DeviceTopologyDiagnosticSeverity.Warning,
                "pci.address-location-conflict",
                $"Device address {address.Device:X2}.{address.Function} conflicts with Location Path {locationDevice:X2}.{locationFunction}.",
                nodeId));
        }
        else if (!rawAddressValid && record.BusNumber is <= byte.MaxValue && locationAddressValid)
        {
            address = new PciAddress(null, (byte)record.BusNumber.Value, locationDevice, locationFunction);
            diagnostics.Add(new DeviceTopologyDiagnostic(
                DeviceTopologyDiagnosticSeverity.Information,
                "pci.address-location-fallback",
                "Device/function was derived from Location Path because DEVPKEY_Device_Address was unavailable or invalid.",
                nodeId));
        }
        else if (!rawAddressValid && record.DeviceAddress.HasValue)
        {
            diagnostics.Add(new DeviceTopologyDiagnostic(
                DeviceTopologyDiagnosticSeverity.Warning,
                "pci.invalid-address",
                $"Invalid DEVPKEY_Device_Address value: {record.DeviceAddress.Value.ToString(CultureInfo.InvariantCulture)}.",
                nodeId));
        }

        if (record.IsSyntheticRoot)
        {
            return BuildSyntheticRootNode(record, childNodeIds);
        }

        var deviceType = record.DeviceType is <= 14
            ? (PciDeviceType)(int)record.DeviceType.Value
            : PciDeviceType.Unknown;
        var baseClass = ToByte(record.BaseClass);
        var subClass = ToByte(record.SubClass);
        var programmingInterface = ToByte(record.ProgrammingInterface);
        var kind = ClassifyKind(deviceType, baseClass, parentInstanceId);
        var displayName = FirstUseful(record.DisplayName, record.DeviceDescription, record.InstanceId);

        return new PciTopologyNode(
            nodeId,
            parentInstanceId is null ? null : BuildNodeId(parentInstanceId),
            childNodeIds,
            new PnpDeviceIdentity(
                nodeId,
                record.InstanceId,
                displayName,
                Clean(record.DeviceDescription),
                Clean(record.Manufacturer),
                record.ClassGuid,
                record.ContainerId,
                record.HardwareIds,
                record.CompatibleIds,
                record.LocationPaths,
                "PCI",
                Clean(record.Service),
                new DeviceNodeStatus(record.DevNodeStatus, record.ProblemCode)),
            kind,
            deviceType,
            address,
            record.BusNumber,
            record.DeviceAddress,
            ParseIdentity(record.HardwareIds),
            new PciClassInfo(baseClass, subClass, programmingInterface, FormatClass(baseClass, subClass, programmingInterface)),
            new PciLinkInfo(
                FormatGeneration(record.CurrentLinkSpeed),
                FormatWidth(record.CurrentLinkWidth),
                FormatGeneration(record.MaximumLinkSpeed),
                FormatWidth(record.MaximumLinkWidth),
                FormatPayload(record.CurrentPayloadSize),
                FormatPayload(record.MaximumPayloadSize),
                FormatPayload(record.MaximumReadRequestSize)),
            new PciCapabilityInfo(
                FormatUnsigned(record.ExpressSpecVersion),
                FormatBoolean(record.AerCapabilityPresent),
                FormatUnsigned(record.InterruptSupport),
                FormatUnsigned(record.InterruptMessageMaximum),
                FormatUnsigned(record.BarTypes)),
            new PciDriverInfo(
                Clean(record.DriverDescription),
                Clean(record.DriverProvider),
                Clean(record.DriverVersion),
                Clean(record.DriverInfPath),
                Clean(record.Service)));
    }

    private static void BreakCycles(
        IDictionary<string, string?> parentByInstance,
        ICollection<DeviceTopologyDiagnostic> diagnostics)
    {
        var globallyVisited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var start in parentByInstance.Keys.OrderBy(value => value, StringComparer.OrdinalIgnoreCase))
        {
            if (globallyVisited.Contains(start))
            {
                continue;
            }

            var path = new List<string>();
            var indexes = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            string? current = start;
            while (current is not null && parentByInstance.ContainsKey(current))
            {
                if (indexes.TryGetValue(current, out var cycleStart))
                {
                    var cycle = path.Skip(cycleStart).OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToList();
                    var detached = cycle[0];
                    parentByInstance[detached] = null;
                    diagnostics.Add(new DeviceTopologyDiagnostic(
                        DeviceTopologyDiagnosticSeverity.Error,
                        "pci.parent-cycle",
                        $"A parent cycle was detected and detached at {detached}.",
                        BuildNodeId(detached)));
                    break;
                }

                if (globallyVisited.Contains(current))
                {
                    break;
                }

                indexes[current] = path.Count;
                path.Add(current);
                current = parentByInstance[current];
            }

            globallyVisited.UnionWith(path);
        }
    }

    private static bool IsBridge(PciDeviceType deviceType, byte? baseClass)
    {
        return deviceType is >= PciDeviceType.PciConventionalBridge and <= PciDeviceType.PciExpressBridgeTreatedAsPci
            || baseClass == 0x06;
    }

    private static PciTopologyNodeKind ClassifyKind(
        PciDeviceType deviceType,
        byte? baseClass,
        string? parentInstanceId)
    {
        if (IsBridge(deviceType, baseClass))
        {
            return parentInstanceId is null ? PciTopologyNodeKind.Root : PciTopologyNodeKind.Bridge;
        }

        if (deviceType is >= PciDeviceType.PciConventional and <= PciDeviceType.PciExpressTreatedAsPci
            || deviceType == PciDeviceType.PciExpressEventCollector
            || baseClass.HasValue)
        {
            return PciTopologyNodeKind.Endpoint;
        }

        return PciTopologyNodeKind.Unknown;
    }

    private static void AddSyntheticRoots(IDictionary<string, PciDeviceRecord> records)
    {
        var rootIndexes = records.Values
            .Where(record => !records.ContainsKey(record.ParentInstanceId ?? string.Empty))
            .Select(record => PciAddressParser.TryParseRootIndex(record.LocationPaths, out var rootIndex) ? (uint?)rootIndex : null)
            .Where(rootIndex => rootIndex.HasValue)
            .Select(rootIndex => rootIndex!.Value)
            .Distinct()
            .OrderBy(rootIndex => rootIndex)
            .ToList();

        foreach (var rootIndex in rootIndexes)
        {
            var instanceId = BuildSyntheticRootInstanceId(rootIndex);
            records.TryAdd(instanceId, CreateSyntheticRootRecord(instanceId, rootIndex));
        }
    }

    private static string? ResolveParent(PciDeviceRecord record, IReadOnlyDictionary<string, PciDeviceRecord> records)
    {
        if (record.IsSyntheticRoot)
        {
            return null;
        }

        if (records.ContainsKey(record.ParentInstanceId ?? string.Empty))
        {
            return record.ParentInstanceId;
        }

        return PciAddressParser.TryParseRootIndex(record.LocationPaths, out var rootIndex)
            ? BuildSyntheticRootInstanceId(rootIndex)
            : null;
    }

    private static PciDeviceRecord CreateSyntheticRootRecord(string instanceId, uint rootIndex)
    {
        return new PciDeviceRecord(
            instanceId,
            null,
            $"PCI Root {rootIndex:X}",
            "Synthetic PCI root derived from Windows Location Path",
            "Windows",
            null,
            null,
            [],
            [],
            [$"PCIROOT({rootIndex:X})"],
            string.Empty,
            0,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            string.Empty,
            string.Empty,
            string.Empty,
            string.Empty,
            IsSyntheticRoot: true,
            RootIndex: rootIndex);
    }

    private static PciTopologyNode BuildSyntheticRootNode(PciDeviceRecord record, IReadOnlyList<string> childNodeIds)
    {
        var nodeId = BuildNodeId(record.InstanceId);
        return new PciTopologyNode(
            nodeId,
            null,
            childNodeIds,
            new PnpDeviceIdentity(
                nodeId,
                record.InstanceId,
                record.DisplayName,
                record.DeviceDescription,
                record.Manufacturer,
                null,
                null,
                [],
                [],
                record.LocationPaths,
                "PCIROOT",
                string.Empty,
                new DeviceNodeStatus(0, null)),
            PciTopologyNodeKind.Root,
            PciDeviceType.Unknown,
            null,
            null,
            null,
            new PciIdentity(string.Empty, string.Empty, string.Empty, string.Empty),
            new PciClassInfo(0x06, 0x00, 0x00, "PCI Root Bus"),
            new PciLinkInfo(
                TopologyFieldValue<uint>.Unavailable(),
                TopologyFieldValue<uint>.Unavailable(),
                TopologyFieldValue<uint>.Unavailable(),
                TopologyFieldValue<uint>.Unavailable(),
                TopologyFieldValue<uint>.Unavailable(),
                TopologyFieldValue<uint>.Unavailable(),
                TopologyFieldValue<uint>.Unavailable()),
            new PciCapabilityInfo(
                TopologyFieldValue<uint>.Unavailable(),
                TopologyFieldValue<bool>.Unavailable(),
                TopologyFieldValue<uint>.Unavailable(),
                TopologyFieldValue<uint>.Unavailable(),
                TopologyFieldValue<uint>.Unavailable()),
            new PciDriverInfo(string.Empty, string.Empty, string.Empty, string.Empty, string.Empty));
    }

    private static string BuildSyntheticRootInstanceId(uint rootIndex) => $"PCIROOT({rootIndex:X})";

    private static bool TryParseSyntheticRootInstanceId(string instanceId, out uint rootIndex)
    {
        rootIndex = 0;
        const string prefix = "PCIROOT(";
        return instanceId.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            && instanceId.EndsWith(')')
            && uint.TryParse(instanceId[prefix.Length..^1], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out rootIndex);
    }

    private static PciIdentity ParseIdentity(IEnumerable<string> hardwareIds)
    {
        var joined = string.Join(' ', hardwareIds);
        return new PciIdentity(
            MatchValue(joined, VendorRegex()),
            MatchValue(joined, DeviceRegex()),
            MatchValue(joined, SubsystemRegex()),
            MatchValue(joined, RevisionRegex()));
    }

    private static string MatchValue(string value, Regex regex)
    {
        var match = regex.Match(value);
        return match.Success ? match.Groups[1].Value.ToUpperInvariant() : string.Empty;
    }

    private static TopologyFieldValue<uint> FormatGeneration(uint? raw)
    {
        if (raw is >= 1 and <= 6)
        {
            var transfers = raw.Value switch
            {
                1 => "2.5 GT/s",
                2 => "5.0 GT/s",
                3 => "8.0 GT/s",
                4 => "16.0 GT/s",
                5 => "32.0 GT/s",
                6 => "64.0 GT/s",
                _ => string.Empty
            };
            return Available(raw.Value, $"PCIe Gen {raw.Value} ({transfers})");
        }

        return raw.HasValue
            ? new TopologyFieldValue<uint>(raw.Value, $"未知 (raw {raw.Value})", DeviceTopologyDataSource.PciBusDriver, TopologyFieldAvailability.Available, Note: "Windows returned an unrecognized link-speed encoding.")
            : TopologyFieldValue<uint>.Unavailable();
    }

    private static TopologyFieldValue<uint> FormatWidth(uint? raw)
    {
        return raw is 1 or 2 or 4 or 8 or 12 or 16 or 32
            ? Available(raw.Value, $"x{raw.Value}")
            : raw.HasValue
                ? new TopologyFieldValue<uint>(raw.Value, $"未知 (raw {raw.Value})", DeviceTopologyDataSource.PciBusDriver, TopologyFieldAvailability.Available, Note: "Windows returned an unrecognized link-width encoding.")
                : TopologyFieldValue<uint>.Unavailable();
    }

    private static TopologyFieldValue<uint> FormatPayload(uint? raw)
    {
        if (raw is <= 5)
        {
            var bytes = 128u << (int)raw.Value;
            return Available(bytes, $"{bytes} bytes", note: $"raw encoding {raw.Value}");
        }

        return raw.HasValue
            ? new TopologyFieldValue<uint>(raw.Value, $"未知 (raw {raw.Value})", DeviceTopologyDataSource.PciBusDriver, TopologyFieldAvailability.Available)
            : TopologyFieldValue<uint>.Unavailable();
    }

    private static TopologyFieldValue<uint> FormatUnsigned(uint? raw)
    {
        return raw.HasValue ? Available(raw.Value, raw.Value.ToString(CultureInfo.InvariantCulture)) : TopologyFieldValue<uint>.Unavailable();
    }

    private static TopologyFieldValue<bool> FormatBoolean(bool? raw)
    {
        return raw.HasValue
            ? new TopologyFieldValue<bool>(raw.Value, raw.Value ? "是" : "否", DeviceTopologyDataSource.PciBusDriver, TopologyFieldAvailability.Available)
            : TopologyFieldValue<bool>.Unavailable();
    }

    private static TopologyFieldValue<uint> Available(uint value, string display, string? note = null)
    {
        return new TopologyFieldValue<uint>(value, display, DeviceTopologyDataSource.PciBusDriver, TopologyFieldAvailability.Available, Note: note);
    }

    private static string FormatClass(byte? baseClass, byte? subClass, byte? programmingInterface)
    {
        if (!baseClass.HasValue)
        {
            return "未报告";
        }

        var name = baseClass.Value switch
        {
            0x01 => "Mass Storage Controller",
            0x02 => "Network Controller",
            0x03 => "Display Controller",
            0x04 => "Multimedia Controller",
            0x05 => "Memory Controller",
            0x06 => "Bridge Device",
            0x07 => "Communication Controller",
            0x08 => "System Peripheral",
            0x09 => "Input Device Controller",
            0x0c when subClass == 0x03 && programmingInterface == 0x30 => "USB xHCI Controller",
            0x0c when subClass == 0x03 => "USB Controller",
            0x0c => "Serial Bus Controller",
            0x10 => "Encryption Controller",
            0x12 => "Processing Accelerator",
            _ => "PCI Device"
        };
        return $"{name} ({baseClass.Value:X2}{subClass.GetValueOrDefault():X2}{programmingInterface.GetValueOrDefault():X2})";
    }

    private static byte? ToByte(uint? value) => value is <= byte.MaxValue ? (byte)value.Value : null;

    private static int CompareRecords(PciDeviceRecord left, PciDeviceRecord right)
    {
        var bus = Nullable.Compare(left.BusNumber, right.BusNumber);
        if (bus != 0)
        {
            return bus;
        }

        var address = Nullable.Compare(left.DeviceAddress, right.DeviceAddress);
        return address != 0 ? address : StringComparer.OrdinalIgnoreCase.Compare(left.InstanceId, right.InstanceId);
    }

    private static string FirstUseful(params string?[] values) => values.Select(Clean).FirstOrDefault(value => value.Length > 0) ?? string.Empty;

    private static string Clean(string? value) => string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim().Trim('\0');

    [GeneratedRegex(@"VEN_([0-9A-Fa-f]{4})", RegexOptions.CultureInvariant)]
    private static partial Regex VendorRegex();

    [GeneratedRegex(@"DEV_([0-9A-Fa-f]{4})", RegexOptions.CultureInvariant)]
    private static partial Regex DeviceRegex();

    [GeneratedRegex(@"SUBSYS_([0-9A-Fa-f]{8})", RegexOptions.CultureInvariant)]
    private static partial Regex SubsystemRegex();

    [GeneratedRegex(@"REV_([0-9A-Fa-f]{2})", RegexOptions.CultureInvariant)]
    private static partial Regex RevisionRegex();
}
