using System.Globalization;
using System.Numerics;
using HwScope.Core.Hardware.Storage.Nvme;

namespace HwScope.Core.Hardware.Storage;

internal sealed record NvmeHealthEvaluation(
    StorageHealthSummary Health,
    StorageLifetimeStatistics Lifetime,
    IReadOnlyList<StorageProtocolAttribute> Attributes,
    IReadOnlyList<StorageDataNote> Notes);

internal static class StorageHealthEvaluator
{
    private const byte KnownCriticalWarningMask = 0x3f;

    public static NvmeHealthEvaluation EvaluateNvme(NvmeSmartHealthLog log)
    {
        var flags = DecodeCriticalWarning(log.CriticalWarning).ToList();
        var status = StorageHealthStatus.Good;
        var reasons = new List<string>();

        if ((log.CriticalWarning & 0x0c) != 0)
        {
            status = StorageHealthStatus.Critical;
            reasons.Add("NVMe 报告可靠性下降或介质进入只读模式。");
        }
        else if (log.CriticalWarning != 0)
        {
            status = StorageHealthStatus.Caution;
            reasons.Add("NVMe Critical Warning 包含警告位。");
        }

        if (log.AvailableSparePercent < log.AvailableSpareThresholdPercent)
        {
            status = Max(status, StorageHealthStatus.Caution);
            reasons.Add("可用备用空间低于阈值。");
        }

        if (log.PercentageUsed >= 100)
        {
            status = Max(status, StorageHealthStatus.Caution);
            reasons.Add("NVMe Percentage Used 已达到或超过 100%。");
        }

        if (log.MediaAndDataIntegrityErrors > 0)
        {
            status = Max(status, StorageHealthStatus.Caution);
            reasons.Add("设备报告了累计介质或数据完整性错误。");
        }

        if ((log.CriticalWarning & ~KnownCriticalWarningMask) != 0)
        {
            flags.Add($"Unknown critical warning bits: 0x{log.CriticalWarning & ~KnownCriticalWarningMask:X2}");
        }

        var temperature = KelvinToCelsius(log.CompositeTemperatureKelvin);
        var remainingLife = Math.Max(0, 100 - Math.Min(log.PercentageUsed, (byte)100));
        var health = new StorageHealthSummary(
            status,
            FormatStatus(status),
            reasons.Count == 0 ? "NVMe 标准健康字段未报告异常。" : string.Join(' ', reasons),
            StorageField.Temperature(temperature, StorageDataSource.Nvme),
            StorageField.Percentage(remainingLife, StorageDataSource.Nvme, isEstimated: true, note: "由 NVMe Percentage Used 计算。"),
            flags);

        var lifetime = new StorageLifetimeStatistics(
            CounterBytes(log.DataUnitsRead, "data units", "Host Reads"),
            CounterBytes(log.DataUnitsWritten, "data units", "Host Writes"),
            Counter(log.PowerCycles),
            Counter(log.PowerOnHours, "小时"),
            Counter(log.UnsafeShutdowns),
            Counter(log.MediaAndDataIntegrityErrors),
            Counter(log.ErrorInformationLogEntries));

        return new NvmeHealthEvaluation(
            health,
            lifetime,
            BuildAttributes(log, temperature),
            [new StorageDataNote("NVMe 累计计数来自标准 SMART / Health Information log page 0x02。", StorageDataSource.Nvme)]);
    }

    private static IReadOnlyList<StorageProtocolAttribute> BuildAttributes(NvmeSmartHealthLog log, double? temperature)
    {
        var rows = new List<StorageProtocolAttribute>
        {
            Attribute("01", "严重警告标志", log.CriticalWarning == 0 ? "无" : string.Join(", ", DecodeCriticalWarning(log.CriticalWarning)), null, $"0x{log.CriticalWarning:X2}", log.CriticalWarning == 0 ? StorageAttributeSeverity.Normal : StorageAttributeSeverity.Caution),
            Attribute("02", "综合温度", temperature.HasValue ? temperature.Value.ToString("F0", CultureInfo.InvariantCulture) : StorageField.UnknownText, "°C", $"0x{log.CompositeTemperatureKelvin:X4}"),
            Attribute("03", "可用备用空间", log.AvailableSparePercent.ToString(CultureInfo.InvariantCulture), "%", $"0x{log.AvailableSparePercent:X2}", log.AvailableSparePercent < log.AvailableSpareThresholdPercent ? StorageAttributeSeverity.Caution : StorageAttributeSeverity.Normal),
            Attribute("04", "可用备用空间阈值", log.AvailableSpareThresholdPercent.ToString(CultureInfo.InvariantCulture), "%", $"0x{log.AvailableSpareThresholdPercent:X2}"),
            Attribute("05", "已用寿命百分比", log.PercentageUsed.ToString(CultureInfo.InvariantCulture), "%", $"0x{log.PercentageUsed:X2}", log.PercentageUsed >= 100 ? StorageAttributeSeverity.Caution : StorageAttributeSeverity.Normal),
            CounterAttribute("06", "读取数据单位计数", log.DataUnitsRead),
            CounterAttribute("07", "写入数据单位计数", log.DataUnitsWritten),
            CounterAttribute("08", "主机读命令计数", log.HostReadCommands),
            CounterAttribute("09", "主机写命令计数", log.HostWriteCommands),
            CounterAttribute("0A", "控制器忙状态时间", log.ControllerBusyMinutes, "分钟"),
            CounterAttribute("0B", "启动-关闭循环次数", log.PowerCycles),
            CounterAttribute("0C", "通电时间", log.PowerOnHours, "小时"),
            CounterAttribute("0D", "不安全关机计数", log.UnsafeShutdowns),
            CounterAttribute("0E", "介质与数据完整性错误计数", log.MediaAndDataIntegrityErrors, severity: log.MediaAndDataIntegrityErrors > 0 ? StorageAttributeSeverity.Caution : StorageAttributeSeverity.Normal),
            CounterAttribute("0F", "错误日志项数", log.ErrorInformationLogEntries)
        };

        for (var i = 0; i < log.TemperatureSensorsKelvin.Count; i++)
        {
            var sensor = KelvinToCelsius(log.TemperatureSensorsKelvin[i]);
            if (!sensor.HasValue)
            {
                continue;
            }

            rows.Add(Attribute($"T{i + 1}", $"温度传感器 {i + 1}", sensor.Value.ToString("F0", CultureInfo.InvariantCulture), "°C", $"0x{log.TemperatureSensorsKelvin[i]:X4}"));
        }

        return rows;
    }

    private static StorageProtocolAttribute Attribute(
        string id,
        string name,
        string value,
        string? unit,
        string raw,
        StorageAttributeSeverity severity = StorageAttributeSeverity.Normal)
    {
        return new StorageProtocolAttribute(id, name, severity, value, unit, raw, null, null, null, StorageDataSource.Nvme);
    }

    private static StorageProtocolAttribute CounterAttribute(
        string id,
        string name,
        UInt128 value,
        string? unit = null,
        StorageAttributeSeverity severity = StorageAttributeSeverity.Normal)
    {
        return Attribute(id, name, value.ToString(CultureInfo.InvariantCulture), unit, $"0x{value:X32}", severity);
    }

    private static StorageFieldValue<string> Counter(UInt128 value, string? unit = null)
    {
        var text = value.ToString(CultureInfo.InvariantCulture);
        return StorageField.Text(unit is null ? text : $"{text} {unit}", StorageDataSource.Nvme);
    }

    private static StorageFieldValue<string> CounterBytes(UInt128 dataUnits, string rawUnit, string label)
    {
        var exactUnits = dataUnits.ToString(CultureInfo.InvariantCulture);
        var bytes = BigInteger.Parse(exactUnits, CultureInfo.InvariantCulture) * 512_000;
        var display = FormatBigBytes(bytes);
        return new StorageFieldValue<string>(display, display, StorageDataSource.Nvme, true, Note: $"{label}: {exactUnits} {rawUnit}; 1 data unit = 512,000 bytes.");
    }

    private static string FormatBigBytes(BigInteger bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB", "PB", "EB"];
        var divisor = BigInteger.One;
        var unit = 0;
        while (unit < units.Length - 1 && bytes >= divisor * 1000)
        {
            divisor *= 1000;
            unit++;
        }

        if (unit == 0)
        {
            return $"{bytes.ToString(CultureInfo.InvariantCulture)} B";
        }

        var whole = BigInteger.DivRem(bytes * 10, divisor, out _);
        return $"{(double)whole / 10:0.0} {units[unit]}";
    }

    private static IEnumerable<string> DecodeCriticalWarning(byte warning)
    {
        if ((warning & 0x01) != 0) yield return "备用空间低于阈值";
        if ((warning & 0x02) != 0) yield return "温度超出阈值";
        if ((warning & 0x04) != 0) yield return "可靠性下降";
        if ((warning & 0x08) != 0) yield return "介质只读";
        if ((warning & 0x10) != 0) yield return "易失内存备份失败";
        if ((warning & 0x20) != 0) yield return "持久内存区域只读";
    }

    private static double? KelvinToCelsius(ushort kelvin)
    {
        if (kelvin == 0 || kelvin == ushort.MaxValue)
        {
            return null;
        }

        var value = kelvin - 273.15;
        return value is > -100 and < 300 ? value : null;
    }

    private static StorageHealthStatus Max(StorageHealthStatus left, StorageHealthStatus right)
    {
        return Rank(left) >= Rank(right) ? left : right;
    }

    private static int Rank(StorageHealthStatus status)
    {
        return status switch
        {
            StorageHealthStatus.Critical => 4,
            StorageHealthStatus.Caution => 3,
            StorageHealthStatus.Good => 2,
            StorageHealthStatus.Unknown => 1,
            _ => 0
        };
    }

    public static string FormatStatus(StorageHealthStatus status)
    {
        return status switch
        {
            StorageHealthStatus.Good => "良好",
            StorageHealthStatus.Caution => "注意",
            StorageHealthStatus.Critical => "严重",
            StorageHealthStatus.Unsupported => "不支持",
            _ => "未知"
        };
    }
}
