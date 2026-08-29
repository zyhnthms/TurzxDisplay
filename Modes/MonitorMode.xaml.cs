using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using TurzxDisplay.Services;

namespace TurzxDisplay.Modes;

/// <summary>
/// 监控模式: CPU/GPU/内存/网络, refreshed every 2 s by the shared render loop.
/// </summary>
public sealed partial class MonitorMode : UserControl, IDisplayMode
{
    private double _netPeakKBs = 512;   // auto-scaling for the net bars, decays slowly

    public string Key => "Monitor";
    public string Title => "监控";
    public string IconGlyph => "";
    public FrameworkElement View => this;
    public bool PeriodicRefresh => true;
    public TimeSpan RefreshInterval => TimeSpan.FromSeconds(2);
    public event Action? ContentChanged { add { } remove { } }

    public MonitorMode()
    {
        InitializeComponent();
    }

    public void Tick(DateTime now) => Update();

    public void OnActivated() => Update();
    public void OnDeactivated() { }

    private void Update()
    {
        try
        {
            var s = HardwareMonitor.Sample();

            // CPU
            CpuUsageText.Text = $"{s.CpuUsage:0}%";
            CpuNameText.Text = s.CpuName;
            SetBar(CpuBar, s.CpuUsage / 100.0);

            // memory
            MemText.Text = $"{s.MemUsedGB:0.0} / {s.MemTotalGB:0.0} GB";
            double memPct = s.MemTotalGB > 0 ? s.MemUsedGB / s.MemTotalGB : 0;
            MemPct.Text = $"{memPct:P0}";
            SetBar(MemBar, memPct);

            // GPU
            if (!string.IsNullOrEmpty(s.GpuName))
            {
                GpuNameText.Text = s.GpuName;
                GpuVendorText.Text = s.GpuVendor;
                GpuUsageText.Text = float.IsNaN(s.GpuUsage) ? "—" : $"{s.GpuUsage:0}%";
                SetBar(GpuBar, float.IsNaN(s.GpuUsage) ? 0 : s.GpuUsage / 100.0);
                GpuTempText.Text = s.GpuTempC >= 0 ? $"{s.GpuTempC}°C" : "";
                VramText.Text = s.VramTotalGB > 0 ? $"{s.VramUsedGB:0.0} / {s.VramTotalGB:0.0} GB" : "—";
                double vr = s.VramTotalGB > 0 ? s.VramUsedGB / s.VramTotalGB : 0;
                SetBar(VramBar, Math.Clamp(vr, 0, 1));
                GpuNote.Text = s.GpuTempC < 0 ? "温度需厂商驱动接口" : "";
            }
            else
            {
                GpuNameText.Text = "未检测到 GPU";
            }

            // network
            NetDownText.Text = FmtSpeed(s.NetDownKBs);
            NetUpText.Text = FmtSpeed(s.NetUpKBs);
            _netPeakKBs = Math.Max(_netPeakKBs * 0.98, 64);
            double downP = Math.Clamp(s.NetDownKBs / _netPeakKBs, 0, 1);
            double upP = Math.Clamp(s.NetUpKBs / _netPeakKBs, 0, 1);
            _netPeakKBs = Math.Max(_netPeakKBs, Math.Max(s.NetDownKBs, s.NetUpKBs));
            SetBar(NetDownBar, downP);
            SetBar(NetUpBar, upP);
        }
        catch (Exception ex)
        {
            Log.Write($"monitor update failed: {ex.Message}");
        }
    }

    private static void SetBar(FrameworkElement bar, double ratio)
    {
        ratio = Math.Clamp(ratio, 0.0, 1.0);
        if (bar.Parent is Border track)
            bar.Width = Math.Max(6, (track.ActualWidth - 6) * ratio);
        else
            bar.Width = 40 + 280 * ratio;   // fallback before first layout
    }

    private static string FmtSpeed(double kbPerSec) => kbPerSec >= 1024
        ? $"{kbPerSec / 1024:0.0} MB/s"
        : $"{kbPerSec:0} KB/s";
}
