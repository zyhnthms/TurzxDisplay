using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using TurzxDisplay.Services;

namespace TurzxDisplay.Modes;

/// <summary>
/// 相册模式: photo slideshow on the 800x480 canvas. Own timer advances photos and raises
/// ContentChanged so a fresh frame is pushed to the device on every change.
/// </summary>
public sealed partial class AlbumMode : UserControl, IDisplayMode
{
    private static readonly string[] Extensions = { ".jpg", ".jpeg", ".png", ".bmp", ".gif", ".webp", ".jfif", ".heic" };

    private readonly SettingsService _settings;
    private readonly DispatcherQueueTimer _timer;
    private List<string> _files = new();
    private int _index;
    private int _countdown;
    private string _currentCaption = "";

    public string Key => "Album";
    public string Title => "相册";
    public string IconGlyph => "";
    public FrameworkElement View => this;
    public bool PeriodicRefresh => false;           // own timer drives re-renders
    public TimeSpan RefreshInterval => TimeSpan.FromSeconds(1);
    public event Action? ContentChanged;

    public AlbumMode(SettingsService settings)
    {
        InitializeComponent();
        _settings = settings;
        _timer = DispatcherQueue.CreateTimer();
        _timer.Interval = TimeSpan.FromSeconds(1);
        _timer.Tick += (_, _) => OnTick();

        ApplySettings();
    }

    private void ApplySettings()
    {
        var s = _settings.Settings;
        if (!string.IsNullOrEmpty(s.AlbumFolder))
            LoadFolder(s.AlbumFolder);
        SetFill(s.AlbumFill);
    }

    public void Tick(DateTime now) { }
    public void OnActivated() => StartOrResume();
    public void OnDeactivated() => _timer.Stop();

    // ---------------- slideshow engine ----------------

    private void OnTick()
    {
        if (_files.Count == 0) return;
        _countdown--;
        if (_countdown > 0) return;
        Next();
    }

    private void StartOrResume()
    {
        if (_files.Count == 0)
        {
            EmptyHint.Visibility = Visibility.Visible;
            return;
        }
        _countdown = Math.Max(1, _settings.Settings.AlbumIntervalSec);
        if (!_timer.IsRunning) _timer.Start();
        if (Photo.Source is null) Show();
    }

    private void Next()
    {
        if (_files.Count == 0) return;
        _index++;
        if (_index >= _files.Count)
        {
            if (_settings.Settings.AlbumShuffle) Shuffle();
            _index = 0;
        }
        _countdown = Math.Max(1, _settings.Settings.AlbumIntervalSec);
        Show();
    }

    private void Show()
    {
        try
        {
            var file = _files[_index];
            // decode downscaled (panel is 800x480 — 1600 px covers fill-crop quality);
            // big photos otherwise decode full-size: slow and memory-heavy
            var bmp = new BitmapImage(new Uri(file)) { DecodePixelWidth = 1600 };
            // decode is async: re-render only once pixels are actually available,
            // otherwise the pushed frame shows an empty card (white rectangle)
            var source = bmp;
            bmp.ImageOpened += (_, _) =>
            {
                if (ReferenceEquals(Photo.Source, source)) ContentChanged?.Invoke();
            };
            bmp.ImageFailed += (_, _) =>
                Log.Write($"album decode failed: {System.IO.Path.GetFileName(file)}");
            Photo.Source = bmp;
            _currentCaption = System.IO.Path.GetFileName(file);
            CaptionText.Text = _currentCaption;
            CaptionPill.Opacity = 0.85;
            EmptyHint.Visibility = Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            Services.Log.Write($"album show failed: {ex.Message}");
        }
    }

    private void Shuffle()
    {
        var rnd = new Random();
        for (int i = _files.Count - 1; i > 0; i--)
        {
            int j = rnd.Next(i + 1);
            (_files[i], _files[j]) = (_files[j], _files[i]);
        }
    }

    // ---------------- settings hooks (called from the app panel) ----------------

    public void SetFolder(string path)
    {
        _settings.Settings.AlbumFolder = path;
        _settings.Save();
        LoadFolder(path);
        _index = 0;
        Show();
        StartOrResume();
    }

    private void LoadFolder(string path)
    {
        try
        {
            _files = System.IO.Directory.EnumerateFiles(path, "*.*")
                .Where(f => Extensions.Contains(System.IO.Path.GetExtension(f).ToLowerInvariant()))
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (Exception ex)
        {
            Log.Write($"album folder failed: {ex.Message}");
            _files = new List<string>();
        }
        EmptyHint.Visibility = _files.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (_settings.Settings.AlbumShuffle) Shuffle();
    }

    public void SetInterval(int seconds)
    {
        _settings.Settings.AlbumIntervalSec = seconds;
        _settings.Save();
        _countdown = Math.Max(1, seconds);
    }

    public void SetShuffle(bool on)
    {
        _settings.Settings.AlbumShuffle = on;
        _settings.Save();
    }

    public void SetFill(bool fill)
    {
        _settings.Settings.AlbumFill = fill;
        _settings.Save();
        Photo.Stretch = fill ? Stretch.UniformToFill : Stretch.Uniform;
        ContentChanged?.Invoke();
    }

    public int PhotoCount => _files.Count;
}
