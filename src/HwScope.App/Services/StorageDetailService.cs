using System.Windows;
using System.Windows.Threading;
using HwScope.Core.Hardware.Inventory;
using HwScope.Core.Hardware.Storage;

namespace HwScope.App.Services;

public sealed class StorageDetailService
{
    private readonly StorageDetailCollector _collector = new();
    private readonly HardwarePreloadService _hardwarePreload;
    private readonly Dispatcher _dispatcher;
    private readonly TimeSpan _queryTimeout;
    private readonly object _sync = new();
    private readonly Dictionary<string, DeviceLoadState> _states = new(StringComparer.Ordinal);
    private IReadOnlyList<StorageDeviceDescriptor> _devices = [];

    public StorageDetailService(HardwarePreloadService hardwarePreload)
        : this(hardwarePreload, Application.Current.Dispatcher, TimeSpan.FromSeconds(5))
    {
    }

    internal StorageDetailService(HardwarePreloadService hardwarePreload, Dispatcher dispatcher, TimeSpan queryTimeout)
    {
        if (queryTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(queryTimeout));
        }

        _hardwarePreload = hardwarePreload;
        _dispatcher = dispatcher;
        _queryTimeout = queryTimeout;
        _hardwarePreload.InventoryChanged += HardwarePreload_InventoryChanged;
        if (_hardwarePreload.Current is { } current)
        {
            SynchronizeInventory(current);
        }
    }

    public IReadOnlyList<StorageDeviceDescriptor> Devices
    {
        get
        {
            lock (_sync)
            {
                return _devices;
            }
        }
    }

    public event EventHandler? DevicesChanged;
    public event EventHandler<StorageReportChangedEventArgs>? ReportChanged;

    public void SynchronizeInventory(HardwareInventorySnapshot snapshot)
    {
        var devices = snapshot.DiskDrives
            .Select(StorageDeviceDescriptor.FromSnapshot)
            .OrderBy(device => device.PhysicalDriveNumber ?? int.MaxValue)
            .ThenBy(device => device.Model, StringComparer.OrdinalIgnoreCase)
            .ToList();

        lock (_sync)
        {
            _devices = devices;
            var activeIds = devices.Select(device => device.StableId).ToHashSet(StringComparer.Ordinal);
            foreach (var staleId in _states.Keys.Where(id => !activeIds.Contains(id)).ToList())
            {
                _states.Remove(staleId);
            }
        }

        RaiseOnDispatcher(() => DevicesChanged?.Invoke(this, EventArgs.Empty));
    }

    public StorageDetailReport? TryGetCached(string stableId)
    {
        lock (_sync)
        {
            return _states.TryGetValue(stableId, out var state) ? state.Current : null;
        }
    }

    public Task<StorageDetailReport> EnsureLoadedAsync(string stableId, CancellationToken cancellationToken = default)
    {
        return GetOrStartAsync(stableId, forceRefresh: false, cancellationToken);
    }

    public Task<StorageDetailReport> RefreshAsync(string stableId, CancellationToken cancellationToken = default)
    {
        return GetOrStartAsync(stableId, forceRefresh: true, cancellationToken);
    }

    private async Task<StorageDetailReport> GetOrStartAsync(string stableId, bool forceRefresh, CancellationToken cancellationToken)
    {
        DeviceLoadState state;
        StorageDeviceDescriptor device;
        lock (_sync)
        {
            device = _devices.FirstOrDefault(candidate => candidate.StableId == stableId)
                ?? throw new InvalidOperationException("选中的存储设备已不存在。");
            if (!_states.TryGetValue(stableId, out state!))
            {
                state = new DeviceLoadState();
                _states.Add(stableId, state);
            }
        }

        Task<StorageDetailReport> task;
        await state.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            StorageDetailReport? cached;
            lock (_sync)
            {
                cached = state.Current;
            }

            if (!forceRefresh && cached is not null)
            {
                return cached;
            }

            if (state.ActiveTask is { IsCompleted: false })
            {
                task = state.ActiveTask;
            }
            else
            {
                task = LoadAsync(device, state);
                state.ActiveTask = task;
            }
        }
        finally
        {
            state.Gate.Release();
        }

        try
        {
            return await StorageQueryTimeoutPolicy.WaitAsync(task, _queryTimeout, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException ex)
        {
            throw new TimeoutException(
                $"存储设备查询超过 {_queryTimeout.TotalSeconds:0.#} 秒；底层驱动调用可能仍在后台返回。",
                ex);
        }
    }

    private async Task<StorageDetailReport> LoadAsync(StorageDeviceDescriptor device, DeviceLoadState state)
    {
        try
        {
            var report = await _collector.CollectAsync(device).ConfigureAwait(false);
            lock (_sync)
            {
                state.Current = report;
                state.LastError = null;
            }
            RaiseOnDispatcher(() => ReportChanged?.Invoke(this, new StorageReportChangedEventArgs(device.StableId, report, null)));
            return report;
        }
        catch (Exception ex)
        {
            lock (_sync)
            {
                state.LastError = ex;
            }
            RaiseOnDispatcher(() => ReportChanged?.Invoke(this, new StorageReportChangedEventArgs(device.StableId, state.Current, ex)));
            throw;
        }
        finally
        {
            await state.Gate.WaitAsync().ConfigureAwait(false);
            try
            {
                state.ActiveTask = null;
            }
            finally
            {
                state.Gate.Release();
            }
        }
    }

    private void HardwarePreload_InventoryChanged(object? sender, HardwareInventorySnapshot snapshot)
    {
        SynchronizeInventory(snapshot);
    }

    private void RaiseOnDispatcher(Action action)
    {
        if (_dispatcher.CheckAccess())
        {
            action();
        }
        else
        {
            _dispatcher.BeginInvoke(action);
        }
    }

    private sealed class DeviceLoadState
    {
        public SemaphoreSlim Gate { get; } = new(1, 1);
        public Task<StorageDetailReport>? ActiveTask { get; set; }
        public StorageDetailReport? Current { get; set; }
        public Exception? LastError { get; set; }
    }
}

public sealed record StorageReportChangedEventArgs(string StableId, StorageDetailReport? Report, Exception? Error);
