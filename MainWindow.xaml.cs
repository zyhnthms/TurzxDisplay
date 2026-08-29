using System.IO.Ports;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using TurzxDisplay.Modes;
using TurzxDisplay.Services;
using Windows.Storage.Streams;
using WinRT.Interop;

namespace TurzxDisplay;

public sealed partial class MainWindow : Window
{
    private readonly SettingsService _settings = SettingsService.Load();
    private readonly DisplayController _controller = new();
    private readonly List<IDisplayMode> _modes = new();
    private IDisplayMode? _activeMode;
    private readonly DispatcherQueueTimer _timer;
    private bool _rendering;
    private DateTime _lastRender = DateTime.MinValue;
    private int _renderCount;

    private readonly TrayService _tray = new();
    private bool _exiting;
    private bool _trayHintShown;

    public MainWindow()
    {
        InitializeComponent();

        _modes.AddRange(ModeRegistry.CreateAll(_settings));
        _controller.StatusChanged += OnControllerStatus;
        _controller.Rotate180 = _settings.Settings.Rotate180;
        RotateSwitch.IsOn = _settings.Settings.Rotate180;
        AutoPushSwitch.IsOn = _settings.Settings.AutoPush;
        BrightSlider.Value = _settings.Settings.Brightness;

        NotesList.ItemsSource = _settings.Notes;

        RefreshPorts();
        if (!string.IsNullOrEmpty(_settings.Settings.ComPort) && PortCombo.Items.Contains(_settings.Settings.ComPort))
            PortCombo.SelectedItem = _settings.Settings.ComPort;

        // restore last mode (setting IsChecked raises the Checked handler -> SwitchMode)
        var initial = _modes.FirstOrDefault(m => m.Key == _settings.Settings.ModeKey) ?? _modes[0];
        (initial.Key switch
        {
            "StickyNotes" => SegNotes,
            "Album" => SegAlbum,
            "Weather" => SegWeather,
            "Monitor" => SegMonitor,
            "Quota" => SegQuota,
            "Music" => SegMusic,
            _ => SegClock,
        }).IsChecked = true;

        _timer = DispatcherQueue.CreateTimer();
        _timer.Interval = TimeSpan.FromSeconds(1);
        _timer.Tick += (_, _) => OnTimerTick();
        _timer.Start();

        // first frame once the tree is actually rendered (RenderTargetBitmap needs a rendered surface)
        ModeHost.Loaded += (_, _) => _ = RenderAndSubmitAsync(force: true);

        // auto-connect to the last port once everything is up
        this.Activated += OnFirstActivation;

        // ---- tray behavior: closing hides to tray; real exit goes through ExitApp ----
        var hwnd = WindowNative.GetWindowHandle(this);
        _tray.Install(hwnd, "TurzxDisplay");
        _tray.LeftClicked += ToggleWindowFromTray;
        _tray.BuildMenu += BuildTrayMenu;
        _tray.MenuItemPicked += OnTrayMenuPicked;

        AppWindow.Closing += (s, e) =>
        {
            if (_exiting) return;
            e.Cancel = true;
            AppWindow.Hide();
            if (!_trayHintShown)
            {
                _trayHintShown = true;
                _tray.ShowBalloon("TurzxDisplay", "程序已最小化到系统托盘，仍在向显示屏推送画面。右键托盘图标可切换模式或退出。");
            }
        };

        Closed += (_, _) =>
        {
            _timer.Stop();
            _settings.Save();
            _tray.Remove();
            _controller.Dispose();
        };
    }

    private IEnumerable<TrayService.MenuEntry> BuildTrayMenu()
    {
        yield return new TrayService.MenuEntry(100, "显示主窗口", false, false);
        yield return new TrayService.MenuEntry(0, "", false, true);
        yield return new TrayService.MenuEntry(1, "时钟 + 日历", _activeMode?.Key == "ClockCalendar", false);
        yield return new TrayService.MenuEntry(2, "便签", _activeMode?.Key == "StickyNotes", false);
        yield return new TrayService.MenuEntry(3, "相册", _activeMode?.Key == "Album", false);
        yield return new TrayService.MenuEntry(4, "天气", _activeMode?.Key == "Weather", false);
        yield return new TrayService.MenuEntry(5, "监控", _activeMode?.Key == "Monitor", false);
        yield return new TrayService.MenuEntry(6, "GLM 额度", _activeMode?.Key == "Quota", false);
        yield return new TrayService.MenuEntry(7, "音乐", _activeMode?.Key == "Music", false);
        yield return new TrayService.MenuEntry(0, "", false, true);
        yield return new TrayService.MenuEntry(200, "退出", false, false);
    }

    private void OnTrayMenuPicked(int id)
    {
        switch (id)
        {
            case 100:
                ShowWindowFromTray();
                break;
            case 1:
                SegClock.IsChecked = true;   // Checked -> SwitchMode (window stays hidden)
                break;
            case 2:
                SegNotes.IsChecked = true;
                break;
            case 3:
                SegAlbum.IsChecked = true;
                break;
            case 4:
                SegWeather.IsChecked = true;
                break;
            case 5:
                SegMonitor.IsChecked = true;
                break;
            case 6:
                SegQuota.IsChecked = true;
                break;
            case 7:
                SegMusic.IsChecked = true;
                break;
            case 200:
                ExitApp();
                break;
        }
    }

    private void ToggleWindowFromTray()
    {
        if (AppWindow.IsVisible) AppWindow.Hide();
        else ShowWindowFromTray();
    }

    private void ShowWindowFromTray()
    {
        AppWindow.Show();
        Activate();
    }

    private async void ExitApp()
    {
        if (_exiting) return;
        _exiting = true;
        try
        {
            // disconnect the display cleanly before the process goes away
            await _controller.DisconnectAsync();
        }
        catch { /* best effort */ }
        Close();   // _exiting short-circuits the Closing-cancel path; Closed does the cleanup
    }

    private bool _autoConnectDone;

    private async void OnFirstActivation(object sender, WindowActivatedEventArgs args)
    {
        if (_autoConnectDone) return;
        _autoConnectDone = true;
        this.Activated -= OnFirstActivation;

        if (string.IsNullOrEmpty(_settings.Settings.ComPort)) return;
        if (PortCombo.SelectedItem is not string port) return;

        await Task.Delay(400); // let the first frame render
        SetStatus(false, $"正在连接 {port}…");
        var (ok, message) = await _controller.ConnectAsync(port, (int)BrightSlider.Value);
        SetStatus(ok, message);
        ConnectBtn.Content = ok ? "断开" : "连接";
        if (ok) await RenderAndSubmitAsync(force: true);
    }

    // ---------------- mode switching ----------------

    private void OnSegClock(object sender, RoutedEventArgs e) => SwitchMode(_modes.FirstOrDefault(m => m.Key == "ClockCalendar"));
    private void OnSegNotes(object sender, RoutedEventArgs e) => SwitchMode(_modes.FirstOrDefault(m => m.Key == "StickyNotes"));
    private void OnSegAlbum(object sender, RoutedEventArgs e) => SwitchMode(_modes.FirstOrDefault(m => m.Key == "Album"));
    private void OnSegWeather(object sender, RoutedEventArgs e) => SwitchMode(_modes.FirstOrDefault(m => m.Key == "Weather"));
    private void OnSegMonitor(object sender, RoutedEventArgs e) => SwitchMode(_modes.FirstOrDefault(m => m.Key == "Monitor"));
    private void OnSegQuota(object sender, RoutedEventArgs e) => SwitchMode(_modes.FirstOrDefault(m => m.Key == "Quota"));
    private void OnSegMusic(object sender, RoutedEventArgs e) => SwitchMode(_modes.FirstOrDefault(m => m.Key == "Music"));

    private void SwitchMode(IDisplayMode? mode)
    {
        if (mode is null || _activeMode == mode) return;
        if (_activeMode is not null)
        {
            _activeMode.ContentChanged -= OnModeContentChanged;
            _activeMode.OnDeactivated();
        }
        _activeMode = mode;
        mode.ContentChanged += OnModeContentChanged;
        mode.OnActivated();
        ModeHost.Children.Clear();
        ModeHost.Children.Add(mode.View);
        NotesPanel.Visibility = mode.Key == "StickyNotes" ? Visibility.Visible : Visibility.Collapsed;
        AlbumPanel.Visibility = mode.Key == "Album" ? Visibility.Visible : Visibility.Collapsed;
        WeatherPanel.Visibility = mode.Key == "Weather" ? Visibility.Visible : Visibility.Collapsed;
        QuotaPanel.Visibility = mode.Key == "Quota" ? Visibility.Visible : Visibility.Collapsed;
        if (mode.Key == "Quota") UpdateQuotaStatus();
        MusicPanel.Visibility = mode.Key == "Music" ? Visibility.Visible : Visibility.Collapsed;
        if (mode.Key == "Music")
        {
            LyricsFolderText.Text = string.IsNullOrEmpty(_settings.Settings.LyricsFolder) ? "（未选择）" : _settings.Settings.LyricsFolder;
            _ = UpdateMusicSourceAsync();
        }
        if (mode is Modes.AlbumMode album)
        {
            AlbumFolderText.Text = string.IsNullOrEmpty(_settings.Settings.AlbumFolder) ? "（未选择）" : _settings.Settings.AlbumFolder;
            AlbumCount.Text = album.PhotoCount > 0 ? $"{album.PhotoCount} 张照片" : "";
            ShuffleSwitch.IsOn = _settings.Settings.AlbumShuffle;
            FillSwitch.IsOn = _settings.Settings.AlbumFill;
            SelectIntervalItem(_settings.Settings.AlbumIntervalSec);
        }
        if (mode is Modes.WeatherMode)
        {
            var s = _settings.Settings;
            bool qw = s.WeatherSource == "qweather";
            if (qw) SrcQw.IsChecked = true; else SrcOmeteo.IsChecked = true;
            SyncWeatherPanels();
            WeatherCityText.Text = string.IsNullOrEmpty(s.WeatherCity) ? "（未设置位置）" : $"{s.WeatherCity}  ({s.WeatherLat:0.###}, {s.WeatherLon:0.###})";
            if (LatBox.Text.Length == 0) LatBox.Text = s.WeatherLat != 0 ? s.WeatherLat.ToString("0.####") : "";
            if (LonBox.Text.Length == 0) LonBox.Text = s.WeatherLon != 0 ? s.WeatherLon.ToString("0.####") : "";
            QwHostBox.Text = s.QwHost;
            QwDevBox.Text = s.QwDevId;
            QwSubBox.Text = s.QwProjectId;
            QwKidBox.Text = s.QwKeyId;
            QwCityText.Text = string.IsNullOrEmpty(s.QwCity) ? "（未选择位置）" : s.QwCity;
            UpdateQwKeyStatus();
            if (s.WeatherView == "7d") View7d.IsChecked = true; else View24h.IsChecked = true;
        }
        _settings.Settings.ModeKey = mode.Key;
        _settings.Save();
        _ = RenderAndSubmitAsync(force: true);
    }

    private void OnModeContentChanged() => _ = RenderAndSubmitAsync(force: true);

    // ---------------- render loop ----------------

    private void OnTimerTick()
    {
        if (_activeMode is null) return;
        if (_activeMode.PeriodicRefresh)
        {
            _activeMode.Tick(DateTime.Now);
            _ = RenderAndSubmitAsync(force: false);
        }
    }

    private async Task RenderAndSubmitAsync(bool force)
    {
        if (_rendering || _activeMode is null) return;
        // Non-periodic modes only re-render on content change (force) — avoids needless work.
        if (!_activeMode.PeriodicRefresh && !force) return;
        _rendering = true;
        try
        {
            var rtb = new RenderTargetBitmap();
            await rtb.RenderAsync(ModeHost);
            var pixels = await rtb.GetPixelsAsync();
            var raw = new byte[pixels.Length];
            DataReader.FromBuffer(pixels).ReadBytes(raw);

            // RenderTargetBitmap renders at the element's *effective* size (DPI/Viewbox scaling
            // applies) — normalize to the panel's fixed 800x480 before submitting.
            if (rtb.PixelWidth != RevCDisplayClient.PanelWidth || rtb.PixelHeight != RevCDisplayClient.PanelHeight)
            {
                if (_lastRender < DateTime.Now - TimeSpan.FromMinutes(5))
                    Services.Log.Write($"render size {rtb.PixelWidth}x{rtb.PixelHeight} -> resampling to 800x480");
                raw = ResampleToPanel(raw, rtb.PixelWidth, rtb.PixelHeight);
            }

            _lastRender = DateTime.Now;
            if (_controller.IsConnected && (AutoPushSwitch.IsOn || force))
            {
                _controller.SubmitFrame(raw);
                if (++_renderCount % 10 == 1)
                    Services.Log.Write($"render #{_renderCount} ok ({_activeMode.Key} {raw.Length}B)");
            }
        }
        catch (Exception ex)
        {
            Services.Log.Write($"render failed: {ex.Message}");
        }
        finally
        {
            _rendering = false;
        }
    }

    /// <summary>Nearest-neighbour BGRA resample to the fixed panel size; forces opaque alpha.</summary>
    private static byte[] ResampleToPanel(byte[] src, int sw, int sh)
    {
        const int dw = RevCDisplayClient.PanelWidth;
        const int dh = RevCDisplayClient.PanelHeight;
        var dst = new byte[dw * dh * 4];
        for (int y = 0; y < dh; y++)
        {
            int srow = (y * sh / dh) * sw * 4;
            int drow = y * dw * 4;
            for (int x = 0; x < dw; x++)
            {
                int s = srow + (x * sw / dw) * 4;
                int d = drow + x * 4;
                dst[d] = src[s];
                dst[d + 1] = src[s + 1];
                dst[d + 2] = src[s + 2];
                dst[d + 3] = 255;
            }
        }
        return dst;
    }

    // ---------------- connection ----------------

    private void RefreshPorts()
    {
        var ports = SerialPort.GetPortNames().Distinct().OrderBy(p => p).ToList();
        PortCombo.ItemsSource = ports;
        if (PortCombo.SelectedIndex < 0 && ports.Count > 0)
        {
            var saved = _settings.Settings.ComPort;
            PortCombo.SelectedItem = ports.Contains(saved) ? saved : ports[0];
        }
    }

    private void OnRefreshPorts(object sender, RoutedEventArgs e) => RefreshPorts();

    private async void OnConnect(object sender, RoutedEventArgs e)
    {
        if (_controller.IsConnected)
        {
            await _controller.DisconnectAsync();
            SetStatus(false, "已断开");
            ConnectBtn.Content = "连接";
            return;
        }

        if (PortCombo.SelectedItem is not string port)
        {
            SetStatus(false, "请先选择串口");
            return;
        }

        _settings.Settings.ComPort = port;
        _settings.Save();
        SetStatus(false, $"正在连接 {port}…");
        var (ok, message) = await _controller.ConnectAsync(port, (int)BrightSlider.Value);
        SetStatus(ok, message);
        ConnectBtn.Content = ok ? "断开" : "连接";
        if (ok) _ = RenderAndSubmitAsync(force: true);
    }

    private void OnControllerStatus(object? sender, string message)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            SetStatus(_controller.IsConnected, message);
            if (!_controller.IsConnected) ConnectBtn.Content = "连接";
        });
    }

    private void SetStatus(bool ok, string message)
    {
        StatusText.Text = message;
        StatusDot.Fill = new Microsoft.UI.Xaml.Media.SolidColorBrush(
            Microsoft.UI.ColorHelper.FromArgb(255,
                (byte)(ok ? 0x6E : 0xD0),
                (byte)(ok ? 0xB0 : 0x6E),
                (byte)(ok ? 0x89 : 0x6E)));
    }

    // ---------------- controls ----------------

    private void OnBrightChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (BrightValue is null) return; // fires while XAML is still parsing
        BrightValue.Text = ((int)e.NewValue).ToString();
        _settings.Settings.Brightness = (int)e.NewValue;
        _settings.Save();
        _ = _controller.SetBrightnessAsync((int)e.NewValue);
    }

    private void OnAutoPushToggled(object sender, RoutedEventArgs e)
    {
        _settings.Settings.AutoPush = AutoPushSwitch.IsOn;
        _settings.Save();
        if (!AutoPushSwitch.IsOn)
            _tray.ShowBalloon("TurzxDisplay", "自动推送已关闭：屏幕只在切换模式/内容变化时刷新，时钟、监控、音乐等动态画面将不再更新。");
    }

    private void OnRotateToggled(object sender, RoutedEventArgs e)
    {
        _controller.Rotate180 = RotateSwitch.IsOn;
        _settings.Settings.Rotate180 = RotateSwitch.IsOn;
        _settings.Save();
        _ = RenderAndSubmitAsync(force: true);
    }

    private void OnPushNow(object sender, RoutedEventArgs e) => _ = RenderAndSubmitAsync(force: true);

    // ---------------- notes editor ----------------

    private void OnAddNote(object sender, RoutedEventArgs e)
    {
        if (_settings.Notes.Count >= 6) return;
        _settings.Notes.Add(new StickyNote { Text = "", ColorHex = StickyNotePalette.Default });
    }

    private void OnDeleteNote(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is StickyNote note)
        {
            _settings.Notes.Remove(note);
            _settings.TouchNotes();
        }
    }

    private void OnPickColor(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string hex } el && el.DataContext is StickyNote note)
        {
            note.ColorHex = hex;
            _settings.TouchNotesDebounced();
        }
    }

    private void OnNoteTextChanged(object sender, TextChangedEventArgs e)
    {
        // TextBox event fires per keystroke; the note object is the DataContext.
        if (sender is TextBox box && box.DataContext is StickyNote note)
        {
            note.Text = box.Text;
            _settings.TouchNotesDebounced();
        }
    }

    // ---------------- album ----------------

    private Modes.AlbumMode? AlbumMode => _modes.FirstOrDefault(m => m.Key == "Album") as Modes.AlbumMode;

    private async void OnPickFolder(object sender, RoutedEventArgs e)
    {
        var picker = new Windows.Storage.Pickers.FolderPicker();
        WinRT.Interop.InitializeWithWindow.Initialize(picker,
            WinRT.Interop.WindowNative.GetWindowHandle(this));
        picker.FileTypeFilter.Add("*");
        var folder = await picker.PickSingleFolderAsync();
        if (folder is null) return;

        AlbumMode?.SetFolder(folder.Path);
        AlbumFolderText.Text = folder.Path;
        AlbumCount.Text = AlbumMode is { PhotoCount: > 0 } c ? $"{c.PhotoCount} 张照片" : "文件夹里没有照片";
    }

    private void SelectIntervalItem(int seconds)
    {
        foreach (var item in IntervalCombo.Items.OfType<ComboBoxItem>())
        {
            if (item.Tag is string tag && int.Parse(tag) == seconds)
            {
                IntervalCombo.SelectedItem = item;
                return;
            }
        }
        IntervalCombo.SelectedIndex = 1; // default 10s
    }

    private void OnIntervalChanged(object sender, SelectionChangedEventArgs e)
    {
        if (IntervalCombo.SelectedItem is ComboBoxItem { Tag: string tag })
            AlbumMode?.SetInterval(int.Parse(tag));
    }

    private void OnShuffleToggled(object sender, RoutedEventArgs e)
    {
        if (AlbumMode is not null && ShuffleSwitch is not null)
            AlbumMode.SetShuffle(ShuffleSwitch.IsOn);
    }

    private void OnFillToggled(object sender, RoutedEventArgs e)
    {
        if (AlbumMode is not null && FillSwitch is not null)
            AlbumMode.SetFill(FillSwitch.IsOn);
    }

    // ---------------- weather ----------------

    private Modes.WeatherMode? WeatherMode => _modes.FirstOrDefault(m => m.Key == "Weather") as Modes.WeatherMode;
    private async void OnAutoLocate(object sender, RoutedEventArgs e)
    {
        WeatherCityText.Text = "正在定位…";
        var loc = await Services.WeatherService.LocateAsync();
        if (loc is null)
        {
            WeatherCityText.Text = "定位失败，请手动输入经纬度";
            return;
        }
        var (city, lat, lon, cc) = loc.Value;
        if (cc == "CN" && ChinaCityList.Nearest(lat, lon) is { } near)
            city = near.DisplayName;   // refine to a Chinese district-level name
        _settings.Settings.WeatherCity = city;
        _settings.Settings.WeatherLat = lat;
        _settings.Settings.WeatherLon = lon;
        _settings.Save();
        LatBox.Text = lat.ToString("0.####");
        LonBox.Text = lon.ToString("0.####");
        WeatherCityText.Text = $"{city}  ({lat:0.###}, {lon:0.###})";
        if (WeatherMode is not null) await WeatherMode.FetchAsync();
    }

    private async void OnSaveLocation(object sender, RoutedEventArgs e)
    {
        if (!double.TryParse(LatBox.Text.Trim(), out var lat) ||
            !double.TryParse(LonBox.Text.Trim(), out var lon) ||
            Math.Abs(lat) > 90 || Math.Abs(lon) > 180)
        {
            WeatherCityText.Text = "经纬度格式不对（示例：39.9042 / 116.4074）";
            return;
        }
        _settings.Settings.WeatherLat = lat;
        _settings.Settings.WeatherLon = lon;
        if (string.IsNullOrEmpty(_settings.Settings.WeatherCity)) _settings.Settings.WeatherCity = "自定义位置";
        _settings.Save();
        WeatherCityText.Text = $"{_settings.Settings.WeatherCity}  ({lat:0.###}, {lon:0.###})";
        if (WeatherMode is not null) await WeatherMode.FetchAsync();
    }

    private async void OnRefreshWeather(object sender, RoutedEventArgs e)
    {
        if (WeatherMode is not null) await WeatherMode.FetchAsync();
    }

    private void OnView24h(object sender, RoutedEventArgs e) => WeatherMode?.SetView("24h");
    private void OnView7d(object sender, RoutedEventArgs e) => WeatherMode?.SetView("7d");

    // ---------------- qweather source ----------------

    private void OnSrcOmeteo(object sender, RoutedEventArgs e)
    {
        if (_settings.Settings.WeatherSource == "openmeteo") return;   // already (or still syncing)
        _settings.Settings.WeatherSource = "openmeteo";
        _settings.Save();
        SyncWeatherPanels();
        WeatherMode?.SetSource("openmeteo");
    }

    private void OnSrcQw(object sender, RoutedEventArgs e)
    {
        if (_settings.Settings.WeatherSource == "qweather") return;
        _settings.Settings.WeatherSource = "qweather";
        _settings.Save();
        SyncWeatherPanels();
        WeatherMode?.SetSource("qweather");
    }

    private void SyncWeatherPanels()
    {
        bool qw = _settings.Settings.WeatherSource == "qweather";
        OmPanel.Visibility = qw ? Visibility.Collapsed : Visibility.Visible;
        QwPanel.Visibility = qw ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateQwKeyStatus()
    {
        var s = _settings.Settings;
        bool key = QWeatherService.HasKeyFile;
        bool ids = !string.IsNullOrEmpty(s.QwDevId) && !string.IsNullOrEmpty(s.QwProjectId) && !string.IsNullOrEmpty(s.QwKeyId);
        bool host = !string.IsNullOrEmpty(s.QwHost);
        string state = key && ids && host ? "配置完整 ✓" : "配置不完整";
        QwKeyStatus.Text = $"私钥 qweather/ed25519-private.pem：{(key ? "已找到" : "未找到")} · {state}";
    }

    private async void OnSaveQw(object sender, RoutedEventArgs e)
    {
        SyncQwBoxesToSettings();
        UpdateQwKeyStatus();
        if (WeatherMode is not null) await WeatherMode.FetchAsync();
    }

    private void SyncQwBoxesToSettings()
    {
        var s = _settings.Settings;
        s.QwHost = QwHostBox.Text.Trim();
        s.QwDevId = QwDevBox.Text.Trim();
        s.QwProjectId = QwSubBox.Text.Trim();
        s.QwKeyId = QwKidBox.Text.Trim();
        _settings.Save();
    }

    private void OnSearchQwCity(object sender, RoutedEventArgs e) => RunQwSearch();

    private void OnQwSearchKey(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter)
        {
            e.Handled = true;
            RunQwSearch();
        }
    }

    /// <summary>Offline search over the bundled China city/district table (no API call, no quota).</summary>
    private void RunQwSearch()
    {
        string q = QwSearchBox.Text.Trim();
        if (q.Length == 0) return;

        QwResults.Children.Clear();
        var list = ChinaCityList.Search(q);
        if (list.Count == 0)
        {
            QwResults.Children.Add(new TextBlock
            {
                Text = "无结果",
                FontSize = 12,
                Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["InkSoftBrush"],
            });
            return;
        }

        foreach (var c in list)
        {
            var btn = new Button
            {
                Content = $"📍 {c.DisplayName}",
                Tag = c,
                FontSize = 12,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["InkBrush"],
                FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Segoe UI Variable Display"),
            };
            btn.Click += OnPickQwCity;
            QwResults.Children.Add(btn);
        }
    }

    private async void OnAutoLocateQw(object sender, RoutedEventArgs e)
    {
        QwCityText.Text = "正在定位…";
        var loc = await WeatherService.LocateAsync();
        if (loc is null)
        {
            QwCityText.Text = "定位失败，请搜索选择位置";
            return;
        }
        var (_, lat, lon, cc) = loc.Value;
        // refine the coarse IP location to the nearest district in the city table
        if (cc == "CN" && ChinaCityList.Nearest(lat, lon) is { } near)
        {
            await ApplyQwCity(near);
        }
        else
        {
            var s = _settings.Settings;
            s.QwLat = lat;
            s.QwLon = lon;
            s.QwCity = loc.Value.City;
            _settings.Save();
            QwCityText.Text = loc.Value.City;
            if (WeatherMode is not null) await WeatherMode.FetchAsync();
        }
    }

    private async Task ApplyQwCity(CityEntry c)
    {
        var s = _settings.Settings;
        s.QwLat = c.Lat;
        s.QwLon = c.Lon;
        s.QwCity = c.DisplayName;
        _settings.Save();
        QwCityText.Text = c.DisplayName;
        QwResults.Children.Clear();
        if (WeatherMode is not null) await WeatherMode.FetchAsync();
    }

    private async void OnPickQwCity(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is CityEntry c)
            await ApplyQwCity(c);
    }

    // ---------------- glm quota ----------------

    private Modes.QuotaMode? QuotaMode => _modes.FirstOrDefault(m => m.Key == "Quota") as Modes.QuotaMode;

    private async void OnRefreshQuota(object sender, RoutedEventArgs e)
    {
        if (QuotaMode is null) return;
        QuotaStatusText.Text = "正在获取…";
        await QuotaMode.FetchAllAsync();
        UpdateQuotaStatus();
    }

    private async void OnSaveGlmToken(object sender, RoutedEventArgs e)
    {
        _settings.Settings.GlmToken = GlmTokenBox.Password.Trim();
        _settings.Save();
        UpdateQuotaStatus();
        await QuotaMode!.FetchAllAsync();
    }

    private void UpdateQuotaStatus()
    {
        var cred = GlmPlanService.ResolveCredentials(_settings);
        QuotaStatusText.Text = cred is null
            ? "未找到 Token — 请在下方手动填写"
            : $"Token 来源：{cred.Source}（{cred.BaseUrl}）";
    }

    // ---------------- music (now playing) ----------------

    private async void OnPickLyricsFolder(object sender, RoutedEventArgs e)
    {
        var picker = new Windows.Storage.Pickers.FolderPicker();
        WinRT.Interop.InitializeWithWindow.Initialize(picker,
            WinRT.Interop.WindowNative.GetWindowHandle(this));
        picker.FileTypeFilter.Add("*");
        var folder = await picker.PickSingleFolderAsync();
        if (folder is null) return;

        _settings.Settings.LyricsFolder = folder.Path;
        _settings.Save();
        LyricsFolderText.Text = folder.Path;
    }

    private async Task UpdateMusicSourceAsync()
    {
        await NowPlayingService.Instance.InitAsync();
        var t = NowPlayingService.Instance.Track;
        MusicSourceText.Text = NowPlayingService.Instance.Initialized
            ? (t.HasSession ? $"媒体源：{t.Source}" : "未检测到播放中的媒体会话")
            : "SMTC 不可用（需 Windows 10 1809+）";
    }
}
