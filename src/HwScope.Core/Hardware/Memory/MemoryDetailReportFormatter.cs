using System.Text;

namespace HwScope.Core.Hardware.Memory;

public static class MemoryDetailReportFormatter
{
    public static string Format(MemoryDetailReport report)
    {
        var builder = new StringBuilder();
        builder.AppendLine("Memory / SPD");
        builder.AppendLine();

        builder.AppendLine("Summary");
        AppendLine(builder, "类型", report.Summary.Type);
        AppendLine(builder, "总容量", report.Summary.TotalCapacity);
        AppendLine(builder, "模块数", report.Summary.ModuleCount);
        AppendLine(builder, "布局", report.Summary.Layout);
        AppendLine(builder, "配置速率", report.Summary.ConfiguredSpeed);
        AppendLine(builder, "通道模式", report.Summary.ChannelMode);
        builder.AppendLine("SPD 状态：SPD 读取暂未实现");
        builder.AppendLine();

        builder.AppendLine("Runtime");
        AppendLine(builder, "当前内存频率", report.Runtime.ClockMHz);
        AppendLine(builder, "有效速率", report.Runtime.EffectiveRate);
        AppendLine(builder, "Ratio", report.Runtime.Ratio);
        AppendLine(builder, "CAS Latency", report.Runtime.PrimaryTimings.CasLatency);
        AppendLine(builder, "tRCD", report.Runtime.PrimaryTimings.Trcd);
        AppendLine(builder, "tRP", report.Runtime.PrimaryTimings.Trp);
        AppendLine(builder, "tRAS", report.Runtime.PrimaryTimings.Tras);
        AppendLine(builder, "tRC", report.Runtime.PrimaryTimings.Trc);
        AppendLine(builder, "Command Rate", report.Runtime.PrimaryTimings.CommandRate);
        builder.AppendLine();

        for (var i = 0; i < report.Modules.Count; i++)
        {
            var module = report.Modules[i];
            builder.AppendLine($"Module {i + 1}");
            AppendLine(builder, "插槽", module.Identity.Slot);
            AppendLine(builder, "模块名称", module.Identity.DisplayName);
            AppendLine(builder, "容量", module.Identity.Capacity);
            AppendLine(builder, "模块类型", module.Identity.ModuleType);
            AppendLine(builder, "存取类型", module.Identity.MemoryType);
            AppendLine(builder, "最大带宽", module.Identity.MaxBandwidth);
            AppendLine(builder, "模块制造商", module.Identity.Manufacturer);
            AppendLine(builder, "DRAM 制造商", module.Identity.DramManufacturer);
            AppendLine(builder, "Part Number", module.Identity.PartNumber);
            AppendLine(builder, "序列号", module.Identity.SerialNumber);
            AppendLine(builder, "生产日期", module.Identity.ManufacturingDate);
            AppendLine(builder, "Revision", module.Identity.Revision);
            AppendLine(builder, "Data Width", module.Organization.DataWidth);
            AppendLine(builder, "Total Width", module.Organization.TotalWidth);
            AppendLine(builder, "ECC", module.Organization.Ecc);
            AppendLine(builder, "Configured Voltage", module.Voltages.ConfiguredVoltage);
            AppendLine(builder, "Min Voltage", module.Voltages.MinVoltage);
            AppendLine(builder, "Max Voltage", module.Voltages.MaxVoltage);

            builder.AppendLine("Timing Profiles");
            foreach (var profile in module.TimingProfiles)
            {
                builder.Append("  ");
                builder.Append(profile.Name);
                builder.Append(": ");
                builder.Append(profile.Frequency.DisplayText);
                builder.Append(" / CL ");
                builder.Append(profile.CasLatency.DisplayText);
                builder.Append(" / ");
                builder.Append(profile.Voltage.DisplayText);
                builder.Append(" [");
                builder.Append(FormatSource(profile.Source, isEstimated: false));
                builder.AppendLine("]");
            }

            if (module.Notes.Count > 0)
            {
                builder.AppendLine("Module Notes");
                foreach (var note in module.Notes)
                {
                    builder.Append("  - ");
                    builder.AppendLine(note.Message);
                }
            }

            builder.AppendLine();
        }

        if (report.Notes.Count > 0)
        {
            builder.AppendLine("Notes");
            foreach (var note in report.Notes)
            {
                builder.Append("  - ");
                builder.AppendLine(note.Message);
            }

            builder.AppendLine();
        }

        builder.Append("Generated At: ");
        builder.AppendLine(report.GeneratedAt.ToString("yyyy-MM-dd HH:mm:ss"));

        return builder.ToString().TrimEnd();
    }

    private static void AppendLine<T>(StringBuilder builder, string label, MemoryFieldValue<T> value)
    {
        builder.Append(label);
        builder.Append("：");
        builder.Append(value.DisplayText);

        var source = FormatSource(value.Source, value.IsEstimated);
        if (!string.IsNullOrWhiteSpace(source))
        {
            builder.Append(" [");
            builder.Append(source);
            builder.Append(']');
        }

        builder.AppendLine();
    }

    private static string FormatSource(MemoryDataSource source, bool isEstimated)
    {
        var label = source switch
        {
            MemoryDataSource.Wmi => "WMI",
            MemoryDataSource.Smbios => "SMBIOS",
            MemoryDataSource.MemoryController => "控制器",
            MemoryDataSource.Computed => "推导",
            MemoryDataSource.Mapping => "映射",
            MemoryDataSource.Placeholder => "待接入",
            _ => string.Empty
        };

        if (string.IsNullOrWhiteSpace(label))
        {
            return string.Empty;
        }

        return isEstimated ? $"{label}/估算" : label;
    }
}
