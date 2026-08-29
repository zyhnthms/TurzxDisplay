using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using TurzxDisplay.Services;
using Windows.UI;

namespace TurzxDisplay.Modes;

/// <summary>
/// 天气模式: 24-hour curve view and 7-day columns, data from Open-Meteo.
/// Own fetch timer (30 min) + refresh on activation; re-renders on new data / view switch.
/// </summary>
public sealed partial class WeatherMode : UserControl, IDisplayMode
{
    private readonly SettingsService _settings;
    private readonly DispatcherQueueTimer _fetchTimer;
    private WeatherData? _data;

    public string Key => "Weather";
    public string Title => "天气";
    public string IconGlyph => ""; // placeholder; segments use emoji anyway
    public FrameworkElement View => this;
    public bool PeriodicRefresh => false;
    public TimeSpan RefreshInterval => TimeSpan.FromMinutes(1);
    public event Action? ContentChanged;

    public WeatherMode(SettingsService settings)
    {
        InitializeComponent();
        _settings = settings;
        ApplyView(_settings.Settings.WeatherView);

        _fetchTimer = DispatcherQueue.CreateTimer();
        _fetchTimer.Interval = TimeSpan.FromMinutes(30);
        _fetchTimer.Tick += async (_, _) => await FetchAsync();
        _fetchTimer.Start();
    }

    public void Tick(DateTime now) { }

    public async void OnActivated()
    {
        ApplyView(_settings.Settings.WeatherView);
        if (HasLocation && (_data is null || DateTime.Now - _data.Updated > TimeSpan.FromMinutes(10)))
            await FetchAsync();
        else
            UpdateStatus();
    }

    public void OnDeactivated() { }

    private bool UseQWeather => _settings.Settings.WeatherSource == "qweather";

    private bool HasLocation => UseQWeather
        ? Math.Abs(_settings.Settings.QwLat) > 0.001
        : Math.Abs(_settings.Settings.WeatherLat) > 0.001;

    /// <summary>Switch data source (openmeteo / qweather) and refetch.</summary>
    public async void SetSource(string source)
    {
        _settings.Settings.WeatherSource = source;
        _settings.Save();
        _data = null;
        if (HasLocation) await FetchAsync();
        else UpdateStatus();
    }

    // ---------------- data ----------------

    public async Task FetchAsync()
    {
        if (!HasLocation)
        {
            UpdateStatus();
            return;
        }
        ShowStatus(UseQWeather ? "正在获取和风天气…" : "正在获取天气…");
        try
        {
            var s = _settings.Settings;
            WeatherData? data = UseQWeather
                ? await QWeatherService.FetchAsync(s.QwLat, s.QwLon, s.QwCity, s)
                : await WeatherService.FetchAsync(s.WeatherCity, s.WeatherLat, s.WeatherLon);
            if (data is null)
            {
                ShowStatus(UseQWeather
                    ? "和风天气获取失败（检查密钥/额度/配置）"
                    : "获取失败，稍后自动重试");
                return;
            }
            _data = data;
            Render();
        }
        catch (Exception ex)
        {
            Log.Write($"weather fetch failed: {ex.Message}");
            ShowStatus("网络异常，稍后自动重试");
        }
    }

    // ---------------- rendering ----------------

    private void Render()
    {
        if (_data is null) { UpdateStatus(); return; }
        HideStatus();

        CityText.Text = string.IsNullOrEmpty(_data.City) ? "—" : _data.City;
        string srcTag = UseQWeather ? "和风天气" : "Open-Meteo";
        CondText.Text = $"{_data.CurrentText} · {srcTag} · 更新 {_data.Updated:HH:mm}";
        UpdatedText.Text = "";
        CurEmoji.Text = _data.CurrentEmoji;
        CurTempText.Text = $"{_data.CurrentTemp:0}°";

        RenderStrip();

        BuildHourView();
        BuildWeekView();
        ContentChanged?.Invoke();
    }

    /// <summary>Info strip: QWeather extras (wind/humidity/AQI/sun/moon/warning); hidden for Open-Meteo.</summary>
    private void RenderStrip()
    {
        var d = _data!;
        bool any = false;

        if (!string.IsNullOrEmpty(d.Warning))
        {
            WarnText.Text = $"⚠ {d.Warning}";
            WarnChip.Visibility = Visibility.Visible;
            any = true;
        }
        else WarnChip.Visibility = Visibility.Collapsed;

        if (!string.IsNullOrEmpty(d.WindDir) || !string.IsNullOrEmpty(d.WindScale))
        {
            WindText.Text = $"🌬 {d.WindDir} {d.WindScale}级";
            any = true;
        }
        else WindText.Text = "";

        HumText.Text = d.Humidity >= 0 ? $"💧 {d.Humidity}%" : "";

        if (d.Aqi >= 0)
        {
            AqiText.Text = $"AQI {d.Aqi} {d.AqiCategory}";
            AqiChip.Background = new SolidColorBrush(d.Aqi switch
            {
                <= 50 => Color.FromArgb(0xFF, 0x6E, 0xB0, 0x89),
                <= 100 => Color.FromArgb(0xFF, 0xE0, 0xA3, 0x4E),
                <= 150 => Color.FromArgb(0xFF, 0xE0, 0x7A, 0x4E),
                _ => Color.FromArgb(0xFF, 0xE0, 0x65, 0x5A),
            });
            AqiChip.Visibility = Visibility.Visible;
            any = true;
        }
        else AqiChip.Visibility = Visibility.Collapsed;

        if (d.Sunrise is { } sr && d.Sunset is { } ss)
            SunText.Text = $"🌅 {sr:HH:mm}  🌇 {ss:HH:mm}";
        else SunText.Text = "";

        if (!string.IsNullOrEmpty(d.MoonName))
        {
            MoonText.Text = d.MoonIllum >= 0
                ? $"{d.MoonEmoji} {d.MoonName} {d.MoonIllum}%"
                : $"{d.MoonEmoji} {d.MoonName}";
            any = true;
        }
        else MoonText.Text = "";

        // hide the whole strip when the source provides none of the extras (Open-Meteo)
        if (InfoStrip is not null)
            InfoStrip.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
    }

    private void BuildHourView()
    {
        ChartCanvas.Children.Clear();
        HourGrid.Children.Clear();
        HourGrid.ColumnDefinitions.Clear();
        if (_data?.Hours is not { Count: > 1 }) return;

        var hours = _data.Hours;
        double min = hours.Min(h => h.Temp), max = hours.Max(h => h.Temp);
        if (max - min < 1) { min -= 1; max += 1; }

        double w = 740, h = 118, pad = 8;

        // filled area under the curve
        var area = new Polygon { Fill = new SolidColorBrush(Color.FromArgb(0x30, 0x8A, 0xA0, 0xD8)) };
        var line = new Polyline
        {
            Stroke = new SolidColorBrush(Color.FromArgb(0xFF, 0x7E, 0x96, 0xDA)),
            StrokeThickness = 3,
            StrokeLineJoin = PenLineJoin.Round,
        };

        for (int i = 0; i < hours.Count; i++)
        {
            double x = pad + i * (w - 2 * pad) / (hours.Count - 1);
            double y = pad + (max - hours[i].Temp) * (h - 2 * pad) / (max - min);
            line.Points.Add(new Windows.Foundation.Point(x, y));
            area.Points.Add(new Windows.Foundation.Point(x, y));
        }
        area.Points.Add(new Windows.Foundation.Point(w - pad, h));
        area.Points.Add(new Windows.Foundation.Point(pad, h));
        ChartCanvas.Children.Add(area);
        ChartCanvas.Children.Add(line);

        // min / max labels on the curve
        int iMax = hours.IndexOf(hours.MaxBy(h => h.Temp)!);
        int iMin = hours.IndexOf(hours.MinBy(h => h.Temp)!);
        AddTempLabel(hours[iMax].Temp, line.Points[iMax], -26);
        AddTempLabel(hours[iMin].Temp, line.Points[iMin], 14);

        // 8 columns: every 3 h
        for (int c = 0; c < 8; c++)
        {
            HourGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            int idx = Math.Min(c * 3, hours.Count - 1);
            var hp = hours[idx];

            var col = new StackPanel { Spacing = 0, VerticalAlignment = VerticalAlignment.Stretch };
            col.Children.Add(new TextBlock
            {
                Text = hp.Time.ToString("HH时"),
                FontSize = 15, Foreground = new SolidColorBrush(Color.FromArgb(0xFF, 0x76, 0x87, 0xA3)),
                HorizontalAlignment = HorizontalAlignment.Center,
                FontFamily = new FontFamily("Segoe UI Variable Display"),
            });
            col.Children.Add(new TextBlock
            {
                Text = hp.Emoji,
                FontSize = 24, HorizontalAlignment = HorizontalAlignment.Center,
            });
            col.Children.Add(new TextBlock
            {
                Text = $"{hp.Temp:0}°",
                FontSize = 18, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Color.FromArgb(0xFF, 0x2E, 0x3D, 0x59)),
                HorizontalAlignment = HorizontalAlignment.Center,
                FontFamily = new FontFamily("Segoe UI Variable Display"),
            });

            HourGrid.Children.Add(col);
            Grid.SetColumn(col, c);
        }
    }

    private void AddTempLabel(double temp, Windows.Foundation.Point p, double dy)
    {
        ChartCanvas.Children.Add(new TextBlock
        {
            Text = $"{temp:0}°",
            FontSize = 13,
            Foreground = new SolidColorBrush(Color.FromArgb(0xFF, 0x7E, 0x96, 0xDA)),
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            FontFamily = new FontFamily("Segoe UI Variable Display"),
        });
        double tx = Math.Clamp(p.X - 12, 0, 716);
        Canvas.SetLeft(ChartCanvas.Children[^1], tx);
        Canvas.SetTop(ChartCanvas.Children[^1], Math.Clamp(p.Y + dy, 0, 108));
    }

    private void BuildWeekView()
    {
        WeekView.Children.Clear();
        WeekView.ColumnDefinitions.Clear();
        if (_data?.Days is not { Count: > 0 }) return;

        for (int c = 0; c < _data.Days.Count; c++)
        {
            WeekView.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var d = _data.Days[c];
            bool today = d.Date.Date == DateTime.Today;

            var col = new StackPanel { Spacing = 3, VerticalAlignment = VerticalAlignment.Center };
            col.Children.Add(new Border
            {
                CornerRadius = new CornerRadius(10),
                Background = today
                    ? new SolidColorBrush(Color.FromArgb(0xFF, 0x7E, 0x96, 0xDA))
                    : new SolidColorBrush(Color.FromArgb(0x00, 0, 0, 0)),
                Padding = new Thickness(10, 2, 10, 2),
                HorizontalAlignment = HorizontalAlignment.Center,
                Child = new TextBlock
                {
                    Text = today ? "今天" : d.Date.ToString("ddd", new System.Globalization.CultureInfo("zh-CN")),
                    FontSize = today ? 17 : 16,
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                    Foreground = new SolidColorBrush(today ? Microsoft.UI.Colors.White : Color.FromArgb(0xFF, 0x5B, 0x6C, 0x8F)),
                    FontFamily = new FontFamily("Segoe UI Variable Display"),
                },
            });
            col.Children.Add(new TextBlock
            {
                Text = d.Emoji,
                FontSize = 30, HorizontalAlignment = HorizontalAlignment.Center,
            });
            col.Children.Add(new TextBlock
            {
                Text = d.Text,
                FontSize = 13,
                Foreground = new SolidColorBrush(Color.FromArgb(0xFF, 0x76, 0x87, 0xA3)),
                HorizontalAlignment = HorizontalAlignment.Center,
                FontFamily = new FontFamily("Segoe UI Variable Display"),
            });
            col.Children.Add(new TextBlock
            {
                Text = $"{d.TMax:0}°",
                FontSize = 22, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Color.FromArgb(0xFF, 0x2E, 0x3D, 0x59)),
                HorizontalAlignment = HorizontalAlignment.Center,
                FontFamily = new FontFamily("Segoe UI Variable Display"),
            });
            col.Children.Add(new TextBlock
            {
                Text = $"{d.TMin:0}°",
                FontSize = 15,
                Foreground = new SolidColorBrush(Color.FromArgb(0xFF, 0x9A, 0xA9, 0xC2)),
                HorizontalAlignment = HorizontalAlignment.Center,
                FontFamily = new FontFamily("Segoe UI Variable Display"),
            });

            WeekView.Children.Add(col);
            Grid.SetColumn(col, c);
        }
    }

    // ---------------- view switching ----------------

    public void SetView(string view)
    {
        _settings.Settings.WeatherView = view;
        _settings.Save();
        ApplyView(view);
        ContentChanged?.Invoke();
    }

    private void ApplyView(string view)
    {
        bool hour = view != "7d";
        if (HourView is null) return;   // during InitializeComponent
        HourView.Visibility = hour ? Visibility.Visible : Visibility.Collapsed;
        WeekView.Visibility = hour ? Visibility.Collapsed : Visibility.Visible;
    }

    // ---------------- status ----------------

    private void ShowStatus(string msg)
    {
        StatusText.Text = msg;
        StatusText.Visibility = Visibility.Visible;
    }

    private void HideStatus() => StatusText.Visibility = Visibility.Collapsed;

    private void UpdateStatus()
    {
        if (_data is not null) { Render(); return; }
        if (!HasLocation) ShowStatus(UseQWeather
            ? "请先在右侧面板搜索并选择区县级位置"
            : "请先在右侧面板设置位置（自动定位或输入经纬度）");
    }
}
