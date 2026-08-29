using System.IO.Ports;

namespace TurzxDisplay.Services;

/// <summary>
/// Serial protocol client for TURZX 5" display (Turing Smart Screen "rev C" protocol).
/// Validated against the live device: frame commands padded to 250 bytes, full-screen
/// BGRA payload chunked as 249-byte runs separated by a single 0x00, panel-native 800x480.
/// </summary>
public sealed class RevCDisplayClient : IDisposable
{
    private SerialPort? _port;

    public const int PanelWidth = 800;
    public const int PanelHeight = 480;

    /// <summary>Identity string returned by HELLO, e.g. "chs_5inch.dev1_rom1.87".</summary>
    public string? DeviceId { get; private set; }

    public bool IsConnected => _port is { IsOpen: true };

    /// <summary>
    /// Opens the port following the sequence the official app uses:
    /// open with DTR asserted, then assert RTS. The firmware ignores input otherwise.
    /// </summary>
    public void Open(string comPort)
    {
        Close();
        var port = new SerialPort(comPort, 115200, Parity.None, 8, StopBits.One)
        {
            ReadTimeout = 1000,
            WriteTimeout = 4000,
            DtrEnable = true,
            RtsEnable = false,
        };
        port.Open();
        System.Threading.Thread.Sleep(500);
        port.RtsEnable = true;
        System.Threading.Thread.Sleep(200);
        port.DiscardInBuffer();
        _port = port;
    }

    public void Close()
    {
        try { _port?.Close(); } catch { /* ignore */ }
        _port = null;
        DeviceId = null;
    }

    private static byte[] Pad250(byte[] message, byte pad = 0x00)
    {
        int rem = message.Length % 250;
        if (rem == 0) return message;
        var frame = new byte[message.Length + (250 - rem)];
        Buffer.BlockCopy(message, 0, frame, 0, message.Length);
        if (pad != 0x00) Array.Fill(frame, pad, message.Length, frame.Length - message.Length);
        return frame;
    }

    private void SendFrame(byte[] message, byte pad = 0x00)
    {
        var port = _port ?? throw new InvalidOperationException("Port not open");
        var frame = Pad250(message, pad);
        port.Write(frame, 0, frame.Length);
    }

    private byte[] ReadAvailable(int maxWaitMs = 400)
    {
        var port = _port ?? throw new InvalidOperationException("Port not open");
        System.Threading.Thread.Sleep(maxWaitMs);
        int n = port.BytesToRead;
        if (n <= 0) return Array.Empty<byte>();
        var buf = new byte[n];
        port.Read(buf, 0, n);
        return buf;
    }

    private void Drain()
    {
        try { _port?.DiscardInBuffer(); } catch { /* ignore */ }
    }

    /// <summary>HELLO handshake. Returns the device id string, or null when it stays silent.</summary>
    public string? SendHello(int attempts = 3)
    {
        for (int i = 0; i < attempts; i++)
        {
            Drain();
            SendFrame(new byte[] { 0x01, 0xEF, 0x69, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0xC5, 0xD3 });
            var resp = ReadAvailable(500);
            var text = System.Text.Encoding.ASCII.GetString(resp);
            if (text.StartsWith("chs_", StringComparison.Ordinal))
            {
                DeviceId = text.TrimEnd('\0', ' ', '\r', '\n');
                return DeviceId;
            }
            Log.Write($"HELLO attempt {i + 1}: {(resp.Length == 0 ? "silent" : $"{resp.Length}B: {text}")}");
            System.Threading.Thread.Sleep(300);
        }
        return null;
    }

    /// <summary>Stops TF-card video and media playback. Run right after connecting —
    /// a hung boot video otherwise wedges the firmware and it ignores serial input.</summary>
    public void SendStopMedia()
    {
        SendFrame(new byte[] { 0x79, 0xEF, 0x69, 0x00, 0x00, 0x00, 0x01 });   // STOP_VIDEO
        System.Threading.Thread.Sleep(120);
        Drain();
        SendFrame(new byte[] { 0x96, 0xEF, 0x69, 0x00, 0x00, 0x00, 0x01 });   // STOP_MEDIA
        System.Threading.Thread.Sleep(200);
        Drain();
    }

    /// <summary>Orientation/sleep options: no flip, sleep off (validated sequence).</summary>
    public void SendOptions()
    {
        SendFrame(new byte[] { 0x7D, 0xEF, 0x69, 0x00, 0x00, 0x00, 0x05, 0x00, 0x00, 0x00, 0x2D, 0x00, 0x00, 0x00, 0x00 });
        System.Threading.Thread.Sleep(120);
        Drain();
    }

    /// <summary>Backlight level 0-100.</summary>
    public void SetBrightness(int percent)
    {
        int level = Math.Clamp(percent, 0, 100) * 255 / 100;
        SendFrame(new byte[] { 0x7B, 0xEF, 0x69, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, (byte)level });
        System.Threading.Thread.Sleep(80);
        Drain();
    }

    public void ScreenOn() => SendFrame(new byte[] { 0x83, 0xEF, 0x69, 0x00, 0x00, 0x00, 0x00 });
    public void ScreenOff() => SendFrame(new byte[] { 0x83, 0xEF, 0x69, 0x00, 0x00, 0x00, 0x01 });

    /// <summary>
    /// Pushes one full 800x480 BGRA frame (row-major, top-left origin, 4 bytes/pixel).
    /// When <paramref name="rotate180"/> is set the frame is flipped (some units mount
    /// the panel upside-down). Takes ~650 ms on the wire.
    /// </summary>
    public void PushFullImage(byte[] bgra, bool rotate180 = false)
    {
        if (bgra.Length != PanelWidth * PanelHeight * 4)
            throw new ArgumentException($"Expected {PanelWidth}x{PanelHeight}x4 bytes, got {bgra.Length}");

        byte[] src = rotate180 ? Rotate180(bgra) : bgra;

        // Chunk: 249-byte runs joined by a single 0x00, zero-padded to a multiple of 250.
        int chunks = (src.Length + 248) / 249;
        var payload = new byte[chunks * 250];
        int w = 0, r = 0;
        while (r < src.Length)
        {
            int n = Math.Min(249, src.Length - r);
            Buffer.BlockCopy(src, r, payload, w, n);
            w += n;
            payload[w++] = 0x00;
            r += n;
        }

        SendFrame(new byte[] { 0x86, 0xEF, 0x69, 0x00, 0x00, 0x00, 0x01 });            // PRE_UPDATE
        System.Threading.Thread.Sleep(80);

        SendFrame(new byte[] { 0x2C }, 0x2C);                                          // START_DISPLAY (frame of 0x2C)
        System.Threading.Thread.Sleep(80);

        SendFrame(new byte[] { 0xC8, 0xEF, 0x69, 0x00, 0x17, 0x70, 0x0E, 0x10 });      // DISPLAY_BITMAP 5"
        System.Threading.Thread.Sleep(60);

        var port = _port ?? throw new InvalidOperationException("Port not open");
        const int seg = 32768;
        int off = 0;
        while (off < payload.Length)
        {
            int len = Math.Min(seg, payload.Length - off);
            port.BaseStream.Write(payload, off, len);
            off += len;
        }
        port.BaseStream.Flush();

        SendFrame(new byte[] { 0xCF, 0xEF, 0x69, 0x00, 0x00, 0x00, 0x01 });            // QUERY_STATUS
        System.Threading.Thread.Sleep(150);
        Drain();
    }

    private static byte[] Rotate180(byte[] bgra)
    {
        var res = new byte[bgra.Length];
        int px = bgra.Length / 4;
        for (int i = 0; i < px; i++)
        {
            int src = i * 4;
            int dst = (px - 1 - i) * 4;
            res[dst] = bgra[src];
            res[dst + 1] = bgra[src + 1];
            res[dst + 2] = bgra[src + 2];
            res[dst + 3] = bgra[src + 3];
        }
        return res;
    }

    public void Dispose() => Close();
}
