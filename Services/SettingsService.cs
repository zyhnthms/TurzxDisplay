using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TurzxDisplay.Services;

public sealed class StickyNote
{
    public string Text { get; set; } = "";
    public string ColorHex { get; set; } = StickyNotePalette.Default;
}

public static class StickyNotePalette
{
    public const string Default = "#FDF3BE";

    public static readonly string[] All =
    {
        "#FDF3BE", // butter
        "#FBDDE3", // petal
        "#D6F2DE", // mint
        "#D8E9FB", // sky
        "#EFE3FA", // lilac
        "#FBE5CC", // peach
    };
}

public sealed class AppSettings
{
    public string ComPort { get; set; } = "";
    public int Brightness { get; set; } = 80;
    public string ModeKey { get; set; } = "ClockCalendar";
    public bool AutoPush { get; set; } = true;
    public bool Rotate180 { get; set; } = false;
    public string AlbumFolder { get; set; } = "";
    public int AlbumIntervalSec { get; set; } = 10;
    public bool AlbumShuffle { get; set; } = false;
    public bool AlbumFill { get; set; } = true;
    public double WeatherLat { get; set; } = 0;
    public double WeatherLon { get; set; } = 0;
    public string WeatherCity { get; set; } = "";
    public string WeatherView { get; set; } = "24h";
    public string GlmToken { get; set; } = "";
    public string WeatherSource { get; set; } = "openmeteo";   // openmeteo | qweather
    public string QwHost { get; set; } = "";            // 专属 API Host, e.g. abcxyz.qweatherapi.com
    public string QwDevId { get; set; } = "";           // iss: 开发者ID (Q 开头)
    public string QwProjectId { get; set; } = "";       // sub: 项目ID
    public string QwKeyId { get; set; } = "";           // kid: 凭据ID
    public double QwLat { get; set; } = 0;
    public double QwLon { get; set; } = 0;
    public string QwCity { get; set; } = "";            // 省·市·区县
    public string LyricsFolder { get; set; } = "";
    public List<StickyNote> Notes { get; set; } = new()
    {
        new() { Text = "Welcome to TurzxDisplay ☕", ColorHex = "#FDF3BE" },
        new() { Text = "This note lives on the little screen.", ColorHex = "#D8E9FB" },
    };
}

/// <summary>JSON persistence in %LOCALAPPDATA%\TurzxDisplay + the live notes collection.</summary>
public sealed class SettingsService
{
    private static readonly string Dir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TurzxDisplay");
    private static readonly string FilePath = Path.Combine(Dir, "settings.json");

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public AppSettings Settings { get; private set; } = new();

    public ObservableCollection<StickyNote> Notes { get; } = new();

    public event Action? NotesChanged;

    private IDisposable? _saveTimerLock;

    public static SettingsService Load()
    {
        var svc = new SettingsService();
        try
        {
            if (File.Exists(FilePath))
                svc.Settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings();
        }
        catch { /* corrupt file -> defaults */ }

        foreach (var n in svc.Settings.Notes)
            svc.Notes.Add(n);
        svc.Notes.CollectionChanged += (_, _) => { svc.Save(); svc.NotesChanged?.Invoke(); };
        return svc;
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            Settings.Notes = Notes.ToList();
            File.WriteAllText(FilePath, JsonSerializer.Serialize(Settings, JsonOpts));
        }
        catch { /* best-effort persistence */ }
    }

    public void TouchNotes()
    {
        Save();
        NotesChanged?.Invoke();
    }

    /// <summary>Debounced save so per-keystroke edits don't hammer the disk.</summary>
    public void TouchNotesDebounced(int delayMs = 400)
    {
        _saveTimerLock?.Dispose();
        var cts = new CancellationTokenSource();
        _ = Task.Delay(delayMs, cts.Token).ContinueWith(_ =>
        {
            if (!cts.IsCancellationRequested) TouchNotes();
        }, TaskScheduler.Default);
        _saveTimerLock = cts;
    }
}
