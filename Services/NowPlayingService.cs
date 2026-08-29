using Microsoft.UI.Dispatching;
using Windows.Media.Control;
using Windows.Storage.Streams;

namespace TurzxDisplay.Services;

/// <summary>Snapshot of the currently playing track (SMTC).</summary>
public sealed class NowPlayingTrack
{
    public string Title = "";
    public string Artist = "";
    public string Album = "";
    public string Source = "";        // friendly player name
    public bool Playing;
    public bool HasSession;
    public TimeSpan Position;         // extrapolated
    public TimeSpan Duration;         // zero when unknown
    public byte[]? Cover;             // decoded album-art bytes
    public long Generation;           // bumped on media-property changes (new track / art)
}

/// <summary>
/// Now-playing via Windows System Media Transport Controls — works with PotPlayer
/// (enable its SMTC option), foobar2000, Media Player, Spotify, browsers, etc.
/// Position is extrapolated between TimelineProperties updates with a local clock.
/// </summary>
public sealed class NowPlayingService
{
    public static NowPlayingService Instance { get; } = new();

    private GlobalSystemMediaTransportControlsSessionManager? _mgr;
    private GlobalSystemMediaTransportControlsSession? _session;
    private readonly NowPlayingTrack _track = new();
    private DispatcherQueue? _dq;

    private TimeSpan _lastPos;
    private DateTimeOffset _posAt;
    private bool _playing;
    private bool _propsInFlight;

    public NowPlayingTrack Track => _track;
    public bool Initialized => _mgr is not null;

    /// <summary>Raised on the UI thread whenever track/status/timeline changed.</summary>
    public event Action? Changed;

    private NowPlayingService() { }

    public async Task<bool> InitAsync()
    {
        if (_mgr is not null) return true;
        _dq ??= DispatcherQueue.GetForCurrentThread();
        try
        {
            _mgr = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
        }
        catch (Exception ex)
        {
            Log.Write($"smtc init failed: {ex.Message}");
            return false;
        }
        _mgr.SessionsChanged += (_, _) => Rebind();
        Rebind();
        return true;
    }

    // ---------------- session binding ----------------

    private void Rebind()
    {
        var sessions = _mgr!.GetSessions();
        var pick = sessions.FirstOrDefault(s =>
            s.GetPlaybackInfo().PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing)
            ?? (_session is null ? null
                : sessions.FirstOrDefault(s => s.SourceAppUserModelId == _session.SourceAppUserModelId))
            ?? sessions.FirstOrDefault();

        if (pick != _session)
        {
            if (_session is not null)
            {
                _session.MediaPropertiesChanged -= OnProps;
                _session.TimelinePropertiesChanged -= OnTimeline;
                _session.PlaybackInfoChanged -= OnPlayback;
            }
            _session = pick;
            if (pick is not null)
            {
                pick.MediaPropertiesChanged += OnProps;
                pick.TimelinePropertiesChanged += OnTimeline;
                pick.PlaybackInfoChanged += OnPlayback;
            }
            _track.HasSession = pick is not null;
            _track.Source = pick is null ? "" : FriendlyName(pick.SourceAppUserModelId);
            _ = RefreshPropsAsync();
        }
        RaiseChanged();
    }

    private static string FriendlyName(string appId) => appId switch
    {
        "PotPlayer64.exe" or "PotPlayerMini64.exe" or "PotPlayer.exe" => "PotPlayer",
        "Microsoft.Media.Player.exe" or "Microsoft.Media.Player" => "Media Player",
        "Microsoft.Windows.Media.Player.exe" => "Windows Media Player",
        "foobar2000.exe" => "foobar2000",
        _ => appId.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? appId[..^4] : appId,
    };

    // ---------------- event handlers ----------------

    private void OnProps(GlobalSystemMediaTransportControlsSession sender, object args) =>
        _ = RefreshPropsAsync();

    private void OnTimeline(GlobalSystemMediaTransportControlsSession sender, object args)
    {
        var t = sender.GetTimelineProperties();
        _lastPos = t.Position;
        _posAt = DateTimeOffset.Now;
        RaiseChanged();
    }

    private void OnPlayback(GlobalSystemMediaTransportControlsSession sender, object args)
    {
        var info = sender.GetPlaybackInfo();
        var wasPlaying = _playing;
        _playing = info.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
        // re-sync the clock so the extrapolation doesn't drift across pause/resume
        _posAt = DateTimeOffset.Now;
        if (wasPlaying != _playing) RaiseChanged();
    }

    private async Task RefreshPropsAsync()
    {
        var session = _session;
        if (session is null || _propsInFlight) return;
        _propsInFlight = true;
        try
        {
            var props = await session.TryGetMediaPropertiesAsync();
            if (session != _session) return;   // rebound meanwhile

            _track.Title = props.Title ?? "";
            _track.Artist = props.Artist ?? "";
            _track.Album = props.AlbumTitle ?? "";

            _track.Cover = null;
            if (props.Thumbnail is not null)
            {
                try
                {
                    using var stream = await props.Thumbnail.OpenReadAsync();
                    int size = (int)stream.Size;
                    if (size > 0 && size < 12 * 1024 * 1024)
                    {
                        var buffer = new Windows.Storage.Streams.Buffer((uint)size);
                        await stream.ReadAsync(buffer, (uint)size, InputStreamOptions.None);
                        var reader = DataReader.FromBuffer(buffer);
                        var bytes = new byte[size];
                        reader.ReadBytes(bytes);
                        _track.Cover = bytes;
                    }
                }
                catch (Exception ex) { Log.Write($"smtc cover failed: {ex.Message}"); }
            }

            var timeline = session.GetTimelineProperties();
            _lastPos = timeline.Position;
            _track.Duration = timeline.EndTime > timeline.StartTime
                ? timeline.EndTime - timeline.StartTime : TimeSpan.Zero;
            _posAt = DateTimeOffset.Now;
            _track.Generation++;
            RaiseChanged();
        }
        catch (Exception ex)
        {
            Log.Write($"smtc props failed: {ex.Message}");
        }
        finally
        {
            _propsInFlight = false;
        }
    }

    /// <summary>Called by the mode each tick: refreshes the extrapolated snapshot.</summary>
    public void Sample()
    {
        if (_session is null) { _track.HasSession = false; return; }
        _track.HasSession = true;
        _track.Playing = _playing;
        _track.Position = _playing ? _lastPos + (DateTimeOffset.Now - _posAt) : _lastPos;

        var timeline = _session.GetTimelineProperties();
        if (timeline.EndTime > timeline.StartTime)
            _track.Duration = timeline.EndTime - timeline.StartTime;
    }

    private void RaiseChanged()
    {
        if (_dq is not null) _dq.TryEnqueue(() => Changed?.Invoke());
        else Changed?.Invoke();
    }
}
