using System.Globalization;

namespace HwScope.Core.Hardware.Storage;

public sealed record StorageFieldValue<T>(
    T? Value,
    string DisplayText,
    StorageDataSource Source,
    bool IsAvailable,
    bool IsEstimated = false,
    string? Note = null);

public static class StorageField
{
    public const string UnknownText = "未报告";
    public const string UnsupportedText = "不支持";
    public const string PendingHealthText = "健康数据暂不可用";

    public static StorageFieldValue<string> Text(
        string? value,
        StorageDataSource source,
        string unavailable = UnknownText,
        bool isEstimated = false,
        string? note = null)
    {
        var cleaned = Clean(value);
        return cleaned.Length == 0
            ? new StorageFieldValue<string>(null, unavailable, StorageDataSource.Unknown, false, Note: note)
            : new StorageFieldValue<string>(cleaned, cleaned, source, true, isEstimated, note);
    }

    public static StorageFieldValue<int> Number(int? value, StorageDataSource source, string unavailable = UnknownText)
    {
        return value.HasValue
            ? new StorageFieldValue<int>(value.Value, value.Value.ToString(CultureInfo.InvariantCulture), source, true)
            : new StorageFieldValue<int>(default, unavailable, StorageDataSource.Unknown, false);
    }

    public static StorageFieldValue<ulong> Bytes(ulong value, StorageDataSource source, string unavailable = UnknownText)
    {
        return value > 0
            ? new StorageFieldValue<ulong>(value, FormatBinaryBytes(value), source, true, Note: $"{value.ToString(CultureInfo.InvariantCulture)} bytes")
            : new StorageFieldValue<ulong>(default, unavailable, StorageDataSource.Unknown, false);
    }

    public static StorageFieldValue<double> Temperature(double? value, StorageDataSource source, string unavailable = UnknownText)
    {
        return value is > -100 and < 300
            ? new StorageFieldValue<double>(value.Value, $"{value.Value.ToString("F0", CultureInfo.InvariantCulture)} °C", source, true)
            : new StorageFieldValue<double>(default, unavailable, StorageDataSource.Unknown, false);
    }

    public static StorageFieldValue<int> Percentage(int? value, StorageDataSource source, bool isEstimated = false, string? note = null)
    {
        return value is >= 0
            ? new StorageFieldValue<int>(value.Value, $"{value.Value}%", source, true, isEstimated, note)
            : new StorageFieldValue<int>(default, UnknownText, StorageDataSource.Unknown, false, Note: note);
    }

    public static StorageFieldValue<T> Placeholder<T>(string text = PendingHealthText)
    {
        return new StorageFieldValue<T>(default, text, StorageDataSource.Placeholder, false);
    }

    public static string FormatBinaryBytes(ulong bytes)
    {
        return FormatBytes(bytes, 1024, ["B", "KiB", "MiB", "GiB", "TiB", "PiB"]);
    }

    public static string FormatDecimalBytes(ulong bytes)
    {
        return FormatBytes(bytes, 1000, ["B", "KB", "MB", "GB", "TB", "PB"]);
    }

    private static string FormatBytes(ulong bytes, int divisor, IReadOnlyList<string> units)
    {
        var value = (decimal)bytes;
        var unit = 0;
        while (value >= divisor && unit < units.Count - 1)
        {
            value /= divisor;
            unit++;
        }

        var format = unit == 0 ? "0" : value >= 100 ? "0" : value >= 10 ? "0.0" : "0.00";
        return $"{value.ToString(format, CultureInfo.InvariantCulture)} {units[unit]}";
    }

    private static string Clean(string? value)
    {
        return string.IsNullOrWhiteSpace(value)
            ? string.Empty
            : string.Join(' ', value.Trim().Trim('\0').Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }
}
