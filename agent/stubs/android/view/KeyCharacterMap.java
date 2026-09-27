package android.view;

/** Stub: see stubs/README.md. */
public class KeyCharacterMap {
    public static final int VIRTUAL_KEYBOARD = -1;

    public static KeyCharacterMap load(int deviceId) {
        return new KeyCharacterMap();
    }

    public KeyEvent[] getEvents(char[] chars) {
        return null;
    }
}
