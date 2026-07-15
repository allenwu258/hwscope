using HwScope.Core.Hardware.DeviceTopology;
using HwScope.Core.Hardware.DeviceTopology.Usb;

namespace HwScope.Core.Tests.Hardware.DeviceTopology.Usb;

public sealed class UsbTopologyBuilderTests
{
    [Fact]
    public void BuildPreservesEmptyPortsAndNestedPhysicalPortChains()
    {
        var leaf = ConnectedPort(1, "USB\\VID_2222&PID_0002\\SERIAL-B", isHub: false);
        var downstreamHub = new UsbHubRecord("HUB-2", 1, true, false, [leaf]);
        var root = new UsbHubRecord(
            "ROOT-HUB",
            3,
            false,
            true,
            [
                ConnectedPort(1, "USB\\VID_1111&PID_0001\\SERIAL-A", isHub: false),
                EmptyPort(2),
                ConnectedPort(3, "USB\\VID_3333&PID_0003\\HUB", isHub: true, downstreamHub)
            ]);
        var controller = new UsbControllerRecord(
            @"\\?\controller",
            Identity(@"PCI\VEN_1234&DEV_5678\CTRL", "Test xHCI Controller"),
            root);

        var snapshot = UsbTopologyBuilder.Build([controller], generatedAt: DateTimeOffset.UnixEpoch);

        var controllerId = UsbTopologyBuilder.BuildControllerNodeId(controller.Identity.InstanceId);
        var rootId = UsbTopologyBuilder.BuildRootHubNodeId(controller.Identity.InstanceId);
        var emptyPortId = UsbTopologyBuilder.BuildPortNodeId(controller.Identity.InstanceId, "2");
        var hubId = UsbTopologyBuilder.BuildDeviceNodeId(controller.Identity.InstanceId, "3", true);
        var nestedPortId = UsbTopologyBuilder.BuildPortNodeId(controller.Identity.InstanceId, "3.1");
        var nestedDeviceId = UsbTopologyBuilder.BuildDeviceNodeId(controller.Identity.InstanceId, "3.1", false);

        Assert.Equal(9, snapshot.Nodes.Count);
        Assert.Equal([controllerId], snapshot.HostControllerNodeIds);
        Assert.Equal([rootId], Find(snapshot, controllerId).ChildNodeIds);
        Assert.Empty(Find(snapshot, emptyPortId).ChildNodeIds);
        Assert.Equal(UsbConnectionStatus.NoDeviceConnected, Find(snapshot, emptyPortId).Port!.ConnectionStatus);
        Assert.Equal([nestedPortId], Find(snapshot, hubId).ChildNodeIds);
        Assert.Equal([nestedDeviceId], Find(snapshot, nestedPortId).ChildNodeIds);
        Assert.Equal("3.1", Find(snapshot, nestedDeviceId).Port!.PortChain);
    }

    [Fact]
    public void BuildKeepsFailedEnumerationAsPhysicalDeviceNode()
    {
        var failed = EmptyPort(1) with { ConnectionStatus = UsbConnectionStatus.DeviceFailedEnumeration };
        var root = new UsbHubRecord("ROOT", 1, true, true, [failed]);
        var controller = new UsbControllerRecord("controller", Identity(@"PCI\CTRL", "Controller"), root);

        var snapshot = UsbTopologyBuilder.Build([controller]);

        var device = Assert.Single(snapshot.Nodes, node => node.Kind == UsbTopologyNodeKind.Device);
        Assert.Equal("USB Device (enumeration failed)", device.DisplayName);
    }

    [Fact]
    public void BuildIgnoresDuplicateControllerIdsWithDiagnostic()
    {
        var controller = new UsbControllerRecord("controller", Identity(@"PCI\CTRL", "Controller"), null);

        var snapshot = UsbTopologyBuilder.Build([controller, controller]);

        Assert.Single(snapshot.HostControllerNodeIds);
        Assert.Contains(snapshot.Diagnostics.Entries, item => item.Code == "usb.duplicate-controller");
    }

    [Fact]
    public void CollectorReturnsDiagnosticWhenSourceThrows()
    {
        var snapshot = new UsbTopologyCollector(new ThrowingSource()).Collect();

        Assert.Empty(snapshot.Nodes);
        Assert.Equal("usb.enumeration-failed", Assert.Single(snapshot.Diagnostics.Entries).Code);
    }

    [Fact]
    public void RedactorRemovesNativeIdentifiersAndPreservesGraph()
    {
        var root = new UsbHubRecord(
            @"\??\USB#ROOT_HUB30#SECRET",
            1,
            true,
            true,
            [ConnectedPort(1, @"USB\VID_1111&PID_0001\SERIAL-SECRET", false)]);
        var controller = new UsbControllerRecord(
            @"\\?\PCI#VEN_1234#SECRET",
            Identity(@"PCI\VEN_1234&DEV_5678\SECRET", "Controller"),
            root);
        var snapshot = UsbTopologyBuilder.Build([controller]);
        snapshot = snapshot with
        {
            Nodes = snapshot.Nodes.Select(node => node.Kind == UsbTopologyNodeKind.Port
                ? node with { DriverKey = "{CLASS}\\SECRET" }
                : node).ToArray()
        };

        var redacted = UsbTopologyRedactor.RedactSensitiveIds(snapshot);
        var text = System.Text.Json.JsonSerializer.Serialize(redacted);

        Assert.DoesNotContain("SECRET", text, StringComparison.OrdinalIgnoreCase);
        Assert.All(redacted.Nodes, node =>
        {
            Assert.StartsWith("usb-node-", node.NodeId);
            Assert.All(node.ChildNodeIds, childId => Assert.Contains(redacted.Nodes, child => child.NodeId == childId));
        });
    }

    [Fact]
    public void ReportFormatterIncludesPortChainAndDescriptorIdentity()
    {
        var root = new UsbHubRecord("ROOT", 1, true, true, [ConnectedPort(1, @"USB\DEVICE", false)]);
        var snapshot = UsbTopologyBuilder.Build(
            [new UsbControllerRecord("controller", Identity(@"PCI\CTRL", "Controller"), root)]);

        var text = UsbTopologyReportFormatter.Format(snapshot);

        Assert.Contains("chain=1", text);
        Assert.Contains("vid:pid=1234:5678", text);
    }

    private static UsbTopologyNode Find(UsbTopologySnapshot snapshot, string nodeId)
    {
        return Assert.Single(snapshot.Nodes, node => node.NodeId == nodeId);
    }

    private static UsbPortRecord EmptyPort(int portNumber)
    {
        return new UsbPortRecord(
            portNumber,
            UsbConnectionStatus.NoDeviceConnected,
            UsbConnectionSpeed.Unknown,
            UsbSupportedProtocols.Usb20,
            false,
            0,
            0,
            null,
            string.Empty,
            true,
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

    private static UsbPortRecord ConnectedPort(
        int portNumber,
        string instanceId,
        bool isHub,
        UsbHubRecord? downstreamHub = null)
    {
        return new UsbPortRecord(
            portNumber,
            UsbConnectionStatus.DeviceConnected,
            UsbConnectionSpeed.Super,
            UsbSupportedProtocols.Usb20 | UsbSupportedProtocols.Usb30,
            isHub,
            4,
            2,
            new UsbDeviceDescriptorInfo(18, 1, 0x0320, 0, 0, 0, 9, 0x1234, 0x5678, 0x0100, 1, 2, 3, 1),
            "{CLASS}\\0001",
            true,
            false,
            true,
            2,
            "COMPANION",
            true,
            true,
            true,
            false,
            Identity(instanceId, isHub ? "USB Hub" : "USB Device"),
            downstreamHub);
    }

    private static PnpDeviceIdentity Identity(string instanceId, string name)
    {
        return new PnpDeviceIdentity(
            $"pnp:{instanceId.ToLowerInvariant()}",
            instanceId,
            name,
            name,
            "Vendor",
            null,
            Guid.Parse("11111111-2222-3333-4444-555555555555"),
            [$"{instanceId}_HARDWARE"],
            [],
            [$"PCIROOT(0)#USBROOT(0)#{instanceId}"],
            instanceId.Split('\\')[0],
            "usb",
            new DeviceNodeStatus(0, null));
    }

    private sealed class ThrowingSource : IUsbDeviceSource
    {
        public UsbDeviceSourceResult ReadPresentTopology()
        {
            throw new InvalidOperationException("boom");
        }
    }
}
