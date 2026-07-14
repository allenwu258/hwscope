using System.Globalization;

namespace HwScope.Core.Hardware.Storage.Ata;

internal sealed record AtaSmartEvaluation(
    StorageHealthSummary Health,
    StorageLifetimeStatistics Lifetime,
    IReadOnlyList<StorageProtocolAttribute> Attributes,
    IReadOnlyList<StorageDataNote> Notes);

internal static class AtaSmartEvaluator
{
    private static readonly IReadOnlyDictionary<byte, string> AttributeNames = new Dictionary<byte, string>
    {
        [0x05] = "重映射扇区计数",
        [0x09] = "通电时间",
        [0x0C] = "通电次数",
        [0xC2] = "温度",
        [0xC4] = "重映射事件计数",
        [0xC5] = "待映射扇区计数",
        [0xC6] = "脱机不可校正扇区计数",
        [0xC7] = "UDMA CRC 错误计数"
    };

    private static readonly HashSet<byte> MediaWarningAttributes = [0x05, 0xC4, 0xC5, 0xC6];

    public static AtaSmartEvaluation Evaluate(
        AtaSmartData data,
        bool? overallHealthPassed = null,
        StorageError? overallStatusError = null)
    {
        var status = overallHealthPassed switch
        {
            true => StorageHealthStatus.Good,
            false => StorageHealthStatus.Critical,
            _ => StorageHealthStatus.Unknown
        };
        var reasons = new List<string>();
        if (overallHealthPassed == false)
        {
            reasons.Add("ATA SMART overall status 报告设备健康检查失败。");
        }
        else if (!overallHealthPassed.HasValue)
        {
            reasons.Add(overallStatusError is null
                ? "ATA SMART overall status 未报告。"
                : $"ATA SMART overall status 不可用：{overallStatusError.Message}");
        }

        foreach (var attribute in data.Attributes)
        {
            if (attribute.IsPreFailure && attribute.Threshold is > 0 && attribute.Current <= attribute.Threshold)
            {
                status = StorageHealthStatus.Critical;
                reasons.Add($"SMART 属性 {attribute.Id:X2} 已达到阈值。");
            }
            else if (MediaWarningAttributes.Contains(attribute.Id) && attribute.RawValue > 0)
            {
                status = Max(status, StorageHealthStatus.Caution);
                reasons.Add($"SMART 属性 {attribute.Id:X2} 报告非零累计值。");
            }
            else if (attribute.Id == 0xC7 && attribute.RawValue > 0)
            {
                status = Max(status, StorageHealthStatus.Caution);
                reasons.Add("检测到 UDMA CRC 错误；可能与线缆或链路有关。 ");
            }
        }

        var temperature = TryGetTemperature(data.Attributes.FirstOrDefault(attribute => attribute.Id == 0xC2));
        var health = new StorageHealthSummary(
            status,
            StorageHealthEvaluator.FormatStatus(status),
            reasons.Count == 0 ? "ATA SMART overall status、属性和阈值未报告已知异常。" : string.Join(' ', reasons.Distinct()),
            StorageField.Temperature(temperature, StorageDataSource.AtaSmart),
            StorageField.Placeholder<int>("ATA 未提供通用寿命百分比"),
            []);

        var powerHours = data.Attributes.FirstOrDefault(attribute => attribute.Id == 0x09)?.RawValue;
        var powerCycles = data.Attributes.FirstOrDefault(attribute => attribute.Id == 0x0C)?.RawValue;
        var mediaErrors = data.Attributes
            .Where(attribute => MediaWarningAttributes.Contains(attribute.Id))
            .Aggregate<AtaSmartAttribute, ulong>(0, (sum, attribute) => sum + attribute.RawValue);

        var lifetime = new StorageLifetimeStatistics(
            StorageField.Placeholder<string>(),
            StorageField.Placeholder<string>(),
            FormatCounter(powerCycles),
            FormatCounter(powerHours, "小时"),
            StorageField.Placeholder<string>(),
            FormatCounter(mediaErrors),
            StorageField.Placeholder<string>());

        var rows = data.Attributes.Select(ToRow).ToList();
        return new AtaSmartEvaluation(
            health,
            lifetime,
            rows,
            [new StorageDataNote("ATA SMART raw bytes 具有厂商差异；仅对少数常见属性进行保守解码。", StorageDataSource.AtaSmart)]);
    }

    private static StorageHealthStatus Max(StorageHealthStatus left, StorageHealthStatus right)
    {
        static int Rank(StorageHealthStatus value) => value switch
        {
            StorageHealthStatus.Critical => 4,
            StorageHealthStatus.Caution => 3,
            StorageHealthStatus.Good => 2,
            StorageHealthStatus.Unknown => 1,
            _ => 0
        };

        return Rank(left) >= Rank(right) ? left : right;
    }

    private static StorageProtocolAttribute ToRow(AtaSmartAttribute attribute)
    {
        var severity = StorageAttributeSeverity.Normal;
        if (attribute.IsPreFailure && attribute.Threshold is > 0 && attribute.Current <= attribute.Threshold)
        {
            severity = StorageAttributeSeverity.Critical;
        }
        else if ((MediaWarningAttributes.Contains(attribute.Id) || attribute.Id == 0xC7) && attribute.RawValue > 0)
        {
            severity = StorageAttributeSeverity.Caution;
        }

        var decoded = attribute.Id == 0xC2 && TryGetTemperature(attribute) is { } temperature
            ? temperature.ToString("F0", CultureInfo.InvariantCulture)
            : attribute.RawValue.ToString(CultureInfo.InvariantCulture);
        var unit = attribute.Id switch
        {
            0x09 => "小时",
            0xC2 => "°C",
            _ => null
        };
        var raw = string.Concat(attribute.RawBytes.Reverse().Select(value => value.ToString("X2", CultureInfo.InvariantCulture)));
        return new StorageProtocolAttribute(
            attribute.Id.ToString("X2", CultureInfo.InvariantCulture),
            AttributeNames.TryGetValue(attribute.Id, out var name) ? name : "厂商属性",
            severity,
            decoded,
            unit,
            raw,
            attribute.Current,
            attribute.Worst,
            attribute.Threshold,
            StorageDataSource.AtaSmart,
            attribute.IsPreFailure ? "Pre-failure attribute" : "Advisory attribute");
    }

    private static double? TryGetTemperature(AtaSmartAttribute? attribute)
    {
        if (attribute is null || attribute.RawBytes.Length == 0)
        {
            return null;
        }

        var value = attribute.RawBytes[0];
        return value is > 0 and < 150 ? value : null;
    }

    private static StorageFieldValue<string> FormatCounter(ulong? value, string? unit = null)
    {
        if (!value.HasValue)
        {
            return StorageField.Placeholder<string>();
        }

        var display = value.Value.ToString(CultureInfo.InvariantCulture);
        return StorageField.Text(unit is null ? display : $"{display} {unit}", StorageDataSource.AtaSmart);
    }
}
