package android.view;

/** Stub: see stubs/README.md. */
public final class MotionEvent extends InputEvent {
    public static final int ACTION_DOWN = 0;
    public static final int ACTION_UP = 1;
    public static final int ACTION_MOVE = 2;
    public static final int ACTION_CANCEL = 3;
    public static final int ACTION_SCROLL = 8;
    public static final int ACTION_HOVER_MOVE = 7;
    public static final int ACTION_HOVER_ENTER = 9;
    public static final int ACTION_HOVER_EXIT = 10;

    public static final int TOOL_TYPE_MOUSE = 3;
    public static final int AXIS_HSCROLL = 10;
    public static final int AXIS_VSCROLL = 9;

    public static final class PointerProperties {
        public int id;
        public int toolType;
    }

    public static final class PointerCoords {
        public float x;
        public float y;
        public float pressure;
        public float size;

        public void setAxisValue(int axis, float value) {
        }
    }

    private MotionEvent() {
    }

    public static MotionEvent obtain(
            long downTime,
            long eventTime,
            int action,
            int pointerCount,
            PointerProperties[] pointerProperties,
            PointerCoords[] pointerCoords,
            int metaState,
            int buttonState,
            float xPrecision,
            float yPrecision,
            int deviceId,
            int edgeFlags,
            int source,
            int flags) {
        return null;
    }

    public void recycle() {
    }

    @Override
    public int getDeviceId() {
        return 0;
    }
}
