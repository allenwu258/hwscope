using System.Runtime.InteropServices;

namespace HwScope.Core.Windows.Graphics;

internal sealed record DxgiAdapterInfo(
    string Description,
    ulong DedicatedVideoMemoryBytes,
    ulong DedicatedSystemMemoryBytes,
    ulong SharedSystemMemoryBytes,
    uint VendorId,
    uint DeviceId,
    uint SubSystemId,
    uint Revision,
    ulong Luid,
    bool IsSoftware);

internal sealed record DxgiEnumerationResult(
    IReadOnlyList<DxgiAdapterInfo> Adapters,
    IReadOnlyList<string> Diagnostics);

internal static class DxgiAdapterEnumerator
{
    private const int DxgiErrorNotFound = unchecked((int)0x887A0002);
    private const uint MaximumAdapters = 256;

    public static DxgiEnumerationResult Collect()
    {
        var adapters = new List<DxgiAdapterInfo>();
        var diagnostics = new List<string>();
        if (!OperatingSystem.IsWindows() || !Environment.Is64BitProcess)
        {
            return new([], ["DXGI video memory inventory requires a 64-bit Windows process."]);
        }

        nint factoryPointer = 0;
        IDXGIFactory1? factory = null;
        try
        {
            var iid = new Guid("770aae78-f26f-4dba-a829-253c83d1b387");
            var hr = CreateDXGIFactory1(in iid, out factoryPointer);
            if (hr < 0 || factoryPointer == 0)
            {
                diagnostics.Add($"CreateDXGIFactory1 failed (HRESULT 0x{hr:X8}).");
                return new(adapters, diagnostics);
            }

            try
            {
                factory = (IDXGIFactory1)Marshal.GetTypedObjectForIUnknown(factoryPointer, typeof(IDXGIFactory1));
            }
            finally
            {
                Marshal.Release(factoryPointer);
                factoryPointer = 0;
            }

            if (factory is null)
            {
                diagnostics.Add("DXGI factory could not be wrapped as IDXGIFactory1.");
                return new(adapters, diagnostics);
            }

            for (uint index = 0; index < MaximumAdapters; index++)
            {
                IDXGIAdapter1? adapter = null;
                try
                {
                    hr = factory.EnumAdapters1(index, out adapter);
                    if (hr == DxgiErrorNotFound)
                    {
                        return new(adapters, diagnostics);
                    }
                    if (hr < 0 || adapter is null)
                    {
                        diagnostics.Add($"EnumAdapters1({index}) failed (HRESULT 0x{hr:X8}).");
                        return new(adapters, diagnostics);
                    }

                    hr = adapter.GetDesc1(out var description);
                    if (hr < 0)
                    {
                        diagnostics.Add($"GetDesc1({index}) failed (HRESULT 0x{hr:X8}).");
                        continue;
                    }

                    adapters.Add(new DxgiAdapterInfo(
                        description.Description?.TrimEnd('\0').Trim() ?? string.Empty,
                        description.DedicatedVideoMemory,
                        description.DedicatedSystemMemory,
                        description.SharedSystemMemory,
                        description.VendorId,
                        description.DeviceId,
                        description.SubSystemId,
                        description.Revision,
                        ((ulong)(uint)description.LuidHigh << 32) | description.LuidLow,
                        (description.Flags & 2) != 0));
                }
                finally
                {
                    Release(adapter);
                }
            }
            diagnostics.Add($"DXGI enumeration exceeded the {MaximumAdapters}-adapter limit.");
        }
        catch (Exception ex) when (ex is COMException or DllNotFoundException or EntryPointNotFoundException or BadImageFormatException or MarshalDirectiveException or InvalidCastException or ArgumentException or AccessViolationException)
        {
            diagnostics.Add($"DXGI enumeration failed: {ex.Message}");
        }
        finally
        {
            Release(factory);
            if (factoryPointer != 0)
            {
                Marshal.Release(factoryPointer);
            }
        }

        return new(adapters, diagnostics);
    }

    private static void Release(object? instance)
    {
        try
        {
            if (instance is not null && Marshal.IsComObject(instance))
            {
                Marshal.ReleaseComObject(instance);
            }
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or AccessViolationException or ArgumentException)
        {
            // COM cleanup is best effort. The primary enumeration error is preserved.
        }
    }

    [DllImport("dxgi.dll", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int CreateDXGIFactory1(in Guid riid, out nint factory);

    [ComImport]
    [Guid("770aae78-f26f-4dba-a829-253c83d1b387")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDXGIFactory1
    {
        [PreserveSig] int SetPrivateData(in Guid name, uint dataSize, nint data);
        [PreserveSig] int SetPrivateDataInterface(in Guid name, nint unknown);
        [PreserveSig] int GetPrivateData(in Guid name, ref uint dataSize, nint data);
        [PreserveSig] int GetParent(in Guid riid, out nint parent);
        [PreserveSig] int EnumAdapters(uint index, out nint adapter);
        [PreserveSig] int MakeWindowAssociation(nint windowHandle, uint flags);
        [PreserveSig] int GetWindowAssociation(out nint windowHandle);
        [PreserveSig] int CreateSwapChain(nint device, nint description, out nint swapChain);
        [PreserveSig] int CreateSoftwareAdapter(nint module, out nint adapter);
        [PreserveSig] int EnumAdapters1(uint index, [MarshalAs(UnmanagedType.Interface)] out IDXGIAdapter1? adapter);
    }

    [ComImport]
    [Guid("29038f61-3839-4626-91fd-086879011a05")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDXGIAdapter1
    {
        [PreserveSig] int SetPrivateData(in Guid name, uint dataSize, nint data);
        [PreserveSig] int SetPrivateDataInterface(in Guid name, nint unknown);
        [PreserveSig] int GetPrivateData(in Guid name, ref uint dataSize, nint data);
        [PreserveSig] int GetParent(in Guid riid, out nint parent);
        [PreserveSig] int EnumOutputs(uint index, out nint output);
        [PreserveSig] int GetDesc(out nint description);
        [PreserveSig] int CheckInterfaceSupport(in Guid interfaceName, out long userModeDriverVersion);
        [PreserveSig] int GetDesc1(out AdapterDescription description);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct AdapterDescription
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string? Description;
        public uint VendorId;
        public uint DeviceId;
        public uint SubSystemId;
        public uint Revision;
        public nuint DedicatedVideoMemory;
        public nuint DedicatedSystemMemory;
        public nuint SharedSystemMemory;
        public uint LuidLow;
        public int LuidHigh;
        public uint Flags;
    }
}
