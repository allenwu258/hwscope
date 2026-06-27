using System.Text;

namespace HwScope.Core.Hardware.Cpu;

public static class CpuDetailReportFormatter
{
    public static string Format(CpuDetailReport report)
    {
        var builder = new StringBuilder();
        builder.AppendLine("CPU");
        builder.AppendLine("处理器详情");
        builder.AppendLine();

        AppendLine(builder, "处理器", report.Identity.SpecificationName);
        AppendLine(builder, "名称", report.Identity.DisplayName);
        AppendLine(builder, "厂商", report.Identity.Vendor);
        AppendLine(builder, "代号", report.Identity.CodeName);
        AppendLine(builder, "封装", report.Specification.Package);
        AppendLine(builder, "工艺", report.Specification.Technology);
        AppendLine(builder, "TDP", report.Specification.Tdp);
        AppendLine(builder, "核心电压", report.Specification.CoreVoltage);
        AppendLine(builder, "Family", report.Specification.Family);
        AppendLine(builder, "Model", report.Specification.Model);
        AppendLine(builder, "Stepping", report.Specification.Stepping);
        AppendLine(builder, "Ext. Family", report.Specification.ExtendedFamily);
        AppendLine(builder, "Ext. Model", report.Specification.ExtendedModel);
        AppendLine(builder, "Revision", report.Specification.Revision);
        builder.AppendLine();

        AppendLine(builder, "物理处理器", report.Topology.PackageCount);
        AppendLine(builder, "核心数", report.Topology.CoreCount);
        AppendLine(builder, "线程数", report.Topology.LogicalProcessorCount);
        AppendLine(builder, "SMT", report.Topology.SmtEnabled);
        AppendLine(builder, "CPU Groups", report.Topology.CpuGroupCount);
        AppendLine(builder, "NUMA Nodes", report.Topology.NumaNodeCount);
        builder.AppendLine();

        AppendLine(builder, "当前频率", report.Clocks.CurrentMHz);
        AppendLine(builder, "标称/最大频率", report.Clocks.BaseMHz);
        AppendLine(builder, "总线频率", report.Clocks.BusMHz);
        AppendLine(builder, "倍频", report.Clocks.Multiplier);
        builder.AppendLine();

        builder.AppendLine("缓存：");
        foreach (var cache in report.Caches)
        {
            builder.Append("  ");
            builder.Append(cache.Name);
            builder.Append("：");
            builder.AppendLine(FormatCache(cache));
        }
        builder.AppendLine();

        if (report.CoreMappings.Count > 0)
        {
            builder.AppendLine("核心映射：");
            foreach (var core in report.CoreMappings)
            {
                builder.Append("  Core ");
                builder.Append(core.CoreIndex.ToString("D2"));
                builder.Append("：SMT ");
                builder.Append(core.HasSmt ? "是" : "否");
                builder.Append("，Efficiency ");
                builder.Append(core.EfficiencyClass);
                builder.Append("，");
                builder.AppendLine(string.Join("; ", core.LogicalProcessors.Select(mask => mask.DisplayText)));
            }
            builder.AppendLine();
        }

        builder.AppendLine("指令集：");
        var features = report.Features.Where(feature => feature.IsSupported).Select(feature => feature.Name).ToList();
        builder.AppendLine(features.Count > 0 ? string.Join(", ", features) : CpuField.PendingCpuidText);
        builder.AppendLine();

        AppendLine(builder, "主板", report.Platform.Motherboard);
        AppendLine(builder, "BIOS", report.Platform.BiosVersion);
        AppendLine(builder, "芯片组", report.Platform.Chipset);
        AppendLine(builder, "集成显卡", report.Platform.IntegratedVideo);
        AppendLine(builder, "内存类型", report.Platform.MemoryType);
        AppendLine(builder, "内存频率", report.Platform.MemoryClock);
        AppendLine(builder, "DRAM:FSB", report.Platform.DramFsbRatio);
        builder.AppendLine();

        if (report.Notes.Count > 0)
        {
            builder.AppendLine("备注：");
            foreach (var note in report.Notes)
            {
                builder.Append("  - ");
                builder.AppendLine(note.Message);
            }
            builder.AppendLine();
        }

        builder.Append("生成时间：");
        builder.AppendLine(report.GeneratedAt.ToString("yyyy-MM-dd HH:mm:ss"));

        return builder.ToString().TrimEnd();
    }

    public static string FormatCache(CpuCacheInfo cache)
    {
        if (cache.SizeBytes is null or <= 0)
        {
            return CpuField.PendingCpuidText;
        }

        var size = FormatBytes(cache.SizeBytes.Value);
        var count = cache.InstanceCount is > 0 ? $"{cache.InstanceCount} x " : string.Empty;
        var ways = cache.Ways is > 0 ? $"，{cache.Ways}-way" : string.Empty;
        var line = cache.LineSizeBytes is > 0 ? $"，line {cache.LineSizeBytes} B" : string.Empty;
        var shared = cache.SharedLogicalProcessorCount is > 0 ? $"，shared {cache.SharedLogicalProcessorCount} LP" : string.Empty;
        return $"{count}{size}{ways}{line}{shared}";
    }

    private static void AppendLine<T>(StringBuilder builder, string label, CpuFieldValue<T> value)
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

    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB"];
        var value = (double)bytes;
        var index = 0;
        while (value >= 1024 && index < units.Length - 1)
        {
            value /= 1024;
            index++;
        }

        return index == 0 ? $"{bytes} {units[index]}" : $"{value:0.#} {units[index]}";
    }

    private static string FormatSource(CpuDataSource source, bool isEstimated)
    {
        var label = source switch
        {
            CpuDataSource.Wmi => "WMI",
            CpuDataSource.WindowsApi => "Windows API",
            CpuDataSource.Cpuid => "CPUID",
            CpuDataSource.Mapping => "映射",
            CpuDataSource.Computed => "推导",
            CpuDataSource.Placeholder => "待接入",
            _ => string.Empty
        };

        if (string.IsNullOrWhiteSpace(label))
        {
            return string.Empty;
        }

        return isEstimated ? $"{label}/估算" : label;
    }
}
