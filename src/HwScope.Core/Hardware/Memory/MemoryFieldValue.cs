using System.Globalization;

namespace HwScope.Core.Hardware.Memory;

public sealed record MemoryFieldValue<T>(
    T? Value,
    string DisplayText,
    MemoryDataSource Source,
    bool IsAvailable,
    bool IsEstimated = false,
    string? Note = null);

public static class MemoryField
{
    public const string UnknownText = "未识别";
    public const string PendingSpdText = "待接入 SPD 读取";
    public const string PendingControllerText = "待接入内存控制器读取";

    public static MemoryFieldValue<string> Text(
        string? value,
        MemoryDataSource source,
        string unavailable = UnknownText,
        bool isEstimated = false,
        string? note = null)
    {
        return string.IsNullOrWhiteSpace(value)
            ? new MemoryFieldValue<string>(null, unavailable, MemoryDataSource.Unknown, IsAvailable: false, Note: note)
            : new MemoryFieldValue<string>(value.Trim(), value.Trim(), source, IsAvailable: true, isEstimated, note);
    }

    public static MemoryFieldValue<int> Number(
        int? value,
        MemoryDataSource source,
        string unavailable = UnknownText,
        bool isEstimated = false,
        string? note = null)
    {
        return value is > 0
            ? new MemoryFieldValue<int>(value.Value, value.Value.ToString(CultureInfo.InvariantCulture), source, IsAvailable: true, isEstimated, note)
            : new MemoryFieldValue<int>(default, unavailable, MemoryDataSource.Unknown, IsAvailable: false, Note: note);
    }

    public static MemoryFieldValue<string> Bytes(ulong value, MemoryDataSource source, bool isEstimated = false, string? note = null)
    {
        return value > 0
            ? new MemoryFieldValue<string>(FormatBytes(value), FormatBytes(value), source, IsAvailable: true, isEstimated, note)
            : new MemoryFieldValue<string>(null, UnknownText, MemoryDataSource.Unknown, IsAvailable: false, Note: note);
    }

    public static MemoryFieldValue<string> MegaTransfers(uint value, MemoryDataSource source, bool isEstimated = false, string? note = null)
    {
        return value > 0
            ? new MemoryFieldValue<string>($"{value} MT/s", $"{value} MT/s", source, IsAvailable: true, isEstimated, note)
            : new MemoryFieldValue<string>(null, UnknownText, MemoryDataSource.Unknown, IsAvailable: false, Note: note);
    }

    public static MemoryFieldValue<double> MegaHertz(double? value, MemoryDataSource source, bool isEstimated = false, string? note = null)
    {
        return value is > 0
            ? new MemoryFieldValue<double>(value.Value, $"{value.Value.ToString("F1", CultureInfo.InvariantCulture)} MHz", source, IsAvailable: true, isEstimated, note)
            : new MemoryFieldValue<double>(default, UnknownText, MemoryDataSource.Unknown, IsAvailable: false, Note: note);
    }

    public static MemoryFieldValue<string> Millivolts(uint value, MemoryDataSource source, string unavailable = UnknownText)
    {
        return value > 0
            ? new MemoryFieldValue<string>($"{value / 1000.0:F2} V", $"{value / 1000.0:F2} V", source, IsAvailable: true)
            : new MemoryFieldValue<string>(null, unavailable, MemoryDataSource.Unknown, IsAvailable: false);
    }

    public static MemoryFieldValue<T> Placeholder<T>(string text)
    {
        return new MemoryFieldValue<T>(default, text, MemoryDataSource.Placeholder, IsAvailable: false);
    }

    public static string FormatBytes(ulong bytes)
    {
        if (bytes == 0)
        {
            return UnknownText;
        }

        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var value = (decimal)bytes;
        var index = 0;
        while (value >= 1024 && index < units.Length - 1)
        {
            value /= 1024;
            index++;
        }

        return index == 0
            ? $"{bytes} {units[index]}"
            : $"{Math.Round(value, 1).ToString("0.#", CultureInfo.InvariantCulture)} {units[index]}";
    }
}
