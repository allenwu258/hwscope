using System.Management;
using System.Runtime.InteropServices;

namespace HwScope.Core.Windows;

internal static class Wmi
{
    public static IEnumerable<ManagementObject> Query(string query, string scope = @"root\cimv2")
    {
        using var searcher = new ManagementObjectSearcher(scope, query);
        ManagementObjectCollection results;

        try
        {
            results = searcher.Get();
        }
        catch (ManagementException)
        {
            yield break;
        }
        catch (UnauthorizedAccessException)
        {
            yield break;
        }
        catch (COMException)
        {
            yield break;
        }

        foreach (ManagementObject result in results)
        {
            yield return result;
        }
    }

    public static string GetString(ManagementBaseObject? obj, string propertyName)
    {
        if (obj is null)
        {
            return string.Empty;
        }

        try
        {
            return obj[propertyName]?.ToString() ?? string.Empty;
        }
        catch (ManagementException)
        {
            return string.Empty;
        }
    }

    public static uint GetUInt(ManagementBaseObject? obj, string propertyName)
    {
        if (obj is null)
        {
            return 0;
        }

        try
        {
            return obj[propertyName] switch
            {
                uint value => value,
                ushort value => value,
                int value when value > 0 => (uint)value,
                string value when uint.TryParse(value, out var parsed) => parsed,
                _ => 0
            };
        }
        catch (ManagementException)
        {
            return 0;
        }
    }

    public static ulong GetULong(ManagementBaseObject? obj, string propertyName)
    {
        if (obj is null)
        {
            return 0;
        }

        try
        {
            return obj[propertyName] switch
            {
                ulong value => value,
                long value when value > 0 => (ulong)value,
                uint value => value,
                int value when value > 0 => (ulong)value,
                string value when ulong.TryParse(value, out var parsed) => parsed,
                _ => 0
            };
        }
        catch (ManagementException)
        {
            return 0;
        }
    }

    public static int? GetNullableInt(ManagementBaseObject? obj, string propertyName)
    {
        var value = GetRawValue(obj, propertyName);
        return value switch
        {
            int number => number,
            uint number when number <= int.MaxValue => (int)number,
            short number => number,
            ushort number => number,
            long number when number is >= int.MinValue and <= int.MaxValue => (int)number,
            ulong number when number <= int.MaxValue => (int)number,
            string text when int.TryParse(text, out var parsed) => parsed,
            _ => null
        };
    }

    public static uint? GetNullableUInt(ManagementBaseObject? obj, string propertyName)
    {
        var value = GetRawValue(obj, propertyName);
        return value switch
        {
            uint number => number,
            int number when number >= 0 => (uint)number,
            ushort number => number,
            short number when number >= 0 => (uint)number,
            ulong number when number <= uint.MaxValue => (uint)number,
            long number when number is >= 0 and <= uint.MaxValue => (uint)number,
            string text when uint.TryParse(text, out var parsed) => parsed,
            _ => null
        };
    }

    public static bool? GetNullableBool(ManagementBaseObject? obj, string propertyName)
    {
        var value = GetRawValue(obj, propertyName);
        return value switch
        {
            bool boolean => boolean,
            string text when bool.TryParse(text, out var parsed) => parsed,
            byte number => number != 0,
            int number => number != 0,
            uint number => number != 0,
            _ => null
        };
    }

    private static object? GetRawValue(ManagementBaseObject? obj, string propertyName)
    {
        if (obj is null)
        {
            return null;
        }

        try
        {
            var value = obj[propertyName];
            return value is null or DBNull ? null : value;
        }
        catch (ManagementException)
        {
            return null;
        }
    }
}
