using System.Buffers.Binary;
using HwScope.Core.Hardware.Storage;
using HwScope.Core.Windows.Storage;

namespace HwScope.Core.Tests.Hardware.Storage;

public sealed class NvmeProtocolQueryTests
{
    [Fact]
    public void ExtractProtocolPayload_ReturnsHealthBytes()
    {
        var output = new byte[560];
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(0, 4), 48);
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(4, 4), 48);
        BinaryPrimitives.WriteInt32LittleEndian(output.AsSpan(8, 4), 3);
        BinaryPrimitives.WriteInt32LittleEndian(output.AsSpan(12, 4), 2);
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(24, 4), 40);
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(28, 4), 512);
        output[48] = 0x04;

        var result = NvmeProtocolQuery.ExtractProtocolPayload(new StorageNativeQueryResult(output, output.Length, null), 512);

        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal(512, result.BytesReturned);
        Assert.Equal(0x04, result.Buffer[0]);
    }

    [Fact]
    public void ExtractProtocolPayload_RejectsOverflowingRange()
    {
        var output = new byte[64];
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(0, 4), 48);
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(4, 4), 48);
        BinaryPrimitives.WriteInt32LittleEndian(output.AsSpan(8, 4), 3);
        BinaryPrimitives.WriteInt32LittleEndian(output.AsSpan(12, 4), 2);
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(24, 4), uint.MaxValue);
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(28, 4), 512);

        var result = NvmeProtocolQuery.ExtractProtocolPayload(new StorageNativeQueryResult(output, output.Length, null), 512);

        Assert.False(result.Success);
        Assert.Equal(StorageErrorKind.MalformedResponse, result.Error?.Kind);
    }
}
