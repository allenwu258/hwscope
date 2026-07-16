namespace HwScope.Core.Hardware.DeviceTopology.Usb;

public sealed class UsbDeviceDetailCache
{
    private readonly object _sync = new();
    private readonly IUsbDeviceDetailSource _source;
    private readonly Dictionary<string, CacheState> _states = new(StringComparer.OrdinalIgnoreCase);

    public UsbDeviceDetailCache()
        : this(new UsbDeviceDetailCollector())
    {
    }

    internal UsbDeviceDetailCache(IUsbDeviceDetailSource source)
    {
        _source = source;
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
            if (!forceRefresh
                && _states.TryGetValue(target.AttachmentId, out var existing)
                && string.Equals(existing.DeviceNodeId, target.DeviceNodeId, StringComparison.OrdinalIgnoreCase))
            {
                if (existing.Cached is not null)
                {
                    return Task.FromResult(existing.Cached).WaitAsync(cancellationToken);
                }

                if (existing.LoadTask is not null)
                {
                    return existing.LoadTask.WaitAsync(cancellationToken);
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
            var detail = await Task.Run(() => _source.Collect(target)).ConfigureAwait(false);
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
}
