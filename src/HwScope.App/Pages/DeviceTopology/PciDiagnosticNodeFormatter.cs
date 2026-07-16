using System.Globalization;
using System.Text;
using HwScope.Core.Hardware.DeviceTopology;
using HwScope.Core.Hardware.DeviceTopology.Pci;

namespace HwScope.App.Pages.DeviceTopology;

internal sealed record PciDiagnosticFieldView(
    string Label,
    string Value,
    string Source,
    string? Note = null);

internal static class PciDiagnosticNodeFormatter
{
    public static IReadOnlyList<PciDiagnosticFieldView> BuildOverview(
        PciTopologyNode node,
        PciTopologySnapshot snapshot)
    {
        var isSyntheticRoot = IsSyntheticRoot(node);
        var parent = node.ParentNodeId is null
            ? null
            : snapshot.Nodes.FirstOrDefault(candidate =>
                string.Equals(candidate.NodeId, node.ParentNodeId, StringComparison.OrdinalIgnoreCase));
        return
        [
            Reported("节点类型", node.DeviceType.ToString(), isSyntheticRoot ? "Derived" : "PciBusDriver"),
            Reported("拓扑角色", node.Kind.ToString(), "Derived"),
            Reported("BDF", node.Address?.ToString() ?? "未报告", AddressSource(node)),
            Reported("设备名称", node.Identity.DisplayName, DisplayNameSource(node)),
            Reported("设备描述", Empty(node.Identity.DeviceDescription), IdentitySource(node, node.Identity.DeviceDescription)),
            Reported("Vendor / Device", JoinIdentity(node.PciIdentity.VendorId, node.PciIdentity.DeviceId),
                ParsedSource(node.PciIdentity.VendorId, node.PciIdentity.DeviceId, "Hardware ID / parsed")),
            Reported("Subsystem", Empty(node.PciIdentity.SubsystemId),
                ValueSource(node.PciIdentity.SubsystemId, "Hardware ID / parsed")),
            Reported("Revision", Empty(node.PciIdentity.RevisionId),
                ValueSource(node.PciIdentity.RevisionId, "Hardware ID / parsed")),
            Reported("Class", $"{Empty(node.Class.Code)} · {node.Class.DisplayName}",
                isSyntheticRoot ? "Derived" : ValueSource(node.Class.Code, "PciBusDriver")),
            Reported("上游节点", parent is null ? "无" : $"{parent.Address?.ToString() ?? "--"} · {parent.Identity.DisplayName}", "Derived"),
            Reported("下游节点数", node.ChildNodeIds.Count.ToString(CultureInfo.InvariantCulture), "Derived"),
            Reported("设备状态", node.Identity.Status.HasProblem ? $"Problem {node.Identity.Status.ProblemCode}" : "正常",
                isSyntheticRoot ? "Derived" : "SetupAPI device relation property"),
            Reported("Instance ID", Empty(node.Identity.InstanceId),
                isSyntheticRoot ? "Derived" : ValueSource(node.Identity.InstanceId, "SetupAPI enumeration")),
            Reported("Location Path", node.Identity.LocationPaths.FirstOrDefault() ?? "未报告",
                isSyntheticRoot ? "Derived" : CollectionSource(node.Identity.LocationPaths, "SetupAPI"))
        ];
    }

    public static IReadOnlyList<PciDiagnosticFieldView> BuildLinkAndCapabilities(PciTopologyNode node)
    {
        return
        [
            Field("当前链路代际", node.Link.CurrentGeneration),
            Field("当前链路宽度", node.Link.CurrentWidth),
            Field("最大链路代际", node.Link.MaximumGeneration),
            Field("最大链路宽度", node.Link.MaximumWidth),
            Field("当前 Payload", node.Link.CurrentPayloadBytes),
            Field("最大 Payload", node.Link.MaximumPayloadBytes),
            Field("最大 Read Request", node.Link.MaximumReadRequestBytes),
            Field("PCIe 规范版本", node.Capabilities.ExpressSpecVersion),
            Field("AER Capability", node.Capabilities.AerCapabilityPresent),
            Field("Interrupt Support", node.Capabilities.InterruptSupport),
            Field("Interrupt Message Maximum", node.Capabilities.InterruptMessageMaximum),
            Field("BAR Types", node.Capabilities.BarTypes)
        ];
    }

    public static IReadOnlyList<PciDiagnosticFieldView> BuildResources(PciTopologyNode node)
    {
        return
        [
            Reported(
                "数据范围",
                "能力摘要；未读取实际 BAR 地址范围、IRQ/MSI 分配或 Memory/I/O window",
                "HwScope snapshot limitation",
                "此页不表示 Windows 当前分配给设备的实际资源地址。"),
            Reported("Raw BusNumber", FormatRaw(node.RawBusNumber),
                node.RawBusNumber.HasValue ? "DEVPKEY_Device_BusNumber" : "Unavailable"),
            Reported("Raw Address", FormatRaw(node.RawDeviceAddress),
                node.RawDeviceAddress.HasValue ? "DEVPKEY_Device_Address" : "Unavailable"),
            Reported("BDF", node.Address?.ToString() ?? "未报告", AddressSource(node)),
            Field("Interrupt Support", node.Capabilities.InterruptSupport),
            Field("Interrupt Message Maximum", node.Capabilities.InterruptMessageMaximum),
            Field("BAR Types", node.Capabilities.BarTypes),
            Reported("Hardware IDs", Join(node.Identity.HardwareIds), CollectionSource(node.Identity.HardwareIds, "SetupAPI")),
            Reported("Compatible IDs", Join(node.Identity.CompatibleIds), CollectionSource(node.Identity.CompatibleIds, "SetupAPI")),
            Reported("Location Paths", Join(node.Identity.LocationPaths),
                IsSyntheticRoot(node) ? "Derived" : CollectionSource(node.Identity.LocationPaths, "SetupAPI")),
            Reported("Container ID", node.Identity.ContainerId?.ToString("D") ?? "未报告",
                node.Identity.ContainerId.HasValue ? "SetupAPI" : "Unavailable")
        ];
    }

    public static IReadOnlyList<PciDiagnosticFieldView> BuildDriver(PciTopologyNode node)
    {
        return
        [
            Reported("描述", Empty(node.Driver.Description), ValueSource(node.Driver.Description, "SetupAPI driver property")),
            Reported("提供方", Empty(node.Driver.Provider), ValueSource(node.Driver.Provider, "SetupAPI driver property")),
            Reported("版本", Empty(node.Driver.Version), ValueSource(node.Driver.Version, "SetupAPI driver property")),
            Reported("INF", Empty(node.Driver.InfPath), ValueSource(node.Driver.InfPath, "SetupAPI driver property")),
            Reported("Service", Empty(node.Driver.Service), ValueSource(node.Driver.Service, "SetupAPI device property")),
            Reported("设备厂商", Empty(node.Identity.Manufacturer), IdentitySource(node, node.Identity.Manufacturer)),
            Reported("Enumerator", Empty(node.Identity.Enumerator), "Derived"),
            Reported("Class GUID", node.Identity.ClassGuid?.ToString("D") ?? "未报告",
                node.Identity.ClassGuid.HasValue ? "SetupAPI" : "Unavailable"),
            Reported("DevNode Status", $"0x{node.Identity.Status.RawStatus:X8}",
                IsSyntheticRoot(node) ? "Derived" : "SetupAPI device relation property"),
            Reported("Problem Code", node.Identity.Status.ProblemCode?.ToString(CultureInfo.InvariantCulture) ?? "无",
                IsSyntheticRoot(node) ? "Derived" : "SetupAPI device relation property")
        ];
    }

    public static IReadOnlyList<PciDiagnosticFieldView> BuildDiagnosticOverview(
        DeviceTopologyDiagnostic diagnostic,
        PciTopologySnapshot snapshot)
    {
        return
        [
            Reported("严重程度", diagnostic.Severity.ToString(), "HwScope diagnostic"),
            Reported("诊断代码", diagnostic.Code, "HwScope diagnostic"),
            Reported("消息", diagnostic.Message, "HwScope diagnostic"),
            Reported("目标 Node ID", diagnostic.NodeId ?? "未归属节点", diagnostic.NodeId is null ? "Global" : "Diagnostic target"),
            Reported("Snapshot 时间", snapshot.GeneratedAt.ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture), "Snapshot metadata")
        ];
    }

    public static string BuildRawReport(
        PciTopologyNode node,
        PciTopologySnapshot snapshot,
        IReadOnlyList<DeviceTopologyDiagnostic>? diagnostics = null)
    {
        var builder = new StringBuilder();
        builder.AppendLine("PCI Express Node Diagnostic");
        builder.AppendLine($"Generated At       : {snapshot.GeneratedAt:yyyy-MM-dd HH:mm:ss zzz}");
        builder.AppendLine($"Node ID            : {node.NodeId}");
        builder.AppendLine($"Parent Node ID     : {node.ParentNodeId ?? ""}");
        builder.AppendLine($"Child Node IDs     : {string.Join(", ", node.ChildNodeIds)}");
        builder.AppendLine($"Kind / Device Type : {node.Kind} / {node.DeviceType}");
        builder.AppendLine($"BDF                : {node.Address?.ToString() ?? ""}");
        builder.AppendLine($"Raw BusNumber      : {FormatRaw(node.RawBusNumber)}");
        builder.AppendLine($"Raw Address        : {FormatRaw(node.RawDeviceAddress)}");
        builder.AppendLine($"VID / DID          : {JoinIdentity(node.PciIdentity.VendorId, node.PciIdentity.DeviceId)}");
        builder.AppendLine($"Subsystem / Rev    : {Empty(node.PciIdentity.SubsystemId)} / {Empty(node.PciIdentity.RevisionId)}");
        builder.AppendLine($"Class              : {Empty(node.Class.Code)} / {node.Class.DisplayName}");
        builder.AppendLine();
        AppendFields(builder, "Overview", BuildOverview(node, snapshot));
        AppendFields(builder, "Link And Capabilities", BuildLinkAndCapabilities(node));
        AppendFields(builder, "Resource Capability Summary", BuildResources(node));
        AppendFields(builder, "Driver", BuildDriver(node));
        builder.AppendLine("Identity");
        builder.AppendLine($"  Instance ID      : {node.Identity.InstanceId}");
        builder.AppendLine($"  Hardware IDs     : {Join(node.Identity.HardwareIds)}");
        builder.AppendLine($"  Compatible IDs   : {Join(node.Identity.CompatibleIds)}");
        builder.AppendLine($"  Location Paths   : {Join(node.Identity.LocationPaths)}");
        builder.AppendLine($"  Container ID     : {node.Identity.ContainerId?.ToString("D") ?? ""}");
        builder.AppendLine();
        builder.AppendLine("Diagnostics");
        var relevantDiagnostics = (diagnostics ?? snapshot.Diagnostics.Entries).Where(entry =>
            entry.NodeId is null || string.Equals(entry.NodeId, node.NodeId, StringComparison.OrdinalIgnoreCase));
        foreach (var diagnostic in relevantDiagnostics)
        {
            builder.AppendLine($"  {diagnostic.Severity} {diagnostic.Code}: {diagnostic.Message}");
        }

        return builder.ToString().TrimEnd();
    }

    public static string BuildDiagnosticReport(
        DeviceTopologyDiagnostic diagnostic,
        PciTopologySnapshot snapshot)
    {
        var builder = new StringBuilder();
        builder.AppendLine("PCI Express Standalone Diagnostic");
        builder.AppendLine($"Snapshot Generated : {snapshot.GeneratedAt:yyyy-MM-dd HH:mm:ss zzz}");
        builder.AppendLine($"Severity           : {diagnostic.Severity}");
        builder.AppendLine($"Code               : {diagnostic.Code}");
        builder.AppendLine($"Target Node ID     : {diagnostic.NodeId ?? ""}");
        builder.AppendLine($"Message            : {diagnostic.Message}");
        return builder.ToString().TrimEnd();
    }

    private static void AppendFields(
        StringBuilder builder,
        string title,
        IEnumerable<PciDiagnosticFieldView> fields)
    {
        builder.AppendLine(title);
        foreach (var field in fields)
        {
            builder.AppendLine($"  {field.Label,-28}: {field.Value} [{field.Source}]");
            if (!string.IsNullOrWhiteSpace(field.Note))
            {
                builder.AppendLine($"    Note: {field.Note}");
            }
        }

        builder.AppendLine();
    }

    private static PciDiagnosticFieldView Field<T>(string label, TopologyFieldValue<T> field)
    {
        return new PciDiagnosticFieldView(
            label,
            field.DisplayText,
            field.Source.ToString(),
            field.Note ?? field.Availability.ToString());
    }

    private static PciDiagnosticFieldView Reported(
        string label,
        string value,
        string source,
        string? note = null)
    {
        return new PciDiagnosticFieldView(label, value, source, note);
    }

    private static bool IsSyntheticRoot(PciTopologyNode node) =>
        string.Equals(node.Identity.Enumerator, "PCIROOT", StringComparison.OrdinalIgnoreCase);

    private static string IdentitySource(PciTopologyNode node, string value)
    {
        return IsSyntheticRoot(node) ? "Derived" : ValueSource(value, "SetupAPI");
    }

    private static string DisplayNameSource(PciTopologyNode node)
    {
        return IsSyntheticRoot(node) ? "Derived" : "SetupAPI FriendlyName / Description fallback";
    }

    private static string AddressSource(PciTopologyNode node)
    {
        if (node.Address is null)
        {
            return "Unavailable";
        }

        return node.RawBusNumber.HasValue && node.RawDeviceAddress.HasValue
            ? "SetupAPI device properties"
            : "Location Path / derived";
    }

    private static string ParsedSource(string first, string second, string source)
    {
        return string.IsNullOrWhiteSpace(first) && string.IsNullOrWhiteSpace(second)
            ? "Unavailable"
            : source;
    }

    private static string ValueSource(string? value, string source) =>
        string.IsNullOrWhiteSpace(value) ? "Unavailable" : source;

    private static string CollectionSource(IReadOnlyCollection<string> values, string source) =>
        values.Count == 0 ? "Unavailable" : source;

    private static string FormatRaw(uint? value)
    {
        return value.HasValue
            ? $"{value.Value.ToString(CultureInfo.InvariantCulture)} (0x{value.Value:X8})"
            : "未报告";
    }

    private static string JoinIdentity(string vendor, string device)
    {
        return string.IsNullOrWhiteSpace(vendor) && string.IsNullOrWhiteSpace(device)
            ? "未报告"
            : $"{Empty(vendor)} / {Empty(device)}";
    }

    private static string Join(IReadOnlyList<string> values)
    {
        return values.Count == 0 ? "未报告" : string.Join(Environment.NewLine, values);
    }

    private static string Empty(string value) => string.IsNullOrWhiteSpace(value) ? "未报告" : value;
}
