using System.Runtime.InteropServices;

namespace TurzxDisplay.Services;

/// <summary>
/// Minimal DXGI COM interop: enumerate display adapters (name / vendor / VRAM / LUID).
/// Pure user-mode, documented API — no admin, no drivers.
/// </summary>
internal static class Dxgi
{
    [ComImport, Guid("770aae78-f26f-4dba-a829-253c83d1b387"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDXGIFactory1
    {
        // IUnknown (3)
        // IDXGIObject (4)
        void SetPrivateData(); void SetPrivateDataInterface(); void GetPrivateData(); void GetParent();
        // IDXGIFactory (5)
        void EnumAdapters(); void MakeWindowAssociation(); void GetWindowAssociation(); void CreateSwapChain(); void CreateSoftwareAdapter();
        // IDXGIFactory1 (2)
        [PreserveSig]
        int EnumAdapters1(uint index, out IDXGIAdapter1 adapter);
        [PreserveSig]
        int IsCurrent();
    }

    [ComImport, Guid("29038f61-3839-4626-91fd-086879011a05"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDXGIAdapter1
    {
        // IUnknown (3)
        // IDXGIObject (4)
        void SetPrivateData(); void SetPrivateDataInterface(); void GetPrivateData(); void GetParent();
        // IDXGIAdapter (3)
        void EnumOutputs(); void GetDesc(); void CheckInterfaceSupport();
        // IDXGIAdapter1
        [PreserveSig]
        int GetDesc1(out AdapterDesc1 desc);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct AdapterDesc1
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Description;
        public uint VendorId, DeviceId, SubSysId, Revision;
        public ulong DedicatedVideoMemory, DedicatedSystemMemory, SharedSystemMemory;
        public uint LuidLow; public int LuidHigh;
        public uint Flags;
    }

    [DllImport("dxgi.dll")]
    private static extern int CreateDXGIFactory1(in Guid riid, out IDXGIFactory1 factory);

    public static List<HardwareMonitor.GpuAdapter> EnumAdapters()
    {
        var list = new List<HardwareMonitor.GpuAdapter>();
        try
        {
            var iid = new Guid("770aae78-f26f-4dba-a829-253c83d1b387");
            if (CreateDXGIFactory1(in iid, out var factory) != 0) return list;
            for (uint i = 0; factory.EnumAdapters1(i, out var adapter) == 0; i++)
            {
                if (adapter.GetDesc1(out var d) == 0)
                {
                    list.Add(new HardwareMonitor.GpuAdapter(
                        d.Description, VendorName(d.VendorId), (int)d.VendorId,
                        (long)d.DedicatedVideoMemory, d.LuidLow, d.LuidHigh,
                        (long)d.SharedSystemMemory));
                }
                Marshal.ReleaseComObject(adapter);
            }
            Marshal.ReleaseComObject(factory);
        }
        catch (Exception ex)
        {
            Log.Write($"dxgi enum failed: {ex.Message}");
        }
        return list;
    }

    private static string VendorName(uint id) => id switch
    {
        0x10DE => "NVIDIA",
        0x1002 or 0x1022 => "AMD",
        0x8086 => "Intel",
        _ => "GPU",
    };
}
