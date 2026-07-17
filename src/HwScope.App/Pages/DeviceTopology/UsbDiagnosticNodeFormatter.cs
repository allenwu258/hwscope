using System.Globalization;
using System.Text;
using HwScope.Core.Hardware.DeviceTopology;
using HwScope.Core.Hardware.DeviceTopology.Usb;

namespace HwScope.App.Pages.DeviceTopology;

internal sealed record UsbDiagnosticFieldView(
    string Label,
    string Value,
    string Source,
    string? Note = null);

internal static class UsbDiagnosticNodeFormatter
{
    public static IReadOnlyList<UsbDiagnosticFieldView> BuildOverview(
        UsbTopologyNode node,
        UsbTopologySnapshot snapshot)
    {
        var parent = node.ParentNodeId is null
            ? null
            : snapshot.Nodes.FirstOrDefault(candidate =>
                string.Equals(candidate.NodeId, node.ParentNodeId, StringComparison.OrdinalIgnoreCase));
        return
        [
            Field("节点类型", FormatKind(node.Kind), "HwScope physical topology"),
            Field("名称", node.DisplayName, node.Identity is null ? "USB hub IOCTL / derived" : "SetupAPI"),
            Field("上游节点", parent?.DisplayName ?? "无", "HwScope physical topology"),
            Field("下游节点数", node.ChildNodeIds.Count.ToString(CultureInfo.InvariantCulture), "HwScope physical topology"),
            Field("Controller Node ID", Empty(node.ControllerNodeId), "HwScope physical topology"),
            Field("Attachment ID", Empty(node.AttachmentId), node.AttachmentId is null ? "Unavailable" : "HwScope attachment identity"),
            Field("Device Path", Empty(node.DevicePath), Source(node.DevicePath, "SetupAPI / USB interface")),
            Field("枚举状态", FormatNodeStatus(node), StatusSource(node))
        ];
    }

    public static IReadOnlyList<UsbDiagnosticFieldView> BuildConnection(UsbTopologyNode node)
    {
        var port = node.Port;
        var hub = node.Hub;
        return
        [
            Field("端口链", port?.PortChain ?? "不适用", port is null ? "Unavailable" : "USB hub IOCTL / derived"),
            Field("物理端口号", port?.PortNumber.ToString(CultureInfo.InvariantCulture) ?? "不适用", port is null ? "Unavailable" : "USB hub IOCTL"),
            Field("连接状态", port?.ConnectionStatus.ToString() ?? "不适用", port is null ? "Unavailable" : "IOCTL_USB_GET_NODE_CONNECTION_INFORMATION_EX"),
            Field("当前速率", port is null ? "不适用" : FormatSpeed(port.ConnectionSpeed), port is null ? "Unavailable" : "USB hub IOCTL"),
            Field("支持协议", port is null ? "不适用" : FormatProtocols(port.SupportedProtocols), port is null ? "Unavailable" : "IOCTL_USB_GET_NODE_CONNECTION_INFORMATION_EX_V2"),
            Field("SuperSpeed capable", FormatNullable(port?.IsDeviceSuperSpeedCapable), Source(port?.IsDeviceSuperSpeedCapable, "USB hub IOCTL")),
            Field("Operating at SuperSpeed", FormatNullable(port?.IsDeviceOperatingAtSuperSpeed), Source(port?.IsDeviceOperatingAtSuperSpeed, "USB hub IOCTL")),
            Field("SuperSpeedPlus capable", FormatNullable(port?.IsDeviceSuperSpeedPlusCapable), Source(port?.IsDeviceSuperSpeedPlusCapable, "USB hub IOCTL")),
            Field("Operating at SuperSpeedPlus", FormatNullable(port?.IsDeviceOperatingAtSuperSpeedPlus), Source(port?.IsDeviceOperatingAtSuperSpeedPlus, "USB hub IOCTL")),
            Field("用户可连接", FormatNullable(port?.IsUserConnectable), Source(port?.IsUserConnectable, "USB connector property")),
            Field("Type-C", FormatNullable(port?.IsTypeC), Source(port?.IsTypeC, "USB connector property")),
            Field("Debug capable", FormatNullable(port?.IsDebugCapable), Source(port?.IsDebugCapable, "USB connector property")),
            Field("Companion Port", port?.CompanionPortNumber?.ToString(CultureInfo.InvariantCulture) ?? "未报告", Source(port?.CompanionPortNumber, "USB connector property")),
            Field("Companion Hub", Empty(port?.CompanionHubSymbolicName), Source(port?.CompanionHubSymbolicName, "USB connector property")),
            Field("Device Address", port?.DeviceAddress.ToString(CultureInfo.InvariantCulture) ?? "未报告", port is null ? "Unavailable" : "USB hub IOCTL"),
            Field("Open Pipes", port?.OpenPipeCount.ToString(CultureInfo.InvariantCulture) ?? "未报告", port is null ? "Unavailable" : "USB hub IOCTL"),
            Field("Hub Ports", hub?.PortCount.ToString(CultureInfo.InvariantCulture) ?? "不适用", hub is null ? "Unavailable" : "USB hub IOCTL"),
            Field("Hub Power", hub is null ? "不适用" : hub.IsBusPowered switch
            {
                true => "Bus powered",
                false => "Self powered",
                null => "未报告"
            }, Source(hub?.IsBusPowered, "USB hub IOCTL"))
        ];
    }

    public static IReadOnlyList<UsbDiagnosticFieldView> BuildDescriptors(
        UsbTopologyNode node,
        UsbDeviceDetailSnapshot? detail)
    {
        var descriptor = node.DeviceDescriptor;
        var fields = new List<UsbDiagnosticFieldView>
        {
            Field("VID / PID", descriptor?.VendorProduct ?? "未报告", Source(descriptor, "USB device descriptor")),
            Field("bcdUSB", descriptor?.UsbVersion ?? "未报告", Source(descriptor, "USB device descriptor")),
            Field("bcdDevice", descriptor is null ? "未报告" : $"0x{descriptor.DeviceVersionBcd:X4}", Source(descriptor, "USB device descriptor")),
            Field("Class / Subclass / Protocol", descriptor is null
                ? "未报告"
                : $"0x{descriptor.DeviceClass:X2} / 0x{descriptor.DeviceSubClass:X2} / 0x{descriptor.DeviceProtocol:X2}",
                Source(descriptor, "USB device descriptor")),
            Field("Endpoint 0 Max Packet", descriptor is null ? "未报告" : $"{descriptor.MaximumPacketSize0} bytes", Source(descriptor, "USB device descriptor")),
            Field("Declared Configurations", descriptor?.ConfigurationCount.ToString(CultureInfo.InvariantCulture) ?? "未报告", Source(descriptor, "USB device descriptor"))
        };

        if (detail is null)
        {
            fields.Add(Field("深层描述符", descriptor is null ? "此节点不提供设备描述符" : "尚未加载", "USB detail cache"));
            return fields;
        }

        fields.AddRange(
        [
            Field("Manufacturer", Empty(detail.Manufacturer), Source(detail.Manufacturer, "USB string descriptor")),
            Field("Product", Empty(detail.Product), Source(detail.Product, "USB string descriptor")),
            Field("Serial Number", Empty(detail.SerialNumber), Source(detail.SerialNumber, "USB string descriptor")),
            Field("Languages", detail.Languages.IsDefaultOrEmpty
                ? "未报告"
                : string.Join(" / ", detail.Languages.Select(language => language.DisplayName)),
                detail.Languages.IsDefaultOrEmpty ? "Unavailable" : "USB string descriptor"),
            Field("Parsed Configurations", detail.Configurations.Length.ToString(CultureInfo.InvariantCulture), "USB configuration descriptor"),
            Field("BOS", detail.Bos is null ? "未报告" : $"{detail.Bos.Capabilities.Length} capabilities", Source(detail.Bos, "USB BOS descriptor")),
            Field("详情缓存时间", detail.GeneratedAt.ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture), "USB detail cache")
        ]);
        return fields;
    }

    public static IReadOnlyList<UsbDiagnosticFieldView> BuildPnp(UsbTopologyNode node)
    {
        var identity = node.Identity;
        return
        [
            Field("Instance ID", Empty(identity?.InstanceId), Source(identity?.InstanceId, "SetupAPI")),
            Field("Device Description", Empty(identity?.DeviceDescription), Source(identity?.DeviceDescription, "SetupAPI")),
            Field("Manufacturer", Empty(identity?.Manufacturer), Source(identity?.Manufacturer, "SetupAPI")),
            Field("Service", Empty(identity?.Service), Source(identity?.Service, "SetupAPI")),
            Field("Enumerator", Empty(identity?.Enumerator), Source(identity?.Enumerator, "SetupAPI")),
            Field("Class GUID", identity?.ClassGuid?.ToString("D") ?? "未报告", Source(identity?.ClassGuid, "SetupAPI")),
            Field("Container ID", identity?.ContainerId?.ToString("D") ?? "未报告", Source(identity?.ContainerId, "SetupAPI")),
            Field("Location Paths", Join(identity?.LocationPaths), Source(identity?.LocationPaths.FirstOrDefault(), "SetupAPI")),
            Field("Hardware IDs", Join(identity?.HardwareIds), Source(identity?.HardwareIds.FirstOrDefault(), "SetupAPI")),
            Field("Compatible IDs", Join(identity?.CompatibleIds), Source(identity?.CompatibleIds.FirstOrDefault(), "SetupAPI")),
            Field("Driver Key", Empty(node.DriverKey), Source(node.DriverKey, "IOCTL_USB_GET_NODE_CONNECTION_DRIVERKEY_NAME")),
            Field("DevNode Status", identity is null ? "未报告" : $"0x{identity.Status.RawStatus:X8}", identity is null ? "Unavailable" : "Configuration Manager"),
            Field("Problem Code", identity?.Status.ProblemCode?.ToString(CultureInfo.InvariantCulture) ?? "无", identity is null ? "Unavailable" : "Configuration Manager")
        ];
    }

    public static IReadOnlyList<UsbDiagnosticFieldView> BuildDescriptorOverview(
        UsbDeviceDetailSnapshot detail,
        UsbDescriptorSelection selection)
    {
        return selection.Kind switch
        {
            UsbDiagnosticRowKind.Configuration => ConfigurationFields(detail.Configurations[selection.ConfigurationIndex]),
            UsbDiagnosticRowKind.InterfaceAssociation => IadFields(
                detail.Configurations[selection.ConfigurationIndex].InterfaceAssociations[selection.ItemIndex]),
            UsbDiagnosticRowKind.Interface => InterfaceFields(
                detail.Configurations[selection.ConfigurationIndex].Interfaces[selection.ItemIndex]),
            UsbDiagnosticRowKind.Endpoint => EndpointFields(
                detail.Configurations[selection.ConfigurationIndex]
                    .Interfaces[selection.ItemIndex].Endpoints[selection.EndpointIndex]),
            UsbDiagnosticRowKind.AdditionalDescriptor => RawDescriptorFields(
                detail.Configurations[selection.ConfigurationIndex].AdditionalDescriptors[selection.ItemIndex]),
            UsbDiagnosticRowKind.Bos => BosFields(detail.Bos!),
            UsbDiagnosticRowKind.BosCapability => BosCapabilityFields(
                detail.Bos!.Capabilities[selection.ItemIndex]),
            _ => []
        };
    }

    public static (string Title, string Meta) DescribeDescriptor(
        UsbDeviceDetailSnapshot detail,
        UsbDescriptorSelection selection)
    {
        return selection.Kind switch
        {
            UsbDiagnosticRowKind.Configuration =>
                ($"Configuration {detail.Configurations[selection.ConfigurationIndex].DescriptorIndex}", "USB configuration descriptor"),
            UsbDiagnosticRowKind.InterfaceAssociation =>
                ("Interface Association Descriptor", $"Configuration {selection.ConfigurationIndex}"),
            UsbDiagnosticRowKind.Interface =>
                ($"Interface {detail.Configurations[selection.ConfigurationIndex].Interfaces[selection.ItemIndex].InterfaceNumber}",
                    $"Alternate setting {detail.Configurations[selection.ConfigurationIndex].Interfaces[selection.ItemIndex].AlternateSetting}"),
            UsbDiagnosticRowKind.Endpoint =>
                ($"Endpoint 0x{detail.Configurations[selection.ConfigurationIndex].Interfaces[selection.ItemIndex].Endpoints[selection.EndpointIndex].Address:X2}",
                    "USB endpoint descriptor"),
            UsbDiagnosticRowKind.AdditionalDescriptor =>
                ($"Additional Descriptor 0x{detail.Configurations[selection.ConfigurationIndex].AdditionalDescriptors[selection.ItemIndex].DescriptorType:X2}",
                    "Raw or class-specific USB descriptor"),
            UsbDiagnosticRowKind.Bos => ("BOS Descriptor", "Binary Object Store"),
            UsbDiagnosticRowKind.BosCapability =>
                (detail.Bos!.Capabilities[selection.ItemIndex].DisplayName, "USB device capability descriptor"),
            _ => ("USB Descriptor", string.Empty)
        };
    }

    public static string BuildRawReport(
        UsbTopologyNode node,
        UsbTopologySnapshot snapshot,
        UsbDeviceDetailSnapshot? detail,
        IReadOnlyList<DeviceTopologyDiagnostic>? diagnostics = null)
    {
        var builder = new StringBuilder();
        builder.AppendLine("USB Physical Node Diagnostic");
        builder.AppendLine($"Generated At       : {snapshot.GeneratedAt:yyyy-MM-dd HH:mm:ss zzz}");
        builder.AppendLine($"Node ID            : {node.NodeId}");
        builder.AppendLine($"Parent Node ID     : {node.ParentNodeId ?? string.Empty}");
        builder.AppendLine($"Child Node IDs     : {string.Join(", ", node.ChildNodeIds)}");
        builder.AppendLine($"Kind               : {node.Kind}");
        builder.AppendLine($"Name               : {node.DisplayName}");
        builder.AppendLine($"Controller Node ID : {node.ControllerNodeId}");
        builder.AppendLine($"Attachment ID      : {node.AttachmentId ?? string.Empty}");
        builder.AppendLine();
        AppendFields(builder, "Overview", BuildOverview(node, snapshot));
        AppendFields(builder, "Connection And Port", BuildConnection(node));
        AppendFields(builder, "Descriptors", BuildDescriptors(node, detail));
        AppendFields(builder, "PnP And Driver", BuildPnp(node));
        if (detail is not null)
        {
            builder.AppendLine("Parsed Descriptor Directory");
            foreach (var configuration in detail.Configurations)
            {
                builder.AppendLine($"  Configuration {configuration.DescriptorIndex}: value={configuration.ConfigurationValue}, total={configuration.TotalLength}, interfaces={configuration.Interfaces.Length}");
                foreach (var item in configuration.InterfaceAssociations)
                {
                    builder.AppendLine($"    IAD: first={item.FirstInterface}, count={item.InterfaceCount}, class={item.FunctionClass:X2}/{item.FunctionSubClass:X2}/{item.FunctionProtocol:X2}");
                }

                foreach (var item in configuration.Interfaces)
                {
                    builder.AppendLine($"    Interface {item.InterfaceNumber}, alt={item.AlternateSetting}, class={item.InterfaceClass:X2}/{item.InterfaceSubClass:X2}/{item.InterfaceProtocol:X2}");
                    foreach (var endpoint in item.Endpoints)
                    {
                        builder.AppendLine($"      Endpoint 0x{endpoint.Address:X2}: {endpoint.Direction}, {endpoint.TransferType}, max-packet={endpoint.MaximumPacketBytes}, interval={endpoint.Interval}");
                    }
                }

                builder.AppendLine("    Raw:");
                AppendHex(builder, configuration.RawBytes.AsSpan(), "      ");
            }

            if (detail.Bos is not null)
            {
                builder.AppendLine($"  BOS: total={detail.Bos.TotalLength}, capabilities={detail.Bos.Capabilities.Length}");
                AppendHex(builder, detail.Bos.RawBytes.AsSpan(), "    ");
            }
        }

        builder.AppendLine();
        builder.AppendLine("Diagnostics");
        foreach (var diagnostic in (diagnostics ?? snapshot.Diagnostics.Entries)
                     .Where(entry => entry.NodeId is null
                         || string.Equals(entry.NodeId, node.NodeId, StringComparison.OrdinalIgnoreCase))
                     .Concat(detail?.Diagnostics.Entries ?? []))
        {
            builder.AppendLine($"  {diagnostic.Severity} {diagnostic.Code}: {diagnostic.Message}");
        }

        return builder.ToString().TrimEnd();
    }

    public static string BuildDescriptorRawReport(
        UsbDeviceDetailSnapshot detail,
        UsbDescriptorSelection selection)
    {
        var (title, meta) = DescribeDescriptor(detail, selection);
        var builder = new StringBuilder();
        builder.AppendLine("USB Descriptor Diagnostic");
        builder.AppendLine($"Owner Node ID : {selection.OwnerNodeId}");
        builder.AppendLine($"Descriptor    : {title}");
        builder.AppendLine($"Kind          : {selection.Kind}");
        builder.AppendLine($"Context       : {meta}");
        builder.AppendLine();
        AppendFields(builder, "Decoded Fields", BuildDescriptorOverview(detail, selection));
        var raw = GetRawBytes(detail, selection);
        if (!raw.IsEmpty)
        {
            builder.AppendLine("Raw Bytes");
            AppendHex(builder, raw, "  ");
        }

        return builder.ToString().TrimEnd();
    }

    public static IReadOnlyList<UsbDiagnosticFieldView> BuildDiagnosticOverview(
        DeviceTopologyDiagnostic diagnostic,
        UsbTopologySnapshot snapshot)
    {
        return
        [
            Field("严重程度", diagnostic.Severity.ToString(), "HwScope diagnostic"),
            Field("诊断代码", diagnostic.Code, "HwScope diagnostic"),
            Field("消息", diagnostic.Message, "HwScope diagnostic"),
            Field("目标 Node ID", diagnostic.NodeId ?? "未归属节点", diagnostic.NodeId is null ? "Global" : "Diagnostic target"),
            Field("Snapshot 时间", snapshot.GeneratedAt.ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture), "Snapshot metadata")
        ];
    }

    public static string BuildDiagnosticReport(
        DeviceTopologyDiagnostic diagnostic,
        UsbTopologySnapshot snapshot)
    {
        var builder = new StringBuilder();
        builder.AppendLine("USB Topology Diagnostic");
        builder.AppendLine($"Snapshot Generated At : {snapshot.GeneratedAt:yyyy-MM-dd HH:mm:ss zzz}");
        builder.AppendLine($"Severity              : {diagnostic.Severity}");
        builder.AppendLine($"Code                  : {diagnostic.Code}");
        builder.AppendLine($"Node ID               : {diagnostic.NodeId ?? string.Empty}");
        builder.AppendLine($"Message               : {diagnostic.Message}");
        return builder.ToString().TrimEnd();
    }

    private static IReadOnlyList<UsbDiagnosticFieldView> ConfigurationFields(UsbConfigurationDescriptorInfo item) =>
    [
        Field("Descriptor Index", item.DescriptorIndex.ToString(CultureInfo.InvariantCulture), "USB configuration descriptor"),
        Field("Total Length", $"{item.TotalLength} bytes", "USB configuration descriptor"),
        Field("Declared Interfaces", item.DeclaredInterfaceCount.ToString(CultureInfo.InvariantCulture), "USB configuration descriptor"),
        Field("Parsed Interfaces", item.Interfaces.Length.ToString(CultureInfo.InvariantCulture), "HwScope parser"),
        Field("Configuration Value", item.ConfigurationValue.ToString(CultureInfo.InvariantCulture), "USB configuration descriptor"),
        Field("Description", Empty(item.Description), Source(item.Description, "USB string descriptor")),
        Field("Attributes", $"0x{item.Attributes:X2}", "USB configuration descriptor"),
        Field("Self Powered", item.IsSelfPowered ? "是" : "否", "USB configuration descriptor"),
        Field("Remote Wakeup", item.SupportsRemoteWakeup ? "支持" : "不支持", "USB configuration descriptor"),
        Field("Maximum Power", $"{item.MaximumPowerMilliamps} mA", "USB configuration descriptor", "Descriptor maximum, not measured draw or USB-PD contract."),
        Field("Additional Descriptors", item.AdditionalDescriptors.Length.ToString(CultureInfo.InvariantCulture), "HwScope parser")
    ];

    private static IReadOnlyList<UsbDiagnosticFieldView> IadFields(UsbInterfaceAssociationInfo item) =>
    [
        Field("First Interface", item.FirstInterface.ToString(CultureInfo.InvariantCulture), "USB IAD"),
        Field("Interface Count", item.InterfaceCount.ToString(CultureInfo.InvariantCulture), "USB IAD"),
        Field("Function Class", $"0x{item.FunctionClass:X2}", "USB IAD"),
        Field("Function Subclass", $"0x{item.FunctionSubClass:X2}", "USB IAD"),
        Field("Function Protocol", $"0x{item.FunctionProtocol:X2}", "USB IAD"),
        Field("Description Index", item.DescriptionStringIndex.ToString(CultureInfo.InvariantCulture), "USB IAD"),
        Field("Description", Empty(item.Description), Source(item.Description, "USB string descriptor"))
    ];

    private static IReadOnlyList<UsbDiagnosticFieldView> InterfaceFields(UsbInterfaceDescriptorInfo item) =>
    [
        Field("Interface Number", item.InterfaceNumber.ToString(CultureInfo.InvariantCulture), "USB interface descriptor"),
        Field("Alternate Setting", item.AlternateSetting.ToString(CultureInfo.InvariantCulture), "USB interface descriptor"),
        Field("Declared Endpoints", item.DeclaredEndpointCount.ToString(CultureInfo.InvariantCulture), "USB interface descriptor"),
        Field("Parsed Endpoints", item.Endpoints.Length.ToString(CultureInfo.InvariantCulture), "HwScope parser"),
        Field("Class", $"0x{item.InterfaceClass:X2}", "USB interface descriptor"),
        Field("Subclass", $"0x{item.InterfaceSubClass:X2}", "USB interface descriptor"),
        Field("Protocol", $"0x{item.InterfaceProtocol:X2}", "USB interface descriptor"),
        Field("Description Index", item.DescriptionStringIndex.ToString(CultureInfo.InvariantCulture), "USB interface descriptor"),
        Field("Description", Empty(item.Description), Source(item.Description, "USB string descriptor"))
    ];

    private static IReadOnlyList<UsbDiagnosticFieldView> EndpointFields(UsbEndpointDescriptorInfo item) =>
    [
        Field("Address", $"0x{item.Address:X2}", "USB endpoint descriptor"),
        Field("Direction", item.Direction.ToString(), "USB endpoint descriptor"),
        Field("Transfer Type", item.TransferType.ToString(), "USB endpoint descriptor"),
        Field("Synchronization Type", item.SynchronizationType.ToString(CultureInfo.InvariantCulture), "USB endpoint descriptor"),
        Field("Usage Type", item.UsageType.ToString(CultureInfo.InvariantCulture), "USB endpoint descriptor"),
        Field("Raw wMaxPacketSize", $"0x{item.RawMaximumPacketSize:X4}", "USB endpoint descriptor"),
        Field("Maximum Packet", $"{item.MaximumPacketBytes} bytes", "HwScope parser"),
        Field("Transactions / Microframe", item.TransactionsPerMicroframe.ToString(CultureInfo.InvariantCulture), "HwScope parser"),
        Field("Interval", item.Interval.ToString(CultureInfo.InvariantCulture), "USB endpoint descriptor"),
        Field("SS Maximum Burst", item.SuperSpeedCompanion?.MaximumBurst.ToString(CultureInfo.InvariantCulture) ?? "未报告", Source(item.SuperSpeedCompanion, "SuperSpeed endpoint companion")),
        Field("SS Bytes / Interval", item.SuperSpeedCompanion is null ? "未报告" : $"{item.SuperSpeedCompanion.BytesPerInterval} bytes", Source(item.SuperSpeedCompanion, "SuperSpeed endpoint companion"))
    ];

    private static IReadOnlyList<UsbDiagnosticFieldView> BosFields(UsbBosDescriptorInfo item) =>
    [
        Field("Total Length", $"{item.TotalLength} bytes", "USB BOS descriptor"),
        Field("Declared Capabilities", item.DeclaredCapabilityCount.ToString(CultureInfo.InvariantCulture), "USB BOS descriptor"),
        Field("Parsed Capabilities", item.Capabilities.Length.ToString(CultureInfo.InvariantCulture), "HwScope parser")
    ];

    private static IReadOnlyList<UsbDiagnosticFieldView> BosCapabilityFields(UsbBosCapabilityInfo item) =>
    [
        Field("Capability Type", $"0x{item.CapabilityType:X2}", "USB device capability descriptor"),
        Field("Name", item.DisplayName, "HwScope capability decoder"),
        Field("Raw Length", $"{item.RawBytes.Length} bytes", "USB device capability descriptor")
    ];

    private static IReadOnlyList<UsbDiagnosticFieldView> RawDescriptorFields(UsbRawDescriptorInfo item) =>
    [
        Field("Descriptor Type", $"0x{item.DescriptorType:X2}", "USB raw descriptor"),
        Field("Declared Length", $"{item.Length} bytes", "USB raw descriptor"),
        Field("Preserved Bytes", $"{item.Bytes.Length} bytes", "HwScope bounds-checked parser")
    ];

    private static ReadOnlySpan<byte> GetRawBytes(
        UsbDeviceDetailSnapshot detail,
        UsbDescriptorSelection selection)
    {
        return selection.Kind switch
        {
            UsbDiagnosticRowKind.Configuration => detail.Configurations[selection.ConfigurationIndex].RawBytes.AsSpan(),
            UsbDiagnosticRowKind.AdditionalDescriptor => detail.Configurations[selection.ConfigurationIndex]
                .AdditionalDescriptors[selection.ItemIndex].Bytes.AsSpan(),
            UsbDiagnosticRowKind.Bos => detail.Bos!.RawBytes.AsSpan(),
            UsbDiagnosticRowKind.BosCapability => detail.Bos!.Capabilities[selection.ItemIndex].RawBytes.AsSpan(),
            _ => ReadOnlySpan<byte>.Empty
        };
    }

    private static void AppendFields(
        StringBuilder builder,
        string heading,
        IReadOnlyList<UsbDiagnosticFieldView> fields)
    {
        builder.AppendLine(heading);
        foreach (var field in fields)
        {
            builder.AppendLine($"  {field.Label,-28}: {field.Value}");
            builder.AppendLine($"  {"Source",-28}: {field.Source}");
            if (!string.IsNullOrWhiteSpace(field.Note))
            {
                builder.AppendLine($"  {"Note",-28}: {field.Note}");
            }
        }

        builder.AppendLine();
    }

    private static void AppendHex(StringBuilder builder, ReadOnlySpan<byte> bytes, string indent)
    {
        for (var offset = 0; offset < bytes.Length; offset += 16)
        {
            var line = bytes.Slice(offset, Math.Min(16, bytes.Length - offset));
            builder.Append(indent);
            builder.Append(offset.ToString("X4", CultureInfo.InvariantCulture));
            builder.Append("  ");
            for (var index = 0; index < line.Length; index++)
            {
                builder.Append(line[index].ToString("X2", CultureInfo.InvariantCulture));
                builder.Append(' ');
            }

            builder.AppendLine();
        }
    }

    private static UsbDiagnosticFieldView Field(string label, string value, string source, string? note = null) =>
        new(label, value, source, note);

    private static string Empty(string? value) => string.IsNullOrWhiteSpace(value) ? "未报告" : value;

    private static string Join(IReadOnlyList<string>? values) => values is null || values.Count == 0
        ? "未报告"
        : string.Join(Environment.NewLine, values);

    private static string Source<T>(T? value, string source) => value switch
    {
        null => "Unavailable",
        string text when string.IsNullOrWhiteSpace(text) => "Unavailable",
        _ => source
    };

    private static string FormatKind(UsbTopologyNodeKind kind) => kind switch
    {
        UsbTopologyNodeKind.HostController => "Host Controller",
        UsbTopologyNodeKind.RootHub => "Root Hub",
        UsbTopologyNodeKind.Port => "Physical Port",
        UsbTopologyNodeKind.Hub => "USB Hub",
        UsbTopologyNodeKind.Device => "USB Device",
        _ => "Error"
    };

    private static string FormatNodeStatus(UsbTopologyNode node)
    {
        if (node.Identity?.Status.HasProblem == true)
        {
            return $"Problem {node.Identity.Status.ProblemCode}";
        }

        return node.Port?.ConnectionStatus switch
        {
            UsbConnectionStatus.NoDeviceConnected => "空闲",
            null or UsbConnectionStatus.DeviceConnected => "正常",
            var status => status?.ToString() ?? "正常"
        };
    }

    private static string StatusSource(UsbTopologyNode node) => node.Identity?.Status.HasProblem == true
        ? "Configuration Manager"
        : node.Port is null ? "HwScope topology" : "USB hub IOCTL";

    private static string FormatSpeed(UsbConnectionSpeed speed) => speed switch
    {
        UsbConnectionSpeed.Low => "Low-Speed (1.5 Mbps)",
        UsbConnectionSpeed.Full => "Full-Speed (12 Mbps)",
        UsbConnectionSpeed.High => "High-Speed (480 Mbps)",
        UsbConnectionSpeed.Super => "SuperSpeed (5 Gbps)",
        UsbConnectionSpeed.SuperPlus => "SuperSpeedPlus",
        _ => "未协商 / 未知"
    };

    private static string FormatProtocols(UsbSupportedProtocols protocols)
    {
        if (protocols == UsbSupportedProtocols.None)
        {
            return "未报告";
        }

        return string.Join(" / ", new string?[]
        {
            protocols.HasFlag(UsbSupportedProtocols.Usb11) ? "USB 1.1" : null,
            protocols.HasFlag(UsbSupportedProtocols.Usb20) ? "USB 2.0" : null,
            protocols.HasFlag(UsbSupportedProtocols.Usb30) ? "USB 3.x" : null
        }.Where(value => value is not null));
    }

    private static string FormatNullable(bool? value) => value switch
    {
        true => "是",
        false => "否",
        null => "未报告"
    };
}
