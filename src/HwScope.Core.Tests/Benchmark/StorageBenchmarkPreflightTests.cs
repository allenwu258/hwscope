using HwScope.Core.Benchmark.Storage;

namespace HwScope.Core.Tests.Benchmark;

public sealed class StorageBenchmarkPreflightTests
{
    [Fact]
    public void RejectsChangedVolumeGuidBeforeCreatingTestDirectory()
    {
        var target = DiscoverCurrentSystemTarget() with { VolumeGuidPath = @"\\?\Volume{00000000-0000-0000-0000-000000000000}\" };

        var error = Assert.Throws<IOException>(() => StorageBenchmarkPreflight.ValidateCurrentTargetIdentity(target));

        Assert.Contains("identity", error.Message);
    }

    [Fact]
    public void RejectsChangedPhysicalExtent()
    {
        var target = DiscoverCurrentSystemTarget();
        target = target with { PhysicalDriveNumber = target.PhysicalDriveNumber!.Value + 1000 };

        var error = Assert.Throws<IOException>(() => StorageBenchmarkPreflight.ValidateCurrentTargetIdentity(target));

        Assert.Contains("physical extent", error.Message);
    }

    [Fact]
    public void CurrentDiscoveredTargetPassesIdentityValidation()
    {
        StorageBenchmarkPreflight.ValidateCurrentTargetIdentity(DiscoverCurrentSystemTarget());
    }

    private static StorageBenchmarkTarget DiscoverCurrentSystemTarget()
    {
        return new StorageBenchmarkTargetDiscovery().Discover()
            .FirstOrDefault(target => target.IsSystem)
            ?? throw new InvalidOperationException("测试环境没有可复核的系统卷。");
    }
}
