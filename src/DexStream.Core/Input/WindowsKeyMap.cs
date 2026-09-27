namespace DexStream.Core.Input;

/// <summary>
/// Translates Win32 virtual-key codes into Android keycodes.
/// </summary>
/// <remarks>
/// <para>
/// The host layer converts a WPF <c>Key</c> to a virtual-key code with
/// <c>KeyInterop.VirtualKeyFromKey</c> before calling in here, which keeps this table free of any
/// UI framework dependency and testable on any platform.
/// </para>
/// <para>
/// Printable characters are deliberately <em>not</em> resolved through this table during normal
/// typing: the host sends them as UTF-8 text instead, so that the device's own layout, dead keys and
/// IME behaviour apply. The table covers the keys that have no text equivalent (navigation, function
/// keys, modifiers) and the printable keys needed when a shortcut such as Ctrl+C must be delivered
/// as a real keycode.
/// </para>
/// </remarks>
public static class WindowsKeyMap
{
    // Win32 virtual-key codes. Named here rather than pulled from an interop assembly so that Core
    // stays platform-neutral.
    private const int VkBack = 0x08;
    private const int VkTab = 0x09;
    private const int VkClear = 0x0C;
    private const int VkReturn = 0x0D;
    private const int VkShift = 0x10;
    private const int VkControl = 0x11;
    private const int VkMenu = 0x12;
    private const int VkPause = 0x13;
    private const int VkCapital = 0x14;
    private const int VkEscape = 0x1B;
    private const int VkSpace = 0x20;
    private const int VkPrior = 0x21;
    private const int VkNext = 0x22;
    private const int VkEnd = 0x23;
    private const int VkHome = 0x24;
    private const int VkLeft = 0x25;
    private const int VkUp = 0x26;
    private const int VkRight = 0x27;
    private const int VkDown = 0x28;
    private const int VkSnapshot = 0x2C;
    private const int VkInsert = 0x2D;
    private const int VkDelete = 0x2E;
    private const int VkDigit0 = 0x30;
    private const int VkDigit9 = 0x39;
    private const int VkA = 0x41;
    private const int VkZ = 0x5A;
    private const int VkLWin = 0x5B;
    private const int VkRWin = 0x5C;
    private const int VkApps = 0x5D;
    private const int VkNumpad0 = 0x60;
    private const int VkNumpad9 = 0x69;
    private const int VkMultiply = 0x6A;
    private const int VkAdd = 0x6B;
    private const int VkSubtract = 0x6D;
    private const int VkDecimal = 0x6E;
    private const int VkDivide = 0x6F;
    private const int VkF1 = 0x70;
    private const int VkF12 = 0x7B;
    private const int VkNumLock = 0x90;
    private const int VkScroll = 0x91;
    private const int VkLShift = 0xA0;
    private const int VkRShift = 0xA1;
    private const int VkLControl = 0xA2;
    private const int VkRControl = 0xA3;
    private const int VkLMenu = 0xA4;
    private const int VkRMenu = 0xA5;
    private const int VkBrowserBack = 0xA6;
    private const int VkBrowserForward = 0xA7;
    private const int VkBrowserSearch = 0xAA;
    private const int VkVolumeMute = 0xAD;
    private const int VkVolumeDown = 0xAE;
    private const int VkVolumeUp = 0xAF;
    private const int VkMediaNextTrack = 0xB0;
    private const int VkMediaPrevTrack = 0xB1;
    private const int VkMediaStop = 0xB2;
    private const int VkMediaPlayPause = 0xB3;
    private const int VkOem1 = 0xBA; // ; :
    private const int VkOemPlus = 0xBB;
    private const int VkOemComma = 0xBC;
    private const int VkOemMinus = 0xBD;
    private const int VkOemPeriod = 0xBE;
    private const int VkOem2 = 0xBF; // / ?
    private const int VkOem3 = 0xC0; // ` ~
    private const int VkOem4 = 0xDB; // [ {
    private const int VkOem5 = 0xDC; // \ |
    private const int VkOem6 = 0xDD; // ] }
    private const int VkOem7 = 0xDE; // ' "

    /// <summary>
    /// Returns the Android keycode for a virtual key, or <see cref="AndroidKeyCodes.Unknown"/> when
    /// there is no sensible equivalent.
    /// </summary>
    public static int ToAndroidKeyCode(int virtualKey)
    {
        // Contiguous ranges first: these are the bulk of the table and map linearly.
        if (virtualKey is >= VkA and <= VkZ)
        {
            return AndroidKeyCodes.A + (virtualKey - VkA);
        }

        if (virtualKey is >= VkDigit0 and <= VkDigit9)
        {
            return AndroidKeyCodes.Digit0 + (virtualKey - VkDigit0);
        }

        if (virtualKey is >= VkNumpad0 and <= VkNumpad9)
        {
            return AndroidKeyCodes.Numpad0 + (virtualKey - VkNumpad0);
        }

        if (virtualKey is >= VkF1 and <= VkF12)
        {
            return AndroidKeyCodes.F1 + (virtualKey - VkF1);
        }

        return virtualKey switch
        {
            VkBack => AndroidKeyCodes.Del,
            VkTab => AndroidKeyCodes.Tab,
            VkClear => AndroidKeyCodes.Clear,
            VkReturn => AndroidKeyCodes.Enter,
            VkShift or VkLShift => AndroidKeyCodes.ShiftLeft,
            VkRShift => AndroidKeyCodes.ShiftRight,
            VkControl or VkLControl => AndroidKeyCodes.CtrlLeft,
            VkRControl => AndroidKeyCodes.CtrlRight,
            VkMenu or VkLMenu => AndroidKeyCodes.AltLeft,
            VkRMenu => AndroidKeyCodes.AltRight,
            VkPause => AndroidKeyCodes.Break,
            VkCapital => AndroidKeyCodes.CapsLock,
            VkEscape => AndroidKeyCodes.Escape,
            VkSpace => AndroidKeyCodes.Space,
            VkPrior => AndroidKeyCodes.PageUp,
            VkNext => AndroidKeyCodes.PageDown,
            VkEnd => AndroidKeyCodes.MoveEnd,
            VkHome => AndroidKeyCodes.MoveHome,
            VkLeft => AndroidKeyCodes.DpadLeft,
            VkUp => AndroidKeyCodes.DpadUp,
            VkRight => AndroidKeyCodes.DpadRight,
            VkDown => AndroidKeyCodes.DpadDown,
            VkSnapshot => AndroidKeyCodes.Sysrq,
            VkInsert => AndroidKeyCodes.Insert,
            VkDelete => AndroidKeyCodes.ForwardDel,
            VkLWin => AndroidKeyCodes.MetaLeft,
            VkRWin => AndroidKeyCodes.MetaRight,
            VkApps => AndroidKeyCodes.Menu,
            VkMultiply => AndroidKeyCodes.NumpadMultiply,
            VkAdd => AndroidKeyCodes.NumpadAdd,
            VkSubtract => AndroidKeyCodes.NumpadSubtract,
            VkDecimal => AndroidKeyCodes.NumpadDot,
            VkDivide => AndroidKeyCodes.NumpadDivide,
            VkNumLock => AndroidKeyCodes.NumLock,
            VkScroll => AndroidKeyCodes.ScrollLock,
            VkBrowserBack => AndroidKeyCodes.Back,
            VkBrowserForward => AndroidKeyCodes.DpadRight,
            VkBrowserSearch => AndroidKeyCodes.Search,
            VkVolumeMute => AndroidKeyCodes.VolumeMute,
            VkVolumeDown => AndroidKeyCodes.VolumeDown,
            VkVolumeUp => AndroidKeyCodes.VolumeUp,
            VkMediaNextTrack => AndroidKeyCodes.MediaNext,
            VkMediaPrevTrack => AndroidKeyCodes.MediaPrevious,
            VkMediaStop => AndroidKeyCodes.MediaStop,
            VkMediaPlayPause => AndroidKeyCodes.MediaPlayPause,
            VkOem1 => AndroidKeyCodes.Semicolon,
            VkOemPlus => AndroidKeyCodes.EqualsSign,
            VkOemComma => AndroidKeyCodes.Comma,
            VkOemMinus => AndroidKeyCodes.Minus,
            VkOemPeriod => AndroidKeyCodes.Period,
            VkOem2 => AndroidKeyCodes.Slash,
            VkOem3 => AndroidKeyCodes.Grave,
            VkOem4 => AndroidKeyCodes.LeftBracket,
            VkOem5 => AndroidKeyCodes.Backslash,
            VkOem6 => AndroidKeyCodes.RightBracket,
            VkOem7 => AndroidKeyCodes.Apostrophe,
            _ => AndroidKeyCodes.Unknown,
        };
    }

    /// <summary>
    /// True when a virtual key is itself a modifier, so the host can update the meta-state without
    /// also injecting a keycode the device would treat as a character.
    /// </summary>
    public static bool IsModifier(int virtualKey) => virtualKey
        is VkShift or VkLShift or VkRShift
        or VkControl or VkLControl or VkRControl
        or VkMenu or VkLMenu or VkRMenu
        or VkLWin or VkRWin;

    /// <summary>
    /// Builds the Android meta-state for the current modifier set, setting both the generic and the
    /// left/right specific bits because apps check either one.
    /// </summary>
    public static AndroidMetaState BuildMetaState(
        bool shift,
        bool control,
        bool alt,
        bool meta,
        bool capsLock = false,
        bool numLock = false,
        bool scrollLock = false)
    {
        AndroidMetaState state = AndroidMetaState.None;

        if (shift)
        {
            state |= AndroidMetaState.ShiftOn | AndroidMetaState.ShiftLeftOn;
        }

        if (control)
        {
            state |= AndroidMetaState.CtrlOn | AndroidMetaState.CtrlLeftOn;
        }

        if (alt)
        {
            state |= AndroidMetaState.AltOn | AndroidMetaState.AltLeftOn;
        }

        if (meta)
        {
            state |= AndroidMetaState.MetaOn | AndroidMetaState.MetaLeftOn;
        }

        if (capsLock)
        {
            state |= AndroidMetaState.CapsLockOn;
        }

        if (numLock)
        {
            state |= AndroidMetaState.NumLockOn;
        }

        if (scrollLock)
        {
            state |= AndroidMetaState.ScrollLockOn;
        }

        return state;
    }
}
