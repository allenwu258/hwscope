using HwScope.Core.Hardware.DeviceTopology;
using HwScope.Core.Hardware.DeviceTopology.Usb;

namespace HwScope.Core.Tests.Hardware.DeviceTopology.Usb;

public sealed class UsbDeviceDetailCacheTests
{
    [Fact]
    public async Task ConcurrentCallersShareOneCollection()
    {
        var release = new ManualResetEventSlim();
        var source = new FakeSource(target =>
        {
            release.Wait();
            return Detail(target);
        });
        var cache = new UsbDeviceDetailCache(source);
        var snapshot = Snapshot("device-a");

        var first = cache.EnsureLoadedAsync(snapshot, "device-a");
        var second = cache.EnsureLoadedAsync(snapshot, "device-a");
        Assert.True(SpinWait.SpinUntil(() => source.CallCount == 1, TimeSpan.FromSeconds(2)));
        Assert.Equal(1, source.CallCount);
        release.Set();

        Assert.Same(await first, await second);
        Assert.Equal(1, source.CallCount);
    }

    [Fact]
    public async Task CallerCancellationDoesNotCancelSharedCollection()
    {
        var release = new ManualResetEventSlim();
        var source = new FakeSource(target =>
        {
            release.Wait();
            return Detail(target);
        });
        var cache = new UsbDeviceDetailCache(source);
        var snapshot = Snapshot("device-a");
        using var cancellation = new CancellationTokenSource();

        var canceledWait = cache.EnsureLoadedAsync(snapshot, "device-a", cancellation.Token);
        var survivingWait = cache.EnsureLoadedAsync(snapshot, "device-a");
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => canceledWait);
        release.Set();

        Assert.Equal("device-a", (await survivingWait).DeviceNodeId);
        Assert.Equal(1, source.CallCount);
    }

    [Fact]
    public async Task CachedResultIsReusedAndRefreshStartsNewCollection()
    {
        var source = new FakeSource(Detail);
        var cache = new UsbDeviceDetailCache(source);
        var snapshot = Snapshot("device-a");

        var first = await cache.EnsureLoadedAsync(snapshot, "device-a");
        var cached = await cache.EnsureLoadedAsync(snapshot, "device-a");
        var refreshed = await cache.RefreshAsync(snapshot, "device-a");

        Assert.Same(first, cached);
        Assert.NotSame(first, refreshed);
        Assert.Equal(2, source.CallCount);
    }

    [Fact]
    public async Task ConcurrentRefreshesShareTheActiveCollection()
    {
        var release = new ManualResetEventSlim();
        var source = new FakeSource(target =>
        {
            release.Wait();
            return Detail(target);
        });
        var cache = new UsbDeviceDetailCache(source);
        var snapshot = Snapshot("device-a");

        var first = cache.RefreshAsync(snapshot, "device-a");
        var second = cache.RefreshAsync(snapshot, "device-a");
        Assert.True(SpinWait.SpinUntil(() => source.CallCount == 1, TimeSpan.FromSeconds(2)));
        release.Set();

        Assert.Same(await first, await second);
        Assert.Equal(1, source.CallCount);
    }

    [Fact]
    public async Task RefreshSharesAnActiveEnsureLoad()
    {
        var release = new ManualResetEventSlim();
        var source = new FakeSource(target =>
        {
            release.Wait();
            return Detail(target);
        });
        var cache = new UsbDeviceDetailCache(source);
        var snapshot = Snapshot("device-a");

        var ensure = cache.EnsureLoadedAsync(snapshot, "device-a");
        var refresh = cache.RefreshAsync(snapshot, "device-a");
        Assert.True(SpinWait.SpinUntil(() => source.CallCount == 1, TimeSpan.FromSeconds(2)));
        release.Set();

        Assert.Same(await ensure, await refresh);
        Assert.Equal(1, source.CallCount);
    }

    [Fact]
    public async Task DifferentAttachmentsRespectTheCollectionConcurrencyLimit()
    {
        var release = new ManualResetEventSlim();
        var active = 0;
        var maximumActive = 0;
        var source = new FakeSource(target =>
        {
            var current = Interlocked.Increment(ref active);
            InterlockedExtensions.Max(ref maximumActive, current);
            release.Wait();
            Interlocked.Decrement(ref active);
            return Detail(target);
        });
        var cache = new UsbDeviceDetailCache(source, maximumConcurrentCollections: 1);
        var firstSnapshot = Snapshot("device-a", "port-1");
        var secondSnapshot = Snapshot("device-b", "port-2");

        var first = cache.EnsureLoadedAsync(firstSnapshot, "device-a");
        var second = cache.EnsureLoadedAsync(secondSnapshot, "device-b");
        Assert.True(SpinWait.SpinUntil(() => source.CallCount == 1, TimeSpan.FromSeconds(2)));
        Assert.Equal(1, source.CallCount);
        release.Set();
        await Task.WhenAll(first, second);

        Assert.Equal(1, maximumActive);
        Assert.Equal(2, source.CallCount);
    }

    [Fact]
    public async Task NewDeviceAtSameAttachmentInvalidatesCachedResult()
    {
        var source = new FakeSource(Detail);
        var cache = new UsbDeviceDetailCache(source);
        var firstSnapshot = Snapshot("device-a");
        var secondSnapshot = Snapshot("device-b");
        await cache.EnsureLoadedAsync(firstSnapshot, "device-a");

        cache.SynchronizeSnapshot(secondSnapshot);
        Assert.Null(cache.TryGetCached("port-1", "device-a"));
        var second = await cache.EnsureLoadedAsync(secondSnapshot, "device-b");

        Assert.Equal("device-b", second.DeviceNodeId);
        Assert.Equal(2, source.CallCount);
    }

    [Fact]
    public async Task OldInflightCompletionCannotOverwriteReplacementState()
    {
        var firstRelease = new ManualResetEventSlim();
        var secondRelease = new ManualResetEventSlim();
        var source = new FakeSource(target =>
        {
            (target.DeviceNodeId == "device-a" ? firstRelease : secondRelease).Wait();
            return Detail(target);
        });
        var cache = new UsbDeviceDetailCache(source);
        var firstSnapshot = Snapshot("device-a");
        var secondSnapshot = Snapshot("device-b");

        var firstTask = cache.EnsureLoadedAsync(firstSnapshot, "device-a");
        cache.SynchronizeSnapshot(secondSnapshot);
        var secondTask = cache.EnsureLoadedAsync(secondSnapshot, "device-b");
        secondRelease.Set();
        var second = await secondTask;
        firstRelease.Set();
        await firstTask;

        Assert.Same(second, cache.TryGetCached("port-1", "device-b"));
        Assert.Null(cache.TryGetCached("port-1", "device-a"));
    }

    [Fact]
    public async Task NonPhysicalNodeCannotStartDetailCollection()
    {
        var source = new FakeSource(Detail);
        var cache = new UsbDeviceDetailCache(source);
        var snapshot = Snapshot("device-a");

        await Assert.ThrowsAsync<ArgumentException>(() => cache.EnsureLoadedAsync(snapshot, "port-1"));
        Assert.Equal(0, source.CallCount);
    }

    [Fact]
    public async Task FaultedCollectionIsEvictedAndCanBeRetried()
    {
        var attempt = 0;
        var source = new FakeSource(target =>
        {
            if (Interlocked.Increment(ref attempt) == 1)
            {
                throw new InvalidOperationException("test failure");
            }

            return Detail(target);
        });
        var cache = new UsbDeviceDetailCache(source);
        var snapshot = Snapshot("device-a");

        await Assert.ThrowsAsync<InvalidOperationException>(() => cache.EnsureLoadedAsync(snapshot, "device-a"));
        var recovered = await cache.EnsureLoadedAsync(snapshot, "device-a");

        Assert.Equal("device-a", recovered.DeviceNodeId);
        Assert.Equal(2, source.CallCount);
    }

    [Fact]
    public async Task FailedRefreshPreservesLastSuccessfulDetail()
    {
        var failRefresh = false;
        var source = new FakeSource(target => failRefresh
            ? throw new InvalidOperationException("refresh failure")
            : Detail(target));
        var cache = new UsbDeviceDetailCache(source);
        var snapshot = Snapshot("device-a");
        var original = await cache.EnsureLoadedAsync(snapshot, "device-a");
        failRefresh = true;

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            cache.RefreshAsync(snapshot, "device-a"));

        Assert.Same(original, cache.TryGetCached("port-1", "device-a"));
        Assert.Same(original, await cache.EnsureLoadedAsync(snapshot, "device-a"));
        Assert.Equal(2, source.CallCount);
    }

    [Fact]
    public async Task EnsureReturnsStaleSuccessWhileRefreshIsInFlight()
    {
        var refreshRelease = new ManualResetEventSlim();
        var attempt = 0;
        var source = new FakeSource(target =>
        {
            if (Interlocked.Increment(ref attempt) > 1)
            {
                refreshRelease.Wait();
            }

            return Detail(target);
        });
        var cache = new UsbDeviceDetailCache(source);
        var snapshot = Snapshot("device-a");
        var original = await cache.EnsureLoadedAsync(snapshot, "device-a");

        var refresh = cache.RefreshAsync(snapshot, "device-a");
        try
        {
            Assert.True(SpinWait.SpinUntil(() => source.CallCount == 2, TimeSpan.FromSeconds(2)));
            var ensured = await cache.EnsureLoadedAsync(snapshot, "device-a");
            Assert.Same(original, ensured);
        }
        finally
        {
            refreshRelease.Set();
        }

        await refresh;
    }

    private static UsbTopologySnapshot Snapshot(
        string deviceNodeId,
        string attachmentId = "port-1")
    {
        var descriptor = new UsbDeviceDescriptorInfo(
            18, 1, 0x0320, 0, 0, 0, 9, 0x1234, 0x5678, 0x0100, 1, 2, 3, 1);
        var root = new UsbTopologyNode(
            "root", null, [attachmentId], UsbTopologyNodeKind.RootHub, "Root Hub", null,
            "controller", string.Empty, string.Empty, new UsbHubInfo("ROOT-HUB", 1, true, true), null, null);
        var port = new UsbTopologyNode(
            attachmentId, "root", [deviceNodeId], UsbTopologyNodeKind.Port, "Port 1", null,
            "controller", string.Empty, string.Empty, null,
            new UsbPortInfo(1, "1", UsbConnectionStatus.DeviceConnected, UsbConnectionSpeed.Super,
                UsbSupportedProtocols.Usb30, true, true, false, false, true, false, true, null,
                string.Empty, 1, 1),
            descriptor);
        var device = new UsbTopologyNode(
            deviceNodeId, attachmentId, [], UsbTopologyNodeKind.Device, "Device", null,
            "controller", string.Empty, string.Empty, null, port.Port, descriptor, attachmentId);
        return new UsbTopologySnapshot([root, port, device], [], DeviceTopologyDiagnostics.Empty, DateTimeOffset.Now);
    }

    private static class InterlockedExtensions
    {
        public static void Max(ref int location, int value)
        {
            var current = Volatile.Read(ref location);
            while (current < value)
            {
                var observed = Interlocked.CompareExchange(ref location, value, current);
                if (observed == current)
                {
                    return;
                }

                current = observed;
            }
        }
    }

    private static UsbDeviceDetailSnapshot Detail(UsbDeviceDetailTarget target)
    {
        return new UsbDeviceDetailSnapshot(
            target.AttachmentId,
            target.DeviceNodeId,
            null,
            null,
            null,
            [],
            [],
            null,
            DeviceTopologyDiagnostics.Empty,
            DateTimeOffset.Now);
    }

    private sealed class FakeSource(Func<UsbDeviceDetailTarget, UsbDeviceDetailSnapshot> collect)
        : IUsbDeviceDetailSource
    {
        private int _callCount;

        public int CallCount => Volatile.Read(ref _callCount);

        public UsbDeviceDetailSnapshot Collect(UsbDeviceDetailTarget target)
        {
            Interlocked.Increment(ref _callCount);
            return collect(target);
        }
    }
}
