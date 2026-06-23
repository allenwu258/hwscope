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
}

