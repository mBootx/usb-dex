using DexStream.Core.Input;
using Xunit;

namespace DexStream.Core.Tests;

public class WindowsKeyMapTests
{
    [Theory]
    [InlineData(0x41, AndroidKeyCodes.A)]                     // A
    [InlineData(0x5A, AndroidKeyCodes.A + 25)]                // Z
    [InlineData(0x30, AndroidKeyCodes.Digit0)]                // 0
    [InlineData(0x39, AndroidKeyCodes.Digit0 + 9)]            // 9
    [InlineData(0x60, AndroidKeyCodes.Numpad0)]               // Numpad 0
    [InlineData(0x69, AndroidKeyCodes.Numpad0 + 9)]           // Numpad 9
    [InlineData(0x70, AndroidKeyCodes.F1)]                    // F1
    [InlineData(0x7B, AndroidKeyCodes.F1 + 11)]               // F12
    public void ToAndroidKeyCode_MapsContiguousRangesLinearly(int virtualKey, int expected)
        => Assert.Equal(expected, WindowsKeyMap.ToAndroidKeyCode(virtualKey));

    [Theory]
    [InlineData(0x08, AndroidKeyCodes.Del)]            // Backspace -> DEL
    [InlineData(0x2E, AndroidKeyCodes.ForwardDel)]     // Delete -> FORWARD_DEL
    [InlineData(0x0D, AndroidKeyCodes.Enter)]
    [InlineData(0x1B, AndroidKeyCodes.Escape)]
    [InlineData(0x09, AndroidKeyCodes.Tab)]
    [InlineData(0x20, AndroidKeyCodes.Space)]
    [InlineData(0x24, AndroidKeyCodes.MoveHome)]       // Home -> MOVE_HOME, not KEYCODE_HOME
    [InlineData(0x23, AndroidKeyCodes.MoveEnd)]
    [InlineData(0x21, AndroidKeyCodes.PageUp)]
    [InlineData(0x22, AndroidKeyCodes.PageDown)]
    [InlineData(0x25, AndroidKeyCodes.DpadLeft)]
    [InlineData(0x26, AndroidKeyCodes.DpadUp)]
    [InlineData(0x27, AndroidKeyCodes.DpadRight)]
    [InlineData(0x28, AndroidKeyCodes.DpadDown)]
    [InlineData(0x5B, AndroidKeyCodes.MetaLeft)]
    [InlineData(0xA0, AndroidKeyCodes.ShiftLeft)]
    [InlineData(0xA1, AndroidKeyCodes.ShiftRight)]
    [InlineData(0xA2, AndroidKeyCodes.CtrlLeft)]
    [InlineData(0xA3, AndroidKeyCodes.CtrlRight)]
    [InlineData(0xBF, AndroidKeyCodes.Slash)]
    [InlineData(0xC0, AndroidKeyCodes.Grave)]
    [InlineData(0xDE, AndroidKeyCodes.Apostrophe)]
    public void ToAndroidKeyCode_MapsSpecialKeys(int virtualKey, int expected)
        => Assert.Equal(expected, WindowsKeyMap.ToAndroidKeyCode(virtualKey));

    [Fact]
    public void ToAndroidKeyCode_ReturnsUnknownForUnmappedKeys()
    {
        // 0xFF is not a key Windows delivers as a normal virtual key.
        Assert.Equal(AndroidKeyCodes.Unknown, WindowsKeyMap.ToAndroidKeyCode(0xFF));
        Assert.Equal(AndroidKeyCodes.Unknown, WindowsKeyMap.ToAndroidKeyCode(0x07));
    }

    [Fact]
    public void ToAndroidKeyCode_HomeIsNotTheAndroidHomeButton()
    {
        // Mapping the Home key to KEYCODE_HOME would send the DeX desktop to the launcher on every
        // text-navigation keypress, so it must map to MOVE_HOME instead.
        Assert.NotEqual(AndroidKeyCodes.Home, WindowsKeyMap.ToAndroidKeyCode(0x24));
    }

    [Theory]
    [InlineData(0x10)]
    [InlineData(0xA0)]
    [InlineData(0xA1)]
    [InlineData(0x11)]
    [InlineData(0xA2)]
    [InlineData(0x12)]
    [InlineData(0x5B)]
    [InlineData(0x5C)]
    public void IsModifier_RecognisesModifierKeys(int virtualKey)
        => Assert.True(WindowsKeyMap.IsModifier(virtualKey));

    [Theory]
    [InlineData(0x41)]
    [InlineData(0x0D)]
    [InlineData(0x70)]
    public void IsModifier_IsFalseForOrdinaryKeys(int virtualKey)
        => Assert.False(WindowsKeyMap.IsModifier(virtualKey));

    [Fact]
    public void BuildMetaState_SetsBothGenericAndSidedBits()
    {
        AndroidMetaState state = WindowsKeyMap.BuildMetaState(
            shift: true, control: true, alt: false, meta: false);

        Assert.True(state.HasFlag(AndroidMetaState.ShiftOn));
        Assert.True(state.HasFlag(AndroidMetaState.ShiftLeftOn));
        Assert.True(state.HasFlag(AndroidMetaState.CtrlOn));
        Assert.True(state.HasFlag(AndroidMetaState.CtrlLeftOn));
        Assert.False(state.HasFlag(AndroidMetaState.AltOn));
        Assert.False(state.HasFlag(AndroidMetaState.MetaOn));
    }

    [Fact]
    public void BuildMetaState_IncludesLockKeys()
    {
        AndroidMetaState state = WindowsKeyMap.BuildMetaState(
            false, false, false, false, capsLock: true, numLock: true, scrollLock: true);

        Assert.Equal(
            AndroidMetaState.CapsLockOn | AndroidMetaState.NumLockOn | AndroidMetaState.ScrollLockOn,
            state);
    }

    [Fact]
    public void BuildMetaState_IsNoneWhenNothingIsHeld()
        => Assert.Equal(AndroidMetaState.None, WindowsKeyMap.BuildMetaState(false, false, false, false));

    [Fact]
    public void MetaStateValues_MatchAndroidsConstants()
    {
        // Pinned against android.view.KeyEvent so a typo cannot silently change behaviour.
        Assert.Equal(0x01, (int)AndroidMetaState.ShiftOn);
        Assert.Equal(0x02, (int)AndroidMetaState.AltOn);
        Assert.Equal(0x1000, (int)AndroidMetaState.CtrlOn);
        Assert.Equal(0x10000, (int)AndroidMetaState.MetaOn);
        Assert.Equal(0x100000, (int)AndroidMetaState.CapsLockOn);
    }
}
