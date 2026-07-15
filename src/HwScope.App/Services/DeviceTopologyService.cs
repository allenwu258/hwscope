using System.Windows;
using System.Windows.Threading;
using HwScope.Core.Hardware.DeviceTopology;
using HwScope.Core.Hardware.DeviceTopology.Pci;
using HwScope.Core.Hardware.DeviceTopology.Usb;

namespace HwScope.App.Services;

public sealed class DeviceTopologyService
{
    private readonly PciTopologyCollector _pciCollector = new();
    private readonly UsbTopologyCollector _usbCollector = new();
    private readonly Dispatcher _dispatcher;
    private readonly SemaphoreSlim _pciGate = new(1, 1);
    private readonly SemaphoreSlim _usbGate = new(1, 1);
    private Task<PciTopologyRefreshResult>? _pciLoadTask;
    private Task<UsbTopologyRefreshResult>? _usbLoadTask;

    public DeviceTopologyService()
        : this(Application.Current.Dispatcher)
    {
    }

    internal DeviceTopologyService(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
    }

    public PciTopologySnapshot? CurrentPci { get; private set; }
    public UsbTopologySnapshot? CurrentUsb { get; private set; }

    public bool IsPciLoading => _pciLoadTask is { IsCompleted: false };
    public bool IsUsbLoading => _usbLoadTask is { IsCompleted: false };

    public event EventHandler<PciTopologySnapshot>? PciSnapshotChanged;

    public event EventHandler<PciTopologyRefreshResult>? PciStateChanged;
    public event EventHandler<UsbTopologySnapshot>? UsbSnapshotChanged;
    public event EventHandler<UsbTopologyRefreshResult>? UsbStateChanged;

    public Task<PciTopologyRefreshResult> EnsurePciLoadedAsync(CancellationToken cancellationToken = default)
    {
        return GetOrStartPciLoadAsync(forceRefresh: false, cancellationToken);
    }

    public Task<PciTopologyRefreshResult> RefreshPciAsync(CancellationToken cancellationToken = default)
    {
        return GetOrStartPciLoadAsync(forceRefresh: true, cancellationToken);
    }

    public Task<UsbTopologyRefreshResult> EnsureUsbLoadedAsync(CancellationToken cancellationToken = default)
    {
        return GetOrStartUsbLoadAsync(forceRefresh: false, cancellationToken);
    }

    public Task<UsbTopologyRefreshResult> RefreshUsbAsync(CancellationToken cancellationToken = default)
    {
        return GetOrStartUsbLoadAsync(forceRefresh: true, cancellationToken);
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

    private async Task<UsbTopologyRefreshResult> GetOrStartUsbLoadAsync(bool forceRefresh, CancellationToken cancellationToken)
    {
        Task<UsbTopologyRefreshResult> task;
        await _usbGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!forceRefresh && CurrentUsb is not null)
            {
                return new UsbTopologyRefreshResult(
                    CurrentUsb,
                    IsStale: false,
                    CollectionFailed: false,
                    DeviceTopologyDiagnostics.Empty);
            }

            if (_usbLoadTask is { IsCompleted: false })
            {
                task = _usbLoadTask;
            }
            else
            {
                task = LoadUsbAsync();
                _usbLoadTask = task;
            }
        }
        finally
        {
            _usbGate.Release();
        }

        return await task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<UsbTopologyRefreshResult> LoadUsbAsync()
    {
        try
        {
            var attempted = await Task.Run(_usbCollector.Collect).ConfigureAwait(false);
            var result = UsbTopologyRefreshPolicy.Resolve(CurrentUsb, attempted);
            if (!result.CollectionFailed)
            {
                CurrentUsb = result.Snapshot;
                RaiseOnDispatcher(() => UsbSnapshotChanged?.Invoke(this, result.Snapshot));
            }

            RaiseOnDispatcher(() => UsbStateChanged?.Invoke(this, result));
            return result;
        }
        finally
        {
            await _usbGate.WaitAsync().ConfigureAwait(false);
            try
            {
                _usbLoadTask = null;
            }
            finally
            {
                _usbGate.Release();
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
