using System.Buffers.Binary;
using HwScope.Core.Hardware.Storage;
using HwScope.Core.Hardware.Storage.Ata;
using HwScope.Core.Windows.Storage;

namespace HwScope.Core.Tests.Hardware.Storage;

public sealed class AtaSmartParserTests
{
    [Fact]
    public void TryParse_MergesThresholdAndReadsRawValue()
    {
        var attributes = new byte[512];
        var thresholds = new byte[512];
        BinaryPrimitives.WriteUInt16LittleEndian(attributes.AsSpan(0, 2), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(thresholds.AsSpan(0, 2), 1);
        attributes[2] = 0x05;
        BinaryPrimitives.WriteUInt16LittleEndian(attributes.AsSpan(3, 2), 1);
        attributes[5] = 90;
        attributes[6] = 80;
        attributes[7] = 0x34;
        attributes[8] = 0x12;
        thresholds[2] = 0x05;
        thresholds[3] = 10;
        ApplyChecksum(attributes);
        ApplyChecksum(thresholds);

        var success = AtaSmartParser.TryParse(attributes, thresholds, out var data, out var error);

        Assert.True(success, error?.Message);
        var attribute = Assert.Single(data!.Attributes);
        Assert.Equal((byte)0x05, attribute.Id);
        Assert.Equal((byte)10, attribute.Threshold);
        Assert.Equal(0x1234UL, attribute.RawValue);
        Assert.True(attribute.IsPreFailure);
    }

    [Fact]
    public void TryParse_RejectsEmptySectors()
    {
        var success = AtaSmartParser.TryParse(new byte[512], new byte[512], out _, out var error);

        Assert.False(success);
        Assert.Equal(StorageErrorKind.MalformedResponse, error?.Kind);
    }

    [Fact]
    public void TryParse_RejectsInvalidChecksum()
    {
        var attributes = CreateSectorWithAttribute();
        var thresholds = CreateThresholdSector();
        attributes[100] = 1;

        var success = AtaSmartParser.TryParse(attributes, thresholds, out _, out var error);

        Assert.False(success);
        Assert.Contains("checksum", error?.Message);
    }

    [Fact]
    public void TryParse_RejectsShortSector()
    {
        var success = AtaSmartParser.TryParse(new byte[511], new byte[512], out _, out var error);

        Assert.False(success);
        Assert.Equal(StorageErrorKind.MalformedResponse, error?.Kind);
    }

    [Fact]
    public void Evaluate_ThresholdCrossingIsCritical()
    {
        var attribute = new AtaSmartAttribute(0x05, 1, 5, 5, [1, 0, 0, 0, 0, 0], 1, 10);
        var data = new AtaSmartData(1, [attribute], new byte[512], new byte[512]);

        var result = AtaSmartEvaluator.Evaluate(data, overallHealthPassed: true);

        Assert.Equal(StorageHealthStatus.Critical, result.Health.Status);
        Assert.Equal(StorageAttributeSeverity.Critical, Assert.Single(result.Attributes).Severity);
        Assert.False(result.Health.RemainingLifePercent.IsAvailable);
    }

    [Fact]
    public void Evaluate_OverallFailureIsCritical()
    {
        var result = AtaSmartEvaluator.Evaluate(CreateSmartData(), overallHealthPassed: false);

        Assert.Equal(StorageHealthStatus.Critical, result.Health.Status);
        Assert.Contains("overall status", result.Health.StatusReason);
    }

    [Fact]
    public void Evaluate_MissingOverallStatusIsUnknown()
    {
        var result = AtaSmartEvaluator.Evaluate(CreateSmartData());

        Assert.Equal(StorageHealthStatus.Unknown, result.Health.Status);
    }

    [Theory]
    [InlineData(0x4F, 0xC2, true)]
    [InlineData(0xF4, 0x2C, false)]
    public void TryParseOverallStatus_ReadsAtaSignature(byte low, byte high, bool expected)
    {
        var response = new byte[48];
        response[43] = low;
        response[44] = high;

        var success = AtaSmartQuery.TryParseOverallStatus(response, 40, out var passed, out var error);

        Assert.True(success, error?.Message);
        Assert.Equal(expected, passed);
    }

    private static AtaSmartData CreateSmartData()
    {
        var attribute = new AtaSmartAttribute(0x09, 0, 100, 100, [1, 0, 0, 0, 0, 0], 1, 1);
        return new AtaSmartData(1, [attribute], new byte[512], new byte[512]);
    }

    private static byte[] CreateSectorWithAttribute()
    {
        var sector = new byte[512];
        BinaryPrimitives.WriteUInt16LittleEndian(sector.AsSpan(0, 2), 1);
        sector[2] = 0x09;
        sector[5] = 100;
        sector[6] = 100;
        ApplyChecksum(sector);
        return sector;
    }

    private static byte[] CreateThresholdSector()
    {
        var sector = new byte[512];
        BinaryPrimitives.WriteUInt16LittleEndian(sector.AsSpan(0, 2), 1);
        sector[2] = 0x09;
        sector[3] = 1;
        ApplyChecksum(sector);
        return sector;
    }

    private static void ApplyChecksum(byte[] sector)
    {
        sector[^1] = 0;
        var sum = sector.Aggregate(0, (current, value) => (current + value) & 0xFF);
        sector[^1] = unchecked((byte)(0 - sum));
    }
}
