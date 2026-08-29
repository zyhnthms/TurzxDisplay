namespace TurzxDisplay.Services;

/// <summary>
/// Owns the connection + a single background sender loop. The UI renders a frame and calls
/// <see cref="SubmitFrame"/>; only the newest frame is kept — stale frames are dropped while
/// a push (~650 ms) is in flight.
/// </summary>
public sealed class DisplayController : IDisposable
{
    private readonly RevCDisplayClient _client = new();
    private readonly SemaphoreSlim _signal = new(0);
    private readonly object _gate = new();
    private byte[]? _pending;
    private Task? _loop;
    private CancellationTokenSource? _cts;
    private int _connecting;

    public volatile bool Rotate180;

    /// <summary>Friendly status line for the UI. Raised on a worker thread.</summary>
    public event EventHandler<string>? StatusChanged;

    public bool IsConnected { get; private set; }
    public string? DeviceId => _client.DeviceId;

    public Task<(bool Ok, string Message)> ConnectAsync(string comPort, int brightnessPercent)
    {
        // Guard against concurrent attempts (auto-connect racing a manual click double-opens the port)
        if (Interlocked.CompareExchange(ref _connecting, 1, 0) != 0)
            return Task.FromResult((false, "正在连接中，请稍候…"));

        return Task.Run(async () =>
        {
            try
            {
                string? id = null;
                for (int attempt = 1; attempt <= 3 && id is null; attempt++)
                {
                    try
                    {
                        Log.Write($"connect attempt {attempt} on {comPort}");
                        _client.Open(comPort);
                        id = _client.SendHello();
                    }
                    catch (Exception ex)
                    {
                        Log.Write($"open/hello attempt {attempt} failed: {ex.Message}");
                        if (attempt == 3)
                            return (false, $"连接失败：{ex.Message}");
                    }
                    if (id is null)
                    {
                        _client.Close();          // full reopen cycle handles stale handles after replug
                        await Task.Delay(400);
                    }
                }

                if (id is null)
                {
                    IsConnected = false;
                    return (false,
                        "设备无响应。如果屏幕卡在开机画面或视频，请拔下 USB 线等待 5 秒后重新插上，再点击连接。");
                }

                Log.Write($"HELLO ok: {id}");
                _client.SendStopMedia();
                _client.SendOptions();
                _client.SetBrightness(brightnessPercent);
                IsConnected = true;

                _cts?.Cancel();
                _cts = new CancellationTokenSource();
                _loop = Task.Run(() => PushLoopAsync(_cts.Token));

                return (true, $"已连接：{id}");
            }
            catch (Exception ex)
            {
                IsConnected = false;
                try { _client.Close(); } catch { /* ignore */ }
                return (false, $"连接失败：{ex.Message}");
            }
            finally
            {
                Interlocked.Exchange(ref _connecting, 0);
            }
        });
    }

    public Task DisconnectAsync()
    {
        return Task.Run(() =>
        {
            IsConnected = false;
            _cts?.Cancel();
            try { _client.Close(); } catch { /* ignore */ }
            RaiseStatus("已断开");
        });
    }

    /// <summary>Queue a rendered BGRA frame; replaces any not-yet-sent frame.</summary>
    public void SubmitFrame(byte[] bgra800x480)
    {
        if (!IsConnected) return;
        lock (_gate) _pending = bgra800x480;
        try { _signal.Release(); } catch (SemaphoreFullException) { /* loop is behind; frame already queued */ }
    }

    public Task SetBrightnessAsync(int percent)
    {
        return Task.Run(() =>
        {
            if (!IsConnected) return;
            try { _client.SetBrightness(percent); }
            catch (Exception ex) { RaiseStatus($"Brightness failed: {ex.Message}"); }
        });
    }

    private async Task PushLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && IsConnected)
        {
            try
            {
                await _signal.WaitAsync(ct);
                byte[]? frame;
                lock (_gate) { frame = _pending; _pending = null; }
                if (frame is null || !IsConnected) continue;

                _client.PushFullImage(frame, Rotate180);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                IsConnected = false;
                Log.Write($"push loop died: {ex}");
                RaiseStatus($"连接已断开：{ex.Message} — 请重新连接（若设备持续无响应，请拔插 USB 重启设备）。");
                break;
            }
        }
    }

    private void RaiseStatus(string s) => StatusChanged?.Invoke(this, s);

    public void Dispose()
    {
        _cts?.Cancel();
        _client.Dispose();
        _signal.Dispose();
    }
}
