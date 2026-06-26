using System.Globalization;

namespace HwScope.Core.Hardware.Cpu;

public sealed record CpuFieldValue<T>(
    T? Value,
    string DisplayText,
    CpuDataSource Source,
    bool IsAvailable,
    bool IsEstimated = false,
    string? Note = null);

public static class CpuField
{
    public const string UnknownText = "未识别";
    public const string PendingCpuidText = "待接入 native CPUID";

    public static CpuFieldValue<string> Text(
        string? value,
        CpuDataSource source,
        string unavailable = UnknownText,
        bool isEstimated = false,
        string? note = null)
    {
        return string.IsNullOrWhiteSpace(value)
            ? new CpuFieldValue<string>(null, unavailable, CpuDataSource.Unknown, IsAvailable: false, Note: note)
            : new CpuFieldValue<string>(value.Trim(), value.Trim(), source, IsAvailable: true, isEstimated, note);
    }

    public static CpuFieldValue<int> Number(
        int value,
        CpuDataSource source,
        string unavailable = UnknownText,
        bool isEstimated = false,
        string? note = null)
    {
        return value > 0
            ? new CpuFieldValue<int>(value, value.ToString(CultureInfo.InvariantCulture), source, IsAvailable: true, isEstimated, note)
            : new CpuFieldValue<int>(default, unavailable, CpuDataSource.Unknown, IsAvailable: false, Note: note);
    }

    public static CpuFieldValue<int> Number(
        uint value,
        CpuDataSource source,
        string unavailable = UnknownText,
        bool isEstimated = false,
        string? note = null)
    {
        return value <= int.MaxValue
            ? Number((int)value, source, unavailable, isEstimated, note)
            : new CpuFieldValue<int>(default, unavailable, CpuDataSource.Unknown, IsAvailable: false, Note: note);
    }

    public static CpuFieldValue<double> MHz(
        double? value,
        CpuDataSource source,
        bool isEstimated = false,
        string? note = null)
    {
        return value is > 0
            ? new CpuFieldValue<double>(value.Value, $"{value.Value.ToString("F1", CultureInfo.InvariantCulture)} MHz", source, IsAvailable: true, isEstimated, note)
            : new CpuFieldValue<double>(default, UnknownText, CpuDataSource.Unknown, IsAvailable: false, Note: note);
    }

    public static CpuFieldValue<double> Ratio(
        double? value,
        CpuDataSource source,
        bool isEstimated = false,
        string? note = null)
    {
        return value is > 0
            ? new CpuFieldValue<double>(value.Value, $"x {value.Value.ToString("F2", CultureInfo.InvariantCulture)}", source, IsAvailable: true, isEstimated, note)
            : new CpuFieldValue<double>(default, UnknownText, CpuDataSource.Unknown, IsAvailable: false, Note: note);
    }

    public static CpuFieldValue<bool> Boolean(
        bool? value,
        CpuDataSource source,
        string trueText = "是",
        string falseText = "否",
        string unavailable = UnknownText,
        bool isEstimated = false,
        string? note = null)
    {
        return value.HasValue
            ? new CpuFieldValue<bool>(value.Value, value.Value ? trueText : falseText, source, IsAvailable: true, isEstimated, note)
            : new CpuFieldValue<bool>(default, unavailable, CpuDataSource.Unknown, IsAvailable: false, Note: note);
    }

    public static CpuFieldValue<T> Placeholder<T>(string text = PendingCpuidText, string? note = null)
    {
        return new CpuFieldValue<T>(default, text, CpuDataSource.Placeholder, IsAvailable: false, Note: note);
    }
}
