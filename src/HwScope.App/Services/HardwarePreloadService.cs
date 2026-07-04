using HwScope.Core.Hardware.Inventory;
using System.Windows;
using System.Windows.Threading;

namespace HwScope.App.Services;

public sealed class HardwarePreloadService
{
    private readonly HardwareInventoryCollector _collector = new();
    private readonly Dispatcher _dispatcher;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Task<HardwareInventorySnapshot>? _loadTask;

    public HardwarePreloadService()
        : this(Application.Current.Dispatcher)
    {
    }

    public HardwarePreloadService(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
    }

    public HardwarePreloadState State { get; private set; } = HardwarePreloadState.NotStarted;
    public HardwareInventorySnapshot? Current { get; private set; }
    public string? LastStatusMessage { get; private set; }

    public event EventHandler<HardwarePreloadProgress>? ProgressChanged;
    public event EventHandler<HardwareInventorySnapshot>? InventoryChanged;

    public Task<HardwareInventorySnapshot> EnsureLoadedAsync(CancellationToken cancellationToken = default)
    {
        if (Current is not null)
        {
            return Task.FromResult(Current);
        }

        return GetOrStartLoadAsync(forceRefresh: false, cancellationToken);
    }

    public Task<HardwareInventorySnapshot> RefreshAsync(CancellationToken cancellationToken = default)
    {
        return GetOrStartLoadAsync(forceRefresh: true, cancellationToken);
    }

    private async Task<HardwareInventorySnapshot> GetOrStartLoadAsync(bool forceRefresh, CancellationToken cancellationToken)
    {
        Task<HardwareInventorySnapshot> task;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!forceRefresh && Current is not null)
            {
                return Current;
            }

            if (_loadTask is { IsCompleted: false })
            {
                task = _loadTask;
            }
            else
            {
                task = LoadAsync(cancellationToken);
                _loadTask = task;
            }
        }
        finally
        {
            _gate.Release();
        }

        return await task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<HardwareInventorySnapshot> LoadAsync(CancellationToken cancellationToken)
    {
        SetState(HardwarePreloadState.Loading, "正在预加载硬件信息...");
        try
        {
            var snapshot = await Task.Run(_collector.Collect, cancellationToken).ConfigureAwait(false);
            Current = snapshot;
            _loadTask = null;
            SetState(HardwarePreloadState.Ready, $"硬件信息预加载完成，用时 {snapshot.Diagnostics.Elapsed.TotalMilliseconds:F0} ms。");
            RaiseInventoryChanged(snapshot);
            return snapshot;
        }
        catch
        {
            _loadTask = null;
            SetState(HardwarePreloadState.Failed, "硬件信息预加载失败。");
            throw;
        }
    }

    private void SetState(HardwarePreloadState state, string status)
    {
        State = state;
        LastStatusMessage = status;
        RaiseOnDispatcher(() => ProgressChanged?.Invoke(this, new HardwarePreloadProgress(state, status)));
    }

    private void RaiseInventoryChanged(HardwareInventorySnapshot snapshot)
    {
        RaiseOnDispatcher(() => InventoryChanged?.Invoke(this, snapshot));
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
}

public enum HardwarePreloadState
{
    NotStarted,
    Loading,
    Ready,
    Failed
}

public sealed record HardwarePreloadProgress(HardwarePreloadState State, string Message);
