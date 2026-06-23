using System.Text;

namespace HwScope.Core.Hardware;

public static class HardwareReportFormatter
{
    public static string FormatSummary(HardwareReport report)
    {
        var builder = new StringBuilder();
        builder.AppendLine("详细信息");
        AppendLine(builder, "处理器", report.Processor);
        AppendLine(builder, "主板", report.Motherboard);
        AppendLine(builder, "内存", report.Memory);
        AppendLine(builder, "显卡", report.Graphics);
        AppendLine(builder, "显示器", report.Display);
        AppendLine(builder, "硬盘", report.Disk);
        AppendLine(builder, "声卡", report.Audio);
        AppendLine(builder, "网卡", report.Network);
        return builder.ToString().TrimEnd();
    }

    private static void AppendLine(StringBuilder builder, string label, string value)
    {
        builder.Append(label);
        builder.Append("：");
        builder.AppendLine(value);
    }

    private static void AppendLine(StringBuilder builder, string label, IReadOnlyList<string> values)
    {
        if (values.Count == 0)
        {
            AppendLine(builder, label, "未识别");
            return;
        }

        AppendLine(builder, label, values[0]);
        foreach (var value in values.Skip(1))
        {
            builder.Append(' ', 5);
            builder.AppendLine(value);
        }
    }
}

