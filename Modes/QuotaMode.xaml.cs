using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using TurzxDisplay.Services;
using Windows.UI;

namespace TurzxDisplay.Modes;

/// <summary>
/// GLM Coding Plan 额度模式: 5-hour + weekly credit windows with reset countdowns,
/// plus an hourly token-usage bar chart. Quota polled every 5 min, chart every 30 min;
/// the shared 1 s loop only refreshes the countdown labels.
/// </summary>
public sealed partial class QuotaMode : UserControl, IDisplayMode
{
    private readonly SettingsService _settings;
    private readonly DispatcherQueueTimer _quotaTimer;
    private readonly DispatcherQueueTimer _modelTimer;
    private PlanQuota? _quota;
    private ModelUsage? _model;
    private bool _fetching;

    public string Key => "Quota";
    public string Title => "额度";
    public string IconGlyph => "";
    public FrameworkElement View => this;
    public bool PeriodicRefresh => true;   // countdown ticks
    public TimeSpan RefreshInterval => TimeSpan.FromSeconds(1);
    public event Action? ContentChanged { add { } remove { } }

    public QuotaMode(SettingsService settings)
    {
        InitializeComponent();
        _settings = settings;

        _quotaTimer = DispatcherQueue.CreateTimer();
        _quotaTimer.Interval = TimeSpan.FromMinutes(5);
        _quotaTimer.Tick += async (_, _) => await FetchAllAsync();
        _quotaTimer.Start();

        _modelTimer = DispatcherQueue.CreateTimer();
        _modelTimer.Interval = TimeSpan.FromMinutes(30);
        _modelTimer.Tick += async (_, _) => await FetchAllAsync();
        _modelTimer.Start();
    }

    public void Tick(DateTime now) => UpdateCountdowns();

    public async void OnActivated()
    {
        if (_quota is null || DateTime.Now - _quota.FetchedAt > TimeSpan.FromMinutes(4))
            await FetchAllAsync();
    }

    public void OnDeactivated() { }

    // ---------------- data ----------------

    public async Task FetchAllAsync()
    {
        if (_fetching) return;
        var cred = GlmPlanService.ResolveCredentials(_settings);
        if (cred is null)
        {
            ShowStatus("未找到 GLM Token — 在应用右侧面板配置");
            return;
        }
        _fetching = true;
        try
        {
            bool first = _quota is null;
            if (first || DateTime.Now - _quota!.FetchedAt > TimeSpan.FromMinutes(4))
            {
                if (first) ShowStatus("正在获取额度…");
                var q = await GlmPlanService.FetchQuotaAsync(cred);
                if (q is not null) _quota = q;
                if (_quota is null) { ShowStatus("获取失败，稍后自动重试"); return; }
            }
            if (_model is null || DateTime.Now - _model.FetchedAt > TimeSpan.FromMinutes(20))
            {
                var m = await GlmPlanService.FetchModelUsageAsync(cred);
                if (m is not null) _model = m;
            }
            Render();
        }
        catch (Exception ex)
        {
            Log.Write($"quota fetch failed: {ex.Message}");
            ShowStatus("网络异常，稍后自动重试");
        }
        finally
        {
            _fetching = false;
        }
    }

    // ---------------- rendering ----------------

    private void Render()
    {
        if (_quota is null) return;
        HideStatus();

        if (!string.IsNullOrEmpty(_quota.Level))
        {
            string level = char.ToUpperInvariant(_quota.Level[0]) + _quota.Level[1..].ToLowerInvariant();
            LevelText.Text = level;
            LevelBadge.Visibility = Visibility.Visible;
        }
        SubText.Text = $"智谱 GLM 编程套餐 · 更新 {_quota.FetchedAt:HH:mm}";

        Tokens24hText.Text = _model is { TotalTokens: > 0 }
            ? $"{GlmPlanService.FormatTokens(_model.TotalTokens)} tokens" : "—";

        // up to two credit windows, smaller total first (5h &lt; weekly)
        var wins = _quota.Limits
            .Where(l => l.Type != "TIME_LIMIT")
            .OrderBy(l => l.Total)
            .Take(2)
            .ToList();
        FillWindow(0, wins.Count > 0 ? wins[0] : null);
        FillWindow(1, wins.Count > 1 ? wins[1] : null);

        BuildChart();
    }

    private void FillWindow(int slot, QuotaLimit? l)
    {
        var (title, pct, remain, bar, detail) = slot == 0
            ? (Win1Title, Win1Pct, Win1Remain, Win1Bar, Win1Detail)
            : (Win2Title, Win2Pct, Win2Remain, Win2Bar, Win2Detail);

        if (l is null)
        {
            title.Text = "—";
            pct.Text = remain.Text = detail.Text = "";
            SetBar(bar, 0, BarColor(0));
            return;
        }

        title.Text = l.WindowName;
        double usedPct = l.UsedPct > 0 ? l.UsedPct : (l.Total > 0 ? 100.0 * l.Used / l.Total : 0);
        pct.Text = $"已用 {usedPct:0}%";
        pct.Foreground = new SolidColorBrush(BarColor(usedPct));
        remain.Text = $"{l.Remaining:n0}";
        detail.Text = $"{l.Used:n0} / {l.Total:n0}";
        SetBar(bar, l.Total > 0 ? l.Used / (double)l.Total : 0, BarColor(usedPct));
    }

    private void UpdateCountdowns()
    {
        if (_quota is null) return;
        var wins = _quota.Limits.Where(l => l.Type != "TIME_LIMIT").OrderBy(l => l.Total).Take(2).ToList();
        if (wins.Count > 0) AppendCountdown(Win1Detail, wins[0]);
        if (wins.Count > 1) AppendCountdown(Win2Detail, wins[1]);
    }

    private static void AppendCountdown(TextBlock detail, QuotaLimit l)
    {
        string baseText = $"{l.Used:n0} / {l.Total:n0}";
        if (l.ResetAt is { } reset)
        {
            var left = reset - DateTimeOffset.Now;
            detail.Text = left > TimeSpan.Zero ? $"{baseText} · {Countdown(left)}" : baseText;
        }
        else
        {
            detail.Text = baseText;
        }
    }

    private static string Countdown(TimeSpan left) => left.TotalMinutes switch
    {
        < 1 => "即将重置",
        < 60 => $"{left.Minutes} 分钟后重置",
        < 2880 => $"{(int)left.TotalHours} 小时后重置",
        _ => $"{(int)left.TotalDays} 天 {left.Hours} 小时后重置",
    };

    private static Color BarColor(double usedPct) => usedPct switch
    {
        >= 90 => Color.FromArgb(0xFF, 0xE0, 0x65, 0x5A),
        >= 70 => Color.FromArgb(0xFF, 0xE0, 0xA3, 0x4E),
        _ => Color.FromArgb(0xFF, 0x7C, 0x6F, 0xD8),
    };

    private static void SetBar(FrameworkElement bar, double ratio, Color color)
    {
        ratio = Math.Clamp(ratio, 0.0, 1.0);
        if (bar is Border b)
        {
            b.Background = new SolidColorBrush(color);
            b.Width = bar.Parent is Border track
                ? Math.Max(6, (track.ActualWidth - 0) * ratio)
                : 40 + 280 * ratio;   // fallback before first layout
        }
    }

    private void BuildChart()
    {
        ChartCanvas.Children.Clear();
        var buckets = _model?.Buckets;
        if (buckets is not { Count: > 1 }) return;

        // keep the most recent 24 hourly buckets
        var list = buckets.Skip(Math.Max(0, buckets.Count - 24)).ToList();
        double w = 740, h = 84, pad = 4;
        double max = Math.Max(list.Max(b => b.Tokens), 1);
        int n = list.Count;
        double gap = 6;
        double barW = Math.Max(4, (w - gap * (n - 1)) / n);

        int iMax = list.IndexOf(list.MaxBy(b => b.Tokens)!);
        for (int i = 0; i < n; i++)
        {
            double bh = Math.Max(2, (h - 16) * list[i].Tokens / max);
            var bar = new Border
            {
                Width = barW,
                Height = bh,
                CornerRadius = new CornerRadius(3),
                Background = new SolidColorBrush(Color.FromArgb(0xFF, 0x7C, 0x6F, 0xD8)),
                Opacity = 0.85,
            };
            ChartCanvas.Children.Add(bar);
            Canvas.SetLeft(bar, i * (barW + gap));
            Canvas.SetTop(bar, h - bh);

            if (i == iMax && list[i].Tokens > 0)
            {
                var label = new TextBlock
                {
                    Text = GlmPlanService.FormatTokens(list[i].Tokens),
                    FontSize = 11,
                    Foreground = new SolidColorBrush(Color.FromArgb(0xFF, 0x7A, 0x76, 0xA0)),
                    FontFamily = new FontFamily("Segoe UI Variable Display"),
                };
                ChartCanvas.Children.Add(label);
                Canvas.SetLeft(label, Math.Clamp(i * (barW + gap) - 6, 0, w - 70));
                Canvas.SetTop(label, Math.Max(0, h - bh - 16));
            }
        }

        ModelsText.Text = _model?.Models.Count > 0
            ? string.Join(" · ", _model.Models.Take(2).Select(m => m.Name)) : "";
    }

    // ---------------- status ----------------

    private void ShowStatus(string msg)
    {
        StatusText.Text = msg;
        StatusText.Visibility = Visibility.Visible;
    }

    private void HideStatus() => StatusText.Visibility = Visibility.Collapsed;
}
