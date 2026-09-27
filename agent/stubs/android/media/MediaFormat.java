package android.media;

/** Stub: see stubs/README.md. */
public final class MediaFormat {
    public static final String KEY_COLOR_FORMAT = "color-format";
    public static final String KEY_BIT_RATE = "bitrate";
    public static final String KEY_FRAME_RATE = "frame-rate";
    public static final String KEY_I_FRAME_INTERVAL = "i-frame-interval";
    public static final String KEY_BITRATE_MODE = "bitrate-mode";
    public static final String KEY_MAX_B_FRAMES = "max-bframes";
    public static final String KEY_PRIORITY = "priority";
    public static final String KEY_LATENCY = "latency";

    public static MediaFormat createVideoFormat(String mime, int width, int height) {
        return new MediaFormat();
    }

    public void setInteger(String name, int value) {
    }

    public void setFloat(String name, float value) {
    }
}
