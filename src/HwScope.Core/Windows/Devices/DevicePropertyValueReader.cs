using System.Buffers.Binary;
using System.Text;

namespace HwScope.Core.Windows.Devices;

internal static class DevicePropertyValueReader
{
    private const uint TypeMask = 0x00000fff;
    private const uint TypeByte = 0x00000003;
    private const uint TypeUInt16 = 0x00000005;
    private const uint TypeUInt32 = 0x00000007;
    private const uint TypeGuid = 0x0000000d;
    private const uint TypeBoolean = 0x00000011;
    private const uint TypeString = 0x00000012;
    private const uint TypeModifierList = 0x00002000;

    public static string? GetString(
        IReadOnlyDictionary<DevicePropertyKey, NativeDeviceProperty> properties,
        DevicePropertyKey key)
    {
        if (!properties.TryGetValue(key, out var property) || BaseType(property.Type) != TypeString)
        {
            return null;
        }

        return DecodeUnicode(property.Data).Trim().Trim('\0');
    }

    public static IReadOnlyList<string> GetStringList(
        IReadOnlyDictionary<DevicePropertyKey, NativeDeviceProperty> properties,
        DevicePropertyKey key)
    {
        if (!properties.TryGetValue(key, out var property)
            || BaseType(property.Type) != TypeString
            || (property.Type & TypeModifierList) == 0)
        {
            return [];
        }

        return DecodeUnicode(property.Data)
            .Split('\0', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(value => value.Length > 0)
            .ToArray();
    }

    public static uint? GetUInt32(
        IReadOnlyDictionary<DevicePropertyKey, NativeDeviceProperty> properties,
        DevicePropertyKey key)
    {
        if (!properties.TryGetValue(key, out var property))
        {
            return null;
        }

        return BaseType(property.Type) switch
        {
            TypeByte when property.Data.Length >= 1 => property.Data[0],
            TypeUInt16 when property.Data.Length >= 2 => BinaryPrimitives.ReadUInt16LittleEndian(property.Data),
            TypeUInt32 when property.Data.Length >= 4 => BinaryPrimitives.ReadUInt32LittleEndian(property.Data),
            _ => null
        };
    }

    public static bool? GetBoolean(
        IReadOnlyDictionary<DevicePropertyKey, NativeDeviceProperty> properties,
        DevicePropertyKey key)
    {
        return properties.TryGetValue(key, out var property)
               && BaseType(property.Type) == TypeBoolean
               && property.Data.Length >= 1
            ? property.Data[0] != 0
            : null;
    }

    public static Guid? GetGuid(
        IReadOnlyDictionary<DevicePropertyKey, NativeDeviceProperty> properties,
        DevicePropertyKey key)
    {
        return properties.TryGetValue(key, out var property)
               && BaseType(property.Type) == TypeGuid
               && property.Data.Length >= 16
            ? new Guid(property.Data.AsSpan(0, 16))
            : null;
    }

    private static uint BaseType(uint type) => type & TypeMask;

    private static string DecodeUnicode(byte[] data)
    {
        var length = data.Length - (data.Length % 2);
        return length == 0 ? string.Empty : Encoding.Unicode.GetString(data, 0, length);
    }
}
