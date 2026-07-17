using System.Collections.Immutable;
using HwScope.App.Pages.DeviceTopology;
using HwScope.Core.Hardware.DeviceTopology;
using HwScope.Core.Hardware.DeviceTopology.Usb;

namespace HwScope.App.Tests.Pages.DeviceTopology;

public sealed class UsbDiagnosticTreeProjectionTests
{
    [Fact]
    public void DefaultProjectionExpandsConnectedPhysicalBranches()
    {
        var projection = new UsbDiagnosticTreeProjection();
        projection.SetSnapshot(CreateSnapshot());

        var rows = projection.Build(null, UsbDiagnosticTreeFilter.All);

        Assert.Equal(
            ["controller", "root", "empty-port", "connected-port", "device"],
            rows.Select(row => row.RowId));
        Assert.Equal([0, 1, 2, 2, 3], rows.Select(row => row.Depth));
        Assert.Equal("空闲", rows.Single(row => row.RowId == "empty-port").StatusText);
    }

    [Fact]
    public void LoadedDetailAddsExactDescriptorHierarchy()
    {
        var projection = new UsbDiagnosticTreeProjection();
        projection.SetSnapshot(CreateSnapshot());
        projection.SetDeviceDetail(CreateDetail());
        projection.ExpandAll();

        var rows = projection.Build(null, UsbDiagnosticTreeFilter.All);

        Assert.Contains(rows, row => row.RowKind == UsbDiagnosticRowKind.Configuration);
        Assert.Contains(rows, row => row.RowKind == UsbDiagnosticRowKind.InterfaceAssociation);
        Assert.Contains(rows, row => row.RowKind == UsbDiagnosticRowKind.Interface);
        Assert.Contains(rows, row => row.RowKind == UsbDiagnosticRowKind.Endpoint);
        Assert.Contains(rows, row => row.RowKind == UsbDiagnosticRowKind.SuperSpeedEndpointCompanion);
        Assert.Contains(rows, row => row.RowKind == UsbDiagnosticRowKind.AdditionalDescriptor);
        Assert.Contains(rows, row => row.RowKind == UsbDiagnosticRowKind.Bos);
        Assert.Contains(rows, row => row.RowKind == UsbDiagnosticRowKind.BosCapability);
        var endpoint = Assert.Single(rows, row => row.RowKind == UsbDiagnosticRowKind.Endpoint);
        Assert.True(projection.TryGetDescriptorSelection(endpoint.RowId, out var selection));
        Assert.Equal("device", selection!.OwnerNodeId);
        Assert.Equal(UsbDiagnosticRowKind.Endpoint, selection.Kind);
        var interfaceRow = Assert.Single(rows, row => row.RowKind == UsbDiagnosticRowKind.Interface);
        var additional = Assert.Single(rows, row => row.RowKind == UsbDiagnosticRowKind.AdditionalDescriptor);
        Assert.Equal(interfaceRow.RowId, additional.ParentRowId);
        var orderedRows = rows.ToList();
        Assert.True(orderedRows.IndexOf(rows.Single(row =>
                row.RowKind == UsbDiagnosticRowKind.InterfaceAssociation))
            < orderedRows.IndexOf(interfaceRow));
    }

    [Fact]
    public void DescriptorSearchPreservesPhysicalAndDescriptorAncestors()
    {
        var projection = new UsbDiagnosticTreeProjection();
        projection.SetSnapshot(CreateSnapshot());
        projection.SetDeviceDetail(CreateDetail());

        var rows = projection.Build("Bulk", UsbDiagnosticTreeFilter.All);

        Assert.Equal("controller", rows[0].RowId);
        Assert.Contains(rows, row => row.RowId == "device");
        Assert.Contains(rows, row => row.RowKind == UsbDiagnosticRowKind.Configuration);
        Assert.Contains(rows, row => row.RowKind == UsbDiagnosticRowKind.Interface);
        Assert.Contains(rows, row => row.RowKind == UsbDiagnosticRowKind.Endpoint);
    }

    [Fact]
    public void EmptyPortFilterKeepsOnlyExactAncestorPath()
    {
        var projection = new UsbDiagnosticTreeProjection();
        projection.SetSnapshot(CreateSnapshot());

        var rows = projection.Build(null, UsbDiagnosticTreeFilter.EmptyPorts);

        Assert.Equal(["controller", "root", "empty-port"], rows.Select(row => row.RowId));
    }

    [Fact]
    public void DetailFromPreviousAttachmentIsIgnored()
    {
        var projection = new UsbDiagnosticTreeProjection();
        projection.SetSnapshot(CreateSnapshot());
        projection.SetDeviceDetail(CreateDetail() with { AttachmentId = "old-attachment" });

        var rows = projection.Build(null, UsbDiagnosticTreeFilter.Descriptors);

        Assert.Empty(rows);
        Assert.Null(projection.TryGetDetail("device"));
    }

    [Fact]
    public void ProblemsIncludePortFailuresAndDescriptorDiagnostics()
    {
        var snapshot = CreateSnapshot() with
        {
            Nodes = CreateSnapshot().Nodes.Select(node => node.NodeId == "connected-port"
                ? node with
                {
                    Port = node.Port! with
                    {
                        ConnectionStatus = UsbConnectionStatus.DeviceNotEnoughPower
                    }
                }
                : node).ToArray()
        };
        var projection = new UsbDiagnosticTreeProjection();
        projection.SetSnapshot(snapshot);

        var rows = projection.Build(null, UsbDiagnosticTreeFilter.Problems);

        Assert.Equal(1, projection.ProblemCount);
        Assert.Contains(rows, row => row.RowId == "connected-port" && row.HasProblem);
    }

    [Fact]
    public void FormatterEmitsDecodedFieldsAndRawDescriptorBytes()
    {
        var detail = CreateDetail();
        var selection = new UsbDescriptorSelection(
            "device", UsbDiagnosticRowKind.Configuration, ConfigurationIndex: 0);

        var fields = UsbDiagnosticNodeFormatter.BuildDescriptorOverview(detail, selection);
        var report = UsbDiagnosticNodeFormatter.BuildDescriptorRawReport(detail, selection);

        Assert.Contains(fields, field => field.Label == "Maximum Power" && field.Value == "100 mA");
        Assert.Contains("USB Descriptor Diagnostic", report);
        Assert.Contains("0000  09 02 20 00", report);

        var endpointSelection = new UsbDescriptorSelection(
            "device", UsbDiagnosticRowKind.Endpoint,
            ConfigurationIndex: 0, ItemIndex: 0, EndpointIndex: 0, OrderedEntryIndex: 2);
        var endpointFields = UsbDiagnosticNodeFormatter.BuildDescriptorOverview(detail, endpointSelection);
        var endpointReport = UsbDiagnosticNodeFormatter.BuildDescriptorRawReport(detail, endpointSelection);
        Assert.Equal("0x001A", Assert.Single(endpointFields, field => field.Label == "Stream Offset").Value);
        Assert.Contains("0000  04 05 01 02", endpointReport);
    }

    [Fact]
    public void CachedDetailDoesNotReopenManuallyCollapsedDevice()
    {
        var projection = new UsbDiagnosticTreeProjection();
        projection.SetSnapshot(CreateSnapshot());
        var detail = CreateDetail();
        projection.SetDeviceDetail(detail, expandWhenAdded: true);
        projection.Build(null, UsbDiagnosticTreeFilter.All);
        projection.Toggle("device");

        projection.SetDeviceDetail(detail, expandWhenAdded: true);
        var rows = projection.Build(null, UsbDiagnosticTreeFilter.All);

        Assert.False(Assert.Single(rows, row => row.RowId == "device").IsExpanded);
        Assert.DoesNotContain(rows, row => row.RowKind == UsbDiagnosticRowKind.Configuration);
    }

    [Fact]
    public void RefreshExpandsPortThatNewlyGainsConnectedDevice()
    {
        var full = CreateSnapshot();
        var initial = full with
        {
            Nodes = full.Nodes
                .Where(node => node.NodeId != "device")
                .Select(node => node.NodeId == "connected-port"
                    ? node with
                    {
                        ChildNodeIds = [],
                        Port = node.Port! with
                        {
                            ConnectionStatus = UsbConnectionStatus.NoDeviceConnected,
                            ConnectionSpeed = UsbConnectionSpeed.Unknown
                        }
                    }
                    : node)
                .ToArray()
        };
        var projection = new UsbDiagnosticTreeProjection();
        projection.SetSnapshot(initial);
        projection.Build(null, UsbDiagnosticTreeFilter.All);

        projection.SetSnapshot(full);
        var rows = projection.Build(null, UsbDiagnosticTreeFilter.All);

        Assert.Contains(rows, row => row.RowId == "device");
        Assert.True(Assert.Single(rows, row => row.RowId == "connected-port").IsExpanded);
    }

    [Fact]
    public void RefreshDoesNotReopenExplicitlyCollapsedConnectedPort()
    {
        var full = CreateSnapshot();
        var projection = new UsbDiagnosticTreeProjection();
        projection.SetSnapshot(full);
        projection.Build(null, UsbDiagnosticTreeFilter.All);
        projection.Toggle("connected-port");

        var disconnected = full with
        {
            Nodes = full.Nodes
                .Where(node => node.NodeId != "device")
                .Select(node => node.NodeId == "connected-port"
                    ? node with { ChildNodeIds = [] }
                    : node)
                .ToArray()
        };
        projection.SetSnapshot(disconnected);
        projection.SetSnapshot(full);
        var rows = projection.Build(null, UsbDiagnosticTreeFilter.All);

        Assert.False(Assert.Single(rows, row => row.RowId == "connected-port").IsExpanded);
        Assert.DoesNotContain(rows, row => row.RowId == "device");
    }

    [Fact]
    public void ProjectionRetainsOnlyTwoMostRecentDeviceDetails()
    {
        var snapshot = CreateSnapshotWithThreeDevices();
        var projection = new UsbDiagnosticTreeProjection();
        projection.SetSnapshot(snapshot);

        projection.SetDeviceDetail(CreateDetail("device-1", "attachment-1"));
        projection.SetDeviceDetail(CreateDetail("device-2", "attachment-2"));
        projection.SetDeviceDetail(CreateDetail("device-3", "attachment-3"));

        Assert.Null(projection.TryGetDetail("device-1"));
        Assert.NotNull(projection.TryGetDetail("device-2"));
        Assert.NotNull(projection.TryGetDetail("device-3"));
    }

    private static UsbTopologySnapshot CreateSnapshot()
    {
        var controller = Node(
            "controller", null, ["root"], UsbTopologyNodeKind.HostController,
            "xHCI Controller", controllerNodeId: "controller");
        var root = Node(
            "root", "controller", ["empty-port", "connected-port"], UsbTopologyNodeKind.RootHub,
            "USB Root Hub", controllerNodeId: "controller",
            hub: new UsbHubInfo("root-hub", 2, false, true));
        var emptyPort = Node(
            "empty-port", "root", [], UsbTopologyNodeKind.Port,
            "Port 1", controllerNodeId: "controller",
            port: Port(1, "1", UsbConnectionStatus.NoDeviceConnected));
        var connectedPort = Node(
            "connected-port", "root", ["device"], UsbTopologyNodeKind.Port,
            "Port 2", controllerNodeId: "controller",
            port: Port(2, "2", UsbConnectionStatus.DeviceConnected));
        var device = Node(
            "device", "connected-port", [], UsbTopologyNodeKind.Device,
            "USB Storage", controllerNodeId: "controller",
            identity: Identity("device", "USB Storage"),
            descriptor: DeviceDescriptor(),
            attachmentId: "attachment-1");
        return new UsbTopologySnapshot(
            [controller, root, emptyPort, connectedPort, device],
            [controller.NodeId],
            DeviceTopologyDiagnostics.Empty,
            DateTimeOffset.UnixEpoch);
    }

    private static UsbDeviceDetailSnapshot CreateDetail(
        string deviceNodeId = "device",
        string attachmentId = "attachment-1")
    {
        var endpoint = new UsbEndpointDescriptorInfo(
            0x81,
            UsbEndpointDirection.In,
            UsbEndpointTransferType.Bulk,
            0,
            0,
            0x0200,
            512,
            1,
            0,
            new UsbSuperSpeedEndpointCompanionInfo(1, 0, 0));
        var item = new UsbInterfaceDescriptorInfo(
            0,
            0,
            1,
            0x08,
            0x06,
            0x50,
            4,
            "Mass Storage",
            [endpoint]);
        var iad = new UsbInterfaceAssociationInfo(
            0, 1, 0x08, 0x06, 0x50, 5, "Storage Function");
        var configuration = new UsbConfigurationDescriptorInfo(
            0,
            32,
            1,
            1,
            3,
            "Default",
            0x80,
            false,
            false,
            100,
            [iad],
            [item],
            [new UsbRawDescriptorInfo(0x24, 4, [0x04, 0x24, 0x01, 0x02])],
            [0x09, 0x02, 0x20, 0x00])
        {
            OrderedDescriptors =
            [
                Ordered(9, 0x0B, UsbConfigurationDescriptorEntryKind.InterfaceAssociation,
                    UsbConfigurationDescriptorOwnerKind.Configuration, iad: 0),
                Ordered(17, 0x04, UsbConfigurationDescriptorEntryKind.Interface,
                    UsbConfigurationDescriptorOwnerKind.Configuration, item: 0),
                Ordered(26, 0x05, UsbConfigurationDescriptorEntryKind.Endpoint,
                    UsbConfigurationDescriptorOwnerKind.Interface, item: 0, endpoint: 0),
                Ordered(33, 0x30, UsbConfigurationDescriptorEntryKind.SuperSpeedEndpointCompanion,
                    UsbConfigurationDescriptorOwnerKind.Endpoint, item: 0, endpoint: 0),
                Ordered(39, 0x24, UsbConfigurationDescriptorEntryKind.Additional,
                    UsbConfigurationDescriptorOwnerKind.Interface, item: 0, additional: 0)
            ]
        };
        var capability = new UsbBosCapabilityInfo(
            0x02,
            "USB 2.0 Extension",
            [0x07, 0x10, 0x02, 0x02]);
        var bos = new UsbBosDescriptorInfo(
            12,
            1,
            [capability],
            [0x05, 0x0F, 0x0C, 0x00, 0x01]);
        return new UsbDeviceDetailSnapshot(
            attachmentId,
            deviceNodeId,
            "Vendor",
            "USB Storage",
            "SERIAL",
            [new UsbLanguageInfo(0x0409, "English (United States)")],
            [configuration],
            bos,
            DeviceTopologyDiagnostics.Empty,
            DateTimeOffset.UnixEpoch);
    }

    private static UsbConfigurationDescriptorEntryInfo Ordered(
        int offset,
        byte type,
        UsbConfigurationDescriptorEntryKind kind,
        UsbConfigurationDescriptorOwnerKind owner,
        int? iad = null,
        int? item = null,
        int? endpoint = null,
        int? additional = null)
    {
        return new UsbConfigurationDescriptorEntryInfo(
            offset, type, 4, kind, owner, iad, item, endpoint, additional, false,
            [4, type, 1, 2]);
    }

    private static UsbTopologySnapshot CreateSnapshotWithThreeDevices()
    {
        var nodes = new List<UsbTopologyNode>();
        var portIds = new List<string>();
        for (var index = 1; index <= 3; index++)
        {
            var portId = $"attachment-{index}";
            var deviceId = $"device-{index}";
            portIds.Add(portId);
            nodes.Add(Node(
                portId, "root", [deviceId], UsbTopologyNodeKind.Port,
                $"Port {index}", "controller",
                port: Port(index, index.ToString(), UsbConnectionStatus.DeviceConnected)));
            nodes.Add(Node(
                deviceId, portId, [], UsbTopologyNodeKind.Device,
                $"Device {index}", "controller",
                identity: Identity(deviceId, $"Device {index}"),
                descriptor: DeviceDescriptor(),
                attachmentId: portId));
        }

        var controller = Node(
            "controller", null, ["root"], UsbTopologyNodeKind.HostController,
            "xHCI Controller", "controller");
        var root = Node(
            "root", "controller", portIds, UsbTopologyNodeKind.RootHub,
            "USB Root Hub", "controller",
            hub: new UsbHubInfo("root-hub", 3, false, true));
        return new UsbTopologySnapshot(
            [controller, root, .. nodes], ["controller"],
            DeviceTopologyDiagnostics.Empty, DateTimeOffset.UnixEpoch);
    }

    private static UsbTopologyNode Node(
        string nodeId,
        string? parentNodeId,
        IReadOnlyList<string> children,
        UsbTopologyNodeKind kind,
        string displayName,
        string controllerNodeId,
        PnpDeviceIdentity? identity = null,
        UsbHubInfo? hub = null,
        UsbPortInfo? port = null,
        UsbDeviceDescriptorInfo? descriptor = null,
        string? attachmentId = null)
    {
        return new UsbTopologyNode(
            nodeId,
            parentNodeId,
            children,
            kind,
            displayName,
            identity,
            controllerNodeId,
            string.Empty,
            string.Empty,
            hub,
            port,
            descriptor,
            attachmentId);
    }

    private static UsbPortInfo Port(
        int portNumber,
        string portChain,
        UsbConnectionStatus status)
    {
        return new UsbPortInfo(
            portNumber,
            portChain,
            status,
            status == UsbConnectionStatus.NoDeviceConnected
                ? UsbConnectionSpeed.Unknown
                : UsbConnectionSpeed.Super,
            UsbSupportedProtocols.Usb20 | UsbSupportedProtocols.Usb30,
            true,
            true,
            false,
            false,
            true,
            false,
            true,
            null,
            string.Empty,
            status == UsbConnectionStatus.NoDeviceConnected ? (ushort)0 : (ushort)3,
            status == UsbConnectionStatus.NoDeviceConnected ? 0u : 1u);
    }

    private static UsbDeviceDescriptorInfo DeviceDescriptor() => new(
        18, 1, 0x0310, 0, 0, 0, 9, 0x1234, 0x5678, 0x0100, 1, 2, 3, 1);

    private static PnpDeviceIdentity Identity(string id, string name) => new(
        id,
        $"USB\\VID_1234&PID_5678\\{id}",
        name,
        name,
        "Vendor",
        Guid.Parse("11111111-2222-3333-4444-555555555555"),
        null,
        ["USB\\VID_1234&PID_5678"],
        [],
        [$"USBROOT(0)#USB({id})"],
        "USB",
        "usb-storage",
        new DeviceNodeStatus(0, null));
}
