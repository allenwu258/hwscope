using HwScope.Core.Hardware.DeviceTopology.Usb;
using HwScope.Core.Windows.Usb;

namespace HwScope.Core.Tests.Windows.Usb;

public sealed class UsbDescriptorParserTests
{
    [Fact]
    public void ParseConfigurationReadsIadInterfaceEndpointAndCompanion()
    {
        var raw = ValidConfiguration();

        var result = UsbDescriptorParser.ParseConfiguration(
            raw,
            0,
            0x0320,
            new Dictionary<byte, string> { [4] = "Function", [5] = "Data" });

        Assert.Equal(43, result.TotalLength);
        Assert.Equal(800, result.MaximumPowerMilliamps);
        Assert.True(result.SupportsRemoteWakeup);
        Assert.Equal("Function", Assert.Single(result.InterfaceAssociations).Description);
        var item = Assert.Single(result.Interfaces);
        Assert.Equal("Data", item.Description);
        var endpoint = Assert.Single(item.Endpoints);
        Assert.Equal(UsbEndpointDirection.In, endpoint.Direction);
        Assert.Equal(UsbEndpointTransferType.Bulk, endpoint.TransferType);
        Assert.Equal(1024, endpoint.MaximumPacketBytes);
        Assert.Equal(2, endpoint.SuperSpeedCompanion!.MaximumBurst);
        Assert.Equal(1024, endpoint.SuperSpeedCompanion.BytesPerInterval);
        Assert.Equal(0x24, Assert.Single(result.AdditionalDescriptors).DescriptorType);
    }

    [Theory]
    [InlineData(0x0200, 200)]
    [InlineData(0x0300, 800)]
    public void ParseConfigurationUsesUsbVersionCorrectPowerUnits(int usbVersion, int expectedMilliamps)
    {
        var raw = new byte[] { 9, 2, 9, 0, 0, 1, 0, 0x80, 100 };

        var result = UsbDescriptorParser.ParseConfiguration(raw, 0, (ushort)usbVersion);

        Assert.Equal(expectedMilliamps, result.MaximumPowerMilliamps);
    }

    [Fact]
    public void ParseConfigurationPreservesAlternateSettings()
    {
        var raw = new byte[]
        {
            9, 2, 27, 0, 1, 1, 0, 0x80, 10,
            9, 4, 0, 0, 0, 3, 0, 0, 0,
            9, 4, 0, 1, 0, 3, 0, 0, 0
        };

        var result = UsbDescriptorParser.ParseConfiguration(raw, 0, 0x0200);

        Assert.Equal([0, 1], result.Interfaces.Select(item => (int)item.AlternateSetting));
    }

    [Fact]
    public void ParseConfigurationRejectsZeroLengthChild()
    {
        var raw = new byte[] { 9, 2, 11, 0, 0, 1, 0, 0x80, 10, 0, 0x24 };

        Assert.Throws<InvalidDataException>(() => UsbDescriptorParser.ParseConfiguration(raw, 0, 0x0200));
    }

    [Fact]
    public void ParseConfigurationRejectsChildBeyondTotalLength()
    {
        var raw = new byte[] { 9, 2, 12, 0, 0, 1, 0, 0x80, 10, 4, 0x24, 1 };

        Assert.Throws<InvalidDataException>(() => UsbDescriptorParser.ParseConfiguration(raw, 0, 0x0200));
    }

    [Fact]
    public void ParseConfigurationRejectsEndpointWithoutInterface()
    {
        var raw = new byte[] { 9, 2, 16, 0, 0, 1, 0, 0x80, 10, 7, 5, 0x81, 2, 64, 0, 1 };

        Assert.Throws<InvalidDataException>(() => UsbDescriptorParser.ParseConfiguration(raw, 0, 0x0200));
    }

    [Fact]
    public void ConfigurationHeaderRejectsShortAndWrongType()
    {
        Assert.Throws<InvalidDataException>(() => UsbDescriptorParser.ReadConfigurationTotalLength([9, 2, 9]));
        Assert.Throws<InvalidDataException>(() => UsbDescriptorParser.ReadConfigurationTotalLength([9, 3, 9, 0, 0, 0, 0, 0, 0]));
    }

    [Fact]
    public void ParseStringAndLanguagesReadUtf16Descriptors()
    {
        var languages = UsbDescriptorParser.ParseLanguageIds([6, 3, 0x09, 0x04, 0x04, 0x08]);
        var value = UsbDescriptorParser.ParseString([8, 3, (byte)'U', 0, (byte)'S', 0, (byte)'B', 0]);

        Assert.Equal([(ushort)0x0409, (ushort)0x0804], languages);
        Assert.Equal("USB", value);
    }

    [Theory]
    [InlineData(new byte[] { 3, 3, 65 })]
    [InlineData(new byte[] { 8, 3, 65, 0 })]
    [InlineData(new byte[] { 4, 2, 65, 0 })]
    public void ParseStringRejectsMalformedDescriptors(byte[] raw)
    {
        Assert.Throws<InvalidDataException>(() => UsbDescriptorParser.ParseString(raw));
    }

    [Fact]
    public void ParseBosReadsCapabilities()
    {
        var raw = new byte[]
        {
            5, 0x0F, 16, 0, 2,
            7, 0x10, 2, 2, 0, 0, 0,
            4, 0x10, 4, 0
        };

        var result = UsbDescriptorParser.ParseBos(raw);

        Assert.Equal(2, result.Capabilities.Count);
        Assert.Equal("USB 2.0 Extension", result.Capabilities[0].DisplayName);
        Assert.Equal("Container ID", result.Capabilities[1].DisplayName);
    }

    [Fact]
    public void ParseBosRejectsMalformedChildLength()
    {
        var raw = new byte[] { 5, 0x0F, 8, 0, 1, 0, 0x10, 2 };

        Assert.Throws<InvalidDataException>(() => UsbDescriptorParser.ParseBos(raw));
    }

    private static byte[] ValidConfiguration()
    {
        return
        [
            9, 2, 43, 0, 1, 1, 3, 0xA0, 100,
            8, 0x0B, 0, 1, 0xEF, 2, 1, 4,
            9, 4, 0, 0, 1, 0x0A, 0, 0, 5,
            7, 5, 0x81, 2, 0x00, 0x04, 1,
            6, 0x30, 2, 0, 0x00, 0x04,
            4, 0x24, 1, 2
        ];
    }
}
