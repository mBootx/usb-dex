using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using DexStream.App.Services;
using DexStream.Core.Input;
using DexStream.Core.Protocol;

namespace DexStream.App.Views;

/// <summary>
/// Hosts a plain child window for the Direct3D swap chain, and routes mouse and keyboard input to the
/// device.
/// </summary>
/// <remarks>
/// <para>
/// WPF cannot present a DXGI flip-model swap chain directly: <c>D3DImage</c> goes through Direct3D 9
/// and costs a copy plus a frame of latency. Hosting a bare HWND and presenting into it keeps the
/// stream on the fast path. The cost is airspace — WPF cannot draw over the child window — so
/// overlays are separate WPF elements positioned outside it.
/// </para>
/// <para>
/// Input is split for the same reason. The child window swallows mouse messages before WPF sees them,
/// so pointer input is read from the window procedure, where the coordinates are already in the
/// physical client pixels the viewport maps from. Keyboard focus stays with WPF, so keys, text and the
/// wheel (which goes to the focused window) are handled as ordinary WPF events.
/// </para>
/// </remarks>
public sealed class StreamSurface : HwndHost
{
    private const string WindowClassName = "DexStreamSurface";

    private const int WS_CHILD = 0x4000_0000;
    private const int WS_VISIBLE = 0x1000_0000;
    private const int WS_CLIPCHILDREN = 0x0200_0000;
    private const int WS_CLIPSIBLINGS = 0x0400_0000;
    private const uint CS_OWNDC = 0x0020;
    private const int ERROR_CLASS_ALREADY_EXISTS = 1410;

    private const int WM_MOUSEMOVE = 0x0200;
    private const int WM_LBUTTONDOWN = 0x0201;
    private const int WM_LBUTTONUP = 0x0202;
    private const int WM_RBUTTONDOWN = 0x0204;
    private const int WM_RBUTTONUP = 0x0205;
    private const int WM_MBUTTONDOWN = 0x0207;
    private const int WM_MBUTTONUP = 0x0208;
    private const int WM_XBUTTONDOWN = 0x020B;
    private const int WM_XBUTTONUP = 0x020C;
    private const int WM_MOUSELEAVE = 0x02A3;
    private const int WM_SETCURSOR = 0x0020;

    private const uint TME_LEAVE = 0x0000_0002;
    private const int IDC_ARROW = 32512;

    // The window procedure delegate has to outlive every window of the class, so it is rooted in a
    // static field. Letting it be collected would leave the class pointing at freed memory.
    private static readonly WindowProc DefaultWindowProc = DefWindowProcW;
    private static readonly object ClassLock = new();
    private static bool _classRegistered;

    private DexStreamSession? _session;
    private IntPtr _hwnd;
    private DexPointerButtons _buttons;
    private bool _trackingMouseLeave;

    /// <summary>The HWND to present into. Valid from <see cref="SurfaceCreated"/> onwards.</summary>
    public IntPtr SurfaceHandle => _hwnd;

    /// <summary>Raised once the child window exists, so a session can be started against it.</summary>
    public event Action<IntPtr>? SurfaceCreated;

    /// <summary>The session input is routed to. Null stops routing.</summary>
    public DexStreamSession? Session
    {
        get => _session;
        set
        {
            _session = value;
            _buttons = DexPointerButtons.None;
        }
    }

    /// <summary>This element's size in physical pixels, which is the swap chain's size.</summary>
    public (int Width, int Height) PixelSize
    {
        get
        {
            double scale = DpiScale;
            return (
                Math.Max(1, (int)Math.Round(ActualWidth * scale)),
                Math.Max(1, (int)Math.Round(ActualHeight * scale)));
        }
    }

    /// <summary>The DPI scale factor for the monitor this element is on.</summary>
    public double DpiScale =>
        PresentationSource.FromVisual(this)?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;

    protected override HandleRef BuildWindowCore(HandleRef hwndParent)
    {
        EnsureClassRegistered();

        (int width, int height) = PixelSize;

        _hwnd = CreateWindowEx(
            0,
            WindowClassName,
            null,
            WS_CHILD | WS_VISIBLE | WS_CLIPCHILDREN | WS_CLIPSIBLINGS,
            0,
            0,
            width,
            height,
            hwndParent.Handle,
            IntPtr.Zero,
            GetModuleHandle(null),
            IntPtr.Zero);

        if (_hwnd == IntPtr.Zero)
        {
            throw new InvalidOperationException(
                $"Could not create the stream surface window (Win32 error {Marshal.GetLastWin32Error()}).");
        }

        SurfaceCreated?.Invoke(_hwnd);
        return new HandleRef(this, _hwnd);
    }

    protected override void DestroyWindowCore(HandleRef hwnd)
    {
        if (_hwnd != IntPtr.Zero)
        {
            DestroyWindow(_hwnd);
            _hwnd = IntPtr.Zero;
        }
    }

    protected override IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        switch (msg)
        {
            case WM_SETCURSOR:
                // Without this the child window inherits the parent's cursor, which flickers as the
                // pointer crosses the airspace boundary.
                SetCursor(LoadCursor(IntPtr.Zero, IDC_ARROW));
                handled = true;
                return (IntPtr)1;

            case WM_MOUSEMOVE:
                EnsureMouseLeaveTracking();
                SendPointer(DexPointerAction.Move, lParam, DexPointerButtons.None);
                handled = true;
                return IntPtr.Zero;

            case WM_MOUSELEAVE:
                _trackingMouseLeave = false;
                if (_buttons == DexPointerButtons.None)
                {
                    SendHoverExit();
                }

                handled = true;
                return IntPtr.Zero;

            case WM_LBUTTONDOWN:
                OnButtonDown(DexPointerButtons.Primary, lParam);
                handled = true;
                return IntPtr.Zero;

            case WM_LBUTTONUP:
                OnButtonUp(DexPointerButtons.Primary, lParam);
                handled = true;
                return IntPtr.Zero;

            case WM_RBUTTONDOWN:
                OnButtonDown(DexPointerButtons.Secondary, lParam);
                handled = true;
                return IntPtr.Zero;

            case WM_RBUTTONUP:
                OnButtonUp(DexPointerButtons.Secondary, lParam);
                handled = true;
                return IntPtr.Zero;

            case WM_MBUTTONDOWN:
                OnButtonDown(DexPointerButtons.Tertiary, lParam);
                handled = true;
                return IntPtr.Zero;

            case WM_MBUTTONUP:
                OnButtonUp(DexPointerButtons.Tertiary, lParam);
                handled = true;
                return IntPtr.Zero;

            case WM_XBUTTONDOWN:
                OnButtonDown(XButtonOf(wParam), lParam);
                handled = true;
                return (IntPtr)1;

            case WM_XBUTTONUP:
                OnButtonUp(XButtonOf(wParam), lParam);
                handled = true;
                return (IntPtr)1;

            default:
                return IntPtr.Zero;
        }
    }

    private void OnButtonDown(DexPointerButtons button, IntPtr lParam)
    {
        if (button == DexPointerButtons.None)
        {
            return;
        }

        bool first = _buttons == DexPointerButtons.None;
        _buttons |= button;

        // Capturing means a drag that leaves the window still reports moves and the release, so a
        // selection dragged off-screen does not get stuck down on the device.
        if (first && _hwnd != IntPtr.Zero)
        {
            SetCapture(_hwnd);
        }

        // Give WPF the keyboard focus so typing goes to the stream after a click.
        Focus();

        SendPointer(DexPointerAction.Down, lParam, button);
    }

    private void OnButtonUp(DexPointerButtons button, IntPtr lParam)
    {
        if (button == DexPointerButtons.None)
        {
            return;
        }

        _buttons &= ~button;
        SendPointer(DexPointerAction.Up, lParam, button);

        if (_buttons == DexPointerButtons.None)
        {
            ReleaseCapture();
        }
    }

    private void SendPointer(DexPointerAction action, IntPtr lParam, DexPointerButtons actionButton)
    {
        if (_session is null)
        {
            return;
        }

        (int x, int y) = SplitCoordinates(lParam);

        // A move with nothing held is a hover, which is what gives the DeX desktop a mouse cursor
        // rather than a phantom finger.
        if (action == DexPointerAction.Move && _buttons == DexPointerButtons.None)
        {
            action = DexPointerAction.HoverMove;
        }

        _ = _session.SendPointerAsync(action, x, y, _buttons, actionButton);
    }

    private void SendHoverExit()
    {
        if (_session is null)
        {
            return;
        }

        _ = _session.SendPointerAsync(
            DexPointerAction.HoverExit, 0, 0, DexPointerButtons.None, DexPointerButtons.None);
    }

    private void EnsureMouseLeaveTracking()
    {
        if (_trackingMouseLeave || _hwnd == IntPtr.Zero)
        {
            return;
        }

        var track = new TRACKMOUSEEVENT
        {
            cbSize = (uint)Marshal.SizeOf<TRACKMOUSEEVENT>(),
            dwFlags = TME_LEAVE,
            hwndTrack = _hwnd,
            dwHoverTime = 0,
        };

        _trackingMouseLeave = TrackMouseEvent(ref track);
    }

    /// <summary>Splits an <c>lParam</c> into signed client coordinates.</summary>
    internal static (int X, int Y) SplitCoordinates(IntPtr lParam)
    {
        int packed = (int)(lParam.ToInt64() & 0xFFFF_FFFF);

        // The coordinates are signed 16-bit values: a drag above or left of the window is negative.
        return ((short)(packed & 0xFFFF), (short)((packed >> 16) & 0xFFFF));
    }

    /// <summary>Reads which extra mouse button a WM_XBUTTON message refers to.</summary>
    internal static DexPointerButtons XButtonOf(IntPtr wParam)
    {
        int high = (int)((wParam.ToInt64() >> 16) & 0xFFFF);
        return high switch
        {
            1 => DexPointerButtons.Back,
            2 => DexPointerButtons.Forward,
            _ => DexPointerButtons.None,
        };
    }

    // --- WPF-level input (keyboard, text and the wheel) --------------------------------------------

    /// <summary>
    /// Attaches keyboard, text and wheel handlers to the element that holds WPF focus.
    /// </summary>
    /// <remarks>
    /// These three cannot come from the child window: keyboard focus stays with WPF, and the wheel is
    /// delivered to the focused window rather than the one under the pointer.
    /// </remarks>
    public void AttachInput(FrameworkElement inputRoot)
    {
        inputRoot.MouseWheel += OnMouseWheel;
        inputRoot.TextInput += OnTextInput;
        inputRoot.PreviewKeyDown += OnPreviewKeyDown;
        inputRoot.PreviewKeyUp += OnPreviewKeyUp;
        inputRoot.Focusable = true;
    }

    private void OnMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (_session is null)
        {
            return;
        }

        Point position = e.GetPosition(this);
        double scale = DpiScale;

        // A wheel notch is 120 units; Android's scroll axis is in units of one "line".
        _ = _session.SendScrollAsync(
            position.X * scale, position.Y * scale, 0f, e.Delta / 120f, _buttons);
        e.Handled = true;
    }

    /// <summary>
    /// Sends typed characters as text rather than as key codes, so the device's own layout, dead keys
    /// and IME decide what gets typed instead of the host's.
    /// </summary>
    private void OnTextInput(object sender, TextCompositionEventArgs e)
    {
        if (_session is null || string.IsNullOrEmpty(e.Text))
        {
            return;
        }

        if (e.Text.Length == 1 && char.IsControl(e.Text[0]))
        {
            // Enter, Tab and friends arrive here too; they are sent as key events instead.
            return;
        }

        _ = _session.SendTextAsync(e.Text);
        e.Handled = true;
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e) => HandleKey(e, down: true);

    private void OnPreviewKeyUp(object sender, KeyEventArgs e) => HandleKey(e, down: false);

    private void HandleKey(KeyEventArgs e, bool down)
    {
        if (_session is null)
        {
            return;
        }

        Key key = e.Key == Key.System ? e.SystemKey : e.Key;
        int virtualKey = KeyInterop.VirtualKeyFromKey(key);

        bool control = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        bool alt = Keyboard.Modifiers.HasFlag(ModifierKeys.Alt);

        // A bare printable key is already covered by TextInput; sending it here as well would type
        // every character twice. With Ctrl or Alt held there is no TextInput, and the device needs the
        // real keycode for the shortcut to work.
        if (IsPrintable(virtualKey) && !control && !alt)
        {
            e.Handled = true;
            return;
        }

        AndroidMetaState meta = WindowsKeyMap.BuildMetaState(
            shift: Keyboard.Modifiers.HasFlag(ModifierKeys.Shift),
            control: control,
            alt: alt,
            meta: Keyboard.Modifiers.HasFlag(ModifierKeys.Windows),
            capsLock: Keyboard.IsKeyToggled(Key.CapsLock),
            numLock: Keyboard.IsKeyToggled(Key.NumLock),
            scrollLock: Keyboard.IsKeyToggled(Key.Scroll));

        _ = _session.SendKeyAsync(virtualKey, down, meta, e.IsRepeat ? 1 : 0);
        e.Handled = true;
    }

    /// <summary>
    /// True for keys whose characters arrive through <c>TextInput</c>: letters, digits, space and the
    /// OEM punctuation keys.
    /// </summary>
    internal static bool IsPrintable(int virtualKey) => virtualKey switch
    {
        0x20 => true,                                  // space
        >= 0x30 and <= 0x39 => true,                   // 0-9
        >= 0x41 and <= 0x5A => true,                   // A-Z
        >= 0x60 and <= 0x69 => true,                   // numpad 0-9
        0x6A or 0x6B or 0x6D or 0x6E or 0x6F => true,  // numpad * + - . /
        >= 0xBA and <= 0xC0 => true,                   // OEM 1 through OEM 3
        >= 0xDB and <= 0xDE => true,                   // OEM 4 through OEM 7
        _ => false,
    };

    // --- Win32 -----------------------------------------------------------------------------------

    private static void EnsureClassRegistered()
    {
        lock (ClassLock)
        {
            if (_classRegistered)
            {
                return;
            }

            // No background brush: the swap chain owns every pixel, and a brush would flash on resize.
            var windowClass = new WNDCLASSEX
            {
                cbSize = (uint)Marshal.SizeOf<WNDCLASSEX>(),
                style = CS_OWNDC,
                lpfnWndProc = DefaultWindowProc,
                hInstance = GetModuleHandle(null),
                hbrBackground = IntPtr.Zero,
                lpszClassName = WindowClassName,
            };

            if (RegisterClassEx(ref windowClass) == 0)
            {
                int error = Marshal.GetLastWin32Error();
                if (error != ERROR_CLASS_ALREADY_EXISTS)
                {
                    throw new InvalidOperationException(
                        $"Could not register the '{WindowClassName}' window class (Win32 error {error}).");
                }
            }

            _classRegistered = true;
        }
    }

    private delegate IntPtr WindowProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "CreateWindowExW")]
    private static extern IntPtr CreateWindowEx(
        int exStyle,
        string className,
        string? windowName,
        int style,
        int x,
        int y,
        int width,
        int height,
        IntPtr parent,
        IntPtr menu,
        IntPtr instance,
        IntPtr param);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyWindow(IntPtr hwnd);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "RegisterClassExW")]
    private static extern ushort RegisterClassEx(ref WNDCLASSEX windowClass);

    [DllImport("user32.dll", EntryPoint = "DefWindowProcW")]
    private static extern IntPtr DefWindowProcW(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool TrackMouseEvent(ref TRACKMOUSEEVENT eventTrack);

    [DllImport("user32.dll")]
    private static extern IntPtr SetCapture(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool ReleaseCapture();

    [DllImport("user32.dll")]
    private static extern IntPtr SetCursor(IntPtr cursor);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr LoadCursor(IntPtr instance, int cursorName);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetModuleHandleW")]
    private static extern IntPtr GetModuleHandle(string? moduleName);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEX
    {
        public uint cbSize;
        public uint style;
        public WindowProc lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpszMenuName;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpszClassName;
        public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TRACKMOUSEEVENT
    {
        public uint cbSize;
        public uint dwFlags;
        public IntPtr hwndTrack;
        public uint dwHoverTime;
    }
}
