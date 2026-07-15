using System.Buffers.Binary;
using System.Text;
using HwScope.Core.Windows.Usb;

namespace HwScope.Core.Tests.Windows.Usb;

public sealed class UsbNativeBufferParserTests
{
    [Fact]
    public void ParseHubInformationReadsPackedOffsets()
    {
        var buffer = new byte[76];
        buffer[6] = 12;
        buffer[75] = 1;

        var result = UsbNativeBufferParser.ParseHubInformation(buffer);

        Assert.Equal(12, result.PortCount);
        Assert.True(result.IsBusPowered);
    }

    [Fact]
    public void ParseHubInformationRejectsShortBuffer()
    {
        Assert.Throws<InvalidDataException>(() => UsbNativeBufferParser.ParseHubInformation(new byte[75]));
    }

    [Fact]
    public void ParseConnectionInformationReadsDescriptorAndConnectionFields()
    {
        var buffer = new byte[35];
        BinaryPrimitives.WriteUInt32LittleEndian(buffer, 7);
        buffer[4] = 18;
        buffer[5] = 1;
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(6), 0x0320);
        buffer[8] = 0xEF;
        buffer[9] = 2;
        buffer[10] = 1;
        buffer[11] = 9;
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(12), 0x1234);
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(14), 0x5678);
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(16), 0x0102);
        buffer[18] = 1;
        buffer[19] = 2;
        buffer[20] = 3;
        buffer[21] = 4;
        buffer[22] = 1;
        buffer[23] = 3;
        buffer[24] = 1;
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(25), 42);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(27), 5);
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(31), 1);

        var result = UsbNativeBufferParser.ParseConnectionInformation(buffer);

        Assert.Equal((uint)7, result.ConnectionIndex);
        Assert.True(result.DeviceIsHub);
        Assert.Equal((ushort)42, result.DeviceAddress);
        Assert.Equal((uint)5, result.OpenPipeCount);
        Assert.Equal(0x1234, result.DeviceDescriptor!.VendorId);
        Assert.Equal(0x5678, result.DeviceDescriptor.ProductId);
        Assert.Equal((ushort)0x0320, result.DeviceDescriptor.UsbVersionBcd);
        Assert.Equal(4, result.DeviceDescriptor.ConfigurationCount);
    }

    [Fact]
    public void ParseConnectionInformationKeepsEmptyPortWithoutDescriptor()
    {
        var result = UsbNativeBufferParser.ParseConnectionInformation(new byte[35]);

        Assert.Null(result.DeviceDescriptor);
        Assert.Equal(0, result.ConnectionStatus);
    }

    [Fact]
    public void ParseConnectionInformationV2ReadsProtocolsAndFlags()
    {
        var buffer = new byte[16];
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(8), 0x7);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(12), 0xF);

        var result = UsbNativeBufferParser.ParseConnectionInformationV2(buffer);

        Assert.Equal((uint)0x7, result.SupportedProtocols);
        Assert.Equal((uint)0xF, result.Flags);
    }

    [Fact]
    public void ParseConnectorPropertiesUsesPackedNameOffset()
    {
        var name = Encoding.Unicode.GetBytes("USB-C\0");
        var buffer = new byte[16 + name.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(4), (uint)buffer.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(8), 0x9);
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(12), 2);
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(14), 4);
        name.CopyTo(buffer, 16);

        var result = UsbNativeBufferParser.ParseConnectorProperties(buffer);

        Assert.Equal((uint)0x9, result.Properties);
        Assert.Equal((ushort)2, result.CompanionIndex);
        Assert.Equal((ushort)4, result.CompanionPortNumber);
        Assert.Equal("USB-C", result.CompanionHubSymbolicName);
    }

    [Theory]
    [InlineData(14)]
    [InlineData(17)]
    [InlineData(64)]
    public void ParseConnectorPropertiesRejectsMalformedActualLength(int actualLength)
    {
        var buffer = new byte[20];
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(4), (uint)actualLength);

        Assert.Throws<InvalidDataException>(() => UsbNativeBufferParser.ParseConnectorProperties(buffer));
    }

    [Fact]
    public void ParseRootHubNameUsesLengthAtOffsetZero()
    {
        var buffer = BuildNameBuffer("ROOT", lengthOffset: 0, nameOffset: 4);

        Assert.Equal("ROOT", UsbNativeBufferParser.ParseVariableLengthName(buffer, "USB_ROOT_HUB_NAME"));
    }

    [Fact]
    public void ParseConnectionNameUsesLengthAtOffsetFour()
    {
        var buffer = BuildNameBuffer("HUB", lengthOffset: 4, nameOffset: 8);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer, 3);

        Assert.Equal("HUB", UsbNativeBufferParser.ParseConnectionVariableLengthName(buffer, "USB_NODE_CONNECTION_NAME"));
    }

    [Fact]
    public void ParseVariableNameRejectsOutOfBoundsLength()
    {
        var buffer = new byte[10];
        BinaryPrimitives.WriteUInt32LittleEndian(buffer, 12);

        Assert.Throws<InvalidDataException>(() => UsbNativeBufferParser.ParseVariableLengthName(buffer, "USB_ROOT_HUB_NAME"));
    }

    private static byte[] BuildNameBuffer(string value, int lengthOffset, int nameOffset)
    {
        var name = Encoding.Unicode.GetBytes(value + "\0");
        var buffer = new byte[nameOffset + name.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(lengthOffset), (uint)buffer.Length);
        name.CopyTo(buffer, nameOffset);
        return buffer;
    }
}
