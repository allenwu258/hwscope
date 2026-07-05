namespace HwScope.Core.Hardware.Memory;

public sealed class NullSpdProvider : ISpdProvider
{
    public static NullSpdProvider Instance { get; } = new();

    private NullSpdProvider()
    {
    }

    public SpdProviderResult TryCollect()
    {
        return new SpdProviderResult(
            SpdProviderStatus.NotConfigured,
            [],
            ["SPD provider 尚未配置；当前仅显示 Windows WMI / SMBIOS 字段。"]);
    }
}
