using HwScope.Core.Hardware.DeviceTopology.Pci;

namespace HwScope.Core.Tests.Hardware.DeviceTopology.Pci;

public sealed class PciAddressParserTests
{
    [Fact]
    public void TryDecode_UsesWindowsDeviceAddressEncoding()
    {
        var success = PciAddressParser.TryDecode(0xc4, (8u << 16) | 3u, out var address);

        Assert.True(success);
        Assert.Equal((byte)0xc4, address!.Bus);
        Assert.Equal((byte)8, address.Device);
        Assert.Equal((byte)3, address.Function);
        Assert.Equal("C4:08.3", address.ToString());
    }

    [Theory]
    [InlineData(256u, 0u)]
    [InlineData(3u, 32u << 16)]
    [InlineData(3u, 8u)]
    public void TryDecode_RejectsOutOfRangeValues(uint bus, uint encodedAddress)
    {
        Assert.False(PciAddressParser.TryDecode(bus, encodedAddress, out _));
    }

    [Fact]
    public void TryParseLastLocationSegment_ReturnsLastPciHop()
    {
        var success = PciAddressParser.TryParseLastLocationSegment(
            ["PCIROOT(0)#PCI(0801)#PCI(0004)"],
            out var device,
            out var function);

        Assert.True(success);
        Assert.Equal((byte)0, device);
        Assert.Equal((byte)4, function);
    }

    [Fact]
    public void TryParseRootIndex_UsesHexadecimalPciRootSegment()
    {
        var success = PciAddressParser.TryParseRootIndex(
            ["PCIROOT(1A)#PCI(0200)"],
            out var rootIndex);

        Assert.True(success);
        Assert.Equal((uint)0x1a, rootIndex);
    }
}
