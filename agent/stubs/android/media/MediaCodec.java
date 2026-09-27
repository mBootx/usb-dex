package android.media;

import android.os.Bundle;
import android.view.Surface;

import java.io.IOException;
import java.nio.ByteBuffer;

/** Stub: see stubs/README.md. */
public final class MediaCodec {
    public static final int CONFIGURE_FLAG_ENCODE = 1;
    public static final int INFO_TRY_AGAIN_LATER = -1;
    public static final int INFO_OUTPUT_FORMAT_CHANGED = -2;
    public static final int BUFFER_FLAG_CODEC_CONFIG = 2;
    public static final int BUFFER_FLAG_KEY_FRAME = 1;
    public static final int BUFFER_FLAG_END_OF_STREAM = 4;

    public static final String PARAMETER_KEY_VIDEO_BITRATE = "video-bitrate";
    public static final String PARAMETER_KEY_REQUEST_SYNC_FRAME = "request-sync";

    public static final class BufferInfo {
        public int offset;
        public int size;
        public int flags;
        public long presentationTimeUs;
    }

    private MediaCodec() {
    }

    public static MediaCodec createEncoderByType(String type) throws IOException {
        return null;
    }

    public void configure(MediaFormat format, Surface surface, Object crypto, int flags) {
    }

    public Surface createInputSurface() {
        return null;
    }

    public void start() {
    }

    public void stop() {
    }

    public void release() {
    }

    public void setParameters(Bundle parameters) {
    }

    public int dequeueOutputBuffer(BufferInfo info, long timeoutUs) {
        return 0;
    }

    public ByteBuffer getOutputBuffer(int index) {
        return null;
    }

    public void releaseOutputBuffer(int index, boolean render) {
    }

    public MediaFormat getOutputFormat() {
        return null;
    }
}
