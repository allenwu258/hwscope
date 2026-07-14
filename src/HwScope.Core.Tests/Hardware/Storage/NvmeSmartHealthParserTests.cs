using System.Buffers.Binary;
using HwScope.Core.Hardware.Storage;
using HwScope.Core.Hardware.Storage.Nvme;

namespace HwScope.Core.Tests.Hardware.Storage;

public sealed class NvmeSmartHealthParserTests
{
    [Fact]
    public void TryParse_RejectsTruncatedPayload()
    {
        var success = NvmeSmartHealthParser.TryParse(new byte[511], out var log, out var error);

        Assert.False(success);
        Assert.Null(log);
        Assert.Equal(StorageErrorKind.MalformedResponse, error?.Kind);
    }

    [Fact]
    public void TryParse_ReadsTemperaturePercentageAndUInt128()
    {
        var payload = new byte[512];
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(1, 2), 300);
        payload[3] = 100;
        payload[4] = 10;
        payload[5] = 3;
        BinaryPrimitives.WriteUInt64LittleEndian(payload.AsSpan(32, 8), 7);
        BinaryPrimitives.WriteUInt64LittleEndian(payload.AsSpan(40, 8), 1);

        var success = NvmeSmartHealthParser.TryParse(payload, out var log, out var error);

        Assert.True(success, error?.Message);
        Assert.NotNull(log);
        Assert.Equal((ushort)300, log.CompositeTemperatureKelvin);
        Assert.Equal((byte)3, log.PercentageUsed);
        Assert.Equal(((UInt128)1 << 64) | 7, log.DataUnitsRead);
    }

    [Theory]
    [InlineData(0, StorageHealthStatus.Good, 100)]
    [InlineData(3, StorageHealthStatus.Good, 97)]
    [InlineData(99, StorageHealthStatus.Good, 1)]
    [InlineData(100, StorageHealthStatus.Caution, 0)]
    [InlineData(255, StorageHealthStatus.Caution, 0)]
    public void EvaluateNvme_UsesStandardPercentageUsed(byte used, StorageHealthStatus status, int remaining)
    {
        var log = Parse(CreatePayload(percentageUsed: used));

        var result = StorageHealthEvaluator.EvaluateNvme(log);

        Assert.Equal(status, result.Health.Status);
        Assert.Equal(remaining, result.Health.RemainingLifePercent.Value);
    }

    [Fact]
    public void EvaluateNvme_TreatsReliabilityWarningAsCritical()
    {
        var log = Parse(CreatePayload(criticalWarning: 0x04));

        var result = StorageHealthEvaluator.EvaluateNvme(log);

        Assert.Equal(StorageHealthStatus.Critical, result.Health.Status);
        Assert.Contains(result.Health.Flags, flag => flag.Contains("可靠性"));
    }

    private static byte[] CreatePayload(byte criticalWarning = 0, byte percentageUsed = 0)
    {
        var payload = new byte[512];
        payload[0] = criticalWarning;
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(1, 2), 300);
        payload[3] = 100;
        payload[4] = 10;
        payload[5] = percentageUsed;
        return payload;
    }

    private static NvmeSmartHealthLog Parse(byte[] payload)
    {
        Assert.True(NvmeSmartHealthParser.TryParse(payload, out var log, out var error), error?.Message);
        return Assert.IsType<NvmeSmartHealthLog>(log);
    }
}
