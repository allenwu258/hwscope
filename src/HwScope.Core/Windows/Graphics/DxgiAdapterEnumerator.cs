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

    public static unsafe DxgiEnumerationResult Collect()
    {
        var adapters = new List<DxgiAdapterInfo>();
        var diagnostics = new List<string>();
        if (!OperatingSystem.IsWindows() || !Environment.Is64BitProcess)
        {
            return new([], ["DXGI video memory inventory requires a 64-bit Windows process."]);
        }

        nint factory = 0;
        try
        {
            var iid = new Guid("770aae78-f26f-4dba-a829-253c83d1b387");
            var hr = CreateDXGIFactory1(in iid, out factory);
            if (hr < 0 || factory == 0)
            {
                diagnostics.Add($"CreateDXGIFactory1 failed (HRESULT 0x{hr:X8}).");
                return new(adapters, diagnostics);
            }

            // IDXGIFactory1::EnumAdapters1 is slot 12, after IUnknown, IDXGIObject and IDXGIFactory.
            var enumerate = (delegate* unmanaged[Stdcall]<nint, uint, nint*, int>)(*(nint**)factory)[12];
            for (uint index = 0; index < MaximumAdapters; index++)
            {
                nint adapter = 0;
                try
                {
                    hr = enumerate(factory, index, &adapter);
                    if (hr == DxgiErrorNotFound)
                    {
                        return new(adapters, diagnostics);
                    }
                    if (hr < 0 || adapter == 0)
                    {
                        diagnostics.Add($"EnumAdapters1({index}) failed (HRESULT 0x{hr:X8}).");
                        return new(adapters, diagnostics);
                    }

                    // IDXGIAdapter1::GetDesc1 is slot 10; preserve its HRESULT explicitly.
                    var getDescription = (delegate* unmanaged[Stdcall]<nint, AdapterDescription*, int>)(*(nint**)adapter)[10];
                    AdapterDescription description = default;
                    hr = getDescription(adapter, &description);
                    if (hr < 0)
                    {
                        diagnostics.Add($"GetDesc1({index}) failed (HRESULT 0x{hr:X8}).");
                        continue;
                    }

                    adapters.Add(new DxgiAdapterInfo(
                        new string(description.Description, 0, 128).TrimEnd('\0').Trim(),
                        (ulong)description.DedicatedVideoMemory,
                        (ulong)description.DedicatedSystemMemory,
                        (ulong)description.SharedSystemMemory,
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
        catch (Exception ex) when (ex is COMException or DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            diagnostics.Add($"DXGI enumeration failed: {ex.Message}");
        }
        finally
        {
            Release(factory);
        }

        return new(adapters, diagnostics);
    }

    private static unsafe void Release(nint instance)
    {
        if (instance != 0)
        {
            var release = (delegate* unmanaged[Stdcall]<nint, uint>)(*(nint**)instance)[2];
            release(instance);
        }
    }

    [DllImport("dxgi.dll", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int CreateDXGIFactory1(in Guid riid, out nint factory);

    [StructLayout(LayoutKind.Sequential)]
    internal unsafe struct AdapterDescription
    {
        public fixed char Description[128];
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
