using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using TurzxDisplay.Services;
using Windows.Storage.Streams;

namespace TurzxDisplay.Modes;

/// <summary>
/// 音乐模式: now-playing via SMTC (PotPlayer/foobar2000/Media Player/…),
/// cover art, progress bar and time-synced LRC lyrics. Refreshed each second
/// by the shared render loop; SMTC events re-render immediately.
/// </summary>
public sealed partial class MusicMode : UserControl, IDisplayMode
{
    private readonly SettingsService _settings;
    private readonly NowPlayingService _np = NowPlayingService.Instance;
    private Lrc? _lrc;
    private string _lrcKey = "";
    private long _coverGeneration = -1;
    private readonly Marquee _titleMarquee = new(), _artistMarquee = new();

    private sealed class Marquee
    {
        public string Text = "";
        public double Cycle;                    // seconds for a full there-and-back sweep
        public DateTimeOffset Started;
    }

    public string Key => "Music";
    public string Title => "音乐";
    public string IconGlyph => "";
    public FrameworkElement View => this;
    public bool PeriodicRefresh => true;   // 1 s: progress + lyric line
    public TimeSpan RefreshInterval => TimeSpan.FromSeconds(1);
    public event Action? ContentChanged { add { } remove { } }

    public MusicMode(SettingsService settings)
    {
        InitializeComponent();
        _settings = settings;
        _np.Changed += OnNpChanged;
    }

    public void Tick(DateTime now) => Update();

    public async void OnActivated()
    {
        await _np.InitAsync();
        Update();
    }

    public void OnDeactivated() { }

    private void OnNpChanged()
    {
        // fires on the UI thread (service marshals); cover may have arrived
        if (_np.Track.Generation != _coverGeneration)
        {
            _coverGeneration = _np.Track.Generation;
            SetCover(_np.Track.Cover);
        }
        Update();
    }

    private async void SetCover(byte[]? bytes)
    {
        if (bytes is null || bytes.Length == 0)
        {
            CoverHost.Visibility = Visibility.Collapsed;
            return;
        }
        try
        {
            var bmi = new BitmapImage();
            using var ras = new InMemoryRandomAccessStream();
            using (var dw = new DataWriter(ras.GetOutputStreamAt(0)))
            {
                dw.WriteBytes(bytes);
                await dw.StoreAsync();
            }
            ras.Seek(0);
            await bmi.SetSourceAsync(ras);
            CoverImg.Source = bmi;
            CoverHost.Visibility = Visibility.Visible;
        }
        catch (Exception ex)
        {
            Log.Write($"cover decode failed: {ex.Message}");
            CoverHost.Visibility = Visibility.Collapsed;
        }
    }

    private void Update()
    {
        _np.Sample();
        var t = _np.Track;

        if (!t.HasSession || t.Title.Length == 0)
        {
            StatusText.Text = "等待播放器…\n（PotPlayer 需在选项中开启「系统媒体传输控件/SMTC」）";
            StatusText.Visibility = Visibility.Visible;
            SrcBadge.Visibility = Visibility.Collapsed;
            return;
        }
        StatusText.Visibility = Visibility.Collapsed;

        TitleText.Text = t.Title;
        ArtistText.Text = t.Artist;
        AlbumText.Text = t.Album;
        SrcText.Text = $"{t.Source} · {(t.Playing ? "播放中" : "已暂停")}";
        SrcBadge.Visibility = Visibility.Visible;

        // long titles/artists scroll back and forth so nothing is cut off
        UpdateMarquee(TitleText, TitleShift, TitleHost, _titleMarquee);
        UpdateMarquee(ArtistText, ArtistShift, ArtistHost, _artistMarquee);

        // progress
        if (t.Duration > TimeSpan.Zero)
        {
            double ratio = Math.Clamp(t.Position / t.Duration, 0, 1);
            ProgBar.Width = (ProgBar.Parent is Border track ? track.ActualWidth : 380) * ratio;
            PosText.Text = Fmt(t.Position);
            DurText.Text = Fmt(t.Duration);
        }
        else
        {
            ProgBar.Width = 0;
            PosText.Text = Fmt(t.Position);
            DurText.Text = "--:--";
        }

        // lyrics (reload when the track changes)
        string key = $"{t.Artist}|{t.Title}";
        if (key != _lrcKey)
        {
            _lrcKey = key;
            _lrc = LyricsService.Find(_settings.Settings.LyricsFolder, t.Title, t.Artist);
        }
        var (prev, cur, next) = LyricsService.Window(_lrc, t.Position);
        PrevLine.Text = prev;
        CurLine.Text = cur;
        NextLine.Text = next;
        if (_lrc is null)
        {
            StatusText.Text = string.IsNullOrEmpty(_settings.Settings.LyricsFolder)
                ? "未配置歌词文件夹（右侧面板可设置）"
                : "未找到匹配的歌词文件";
            StatusText.Visibility = Visibility.Visible;
        }
        else if (cur.Length == 0 && t.Position < TimeSpan.FromSeconds(3))
        {
            StatusText.Text = "♪ 前奏中";
            StatusText.Visibility = Visibility.Visible;
        }
    }

    private static string Fmt(TimeSpan t) => t >= TimeSpan.FromHours(1)
        ? $"{(int)t.TotalHours}:{t.Minutes:D2}:{t.Seconds:D2}"
        : $"{(int)t.TotalMinutes}:{t.Seconds:D2}";

    /// <summary>
    /// Marquee: when the text overflows its host, sweep it left and back forever.
    /// Position is computed from elapsed time each tick (no Storyboard) — this
    /// matches the device's 1 fps cadence and avoids any animation/RTB quirks.
    /// </summary>
    private static void UpdateMarquee(TextBlock tb, TranslateTransform shift, FrameworkElement host, Marquee m)
    {
        if (host.ActualWidth <= 0) return;

        // clip the host once laid out; the canvas child is unconstrained so ActualWidth = full text width
        if (host.Clip is null || ((RectangleGeometry)host.Clip).Rect.Width != host.ActualWidth)
            host.Clip = new RectangleGeometry { Rect = new Windows.Foundation.Rect(0, 0, host.ActualWidth, host.ActualHeight) };

        double overflow = tb.ActualWidth - host.ActualWidth + 16;
        if (overflow <= 0)
        {
            m.Text = "";
            m.Cycle = 0;
            shift.X = 0;
            return;
        }
        if (m.Text != tb.Text)
        {
            m.Text = tb.Text;
            m.Cycle = Math.Max(2.0, overflow / 45) * 2 + 3.2;
            m.Started = DateTimeOffset.Now;
        }
        if (m.Cycle <= 0) return;

        double dur = (m.Cycle - 3.2) / 2;
        double t = (DateTimeOffset.Now - m.Started).TotalSeconds % m.Cycle;
        double x;
        if (t < 1.6) x = 0;
        else if (t < 1.6 + dur) x = -overflow * (t - 1.6) / dur;
        else if (t < 3.2 + dur) x = -overflow;
        else x = -overflow * (1 - (t - 3.2 - dur) / dur);
        shift.X = x;
    }
}
