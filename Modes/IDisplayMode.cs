using Microsoft.UI.Xaml;
using TurzxDisplay.Services;

namespace TurzxDisplay.Modes;

/// <summary>
/// A display mode renders an 800x480 landscape canvas that is both previewed in the app
/// and streamed verbatim to the device.
/// </summary>
public interface IDisplayMode
{
    string Key { get; }
    string Title { get; }
    string IconGlyph { get; }

    /// <summary>Fixed 800x480 root element of this mode.</summary>
    FrameworkElement View { get; }

    /// <summary>True when the view depends on time and should be re-rendered periodically.</summary>
    bool PeriodicRefresh { get; }

    TimeSpan RefreshInterval { get; }

    /// <summary>Called before each periodic re-render so time-driven modes can refresh their content.</summary>
    void Tick(DateTime now);

    /// <summary>Raised when content changed and a re-render is wanted immediately.</summary>
    event Action? ContentChanged;

    void OnActivated();
    void OnDeactivated();
}

public static class ModeRegistry
{
    public static IReadOnlyList<IDisplayMode> CreateAll(SettingsService settings) => new IDisplayMode[]
    {
        new ClockCalendarMode(),
        new StickyNotesMode(settings),
        new AlbumMode(settings),
        new WeatherMode(settings),
        new MonitorMode(),
        new QuotaMode(settings),
        new MusicMode(settings),
    };
}
