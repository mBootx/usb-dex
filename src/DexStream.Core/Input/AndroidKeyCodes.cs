namespace DexStream.Core.Input;

/// <summary>
/// The <c>android.view.KeyEvent.KEYCODE_*</c> values DexStream injects. Kept as a plain constant
/// table so the mapping can be tested without an Android SDK on the build machine.
/// </summary>
public static class AndroidKeyCodes
{
    public const int Unknown = 0;
    public const int Home = 3;
    public const int Back = 4;
    public const int Digit0 = 7;
    public const int Star = 17;
    public const int Pound = 18;
    public const int DpadUp = 19;
    public const int DpadDown = 20;
    public const int DpadLeft = 21;
    public const int DpadRight = 22;
    public const int DpadCenter = 23;
    public const int VolumeUp = 24;
    public const int VolumeDown = 25;
    public const int Power = 26;
    public const int Clear = 28;
    public const int A = 29;
    public const int Comma = 55;
    public const int Period = 56;
    public const int AltLeft = 57;
    public const int AltRight = 58;
    public const int ShiftLeft = 59;
    public const int ShiftRight = 60;
    public const int Tab = 61;
    public const int Space = 62;
    public const int Enter = 66;

    /// <summary>Backspace. Android's <c>DEL</c> is the backwards delete.</summary>
    public const int Del = 67;

    public const int Grave = 68;
    public const int Minus = 69;
    public const int Equals = 70;
    public const int LeftBracket = 71;
    public const int RightBracket = 72;
    public const int Backslash = 73;
    public const int Semicolon = 74;
    public const int Apostrophe = 75;
    public const int Slash = 76;
    public const int At = 77;
    public const int Plus = 81;
    public const int Menu = 82;
    public const int Search = 84;
    public const int MediaPlayPause = 85;
    public const int MediaStop = 86;
    public const int MediaNext = 87;
    public const int MediaPrevious = 88;
    public const int PageUp = 92;
    public const int PageDown = 93;
    public const int Escape = 111;

    /// <summary>The Delete key. Android's <c>FORWARD_DEL</c> is the forwards delete.</summary>
    public const int ForwardDel = 112;

    public const int CtrlLeft = 113;
    public const int CtrlRight = 114;
    public const int CapsLock = 115;
    public const int ScrollLock = 116;
    public const int MetaLeft = 117;
    public const int MetaRight = 118;
    public const int Sysrq = 120;
    public const int Break = 121;
    public const int MoveHome = 122;
    public const int MoveEnd = 123;
    public const int Insert = 124;
    public const int F1 = 131;
    public const int NumLock = 143;
    public const int Numpad0 = 144;
    public const int NumpadDivide = 154;
    public const int NumpadMultiply = 155;
    public const int NumpadSubtract = 156;
    public const int NumpadAdd = 157;
    public const int NumpadDot = 158;
    public const int NumpadEnter = 160;
    public const int VolumeMute = 164;
    public const int AppSwitch = 187;
    public const int Notification = 83;
    public const int BrightnessDown = 220;
    public const int BrightnessUp = 221;
}

/// <summary>The <c>android.view.KeyEvent.META_*</c> bits DexStream sets.</summary>
[Flags]
public enum AndroidMetaState
{
    None = 0,
    ShiftOn = 0x0000_0001,
    AltOn = 0x0000_0002,
    AltLeftOn = 0x0000_0010,
    AltRightOn = 0x0000_0020,
    ShiftLeftOn = 0x0000_0040,
    ShiftRightOn = 0x0000_0080,
    CtrlOn = 0x0000_1000,
    CtrlLeftOn = 0x0000_2000,
    CtrlRightOn = 0x0000_4000,
    MetaOn = 0x0001_0000,
    MetaLeftOn = 0x0002_0000,
    MetaRightOn = 0x0004_0000,
    CapsLockOn = 0x0010_0000,
    NumLockOn = 0x0020_0000,
    ScrollLockOn = 0x0040_0000,
}
