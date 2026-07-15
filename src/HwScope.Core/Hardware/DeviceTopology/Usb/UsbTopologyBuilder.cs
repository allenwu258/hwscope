namespace HwScope.Core.Hardware.DeviceTopology.Usb;

internal static class UsbTopologyBuilder
{
    public static UsbTopologySnapshot Build(
        IReadOnlyList<UsbControllerRecord> controllers,
        IReadOnlyList<DeviceTopologyDiagnostic>? sourceDiagnostics = null,
        DateTimeOffset? generatedAt = null)
    {
        var nodes = new List<UsbTopologyNode>();
        var controllerIds = new List<string>();
        var diagnostics = sourceDiagnostics?.ToList() ?? [];
        var seenControllers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var usedNodeIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var controller in controllers)
        {
            var controllerId = BuildControllerNodeId(controller.Identity.InstanceId);
            if (!seenControllers.Add(controllerId))
            {
                diagnostics.Add(new DeviceTopologyDiagnostic(
                    DeviceTopologyDiagnosticSeverity.Warning,
                    "usb.duplicate-controller",
                    $"Duplicate USB host controller {controller.Identity.InstanceId} was ignored.",
                    controllerId));
                continue;
            }

            controllerIds.Add(controllerId);
            usedNodeIds.Add(controllerId);
            var controllerChildren = controller.RootHub is null
                ? Array.Empty<string>()
                : [BuildRootHubNodeId(controller.Identity.InstanceId)];
            nodes.Add(new UsbTopologyNode(
                controllerId,
                null,
                controllerChildren,
                UsbTopologyNodeKind.HostController,
                controller.Identity.DisplayName,
                controller.Identity with { StableId = controllerId },
                controllerId,
                controller.DevicePath,
                string.Empty,
                null,
                null,
                null));

            if (controller.RootHub is not null)
            {
                AddHub(
                    nodes,
                    controller.RootHub,
                    controllerId,
                    controller.Identity.InstanceId,
                    portChainPrefix: string.Empty,
                    isRoot: true,
                    diagnostics: diagnostics,
                    usedNodeIds: usedNodeIds);
            }
        }

        return new UsbTopologySnapshot(
            nodes,
            controllerIds,
            new DeviceTopologyDiagnostics(diagnostics),
            generatedAt ?? DateTimeOffset.Now);
    }

    internal static string BuildControllerNodeId(string controllerInstanceId)
    {
        return $"usb-controller:{Normalize(controllerInstanceId)}";
    }

    internal static string BuildRootHubNodeId(string controllerInstanceId)
    {
        return $"usb-root-hub:{Normalize(controllerInstanceId)}";
    }

    internal static string BuildPortNodeId(string controllerInstanceId, string portChain)
    {
        return $"usb-port:{Normalize(controllerInstanceId)}:{portChain}";
    }

    internal static string BuildDeviceNodeId(string controllerInstanceId, string portChain, bool isHub)
    {
        return $"{(isHub ? "usb-hub" : "usb-device")}:{Normalize(controllerInstanceId)}:{portChain}";
    }

    internal static string BuildPnpNodeId(string instanceId, bool isHub)
    {
        return $"{(isHub ? "usb-hub" : "usb-device")}:{Normalize(instanceId)}";
    }

    private static void AddHub(
        ICollection<UsbTopologyNode> nodes,
        UsbHubRecord hub,
        string controllerId,
        string controllerInstanceId,
        string portChainPrefix,
        bool isRoot,
        string? existingHubNodeId = null,
        ICollection<DeviceTopologyDiagnostic>? diagnostics = null,
        ISet<string>? usedNodeIds = null)
    {
        var hubId = isRoot
            ? BuildRootHubNodeId(controllerInstanceId)
            : existingHubNodeId ?? BuildDeviceNodeId(controllerInstanceId, portChainPrefix, isHub: true);
        var parentId = isRoot
            ? controllerId
            : BuildPortNodeId(controllerInstanceId, portChainPrefix);
        var childPortIds = hub.Ports
            .Select(port => BuildPortNodeId(
                controllerInstanceId,
                AppendPort(portChainPrefix, port.PortNumber)))
            .ToArray();

        if (isRoot)
        {
            usedNodeIds?.Add(hubId);
            nodes.Add(new UsbTopologyNode(
                hubId,
                parentId,
                childPortIds,
                UsbTopologyNodeKind.RootHub,
                "USB Root Hub",
                null,
                controllerId,
                hub.SymbolicName,
                string.Empty,
                new UsbHubInfo(hub.SymbolicName, hub.PortCount, hub.IsBusPowered, true),
                null,
                null));
        }

        foreach (var port in hub.Ports)
        {
            var portChain = AppendPort(portChainPrefix, port.PortNumber);
            var portId = BuildPortNodeId(controllerInstanceId, portChain);
            usedNodeIds?.Add(portId);
            var hasPhysicalNode = HasPhysicalNode(port);
            var candidatePhysicalId = hasPhysicalNode
                ? port.Identity is null
                    ? BuildDeviceNodeId(controllerInstanceId, portChain, port.DeviceIsHub)
                    : BuildPnpNodeId(port.Identity.InstanceId, port.DeviceIsHub)
                : null;
            var physicalId = candidatePhysicalId;
            if (physicalId is not null && usedNodeIds is not null && !usedNodeIds.Add(physicalId))
            {
                diagnostics?.Add(new DeviceTopologyDiagnostic(
                    DeviceTopologyDiagnosticSeverity.Warning,
                    "usb.duplicate-device-identity",
                    $"Multiple USB attachments resolved to the same device identity; port {portChain} uses its attachment identity.",
                    portId));
                physicalId = BuildDeviceNodeId(controllerInstanceId, portChain, port.DeviceIsHub);
                usedNodeIds.Add(physicalId);
            }
            nodes.Add(new UsbTopologyNode(
                portId,
                hubId,
                physicalId is null ? [] : [physicalId],
                UsbTopologyNodeKind.Port,
                $"Port {port.PortNumber}",
                null,
                controllerId,
                string.Empty,
                port.DriverKey,
                null,
                BuildPortInfo(port, portChain),
                null));

            if (!hasPhysicalNode)
            {
                continue;
            }

            var physicalChildren = port.DeviceIsHub && port.DownstreamHub is not null
                ? port.DownstreamHub.Ports
                    .Select(child => BuildPortNodeId(controllerInstanceId, AppendPort(portChain, child.PortNumber)))
                    .ToArray()
                : [];
            var displayName = BuildPhysicalDisplayName(port);
            nodes.Add(new UsbTopologyNode(
                physicalId!,
                portId,
                physicalChildren,
                port.DeviceIsHub ? UsbTopologyNodeKind.Hub : UsbTopologyNodeKind.Device,
                displayName,
                port.Identity,
                controllerId,
                port.DownstreamHub?.SymbolicName ?? string.Empty,
                port.DriverKey,
                port.DeviceIsHub && port.DownstreamHub is not null
                    ? new UsbHubInfo(
                        port.DownstreamHub.SymbolicName,
                        port.DownstreamHub.PortCount,
                        port.DownstreamHub.IsBusPowered,
                        false)
                    : null,
                BuildPortInfo(port, portChain),
                port.DeviceDescriptor,
                portId));

            if (port.DeviceIsHub && port.DownstreamHub is not null)
            {
                AddHub(
                    nodes,
                    port.DownstreamHub,
                    controllerId,
                    controllerInstanceId,
                    portChain,
                    isRoot: false,
                    physicalId,
                    diagnostics,
                    usedNodeIds);
            }
        }
    }

    private static UsbPortInfo BuildPortInfo(UsbPortRecord port, string portChain)
    {
        return new UsbPortInfo(
            port.PortNumber,
            portChain,
            port.ConnectionStatus,
            port.ConnectionSpeed,
            port.SupportedProtocols,
            port.IsDeviceSuperSpeedCapable,
            port.IsDeviceOperatingAtSuperSpeed,
            port.IsDeviceSuperSpeedPlusCapable,
            port.IsDeviceOperatingAtSuperSpeedPlus,
            port.IsUserConnectable,
            port.IsDebugCapable,
            port.IsTypeC,
            port.CompanionPortNumber,
            port.CompanionHubSymbolicName,
            port.DeviceAddress,
            port.OpenPipeCount);
    }

    private static bool HasPhysicalNode(UsbPortRecord port)
    {
        return port.ConnectionStatus != UsbConnectionStatus.NoDeviceConnected
            && (port.ConnectionStatus != UsbConnectionStatus.Unknown
                || port.DeviceDescriptor is not null
                || port.DeviceIsHub
                || port.Identity is not null);
    }

    private static string BuildPhysicalDisplayName(UsbPortRecord port)
    {
        if (!string.IsNullOrWhiteSpace(port.Identity?.DisplayName))
        {
            return port.Identity.DisplayName;
        }

        if (port.DeviceDescriptor is not null)
        {
            return $"{(port.DeviceIsHub ? "USB Hub" : "USB Device")} {port.DeviceDescriptor.VendorProduct}";
        }

        return port.ConnectionStatus == UsbConnectionStatus.DeviceFailedEnumeration
            ? "USB Device (enumeration failed)"
            : port.DeviceIsHub ? "USB Hub" : "USB Device";
    }

    private static string AppendPort(string prefix, int portNumber)
    {
        return string.IsNullOrEmpty(prefix)
            ? portNumber.ToString(System.Globalization.CultureInfo.InvariantCulture)
            : $"{prefix}.{portNumber}";
    }

    private static string Normalize(string value) => value.Trim().ToLowerInvariant();
}
