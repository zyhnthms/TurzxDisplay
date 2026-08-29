using System.Runtime.InteropServices;

namespace TurzxDisplay.Services;

/// <summary>
/// Win32 tray icon for WinUI 3 (no external packages):
/// Shell_NotifyIcon + a subclassed window procedure + a native popup menu.
/// All callbacks arrive on the UI thread (the window's own thread), so handlers
/// may touch XAML directly.
/// </summary>
public sealed class TrayService
{
    public sealed record MenuEntry(int Id, string Label, bool Checked, bool Separator);

    /// <summary>Raised on left click (toggle main window).</summary>
    public event Action? LeftClicked;

    /// <summary>Return the menu to show on right click.</summary>
    public Func<IEnumerable<MenuEntry>>? BuildMenu;

    /// <summary>Raised when a menu entry is picked.</summary>
    public event Action<int>? MenuItemPicked;

    private IntPtr _hwnd;
    private IntPtr _icon;
    private IntPtr _menu;
    private NOTIFYICONDATA _nid;
    private SUBCLASSPROC? _proc;   // keep the delegate alive

    private const uint WM_APP_TRAY = 0x8000 + 42;
    private const int SubclassId = 0x5352;

    private const uint WM_LBUTTONUP = 0x0202;
    private const uint WM_RBUTTONUP = 0x0205;

    public void Install(IntPtr hwnd, string tooltip)
    {
        _hwnd = hwnd;
        _icon = BuildIcon();

        _proc = WndProc;
        _ = SetWindowSubclass(hwnd, _proc, SubclassId, IntPtr.Zero);

        _nid = new NOTIFYICONDATA
        {
            cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATA>(),
            hWnd = hwnd,
            uID = 1,
            uFlags = 0x01 | 0x02 | 0x04,          // MESSAGE | ICON | TIP
            uCallbackMessage = WM_APP_TRAY,
            hIcon = _icon,
        };
        WriteTip(ref _nid, tooltip);
        _ = Shell_NotifyIcon(0x00, ref _nid);      // NIM_ADD
    }

    public void Remove()
    {
        if (_hwnd != IntPtr.Zero)
        {
            _ = Shell_NotifyIcon(0x02, ref _nid);  // NIM_DELETE
            RemoveWindowSubclass(_hwnd, _proc!, SubclassId);
        }
        if (_icon != IntPtr.Zero) { DestroyIcon(_icon); _icon = IntPtr.Zero; }
    }

    public void ShowBalloon(string title, string text)
    {
        var nid = _nid;
        nid.uFlags = 0x10;                         // NIF_INFO
        WriteInfo(ref nid, title, text);
        _ = Shell_NotifyIcon(0x01, ref nid);       // NIM_MODIFY
    }

    // ---------------- window procedure ----------------

    private IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam, IntPtr uIdSubclass, IntPtr dwRefData)
    {
        if (msg == WM_APP_TRAY)
        {
            var mouse = (uint)lParam.ToInt64() & 0xFFFF;
            if (mouse == WM_LBUTTONUP)
            {
                LeftClicked?.Invoke();
                return IntPtr.Zero;
            }
            if (mouse == WM_RBUTTONUP)
            {
                ShowContextMenu();
                return IntPtr.Zero;
            }
        }
        return DefSubclassProc(hwnd, msg, wParam, lParam);
    }

    private void ShowContextMenu()
    {
        if (BuildMenu is null) return;
        DestroyMenuSafe();
        _menu = CreatePopupMenu();
        if (_menu == IntPtr.Zero) return;

        foreach (var e in BuildMenu())
        {
            uint flags = e.Separator ? 0x800u : 0x00u;              // MF_SEPARATOR : MF_STRING
            if (e.Checked) flags |= 0x8;                            // MF_CHECKED
            // note: MF_BYCOMMAND == 0x0000 (nothing to OR); 0x0001 is MF_GRAYED — don't repeat that bug
            _ = AppendMenuW(_menu, flags, (UIntPtr)(uint)e.Id, e.Label);
        }

        GetCursorPos(out var pt);
        _ = SetForegroundWindow(_hwnd);
        int choice = TrackPopupMenuEx(_menu, 0x0100 /*TPM_RETURNCMD*/ | 0x08 /*TPM_RIGHTALIGN*/ | 0x20 /*TPM_BOTTOMALIGN*/,
            pt.X, pt.Y, _hwnd, IntPtr.Zero);
        _ = PostMessageW(_hwnd, 0x0000, IntPtr.Zero, IntPtr.Zero);  // WM_NULL: allows menu dismissal
        DestroyMenuSafe();
        if (choice != 0) MenuItemPicked?.Invoke(choice);
    }

    private void DestroyMenuSafe()
    {
        if (_menu != IntPtr.Zero) { DestroyMenu(_menu); _menu = IntPtr.Zero; }
    }

    // ---------------- icon (generated, no .ico file) ----------------

    private static IntPtr BuildIcon()
    {
        const int s = 32;
        var px = new int[s * s]; // top-down ARGB

        for (int y = 0; y < s; y++)
        {
            for (int x = 0; x < s; x++)
            {
                int argb = 0; // transparent
                if (InRounded(x, y))
                {
                    argb = unchecked((int)0xFF7E96DA);                       // soft blue card
                    double dx = x - 15.5, dy = y - 15.5;
                    if (dx * dx + dy * dy <= 9.5 * 9.5)      // clock face
                        argb = unchecked((int)0xFFF4F8FC);
                    // hands
                    if (Math.Abs(dx) <= 1.4 && dy >= -8 && dy <= -1) argb = unchecked((int)0xFF7E96DA);   // minute, up
                    if (Math.Abs(dy) <= 1.4 && dx >= 1 && dx <= 7.5) argb = unchecked((int)0xFF7E96DA);   // hour, right
                }
                px[y * s + x] = argb;
            }
        }

        // color bitmap: 32bpp BGRA, bottom-up rows
        var color = new byte[s * s * 4];
        for (int y = 0; y < s; y++)
        {
            int srcRow = (s - 1 - y) * s;
            for (int x = 0; x < s; x++)
            {
                int argb = px[srcRow + x];
                int o = (y * s + x) * 4;
                color[o] = (byte)((argb >> 16) & 0xFF);  // B
                color[o + 1] = (byte)((argb >> 8) & 0xFF);   // G
                color[o + 2] = (byte)(argb & 0xFF);      // R
                color[o + 3] = (byte)((argb >> 24) & 0xFF);  // A
            }
        }

        // mask bitmap: 1bpp (1 = transparent), 4-byte rows, bottom-up
        var mask = new byte[s * 4];
        for (int y = 0; y < s; y++)
        {
            int srcRow = (s - 1 - y) * s;
            for (int x = 0; x < s; x++)
            {
                if ((byte)((px[srcRow + x] >> 24) & 0xFF) == 0)
                    mask[y * 4 + x / 8] |= (byte)(0x80 >> (x % 8));
            }
        }

        IntPtr hColor = CreateBitmap(s, s, 1, 32, color);
        IntPtr hMask = CreateBitmap(s, s, 1, 1, mask);
        var ii = new ICONINFO { fIcon = true, hbmMask = hMask, hbmColor = hColor };
        IntPtr hIcon = CreateIconIndirect(ref ii);
        if (hColor != IntPtr.Zero) DeleteObject(hColor);
        if (hMask != IntPtr.Zero) DeleteObject(hMask);
        return hIcon != IntPtr.Zero ? hIcon : LoadIcon(IntPtr.Zero, new IntPtr(32512)); // IDI_APPLICATION fallback
    }

    private static bool InRounded(int x, int y)
    {
        const int r = 8, min = 1, max = 30;
        if (x < min || y < min || x > max || y > max) return false;
        int cx = Math.Max(min + r - 1, Math.Min(max - r, x));
        int cy = Math.Max(min + r - 1, Math.Min(max - r, y));
        int dx = x - cx, dy = y - cy;
        return dx * dx + dy * dy <= r * r;
    }

    private static void WriteTip(ref NOTIFYICONDATA nid, string tip)
    {
        nid.szTip = tip.Length > 127 ? tip[..127] : tip;
    }

    private static void WriteInfo(ref NOTIFYICONDATA nid, string title, string text)
    {
        nid.szInfo = text.Length > 255 ? text[..255] : text;
        nid.szInfoTitle = title.Length > 63 ? title[..63] : title;
        nid.dwInfoFlags = 0; // NIIF_NONE
    }

    // ---------------- interop ----------------

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATA
    {
        public uint cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szTip;
        public uint dwState;
        public uint dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string szInfo;
        public uint uVersion;   // union: uTimeout/uVersion
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        // (trailing members of the full struct are irrelevant for our flags)

        public NOTIFYICONDATA() { }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ICONINFO
    {
        public bool fIcon;
        public int xHotspot;
        public int yHotspot;
        public IntPtr hbmMask;
        public IntPtr hbmColor;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    private delegate IntPtr SUBCLASSPROC(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam, IntPtr uIdSubclass, IntPtr dwRefData);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Shell_NotifyIcon(uint dwMessage, ref NOTIFYICONDATA lpData);

    [DllImport("comctl32.dll")]
    private static extern bool SetWindowSubclass(IntPtr hWnd, SUBCLASSPROC pfnSubclass, int uIdSubclass, IntPtr dwRefData);

    [DllImport("comctl32.dll")]
    private static extern bool RemoveWindowSubclass(IntPtr hWnd, SUBCLASSPROC pfnSubclass, int uIdSubclass);

    [DllImport("comctl32.dll")]
    private static extern IntPtr DefSubclassProc(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool AppendMenuW(IntPtr hMenu, uint uFlags, UIntPtr uIDNewItem, string lpNewItem);

    [DllImport("user32.dll")]
    private static extern int TrackPopupMenuEx(IntPtr hMenu, uint uFlags, int x, int y, IntPtr hWnd, IntPtr lptpm);

    [DllImport("user32.dll")]
    private static extern bool DestroyMenu(IntPtr hMenu);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool PostMessageW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateBitmap(int nWidth, int nHeight, uint cPlanes, uint cBitsCount, byte[] lpvBits);

    [DllImport("user32.dll")]
    private static extern IntPtr CreateIconIndirect(ref ICONINFO piconinfo);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr hIcon);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr LoadIcon(IntPtr hInstance, IntPtr lpIconName);
}
