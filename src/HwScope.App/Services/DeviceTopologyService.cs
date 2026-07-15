using System.Windows;
using System.Windows.Threading;
using HwScope.Core.Hardware.DeviceTopology;
using HwScope.Core.Hardware.DeviceTopology.Pci;

namespace HwScope.App.Services;

public sealed class DeviceTopologyService
{
    private readonly PciTopologyCollector _pciCollector = new();
    private readonly Dispatcher _dispatcher;
    private readonly SemaphoreSlim _pciGate = new(1, 1);
    private Task<PciTopologyRefreshResult>? _pciLoadTask;

    public DeviceTopologyService()
        : this(Application.Current.Dispatcher)
    {
    }

    internal DeviceTopologyService(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
    }

    public PciTopologySnapshot? CurrentPci { get; private set; }

    public bool IsPciLoading => _pciLoadTask is { IsCompleted: false };

    public event EventHandler<PciTopologySnapshot>? PciSnapshotChanged;

    public event EventHandler<PciTopologyRefreshResult>? PciStateChanged;

    public Task<PciTopologyRefreshResult> EnsurePciLoadedAsync(CancellationToken cancellationToken = default)
    {
        return GetOrStartPciLoadAsync(forceRefresh: false, cancellationToken);
    }

    public Task<PciTopologyRefreshResult> RefreshPciAsync(CancellationToken cancellationToken = default)
    {
        return GetOrStartPciLoadAsync(forceRefresh: true, cancellationToken);
    }

    private async Task<PciTopologyRefreshResult> GetOrStartPciLoadAsync(bool forceRefresh, CancellationToken cancellationToken)
    {
        Task<PciTopologyRefreshResult> task;
        await _pciGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!forceRefresh && CurrentPci is not null)
            {
                return new PciTopologyRefreshResult(
                    CurrentPci,
                    IsStale: false,
                    CollectionFailed: false,
                    DeviceTopologyDiagnostics.Empty);
            }

            if (_pciLoadTask is { IsCompleted: false })
            {
                task = _pciLoadTask;
            }
            else
            {
                task = LoadPciAsync();
                _pciLoadTask = task;
            }
        }
        finally
        {
            _pciGate.Release();
        }

        return await task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<PciTopologyRefreshResult> LoadPciAsync()
    {
        try
        {
            var attempted = await Task.Run(_pciCollector.Collect).ConfigureAwait(false);
            var result = PciTopologyRefreshPolicy.Resolve(CurrentPci, attempted);
            if (!result.CollectionFailed)
            {
                CurrentPci = result.Snapshot;
                RaiseOnDispatcher(() => PciSnapshotChanged?.Invoke(this, result.Snapshot));
            }

            RaiseOnDispatcher(() => PciStateChanged?.Invoke(this, result));
            return result;
        }
        finally
        {
            await _pciGate.WaitAsync().ConfigureAwait(false);
            try
            {
                _pciLoadTask = null;
            }
            finally
            {
                _pciGate.Release();
            }
        }
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
