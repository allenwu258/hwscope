using System.Runtime.InteropServices;
using HwScope.Core.Hardware;
using HwScope.Core.Hardware.Graphics;
using HwScope.Core.Hardware.Inventory;
using HwScope.Core.Windows.Graphics;

namespace HwScope.Core.Tests.Hardware.Graphics;

public sealed class GraphicsInventoryTests
{
    private const ulong GiB = 1024UL * 1024 * 1024;
    private const string Pnp = @"PCI\VEN_10DE&DEV_2D59&SUBSYS_3E2517AA&REV_A1\INSTANCE1";

    [Theory]
    [InlineData(2)]
    [InlineData(4)]
    [InlineData(6)]
    [InlineData(8)]
    [InlineData(12)]
    [InlineData(16)]
    [InlineData(48)]
    public void DxgiCapacityWinsOverLimitedWmiValue(int gib)
    {
        var adapter = Adapter((ulong)gib * GiB);
        var controller = new VideoControllerSnapshot("GPU", 4293918720, Pnp);
        var merged = Assert.Single(Merge([controller], [adapter]));

        Assert.Equal((ulong)gib * GiB, merged.DedicatedVideoMemoryBytes);
        Assert.Equal(4293918720UL, merged.AdapterRam);
        Assert.Equal(32 * GiB, merged.SharedSystemMemoryBytes);
        Assert.Equal(GraphicsMemorySource.Dxgi, merged.MemorySource);
        Assert.False(merged.IsEstimated);
        Assert.Equal($"GPU（专用显存 {gib} GiB）", Summary([merged]).Graphics);
    }

    [Fact]
    public void ReportPreservesFractionalCapacityRatherThanGuessingInstalledSize()
    {
        var merged = Merge([new("GPU", 4293918720, Pnp)], [Adapter(8282701824)]);
        Assert.Equal("GPU（专用显存 7.71 GiB）", Summary(merged).Graphics);
    }

    [Fact]
    public void ZeroDedicatedMemoryDoesNotFallBackToWmiOrSharedMemory()
    {
        var adapter = Adapter(0) with { DedicatedSystemMemoryBytes = GiB };
        var merged = Assert.Single(Merge([new("GPU", 512 * 1024 * 1024, Pnp)], [adapter]));
        Assert.Equal(0UL, merged.DedicatedVideoMemoryBytes);
        Assert.Equal(GiB, merged.DedicatedSystemMemoryBytes);
        Assert.Equal("GPU（无专用显存）", Summary([merged]).Graphics);
    }

    [Fact]
    public void SubGiBCapacityIsNotRoundedToZero()
    {
        var merged = Merge([new("GPU", 0, Pnp)], [Adapter(512 * 1024 * 1024)]);
        Assert.Equal("GPU（专用显存 512 MiB）", Summary(merged).Graphics);
    }

    [Theory]
    [InlineData(4293918720UL, "WMI 报告 4 GiB")]
    [InlineData(2 * GiB, "WMI 报告 2 GiB")]
    public void WmiFallbackAlwaysDisclosesUnverifiedCapacity(ulong raw, string expected)
    {
        var result = GraphicsAdapterMerger.Merge([new("GPU", raw, Pnp)], new([], ["DXGI unavailable"]));
        var controller = Assert.Single(result);
        Assert.Equal(GraphicsMemorySource.Wmi, controller.MemorySource);
        Assert.True(controller.IsEstimated);
        Assert.Null(controller.DedicatedVideoMemoryBytes);
        Assert.Contains("DXGI unavailable", controller.MemoryDiagnostics!);
        Assert.Contains("显存容量未确认", Summary(result).Graphics);
        Assert.Contains(expected, Summary(result).Graphics);
    }

    [Fact]
    public void MissingCapacityDoesNotBecomeZeroGiB()
    {
        var result = Merge([new("GPU", 0, Pnp)], []);
        Assert.Equal(GraphicsMemorySource.Unknown, Assert.Single(result).MemorySource);
        Assert.Equal("GPU（显存容量未提供）", Summary(result).Graphics);
    }

    [Fact]
    public void DxgiOnlyHardwareSurvivesEmptyWmiAndSoftwareAdapterIsFiltered()
    {
        var result = Merge([], [Adapter(8 * GiB), Adapter(0) with { IsSoftware = true }]);
        var hardware = Assert.Single(result);
        Assert.Equal(8 * GiB, hardware.DedicatedVideoMemoryBytes);
        Assert.Equal(string.Empty, hardware.PnpDeviceId);
        Assert.Equal(1UL, hardware.AdapterLuid);
    }

    [Fact]
    public void SameVendorDifferentDevicesMatchIndependentlyOfOrderOrName()
    {
        var controllers = new VideoControllerSnapshot[]
        {
            new("Same name", 0, Pnp),
            new("Same name", 0, Pnp.Replace("2D59", "1234"))
        };
        var result = Merge(controllers,
            [Adapter(16 * GiB) with { DeviceId = 0x1234, Luid = 2 }, Adapter(8 * GiB)]);
        Assert.Equal(2, result.Count);
        Assert.Equal(8 * GiB, result[0].DedicatedVideoMemoryBytes);
        Assert.Equal(16 * GiB, result[1].DedicatedVideoMemoryBytes);
    }

    [Fact]
    public void SubsystemIdentifiesDifferentBoardsWithTheSameChip()
    {
        var controllers = new VideoControllerSnapshot[]
        {
            new("GPU", 0, Pnp),
            new("GPU", 0, Pnp.Replace("3E2517AA", "12345678"))
        };
        var result = Merge(controllers,
            [Adapter(16 * GiB) with { SubSystemId = 0x12345678, Luid = 2 }, Adapter(8 * GiB)]);
        Assert.Equal(2, result.Count);
        Assert.Equal(8 * GiB, result[0].DedicatedVideoMemoryBytes);
        Assert.Equal(16 * GiB, result[1].DedicatedVideoMemoryBytes);
    }

    [Theory]
    [InlineData("ROOT\\DISPLAY\\0000")]
    [InlineData("PCI\\VEN_10DE&DEV_FFFF")]
    [InlineData("PCI\\VEN_FFFF&DEV_2D59")]
    [InlineData("PCI\\VEN_10DE&DEV_2D59&SUBSYS_FFFFFFFF")]
    [InlineData("PCI\\VEN_10DE&DEV_2D59&REV_FF")]
    [InlineData("PCI\\VEN_10DE&DEV_2D590")]
    [InlineData("")]
    public void NameOrVendorAloneCannotAssignMemory(string pnp)
    {
        var result = Merge([new("GPU", GiB, pnp)], [Adapter(8 * GiB)]);
        var controller = Assert.Single(result);
        Assert.Null(controller.DedicatedVideoMemoryBytes);
        Assert.Null(controller.AdapterLuid);
        Assert.Equal(pnp, controller.PnpDeviceId);
    }

    [Fact]
    public void IdenticalBoardsAreNotJoinedByEnumerationOrder()
    {
        var result = Merge([new("GPU", 0, Pnp), new("GPU", 0, Pnp.Replace("INSTANCE1", "INSTANCE2"))],
            [Adapter(8 * GiB), Adapter(16 * GiB) with { Luid = 2 }]);
        Assert.Equal(2, result.Count);
        Assert.All(result, item => Assert.Null(item.DedicatedVideoMemoryBytes));
        Assert.All(result, item => Assert.Contains(item.MemoryDiagnostics!, note => note.Contains("ambiguous")));
        Assert.All(result, item => Assert.Null(item.AdapterLuid));
    }

    [Fact]
    public void TwoWmiRecordsCannotClaimOneDxgiAdapter()
    {
        var result = Merge([new("GPU", 0, Pnp), new("GPU", 0, Pnp.Replace("INSTANCE1", "INSTANCE2"))], [Adapter(8 * GiB)]);
        Assert.Equal(2, result.Count);
        Assert.All(result, item => Assert.Null(item.DedicatedVideoMemoryBytes));
    }

    [Fact]
    public void UnmatchedDxgiAdapterDoesNotDuplicateAnExistingDeviceList()
    {
        var result = Merge([new("GPU", 0, Pnp)],
            [Adapter(8 * GiB), Adapter(16 * GiB) with { DeviceId = 0x1234, Luid = 2 }]);

        var controller = Assert.Single(result);
        Assert.Equal(8 * GiB, controller.DedicatedVideoMemoryBytes);
        Assert.Equal(1UL, controller.AdapterLuid);
        Assert.Contains(controller.MemoryDiagnostics!, note => note.Contains("omitted"));
    }

    [Fact]
    public void PciMatchingIsCaseInsensitiveAndAllowsMissingOptionalIds()
    {
        var result = Merge([new("Different driver name", 0, @"pci\ven_10de&dev_2d59\instance")], [Adapter(8 * GiB)]);
        Assert.Equal(8 * GiB, Assert.Single(result).DedicatedVideoMemoryBytes);
    }

    [Fact]
    public void NativeDescriptionMatchesWindowsX64Abi()
    {
        if (!Environment.Is64BitProcess) return;
        Assert.Equal(312, Marshal.SizeOf<DxgiAdapterEnumerator.AdapterDescription>());
        Assert.Equal(272, Marshal.OffsetOf<DxgiAdapterEnumerator.AdapterDescription>("DedicatedVideoMemory").ToInt32());
        Assert.Equal(296, Marshal.OffsetOf<DxgiAdapterEnumerator.AdapterDescription>("LuidLow").ToInt32());
        Assert.Equal(304, Marshal.OffsetOf<DxgiAdapterEnumerator.AdapterDescription>("Flags").ToInt32());
    }

    private static DxgiAdapterInfo Adapter(ulong bytes) => new("GPU", bytes, 0, 32 * GiB, 0x10DE, 0x2D59, 0x3E2517AA, 0xA1, 1, false);

    private static IReadOnlyList<VideoControllerSnapshot> Merge(IReadOnlyList<VideoControllerSnapshot> wmi,
        IReadOnlyList<DxgiAdapterInfo> dxgi) => GraphicsAdapterMerger.Merge(wmi, new(dxgi, []));

    private static HardwareReport Summary(IReadOnlyList<VideoControllerSnapshot> controllers) =>
        new HardwareCollector().CreateSummary(new HardwareInventorySnapshot([], null, null, [], controllers,
            [], [], [], [], 0, null, new([], TimeSpan.Zero), DateTimeOffset.Now));
}
