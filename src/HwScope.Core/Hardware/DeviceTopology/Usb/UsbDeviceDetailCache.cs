namespace HwScope.Core.Hardware.DeviceTopology.Usb;

public sealed class UsbDeviceDetailCache
{
    private readonly object _sync = new();
    private readonly IUsbDeviceDetailSource _source;
    private readonly SemaphoreSlim _collectionGate;
    private readonly Dictionary<string, CacheState> _states = new(StringComparer.OrdinalIgnoreCase);

    public UsbDeviceDetailCache()
        : this(CreateDefaultSource(), maximumConcurrentCollections: 2)
    {
    }

    private static IUsbDeviceDetailSource CreateDefaultSource()
    {
        var workerPath = Path.Combine(AppContext.BaseDirectory, "HwScope.UsbWorker.exe");
        return File.Exists(workerPath)
            ? new UsbDeviceDetailWorkerSource(workerPath)
            : new UnavailableWorkerSource(workerPath);
    }

    internal UsbDeviceDetailCache(
        IUsbDeviceDetailSource source,
        int maximumConcurrentCollections = 2)
    {
        if (maximumConcurrentCollections <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumConcurrentCollections));
        }

        _source = source;
        _collectionGate = new SemaphoreSlim(
            maximumConcurrentCollections,
            maximumConcurrentCollections);
    }

    public UsbDeviceDetailSnapshot? TryGetCached(string attachmentId, string deviceNodeId)
    {
        lock (_sync)
        {
            return _states.TryGetValue(attachmentId, out var state)
                && string.Equals(state.DeviceNodeId, deviceNodeId, StringComparison.OrdinalIgnoreCase)
                ? state.Cached
                : null;
        }
    }

    public Task<UsbDeviceDetailSnapshot> EnsureLoadedAsync(
        UsbTopologySnapshot snapshot,
        string nodeId,
        CancellationToken cancellationToken = default)
    {
        return GetOrStartAsync(snapshot, nodeId, forceRefresh: false, cancellationToken);
    }

    public Task<UsbDeviceDetailSnapshot> RefreshAsync(
        UsbTopologySnapshot snapshot,
        string nodeId,
        CancellationToken cancellationToken = default)
    {
        return GetOrStartAsync(snapshot, nodeId, forceRefresh: true, cancellationToken);
    }

    public void SynchronizeSnapshot(UsbTopologySnapshot snapshot)
    {
        var current = snapshot.Nodes
            .Where(node => node.AttachmentId is not null
                && node.Kind is UsbTopologyNodeKind.Device or UsbTopologyNodeKind.Hub)
            .ToDictionary(node => node.AttachmentId!, node => node.NodeId, StringComparer.OrdinalIgnoreCase);

        lock (_sync)
        {
            foreach (var entry in _states.ToArray())
            {
                if (!current.TryGetValue(entry.Key, out var deviceNodeId)
                    || !string.Equals(entry.Value.DeviceNodeId, deviceNodeId, StringComparison.OrdinalIgnoreCase))
                {
                    _states.Remove(entry.Key);
                }
            }
        }
    }

    private Task<UsbDeviceDetailSnapshot> GetOrStartAsync(
        UsbTopologySnapshot snapshot,
        string nodeId,
        bool forceRefresh,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var target = UsbDeviceDetailTarget.FromSnapshot(snapshot, nodeId)
            ?? throw new ArgumentException("The selected USB node is not a connected physical device or hub.", nameof(nodeId));

        Task<UsbDeviceDetailSnapshot> sharedTask;
        lock (_sync)
        {
            if (_states.TryGetValue(target.AttachmentId, out var existing)
                && string.Equals(existing.DeviceNodeId, target.DeviceNodeId, StringComparison.OrdinalIgnoreCase))
            {
                if (existing.LoadTask is not null)
                {
                    return existing.LoadTask.WaitAsync(cancellationToken);
                }

                if (!forceRefresh && existing.Cached is not null)
                {
                    return Task.FromResult(existing.Cached).WaitAsync(cancellationToken);
                }
            }

            var state = new CacheState(target.DeviceNodeId);
            _states[target.AttachmentId] = state;
            sharedTask = LoadAndPublishAsync(target, state);
            state.LoadTask = sharedTask;
        }

        return sharedTask.WaitAsync(cancellationToken);
    }

    private async Task<UsbDeviceDetailSnapshot> LoadAndPublishAsync(
        UsbDeviceDetailTarget target,
        CacheState owner)
    {
        try
        {
            await _collectionGate.WaitAsync().ConfigureAwait(false);
            UsbDeviceDetailSnapshot detail;
            try
            {
                detail = await Task.Run(() => _source.Collect(target)).ConfigureAwait(false);
            }
            finally
            {
                _collectionGate.Release();
            }

            lock (_sync)
            {
                if (_states.TryGetValue(target.AttachmentId, out var current) && ReferenceEquals(current, owner))
                {
                    owner.Cached = detail;
                    owner.LoadTask = null;
                }
            }

            return detail;
        }
        catch
        {
            lock (_sync)
            {
                if (_states.TryGetValue(target.AttachmentId, out var current) && ReferenceEquals(current, owner))
                {
                    _states.Remove(target.AttachmentId);
                }
            }

            throw;
        }
    }

    private sealed class CacheState(string deviceNodeId)
    {
        public string DeviceNodeId { get; } = deviceNodeId;
        public UsbDeviceDetailSnapshot? Cached { get; set; }
        public Task<UsbDeviceDetailSnapshot>? LoadTask { get; set; }
    }

    private sealed class UnavailableWorkerSource(string workerPath) : IUsbDeviceDetailSource
    {
        public UsbDeviceDetailSnapshot Collect(UsbDeviceDetailTarget target)
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
                new DeviceTopologyDiagnostics(
                [
                    new DeviceTopologyDiagnostic(
                        DeviceTopologyDiagnosticSeverity.Error,
                        "usb.detail.worker-missing",
                        $"USB descriptor worker is unavailable at {Path.GetFileName(workerPath)}.",
                        target.DeviceNodeId)
                ]),
                DateTimeOffset.Now);
        }
    }
}
