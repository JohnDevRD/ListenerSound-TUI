using System.Runtime.InteropServices;

namespace ListenerSound.Common;

public sealed class WindowsTray : IDisposable
{
    public event Action? HotkeyPressed;
    public event Action? ExitRequested;

    private const int HotkeyId = 1;
    private const int TrayIconId = 1;
    private const uint TrayMsg = 0x8000 + 1; // WM_APP + 1
    private const uint ShutdownMsg = 0x8000 + 2;
    private const uint TimerId = 1;

    private static WndProcDelegate? _wndProcDelegate;
    private static ConsoleCtrlHandlerDelegate? _ctrlHandler;
    private static WindowsTray? _active;

    private IntPtr _hwnd;
    private IntPtr _hIcon;
    private Thread? _pumpThread;
    private volatile bool _hidden;
    private bool _hotkeyActive;
    private volatile bool _disposed;
    private uint _pendingVk;

    public bool IsConsoleVisible => !_hidden;

    public WindowsTray(string? triggerKey = null)
    {
        _pendingVk = VkFromKey(triggerKey);
        _hotkeyActive = _pendingVk != 0;
        _wndProcDelegate ??= WndProc;
        _ctrlHandler ??= CtrlHandler;
        _active = this;
    }

    public void Start()
    {
        if (_pumpThread != null) return;

        _pumpThread = new Thread(Pump)
        {
            IsBackground = true,
            Name = "ListenerSound-TrayPump"
        };
        if (OperatingSystem.IsWindows())
        {
            _pumpThread.SetApartmentState(ApartmentState.STA);
        }
        _pumpThread.Start();

        if (_ctrlHandler != null)
        {
            SetConsoleCtrlHandler(_ctrlHandler, true);
        }
    }

    // Re-registra la tecla global cuando el usuario cambia la tecla de disparo.
    public void SetHotkey(string? triggerKey)
    {
        if (_disposed) return;
        try
        {
            var vk = VkFromKey(triggerKey);
            if (_hwnd != IntPtr.Zero)
            {
                UnregisterHotKey(_hwnd, HotkeyId);
                if (vk != 0)
                {
                    _hotkeyActive = RegisterHotKey(_hwnd, HotkeyId, 0, vk);
                    if (!_hotkeyActive)
                        LogFile.Append("No se pudo registrar la tecla global (posible conflicto). Se usa solo en consola.");
                }
                else
                {
                    _hotkeyActive = false;
                }
            }
            else
            {
                _pendingVk = vk;
            }
        }
        catch { _hotkeyActive = false; }
    }

    private void Pump()
    {
        var className = "ListenerSound_TrayWnd";
        var hInstance = GetModuleHandle(null);

        var wc = new WNDCLASS
        {
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProcDelegate!),
            hInstance = hInstance,
            lpszClassName = className
        };
        RegisterClass(ref wc);

        _hwnd = CreateWindowEx(0, className, "ListenerSound", 0, 0, 0, 0, 0, IntPtr.Zero, IntPtr.Zero, hInstance, IntPtr.Zero);
        if (_hwnd == IntPtr.Zero) return;

        // Registrar hotkey si hay tecla asignada
        if (_pendingVk != 0)
        {
            ActivateHotkey(_pendingVk);
        }

        _hIcon = LoadAppIcon();
        AddTrayIcon();
        SetTimer(_hwnd, new IntPtr(TimerId), 500, IntPtr.Zero);

        while (!_disposed)
        {
            if (!GetMessage(out var msg, IntPtr.Zero, 0, 0)) break;
            TranslateMessage(ref msg);
            DispatchMessage(ref msg);
        }

        Cleanup();
    }

    private void ActivateHotkey(uint vk)
    {
        if (_hwnd == IntPtr.Zero) return;
        _hotkeyActive = RegisterHotKey(_hwnd, HotkeyId, 0, vk);
        if (!_hotkeyActive)
            LogFile.Append("Tecla global no registrada (conflicto). Se usa solo en consola.");
    }
private static IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        var t = _active;
        if (msg == 0x0312) // WM_HOTKEY
        {
            t?.HotkeyPressed?.Invoke();
            return IntPtr.Zero;
        }
        if (msg == 0x0113) // WM_TIMER
        {
            t?.OnTimer();
            return IntPtr.Zero;
        }
        if (msg == TrayMsg)
        {
            var l = (uint)lParam;
            if (l == 0x0205) // WM_RBUTTONUP
                t?.ShowContextMenu();
            else if (l == 0x0203) // WM_LBUTTONDBLCLK
                t?.Toggle();
            return IntPtr.Zero;
        }
        if (msg == 0x0111) // WM_COMMAND
        {
            var cmd = (ushort)((ulong)wParam & 0xFFFF);
            if (cmd == 1) t?.ShowConsole();
            else if (cmd == 2) t?.ExitRequested?.Invoke();
            return IntPtr.Zero;
        }
        if (msg == ShutdownMsg)
        {
            PostQuitMessage(0);
            return IntPtr.Zero;
        }
        return DefWindowProc(hWnd, msg, wParam, lParam);
    }

    private void OnTimer()
    {
        if (_disposed) return;
        var cw = GetConsoleWindow();
        if (IsIconic(cw) && !_hidden)
            HideToTray();
    }

    public void Show() => ShowConsole();
    public void Hide() => HideToTray();

    private void Toggle()
    {
        if (_hidden) ShowConsole();
        else HideToTray();
    }

    private void ShowConsole()
    {
        if (!_hidden) return;
        _hidden = false;
        var cw = GetConsoleWindow();
        ShowWindow(cw, 9 /*SW_RESTORE*/);
        SetForegroundWindow(cw);
    }

    private void HideToTray()
    {
        if (_hidden) return;
        _hidden = true;
        ShowWindow(GetConsoleWindow(), 0 /*SW_HIDE*/);
        var msg = _pendingVk != 0
            ? "Doble clic en este icono para volver. La tecla de disparo sigue activa."
            : "Doble clic en este icono para volver. El servidor sigue activo.";
        ShowBalloon("ListenerSound sigue en segundo plano", msg);
    }

    private void ShowContextMenu()
    {
        SetForegroundWindow(_hwnd);
        var menu = CreatePopupMenu();
        AppendMenu(menu, 0x0 /*MF_STRING*/, 1, "Abrir ListenerSound");
        AppendMenu(menu, 0x0 /*MF_STRING*/, 2, "Salir");
        GetCursorPos(out var pt);
        // TPM_RETURNCMD | TPM_RIGHTBUTTON | TPM_NONOTIFY
        var cmd = TrackPopupMenu(menu, 0x100 | 0x2 | 0x80, pt.X, pt.Y, 0, _hwnd, IntPtr.Zero);
        DestroyMenu(menu);
        if (cmd == 1) ShowConsole();
        else if (cmd == 2) ExitRequested?.Invoke();
    }

    private void AddTrayIcon()
    {
        var nid = new NOTIFYICONDATA
        {
            cbSize = Marshal.SizeOf<NOTIFYICONDATA>(),
            hWnd = _hwnd,
            uID = TrayIconId,
            uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP,
            uCallbackMessage = TrayMsg,
            hIcon = _hIcon,
            szTip = "ListenerSound - doble clic para abrir"
        };
        Shell_NotifyIcon(0 /*NIM_ADD*/, ref nid);
        nid.uVersion = 4; // NOTIFYICON_VERSION_4
        Shell_NotifyIcon(0x80000004 /*NIM_SETVERSION*/, ref nid);
    }

    private void ShowBalloon(string title, string text)
    {
        var nid = new NOTIFYICONDATA
        {
            cbSize = Marshal.SizeOf<NOTIFYICONDATA>(),
            hWnd = _hwnd,
            uID = TrayIconId,
            uFlags = NIF_INFO,
            dwInfoFlags = 0x1 /*NIIF_INFO*/,
            szInfoTitle = title,
            szInfo = text
        };
        Shell_NotifyIcon(1 /*NIM_MODIFY*/, ref nid);
    }

    private void Cleanup()
    {
        if (_hwnd != IntPtr.Zero)
        {
            var nid = new NOTIFYICONDATA
            {
                cbSize = Marshal.SizeOf<NOTIFYICONDATA>(),
                hWnd = _hwnd,
                uID = TrayIconId
            };
            Shell_NotifyIcon(2 /*NIM_DELETE*/, ref nid);
            UnregisterHotKey(_hwnd, HotkeyId);
            KillTimer(_hwnd, new IntPtr(TimerId));
        }
        if (_hIcon != IntPtr.Zero)
            DestroyIcon(_hIcon);
    }

    private static bool CtrlHandler(uint ctrlType)
    {
        // CTRL_CLOSE_EVENT (2): interceptar el botón X para que no mate el proceso.
        if (ctrlType == 2)
        {
            _active?.HideToTray();
            return true; // evita la terminación
        }
        return false;
    }

    private static uint VkFromKey(string? keyName)
    {
        if (string.IsNullOrWhiteSpace(keyName)) return 0;

        if (keyName.Length > 1 && (keyName[0] == 'F' || keyName[0] == 'f'))
        {
            if (int.TryParse(keyName.AsSpan(1), out var n) && n >= 1 && n <= 24)
                return 0x70u + (uint)(n - 1); // VK_F1 + offset
        }

        if (Enum.TryParse<ConsoleKey>(keyName, true, out var consoleKey))
        {
            return (uint)consoleKey;
        }

        if (string.Equals(keyName, "Space", StringComparison.OrdinalIgnoreCase)) return 0x20u;
        if (string.Equals(keyName, "Esc", StringComparison.OrdinalIgnoreCase)) return 0x1Bu;
        if (string.Equals(keyName, "Return", StringComparison.OrdinalIgnoreCase)) return 0x0Du;

        if (keyName.Length == 1)
        {
            var c = char.ToUpperInvariant(keyName[0]);
            if ((c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9'))
                return (uint)c;
        }

        return 0x73u;
    }

    private IntPtr LoadAppIcon()
    {
        try
        {
            var path = Environment.ProcessPath ?? AppContext.BaseDirectory;
            ExtractIconEx(path, 0, out var large, out var small, 1);
            return large != IntPtr.Zero ? large : small;
        }
        catch { return IntPtr.Zero; }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { if (_pumpThread != null) PostMessage(_hwnd, ShutdownMsg, IntPtr.Zero, IntPtr.Zero); } catch { }
        try { _pumpThread?.Join(1500); } catch { }
        try { if (_ctrlHandler != null) SetConsoleCtrlHandler(_ctrlHandler, false); } catch { }
        if (_active == this) _active = null;
    }
// ---------- P/Invoke ----------
    private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
    private delegate bool ConsoleCtrlHandlerDelegate(uint dwCtrlType);

    private const uint NIF_MESSAGE = 0x1, NIF_ICON = 0x2, NIF_TIP = 0x4, NIF_INFO = 0x10;

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern ushort RegisterClass(ref WNDCLASS lp);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowEx(uint dwExStyle, string className, string winName,
        uint style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr GetModuleHandle(string? name);

    [DllImport("user32.dll")]
    private static extern IntPtr DefWindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool GetMessage(out MSG msg, IntPtr hWnd, uint min, uint max);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref MSG msg);

    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessage(ref MSG msg);

    [DllImport("user32.dll")]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint mods, uint vk);

    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    [DllImport("user32.dll")]
    private static extern IntPtr SetTimer(IntPtr hWnd, IntPtr id, uint elapse, IntPtr fn);

    [DllImport("user32.dll")]
    private static extern bool KillTimer(IntPtr hWnd, IntPtr id);

    [DllImport("user32.dll")]
    private static extern bool PostQuitMessage(int code);

    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetConsoleWindow();

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int cmd);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Shell_NotifyIcon(uint msg, ref NOTIFYICONDATA data);

    [DllImport("shell32.dll", CharSet = CharSet.Auto)]
    private static extern uint ExtractIconEx(string file, int index, out IntPtr large, out IntPtr small, uint count);

    [DllImport("user32.dll")]
    private static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool AppendMenu(IntPtr menu, uint flags, uint id, string text);

    [DllImport("user32.dll")]
    private static extern uint TrackPopupMenu(IntPtr menu, uint flags, int x, int y, int reserved, IntPtr hWnd, IntPtr rect);

    [DllImport("user32.dll")]
    private static extern bool DestroyMenu(IntPtr menu);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT pt);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr icon);

    [DllImport("kernel32.dll")]
    private static extern bool SetConsoleCtrlHandler(ConsoleCtrlHandlerDelegate handler, bool add);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASS
    {
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        public string lpszMenuName;
        public string lpszClassName;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public POINT pt;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATA
    {
        public int cbSize;
        public IntPtr hWnd;
        public int uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public int dwState;
        public int dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public int uVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public int dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }
    }