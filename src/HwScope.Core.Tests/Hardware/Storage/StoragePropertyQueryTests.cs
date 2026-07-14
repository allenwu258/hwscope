using System.Buffers.Binary;
using System.Text;
using HwScope.Core.Hardware.Storage;
using HwScope.Core.Windows.Storage;

namespace HwScope.Core.Tests.Hardware.Storage;

public sealed class StoragePropertyQueryTests
{
    [Fact]
    public void TryParseDeviceDescriptor_ReadsOffsetsAndBus()
    {
        var buffer = new byte[128];
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(0, 4), 36);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(4, 4), (uint)buffer.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(12, 4), 36);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(16, 4), 44);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(20, 4), 58);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(24, 4), 62);
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(28, 4), 17);
        WriteAscii(buffer, 36, "Samsung");
        WriteAscii(buffer, 44, "Sample NVMe");
        WriteAscii(buffer, 58, "1.0");
        WriteAscii(buffer, 62, "SERIAL");

        var success = StoragePropertyQuery.TryParseDeviceDescriptor(buffer, out var descriptor, out var error);

        Assert.True(success, error?.Message);
        Assert.Equal(StorageBusKind.Nvme, descriptor.Bus);
        Assert.Equal("Sample NVMe", descriptor.Product);
        Assert.Equal("SERIAL", descriptor.SerialNumber);
    }

    [Fact]
    public void TryParseDeviceDescriptor_RejectsOutOfRangeOffset()
    {
        var buffer = new byte[36];
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(4, 4), 36);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(12, 4), 100);

        var success = StoragePropertyQuery.TryParseDeviceDescriptor(buffer, out _, out var error);

        Assert.False(success);
        Assert.Equal(StorageErrorKind.MalformedResponse, error?.Kind);
    }

    private static void WriteAscii(byte[] buffer, int offset, string text)
    {
        Encoding.ASCII.GetBytes(text).CopyTo(buffer, offset);
        buffer[offset + text.Length] = 0;
    }
}
