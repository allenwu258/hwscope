using HwScope.Core.Windows.Devices;
using HwScope.Core.Windows.Usb;

namespace HwScope.Core.Hardware.DeviceTopology.Usb;

internal sealed class UsbWindowsDeviceSource : IUsbDeviceSource
{
    private const int MaxControllers = 32;
    private const int MaxDepth = 16;
    private const int MaxPortsPerHub = 255;
    private const int MaxTotalNodes = 10_000;

    private static readonly Guid HostControllerInterface = new("3abf6f2d-71c4-462a-8a92-1e6861e6af27");
    private static readonly Guid DevicePropertyNamespace = new("a45c254e-df1c-4efd-8020-67d146a850e0");
    private static readonly Guid DeviceRelationsNamespace = new("4340a6c5-93fa-4706-972c-7b648008a5a7");
    private static readonly Guid DeviceContainerNamespace = new("8c7ed206-3f8a-4827-b3ab-ae9e1faefc6c");

    private static readonly DevicePropertyKey DeviceDescription = Device(2);
    private static readonly DevicePropertyKey HardwareIds = Device(3);
    private static readonly DevicePropertyKey CompatibleIds = Device(4);
    private static readonly DevicePropertyKey Service = Device(6);
    private static readonly DevicePropertyKey Driver = Device(11);
    private static readonly DevicePropertyKey ClassGuid = Device(10);
    private static readonly DevicePropertyKey Manufacturer = Device(13);
    private static readonly DevicePropertyKey FriendlyName = Device(14);
    private static readonly DevicePropertyKey LocationPaths = Device(37);
    private static readonly DevicePropertyKey DevNodeStatus = new(DeviceRelationsNamespace, 2);
    private static readonly DevicePropertyKey ProblemCode = new(DeviceRelationsNamespace, 3);
    private static readonly DevicePropertyKey ContainerId = new(DeviceContainerNamespace, 2);

    private static readonly IReadOnlyList<DevicePropertyRequest> Requests =
    [
        Request(nameof(DeviceDescription), DeviceDescription),
        Request(nameof(HardwareIds), HardwareIds),
        Request(nameof(CompatibleIds), CompatibleIds),
        Request(nameof(Service), Service),
        Request(nameof(Driver), Driver),
        Request(nameof(ClassGuid), ClassGuid),
        Request(nameof(Manufacturer), Manufacturer),
        Request(nameof(FriendlyName), FriendlyName),
        Request(nameof(LocationPaths), LocationPaths),
        Request(nameof(DevNodeStatus), DevNodeStatus),
        Request(nameof(ProblemCode), ProblemCode),
        Request(nameof(ContainerId), ContainerId)
    ];

    private readonly SetupApiDeviceEnumerator _deviceEnumerator = new();
    private readonly SetupApiDeviceInterfaceEnumerator _interfaceEnumerator = new();

    public UsbDeviceSourceResult ReadPresentTopology()
    {
        var diagnostics = new List<DeviceTopologyDiagnostic>();
        var pnpResult = _deviceEnumerator.EnumeratePresentDevices("USB", Requests);
        AddNativeDiagnostics(pnpResult.Diagnostics, diagnostics);

        var identitiesByDriver = pnpResult.Devices
            .Select(device => new
            {
                Driver = DevicePropertyValueReader.GetString(device.Properties, Driver),
                Identity = ToIdentity(device.InstanceId, device.Properties)
            })
            .Where(item => !string.IsNullOrWhiteSpace(item.Driver))
            .GroupBy(item => item.Driver!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First().Identity, StringComparer.OrdinalIgnoreCase);

        var controllersResult = _interfaceEnumerator.EnumeratePresentInterfaces(HostControllerInterface, Requests);
        AddNativeDiagnostics(controllersResult.Diagnostics, diagnostics);

        var controllers = new List<UsbControllerRecord>();
        var nodeBudget = Math.Min(controllersResult.Interfaces.Count, MaxControllers);
        foreach (var controller in controllersResult.Interfaces.Take(MaxControllers))
        {
            var identity = ToIdentity(controller.InstanceId, controller.Properties);
            UsbHubRecord? rootHub = null;
            try
            {
                var rootHubName = UsbHubIoControl.QueryRootHubName(controller.DevicePath);
                var visitedHubs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                rootHub = ReadHub(
                    rootHubName,
                    isRoot: true,
                    depth: 0,
                    controller.InstanceId,
                    portChainPrefix: string.Empty,
                    identitiesByDriver,
                    visitedHubs,
                    diagnostics,
                    ref nodeBudget);
            }
            catch (Exception ex) when (IsExpectedNativeFailure(ex))
            {
                diagnostics.Add(Diagnostic(
                    DeviceTopologyDiagnosticSeverity.Error,
                    "usb.controller-enumeration-failed",
                    $"Host controller {identity.DisplayName} could not be enumerated: {RootMessage(ex)}.",
                    ControllerNodeId(controller.InstanceId)));
            }

            controllers.Add(new UsbControllerRecord(controller.DevicePath, identity, rootHub));
        }

        if (controllersResult.Interfaces.Count > MaxControllers)
        {
            diagnostics.Add(Diagnostic(
                DeviceTopologyDiagnosticSeverity.Error,
                "usb.controller-limit",
                $"Windows reported {controllersResult.Interfaces.Count} USB host controllers; only the first {MaxControllers} were read."));
        }

        return new UsbDeviceSourceResult(controllers, diagnostics);
    }

    private static UsbHubRecord ReadHub(
        string symbolicName,
        bool isRoot,
        int depth,
        string controllerInstanceId,
        string portChainPrefix,
        IReadOnlyDictionary<string, PnpDeviceIdentity> identitiesByDriver,
        ISet<string> visitedHubs,
        ICollection<DeviceTopologyDiagnostic> diagnostics,
        ref int nodeBudget)
    {
        if (depth > MaxDepth)
        {
            throw new InvalidDataException($"USB hub nesting exceeds the maximum depth of {MaxDepth}.");
        }

        if (++nodeBudget > MaxTotalNodes)
        {
            throw new InvalidDataException($"USB topology exceeds the maximum node count of {MaxTotalNodes}.");
        }

        var hubKey = NormalizeHubKey(symbolicName);
        if (!visitedHubs.Add(hubKey))
        {
            throw new InvalidDataException($"USB hub cycle detected for {symbolicName}.");
        }

        try
        {
            using var hub = UsbHubIoControl.OpenHub(symbolicName);
            var hubInfo = hub.QueryHubInformation();
            var portCount = Math.Min(hubInfo.PortCount, MaxPortsPerHub);
            if (hubInfo.PortCount > MaxPortsPerHub)
            {
                diagnostics.Add(Diagnostic(
                    DeviceTopologyDiagnosticSeverity.Warning,
                    "usb.hub-port-limit",
                    $"Hub {symbolicName} reported {hubInfo.PortCount} ports; only {MaxPortsPerHub} were read."));
            }

            var ports = new List<UsbPortRecord>(portCount);
            for (var portNumber = 1; portNumber <= portCount; portNumber++)
            {
                var portChain = string.IsNullOrEmpty(portChainPrefix)
                    ? portNumber.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    : $"{portChainPrefix}.{portNumber}";
                ports.Add(ReadPort(
                    hub,
                    portNumber,
                    portChain,
                    depth,
                    controllerInstanceId,
                    identitiesByDriver,
                    visitedHubs,
                    diagnostics,
                    ref nodeBudget));
            }

            return new UsbHubRecord(symbolicName, hubInfo.PortCount, hubInfo.IsBusPowered, isRoot, ports);
        }
        finally
        {
            visitedHubs.Remove(hubKey);
        }
    }

    private static UsbPortRecord ReadPort(
        UsbHubIoControl hub,
        int portNumber,
        string portChain,
        int hubDepth,
        string controllerInstanceId,
        IReadOnlyDictionary<string, PnpDeviceIdentity> identitiesByDriver,
        ISet<string> visitedHubs,
        ICollection<DeviceTopologyDiagnostic> diagnostics,
        ref int nodeBudget)
    {
        if (++nodeBudget > MaxTotalNodes)
        {
            throw new InvalidDataException($"USB topology exceeds the maximum node count of {MaxTotalNodes}.");
        }

        UsbNativePortConnection connection;
        try
        {
            connection = hub.QueryConnection(portNumber);
        }
        catch (Exception ex) when (IsExpectedNativeFailure(ex))
        {
            diagnostics.Add(PortDiagnostic(
                "usb.port-query-failed",
                $"Port {portChain} could not be queried: {RootMessage(ex)}.",
                controllerInstanceId,
                portChain));
            return EmptyPort(portNumber, UsbConnectionStatus.Unknown);
        }

        var connectionStatus = ToConnectionStatus(connection.ConnectionStatus);
        if (connectionStatus is not UsbConnectionStatus.NoDeviceConnected and not UsbConnectionStatus.Unknown
            && ++nodeBudget > MaxTotalNodes)
        {
            throw new InvalidDataException($"USB topology exceeds the maximum node count of {MaxTotalNodes}.");
        }
        UsbNativeConnectionV2? connectionV2 = null;
        UsbNativeConnectorProperties? connector = null;
        string driverKey = string.Empty;

        TryOptional(
            () => connectionV2 = hub.TryQueryConnectionV2(portNumber),
            "usb.port-v2-query-failed",
            portChain,
            controllerInstanceId,
            diagnostics);
        TryOptional(
            () => connector = hub.TryQueryConnectorProperties(portNumber),
            "usb.port-connector-query-failed",
            portChain,
            controllerInstanceId,
            diagnostics);

        if (connectionStatus != UsbConnectionStatus.NoDeviceConnected)
        {
            TryOptional(
                () => driverKey = hub.TryQueryDriverKey(portNumber),
                "usb.port-driver-key-query-failed",
                portChain,
                controllerInstanceId,
                diagnostics);
        }

        identitiesByDriver.TryGetValue(driverKey, out var identity);
        UsbHubRecord? downstreamHub = null;
        if (connection.DeviceIsHub && connectionStatus != UsbConnectionStatus.NoDeviceConnected)
        {
            string downstreamName = string.Empty;
            TryOptional(
                () => downstreamName = hub.TryQueryConnectionName(portNumber),
                "usb.hub-name-query-failed",
                portChain,
                controllerInstanceId,
                diagnostics);
            if (!string.IsNullOrWhiteSpace(downstreamName))
            {
                try
                {
                    downstreamHub = ReadHub(
                        downstreamName,
                        isRoot: false,
                        depth: hubDepth + 1,
                        controllerInstanceId,
                        portChain,
                        identitiesByDriver,
                        visitedHubs,
                        diagnostics,
                        ref nodeBudget);
                }
                catch (Exception ex) when (IsExpectedNativeFailure(ex))
                {
                    diagnostics.Add(PortDiagnostic(
                        "usb.downstream-hub-enumeration-failed",
                        $"Hub on port {portChain} could not be enumerated: {RootMessage(ex)}.",
                        controllerInstanceId,
                        portChain));
                }
            }
        }

        var v2Flags = connectionV2?.Flags ?? 0;
        var connectorFlags = connector?.Properties ?? 0;
        return new UsbPortRecord(
            portNumber,
            connectionStatus,
            connectionStatus == UsbConnectionStatus.NoDeviceConnected
                ? UsbConnectionSpeed.Unknown
                : ToSpeed(connection.Speed, v2Flags),
            ToProtocols(connectionV2?.SupportedProtocols),
            connection.DeviceIsHub,
            connection.DeviceAddress,
            connection.OpenPipeCount,
            ToDescriptor(connection.DeviceDescriptor),
            driverKey,
            (connectorFlags & 0x1) != 0,
            (connectorFlags & 0x2) != 0,
            (connectorFlags & 0x8) != 0,
            connector?.CompanionPortNumber is > 0 ? connector.CompanionPortNumber : null,
            connector?.CompanionHubSymbolicName ?? string.Empty,
            (v2Flags & 0x2) != 0,
            (v2Flags & 0x1) != 0,
            (v2Flags & 0x8) != 0,
            (v2Flags & 0x4) != 0,
            identity,
            downstreamHub);
    }

    private static UsbPortRecord EmptyPort(int portNumber, UsbConnectionStatus status)
    {
        return new UsbPortRecord(
            portNumber,
            status,
            UsbConnectionSpeed.Unknown,
            UsbSupportedProtocols.None,
            false,
            0,
            0,
            null,
            string.Empty,
            false,
            false,
            false,
            null,
            string.Empty,
            false,
            false,
            false,
            false,
            null,
            null);
    }

    private static void TryOptional(
        Action query,
        string code,
        string portChain,
        string controllerInstanceId,
        ICollection<DeviceTopologyDiagnostic> diagnostics)
    {
        try
        {
            query();
        }
        catch (Exception ex) when (IsExpectedNativeFailure(ex))
        {
            diagnostics.Add(PortDiagnostic(
                code,
                $"Optional USB data for port {portChain} is unavailable: {RootMessage(ex)}.",
                controllerInstanceId,
                portChain,
                DeviceTopologyDiagnosticSeverity.Information));
        }
    }

    private static PnpDeviceIdentity ToIdentity(
        string instanceId,
        IReadOnlyDictionary<DevicePropertyKey, NativeDeviceProperty> properties)
    {
        var description = DevicePropertyValueReader.GetString(properties, DeviceDescription) ?? string.Empty;
        var displayName = DevicePropertyValueReader.GetString(properties, FriendlyName) ?? description;
        var enumeratorSeparator = instanceId.IndexOf('\\');
        var enumerator = enumeratorSeparator > 0 ? instanceId[..enumeratorSeparator] : string.Empty;
        var stableId = $"pnp:{instanceId.Trim().ToLowerInvariant()}";
        return new PnpDeviceIdentity(
            stableId,
            instanceId,
            string.IsNullOrWhiteSpace(displayName) ? instanceId : displayName,
            description,
            DevicePropertyValueReader.GetString(properties, Manufacturer) ?? string.Empty,
            DevicePropertyValueReader.GetGuid(properties, ClassGuid),
            DevicePropertyValueReader.GetGuid(properties, ContainerId),
            DevicePropertyValueReader.GetStringList(properties, HardwareIds),
            DevicePropertyValueReader.GetStringList(properties, CompatibleIds),
            DevicePropertyValueReader.GetStringList(properties, LocationPaths),
            enumerator,
            DevicePropertyValueReader.GetString(properties, Service) ?? string.Empty,
            new DeviceNodeStatus(
                DevicePropertyValueReader.GetUInt32(properties, DevNodeStatus) ?? 0,
                DevicePropertyValueReader.GetUInt32(properties, ProblemCode)));
    }

    private static UsbDeviceDescriptorInfo? ToDescriptor(UsbNativeDeviceDescriptor? descriptor)
    {
        return descriptor is null
            ? null
            : new UsbDeviceDescriptorInfo(
                descriptor.Length,
                descriptor.DescriptorType,
                descriptor.UsbVersionBcd,
                descriptor.DeviceClass,
                descriptor.DeviceSubClass,
                descriptor.DeviceProtocol,
                descriptor.MaximumPacketSize0,
                descriptor.VendorId,
                descriptor.ProductId,
                descriptor.DeviceVersionBcd,
                descriptor.ManufacturerStringIndex,
                descriptor.ProductStringIndex,
                descriptor.SerialNumberStringIndex,
                descriptor.ConfigurationCount);
    }

    private static UsbConnectionStatus ToConnectionStatus(int value)
    {
        return Enum.IsDefined(typeof(UsbConnectionStatus), value)
            ? (UsbConnectionStatus)value
            : UsbConnectionStatus.Unknown;
    }

    private static UsbConnectionSpeed ToSpeed(byte speed, uint flags)
    {
        if ((flags & 0x4) != 0)
        {
            return UsbConnectionSpeed.SuperPlus;
        }

        return speed switch
        {
            0 => UsbConnectionSpeed.Low,
            1 => UsbConnectionSpeed.Full,
            2 => UsbConnectionSpeed.High,
            3 => UsbConnectionSpeed.Super,
            _ => UsbConnectionSpeed.Unknown
        };
    }

    private static UsbSupportedProtocols ToProtocols(uint? value)
    {
        return value is null ? UsbSupportedProtocols.None : (UsbSupportedProtocols)(value.Value & 0x7);
    }

    private static bool IsExpectedNativeFailure(Exception ex)
    {
        return ex is System.ComponentModel.Win32Exception
            or InvalidDataException
            or IOException
            or UnauthorizedAccessException;
    }

    private static string RootMessage(Exception ex) => ex.GetBaseException().Message;

    private static string NormalizeHubKey(string value)
    {
        return value.Trim().TrimEnd('\0').Replace(@"\??\", @"\\?\", StringComparison.Ordinal).ToLowerInvariant();
    }

    private static void AddNativeDiagnostics(
        IEnumerable<NativeDeviceDiagnostic> source,
        ICollection<DeviceTopologyDiagnostic> destination)
    {
        foreach (var item in source)
        {
            destination.Add(Diagnostic(
                DeviceTopologyDiagnosticSeverity.Warning,
                item.Code,
                string.IsNullOrWhiteSpace(item.PropertyName) ? item.Message : $"{item.PropertyName}: {item.Message}"));
        }
    }

    private static DeviceTopologyDiagnostic PortDiagnostic(
        string code,
        string message,
        string controllerInstanceId,
        string portChain,
        DeviceTopologyDiagnosticSeverity severity = DeviceTopologyDiagnosticSeverity.Warning)
    {
        return Diagnostic(severity, code, message, $"usb-port:{NormalizeId(controllerInstanceId)}:{portChain}");
    }

    private static DeviceTopologyDiagnostic Diagnostic(
        DeviceTopologyDiagnosticSeverity severity,
        string code,
        string message,
        string? nodeId = null)
    {
        return new DeviceTopologyDiagnostic(severity, code, message, nodeId);
    }

    private static string ControllerNodeId(string instanceId) => $"usb-controller:{NormalizeId(instanceId)}";

    private static string NormalizeId(string value) => value.Trim().ToLowerInvariant();

    private static DevicePropertyRequest Request(string name, DevicePropertyKey key) => new(name, key);

    private static DevicePropertyKey Device(uint propertyId) => new(DevicePropertyNamespace, propertyId);
}
