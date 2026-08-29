using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;

namespace TurzxDisplay.Services;

public sealed class HardwareSnapshot
{
    public string CpuName = "";
    public float CpuUsage;            // 0..100
    public double MemUsedGB, MemTotalGB;
    public string GpuName = "";
    public string GpuVendor = "";
    public float GpuUsage;            // 0..100 (—: NaN when unavailable)
    public int GpuTempC = -1;         // -1 unknown
    public double VramUsedGB, VramTotalGB;
    public double NetDownKBs, NetUpKBs;
}

/// <summary>
/// Safe hardware sampling (no kernel drivers, no admin):
///  CPU model = registry; CPU% = GetSystemTimes deltas; memory = GlobalMemoryStatusEx;
///  GPU = DXGI adapter enumeration (discrete first) + "GPU Engine"/"GPU Adapter Memory"
///  performance counters filtered by adapter LUID; GPU temp = NVML/ADL when present.
/// </summary>
public static class HardwareMonitor
{
    // ---------------- snapshot ----------------

    public static HardwareSnapshot Sample()
    {
        var s = new HardwareSnapshot
        {
            CpuName = CpuName(),
            CpuUsage = CpuUsage(),
            MemUsedGB = _mem.ullTotalPhys - _mem.ullAvailPhys,
            MemTotalGB = _mem.ullTotalPhys,
        };
        s.MemUsedGB /= 1024 * 1024 * 1024.0;
        s.MemTotalGB /= 1024 * 1024 * 1024.0;

        var gpu = PreferredGpu();
        if (gpu is not null)
        {
            s.GpuName = gpu.Value.Name;
            s.GpuVendor = gpu.Value.Vendor;
            s.VramTotalGB = gpu.Value.TotalBytes / 1024.0 / 1024 / 1024;
            (s.GpuUsage, s.VramUsedGB) = GpuCounters(gpu.Value);
            s.GpuTempC = gpu.Value.VendorId switch
            {
                0x10DE => _nvml?.Temp() ?? -1,
                0x1002 or 0x1022 => _adl?.Temp() ?? -1,
                _ => -1,
            };
        }

        (s.NetDownKBs, s.NetUpKBs) = NetRates();
        return s;
    }

    // ---------------- CPU ----------------

    private static string? _cpuName;
    private static string CpuName() => _cpuName ??= Microsoft.Win32.Registry.GetValue(
        @"HKEY_LOCAL_MACHINE\HARDWARE\DESCRIPTION\System\CentralProcessor\0", "ProcessorNameString", "CPU") as string ?? "CPU";

    private static long _idle, _kernel, _user;
    private static float CpuUsage()
    {
        GetSystemTimes(out var idle, out var kernel, out var user);
        long di = idle - _idle, dk = kernel - _kernel, du = user - _user;
        _idle = idle; _kernel = kernel; _user = user;
        if (dk + du <= 0 || di < 0) return 0;
        return Math.Clamp(100f * (1f - di / (float)(dk + du)), 0, 100);
    }

    // ---------------- memory ----------------

    [StructLayout(LayoutKind.Sequential)]
    private class MEMORYSTATUSEX
    {
        public uint dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>();
        public uint dwMemoryLoad;
        public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile, ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual;
    }
    private static readonly MEMORYSTATUSEX _mem = new();
    static HardwareMonitor() { _ = GlobalMemoryStatusEx(_mem); }

    // ---------------- GPU: DXGI ----------------

    public readonly record struct GpuAdapter(string Name, string Vendor, int VendorId, long VramBytes, uint LuidLow, int LuidHigh, long SharedBytes = 0)
    {
        /// <summary>iGPU with tiny dedicated VRAM (e.g. Intel Arc iGPU): memory budget is shared system memory.</summary>
        public bool IsIntegrated => VendorId == 0x8086 && VramBytes < (1L << 30);
        public long TotalBytes => IsIntegrated ? VramBytes + SharedBytes : VramBytes;
    }

    private static List<GpuAdapter>? _adapters;
    private static GpuAdapter? PreferredGpu()
    {
        if (_adapters is null)
        {
            _adapters = Dxgi.EnumAdapters();
            Log.Write($"dxgi adapters: {string.Join(" | ", _adapters.Select(a => $"{a.Vendor}:{a.Name}({a.VramBytes / 1048576}MB,luid_0x{a.LuidLow:x8}_0x{a.LuidHigh:x8},shared={a.SharedBytes / 1048576}MB)"))}");
        }
        if (_adapters.Count == 0) return null;

        // discrete (NVIDIA/AMD, or >= 1 GB dedicated) first, largest VRAM wins; else any real adapter
        var real = _adapters.Where(a => !a.Name.Contains("Basic Render", StringComparison.OrdinalIgnoreCase)).ToList();
        var discrete = real.Where(a => (a.VendorId is 0x10DE or 0x1002 or 0x1022) || a.VramBytes >= 1L << 30)
                           .OrderByDescending(a => a.VramBytes).ToList();
        return discrete.Count > 0 ? discrete[0] : real.OrderByDescending(a => a.VramBytes).FirstOrDefault();
    }

    // ---------------- GPU: perf counters (all vendors) ----------------

    private static List<PerformanceCounter>? _utilCounters;
    private static List<PerformanceCounter>? _memCounters;
    private static int _counterAge;

    private static (float usage, double vramUsedGB) GpuCounters(GpuAdapter gpu)
    {
        try
        {
            if (_utilCounters is null || _counterAge-- <= 0)
            {
                RefreshGpuCounters(gpu);
                _counterAge = 15;
            }

            float sum = 0;
            foreach (var c in _utilCounters!) sum += c.NextValue();

            double bytes = 0;
            foreach (var c in _memCounters!) bytes += c.NextValue();

            return (Math.Clamp(sum, 0, 100), bytes / 1024.0 / 1024 / 1024);
        }
        catch (Exception ex)
        {
            Log.Write($"gpu counters failed: {ex.Message}");
            _utilCounters = null;
            return (float.NaN, 0);
        }
    }

    /// <summary>
    /// Bind "GPU Engine"/"GPU Adapter Memory" counters to the adapter. Instance names
    /// print the LUID as luid_0x&lt;High&gt;_0x&lt;Low&gt; (opposite of the DXGI field order);
    /// both orders are tried just in case.
    /// </summary>
    private static void RefreshGpuCounters(GpuAdapter gpu)
    {
        string[] orders =
        {
            $"luid_0x{gpu.LuidHigh:X8}_0x{gpu.LuidLow:X8}",
            $"luid_0x{gpu.LuidLow:X8}_0x{gpu.LuidHigh:X8}",
        };

        foreach (var luid in orders)
        {
            _utilCounters = new List<PerformanceCounter>();
            var eng = new PerformanceCounterCategory("GPU Engine");
            foreach (var inst in eng.GetInstanceNames().Where(i => i.Contains(luid)))
            {
                foreach (var c in eng.GetCounters(inst))
                    if (c.CounterName == "Utilization Percentage")
                        _utilCounters.Add(c);
            }

            // iGPUs live in shared memory; dGPUs in dedicated. Track the right counters.
            _memCounters = new List<PerformanceCounter>();
            var mem = new PerformanceCounterCategory("GPU Adapter Memory");
            var want = gpu.IsIntegrated
                ? new[] { "Dedicated Usage", "Shared Usage" }
                : new[] { "Dedicated Usage" };
            foreach (var inst in mem.GetInstanceNames().Where(i => i.Contains(luid)))
            {
                foreach (var c in mem.GetCounters(inst))
                    if (want.Contains(c.CounterName))
                        _memCounters.Add(c);
            }

            if (_utilCounters.Count > 0 || _memCounters.Count > 0)
            {
                Log.Write($"gpu counters: {luid} integrated={gpu.IsIntegrated} util={_utilCounters.Count} mem={_memCounters.Count}");
                return;
            }
        }
        Log.Write($"gpu counters: no instances matched {orders[0]} / {orders[1]}");
    }

    // ---------------- GPU temp (optional vendor libs) ----------------

    private static readonly Nvml? _nvml = Nvml.TryCreate();
    private static readonly Adl? _adl = Adl.TryCreate();

    private sealed class Nvml
    {
        [DllImport("nvml.dll")] private static extern int nvmlInit_v2();
        [DllImport("nvml.dll")] private static extern int nvmlDeviceGetHandleByIndex_v2(int index, out IntPtr device);
        [DllImport("nvml.dll")] private static extern int nvmlDeviceGetTemperature(IntPtr device, int sensorType, out int temp);
        [DllImport("nvml.dll")] private static extern int nvmlShutdown();

        private readonly IntPtr _dev;
        private Nvml(IntPtr dev) { _dev = dev; }

        public static Nvml? TryCreate()
        {
            try
            {
                if (nvmlInit_v2() != 0) return null;
                if (nvmlDeviceGetHandleByIndex_v2(0, out var dev) != 0) { nvmlShutdown(); return null; }
                Log.Write("nvml: loaded");
                return new Nvml(dev);
            }
            catch { return null; }   // dll absent
        }

        public int Temp() => nvmlDeviceGetTemperature(_dev, 0 /*NVML_TEMPERATURE_GPU*/, out var t) == 0 ? t : -1;
    }

    private sealed class Adl
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct ADLTemperature { public int iSize; public int iTemperature; }

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr Alloc(int size);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void Free(IntPtr ptr);
        [DllImport("atiadlxx.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern int ADL_Main_Control_Create(Alloc alloc, int enumConnectedAdapters);
        [DllImport("atiadlxx.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern int ADL_Overdrive5_Temperature_Get(int adapter, int thermalIndex, ref ADLTemperature temp);

        private static Alloc? _alloc;     // keep delegates alive
        private static Free? _free;

        public static Adl? TryCreate()
        {
            try
            {
                _alloc = size => Marshal.AllocHGlobal(size);
                _free = ptr => Marshal.FreeHGlobal(ptr);
                if (ADL_Main_Control_Create(_alloc, 1) != 0) return null;
                Log.Write("adl: loaded");
                return new Adl();
            }
            catch { return null; }
        }

        public int Temp()
        {
            var t = new ADLTemperature { iSize = 8 };
            return ADL_Overdrive5_Temperature_Get(0, 0, ref t) == 0 ? t.iTemperature : -1;
        }
    }

    // ---------------- network ----------------

    private static double _rx, _tx;
    private static DateTime _netAt = DateTime.MinValue;

    private static (double downKBs, double upKBs) NetRates()
    {
        double rx = 0, tx = 0;
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
                if (nic.OperationalStatus != OperationalStatus.Up) continue;
                var st = nic.GetIPv4Statistics();
                rx += st.BytesReceived;
                tx += st.BytesSent;
            }
        }
        catch { /* transient */ }

        var now = DateTime.UtcNow;
        double down = 0, up = 0;
        if (_netAt != DateTime.MinValue)
        {
            var dt = (now - _netAt).TotalSeconds;
            if (dt > 0.2) { down = Math.Max(0, rx - _rx) / dt / 1024.0; up = Math.Max(0, tx - _tx) / dt / 1024.0; }
        }
        _rx = rx; _tx = tx; _netAt = now;
        return (down, up);
    }

    // ---------------- P/Invoke ----------------

    [DllImport("kernel32.dll")]
    private static extern bool GetSystemTimes(out long idle, out long kernel, out long user);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx([In, Out] MEMORYSTATUSEX data);
}
