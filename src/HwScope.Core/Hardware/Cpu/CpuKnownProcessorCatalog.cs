namespace HwScope.Core.Hardware.Cpu;

internal sealed record CpuKnownProcessorInfo(
    string MatchText,
    string DisplayName,
    string CodeName,
    string Package,
    string Technology,
    string Tdp,
    string Revision,
    IReadOnlyList<CpuCacheInfo> Caches,
    IReadOnlyList<CpuFeature> Features);

internal static class CpuKnownProcessorCatalog
{
    private static readonly IReadOnlyList<CpuKnownProcessorInfo> KnownProcessors =
    [
        new CpuKnownProcessorInfo(
            MatchText: "AMD Ryzen 7 8745H",
            DisplayName: "AMD Ryzen 7 8745H",
            CodeName: "Hawk Point",
            Package: "Socket FP7/FP7r2",
            Technology: "4 nm",
            Tdp: "45 W",
            Revision: "HPT1-A2",
            Caches:
            [
                Cache(CpuCacheLevel.L1Data, "L1 Data", 8, 32 * 1024, 8),
                Cache(CpuCacheLevel.L1Instruction, "L1 Instruction", 8, 32 * 1024, 8),
                Cache(CpuCacheLevel.L2, "L2", 8, 1024 * 1024, 8),
                Cache(CpuCacheLevel.L3, "L3", null, 16 * 1024 * 1024, 16)
            ],
            Features:
            [
                Feature("x86", CpuFeatureGroup.Basic),
                Feature("x86-64", CpuFeatureGroup.Basic),
                Feature("MMX", CpuFeatureGroup.Simd),
                Feature("MMX+", CpuFeatureGroup.Simd),
                Feature("SSE", CpuFeatureGroup.Simd),
                Feature("SSE2", CpuFeatureGroup.Simd),
                Feature("SSE3", CpuFeatureGroup.Simd),
                Feature("SSSE3", CpuFeatureGroup.Simd),
                Feature("SSE4.1", CpuFeatureGroup.Simd),
                Feature("SSE4.2", CpuFeatureGroup.Simd),
                Feature("SSE4A", CpuFeatureGroup.Simd),
                Feature("AVX", CpuFeatureGroup.Simd),
                Feature("AVX2", CpuFeatureGroup.Simd),
                Feature("AVX-512", CpuFeatureGroup.Simd),
                Feature("FMA", CpuFeatureGroup.Simd),
                Feature("AES", CpuFeatureGroup.Crypto),
                Feature("SHA", CpuFeatureGroup.Crypto)
            ])
    ];

    public static CpuKnownProcessorInfo? Match(string processorName)
    {
        if (string.IsNullOrWhiteSpace(processorName))
        {
            return null;
        }

        return KnownProcessors.FirstOrDefault(info =>
            processorName.Contains(info.MatchText, StringComparison.OrdinalIgnoreCase));
    }

    private static CpuCacheInfo Cache(CpuCacheLevel level, string name, int? instanceCount, long sizeBytes, int? ways)
    {
        return new CpuCacheInfo(
            level,
            name,
            instanceCount,
            sizeBytes,
            ways,
            LineSizeBytes: null,
            SharedLogicalProcessorCount: null,
            CacheType: null,
            SharedMasks: [],
            CpuDataSource.Mapping,
            IsEstimated: true,
            Note: "来自处理器型号映射，后续将由 native CPUID 或 Windows 拓扑 API 校验。");
    }

    private static CpuFeature Feature(string name, CpuFeatureGroup group)
    {
        return new CpuFeature(name, group, IsSupported: true, CpuDataSource.Mapping, IsEstimated: true);
    }
}
