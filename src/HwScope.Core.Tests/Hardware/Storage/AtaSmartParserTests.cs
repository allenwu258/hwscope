using System.Buffers.Binary;
using HwScope.Core.Hardware.Storage;
using HwScope.Core.Hardware.Storage.Ata;

namespace HwScope.Core.Tests.Hardware.Storage;

public sealed class AtaSmartParserTests
{
    [Fact]
    public void TryParse_MergesThresholdAndReadsRawValue()
    {
        var attributes = new byte[512];
        var thresholds = new byte[512];
        BinaryPrimitives.WriteUInt16LittleEndian(attributes.AsSpan(0, 2), 1);
        attributes[2] = 0x05;
        BinaryPrimitives.WriteUInt16LittleEndian(attributes.AsSpan(3, 2), 1);
        attributes[5] = 90;
        attributes[6] = 80;
        attributes[7] = 0x34;
        attributes[8] = 0x12;
        thresholds[2] = 0x05;
        thresholds[3] = 10;

        var success = AtaSmartParser.TryParse(attributes, thresholds, out var data, out var error);

        Assert.True(success, error?.Message);
        var attribute = Assert.Single(data!.Attributes);
        Assert.Equal((byte)0x05, attribute.Id);
        Assert.Equal((byte)10, attribute.Threshold);
        Assert.Equal(0x1234UL, attribute.RawValue);
        Assert.True(attribute.IsPreFailure);
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

        var result = AtaSmartEvaluator.Evaluate(data);

        Assert.Equal(StorageHealthStatus.Critical, result.Health.Status);
        Assert.Equal(StorageAttributeSeverity.Critical, Assert.Single(result.Attributes).Severity);
        Assert.False(result.Health.RemainingLifePercent.IsAvailable);
    }
}
