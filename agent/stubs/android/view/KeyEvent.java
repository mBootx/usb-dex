package android.view;

/** Stub: see stubs/README.md. */
public class KeyEvent extends InputEvent {
    public static final int ACTION_DOWN = 0;
    public static final int ACTION_UP = 1;
    public static final int FLAG_FROM_SYSTEM = 0x8;

    public KeyEvent(
            long downTime,
            long eventTime,
            int action,
            int code,
            int repeat,
            int metaState,
            int deviceId,
            int scancode,
            int flags,
            int source) {
    }

    @Override
    public int getDeviceId() {
        return 0;
    }
}
