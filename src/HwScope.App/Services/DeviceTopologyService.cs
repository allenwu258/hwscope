using System.Windows;
using System.Windows.Threading;
using HwScope.Core.Hardware.DeviceTopology.Pci;

namespace HwScope.App.Services;

public sealed class DeviceTopologyService
{
    private readonly PciTopologyCollector _pciCollector = new();
    private readonly Dispatcher _dispatcher;
    private readonly SemaphoreSlim _pciGate = new(1, 1);
    private Task<PciTopologySnapshot>? _pciLoadTask;

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

    public Task<PciTopologySnapshot> EnsurePciLoadedAsync(CancellationToken cancellationToken = default)
    {
        return GetOrStartPciLoadAsync(forceRefresh: false, cancellationToken);
    }

    public Task<PciTopologySnapshot> RefreshPciAsync(CancellationToken cancellationToken = default)
    {
        return GetOrStartPciLoadAsync(forceRefresh: true, cancellationToken);
    }

    private async Task<PciTopologySnapshot> GetOrStartPciLoadAsync(bool forceRefresh, CancellationToken cancellationToken)
    {
        Task<PciTopologySnapshot> task;
        await _pciGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!forceRefresh && CurrentPci is not null)
            {
                return CurrentPci;
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

    private async Task<PciTopologySnapshot> LoadPciAsync()
    {
        try
        {
            var snapshot = await Task.Run(_pciCollector.Collect).ConfigureAwait(false);
            CurrentPci = snapshot;
            RaiseOnDispatcher(() => PciSnapshotChanged?.Invoke(this, snapshot));
            return snapshot;
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
