using System.Buffers.Binary;
using System.Text;
using HwScope.Core.Windows.Devices;

namespace HwScope.Core.Tests.Windows.Devices;

public sealed class DevicePropertyValueReaderTests
{
    [Fact]
    public void ReadsStringListAndUnsignedValues()
    {
        var stringKey = new DevicePropertyKey(Guid.NewGuid(), 1);
        var numberKey = new DevicePropertyKey(Guid.NewGuid(), 2);
        var numberBytes = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(numberBytes, 0x12345678);
        var properties = new Dictionary<DevicePropertyKey, NativeDeviceProperty>
        {
            [stringKey] = new(0x2012, Encoding.Unicode.GetBytes("PCI\\ONE\0PCI\\TWO\0\0")),
            [numberKey] = new(0x0007, numberBytes)
        };

        Assert.Equal(["PCI\\ONE", "PCI\\TWO"], DevicePropertyValueReader.GetStringList(properties, stringKey));
        Assert.Equal((uint)0x12345678, DevicePropertyValueReader.GetUInt32(properties, numberKey));
    }

    [Fact]
    public void RejectsMismatchedPropertyTypes()
    {
        var key = new DevicePropertyKey(Guid.NewGuid(), 1);
        var properties = new Dictionary<DevicePropertyKey, NativeDeviceProperty>
        {
            [key] = new(0x0012, Encoding.Unicode.GetBytes("text\0"))
        };

        Assert.Null(DevicePropertyValueReader.GetUInt32(properties, key));
        Assert.Empty(DevicePropertyValueReader.GetStringList(properties, key));
    }
}
