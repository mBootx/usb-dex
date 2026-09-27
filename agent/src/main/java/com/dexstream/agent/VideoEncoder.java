package com.dexstream.agent;

import android.media.MediaCodec;
import android.media.MediaCodecInfo;
import android.media.MediaFormat;
import android.os.Build;
import android.os.Bundle;
import android.os.SystemClock;
import android.view.Surface;

import java.io.IOException;
import java.nio.ByteBuffer;
import java.util.concurrent.atomic.AtomicBoolean;
import java.util.concurrent.atomic.AtomicInteger;

/**
 * Encodes the captured display with MediaCodec and writes DexStream packets to the video channel.
 *
 * <p>The configuration is tuned for interactive latency rather than for file size: constant bitrate
 * so the encoder never saves up bits for a future frame, no B-frames so no frame waits for a later
 * one, {@code KEY_LATENCY} of one frame where the platform supports it, and realtime priority. The
 * key-frame interval is deliberately long — a periodic IDR is a large packet that shows up as a
 * latency spike — and the host asks for one on demand instead, after a decoder reset.
 */
public final class VideoEncoder implements AutoCloseable {

    /**
     * Encoders reject or silently misbehave on odd dimensions, and many hardware encoders want a
     * multiple of 16. Rounding to 16 costs at most a few pixels of the captured area and avoids a
     * whole class of device-specific failure.
     */
    private static final int DIMENSION_ALIGNMENT = 16;

    /** How long to block waiting for an encoded frame before checking for shutdown. */
    private static final long DEQUEUE_TIMEOUT_US = 100_000;

    private final MediaCodec codec;
    private final Surface inputSurface;
    private final int width;
    private final int height;
    private final AtomicBoolean keyFrameRequested = new AtomicBoolean();
    private final AtomicInteger pendingBitrate = new AtomicInteger();

    private VideoEncoder(MediaCodec codec, Surface inputSurface, int width, int height) {
        this.codec = codec;
        this.inputSurface = inputSurface;
        this.width = width;
        this.height = height;
    }

    public Surface getInputSurface() {
        return inputSurface;
    }

    public int getWidth() {
        return width;
    }

    public int getHeight() {
        return height;
    }

    /**
     * Creates and starts an encoder.
     *
     * @param sourceWidth  the display's width
     * @param sourceHeight the display's height
     * @param maxSize      longest edge of the encoded frame; 0 keeps the source size
     */
    public static VideoEncoder create(Options options, int sourceWidth, int sourceHeight)
            throws IOException {
        int[] size = computeEncodedSize(sourceWidth, sourceHeight, options.maxSize);
        int width = size[0];
        int height = size[1];

        String mime = options.mimeType();
        MediaFormat format = MediaFormat.createVideoFormat(mime, width, height);
        format.setInteger(MediaFormat.KEY_COLOR_FORMAT, MediaCodecInfo.CodecCapabilities.COLOR_FormatSurface);
        format.setInteger(MediaFormat.KEY_BIT_RATE, options.bitrate);
        format.setInteger(MediaFormat.KEY_FRAME_RATE, Math.max(options.maxFps, 1));

        // 10 seconds between automatic key frames: long enough that the periodic IDR is not a
        // recurring latency spike, short enough to bound recovery if the host misses a request.
        format.setInteger(MediaFormat.KEY_I_FRAME_INTERVAL, 10);
        format.setInteger(
                MediaFormat.KEY_BITRATE_MODE, MediaCodecInfo.EncoderCapabilities.BITRATE_MODE_CBR);
        format.setInteger(MediaFormat.KEY_MAX_B_FRAMES, 0);

        // Realtime priority. 0 means "realtime" to MediaCodec; 1 means "best effort".
        format.setInteger(MediaFormat.KEY_PRIORITY, 0);

        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.R) {
            // Ask the encoder to emit each frame as soon as it is encoded instead of buffering.
            format.setInteger(MediaFormat.KEY_LATENCY, 1);
        }

        // With a Surface input there is no fixed capture rate, so tell the encoder the frame interval
        // it should assume. Without this some encoders assume 30fps and under-allocate bits.
        // Set by key string rather than through MediaFormat.KEY_MAX_FPS_TO_ENCODER, whose
        // availability varies by API level while the key itself has been stable throughout.
        format.setFloat("max-fps-to-encoder", Math.max(options.maxFps, 1));

        MediaCodec codec = MediaCodec.createEncoderByType(mime);
        try {
            codec.configure(format, null, null, MediaCodec.CONFIGURE_FLAG_ENCODE);
            Surface surface = codec.createInputSurface();
            codec.start();

            Ln.i("Encoder started: " + mime + " " + width + "x" + height
                    + " @" + options.maxFps + "fps, " + (options.bitrate / 1_000_000.0) + " Mbit/s");
            return new VideoEncoder(codec, surface, width, height);
        } catch (Exception e) {
            codec.release();
            throw new IOException(
                    "Could not start a " + mime + " encoder at " + width + "x" + height
                            + ". The device may not support this resolution for this codec: "
                            + Ln.describe(e), e);
        }
    }

    /**
     * Works out the encoded frame size: the source size scaled so that its longest edge is at most
     * {@code maxSize}, with both edges rounded to the encoder alignment.
     */
    static int[] computeEncodedSize(int sourceWidth, int sourceHeight, int maxSize) {
        if (sourceWidth <= 0 || sourceHeight <= 0) {
            throw new IllegalArgumentException(
                    "Invalid source size " + sourceWidth + "x" + sourceHeight);
        }

        int width = sourceWidth;
        int height = sourceHeight;

        if (maxSize > 0) {
            int longest = Math.max(width, height);
            if (longest > maxSize) {
                double scale = (double) maxSize / longest;
                width = (int) Math.round(width * scale);
                height = (int) Math.round(height * scale);
            }
        }

        return new int[]{align(width), align(height)};
    }

    private static int align(int value) {
        int aligned = value - (value % DIMENSION_ALIGNMENT);
        return Math.max(aligned, DIMENSION_ALIGNMENT);
    }

    /** Asks the encoder for an immediate key frame. Applied before the next dequeue. */
    public void requestKeyFrame() {
        keyFrameRequested.set(true);
    }

    /** Changes the target bitrate. Applied before the next dequeue. */
    public void setBitrate(int bitsPerSecond) {
        if (bitsPerSecond > 0) {
            pendingBitrate.set(bitsPerSecond);
        }
    }

    private void applyPendingParameters() {
        int bitrate = pendingBitrate.getAndSet(0);
        if (bitrate > 0) {
            try {
                Bundle parameters = new Bundle();
                parameters.putInt(MediaCodec.PARAMETER_KEY_VIDEO_BITRATE, bitrate);
                codec.setParameters(parameters);
                Ln.i("Encoder bitrate changed to " + (bitrate / 1_000_000.0) + " Mbit/s");
            } catch (Exception e) {
                Ln.w("Could not change the encoder bitrate", e);
            }
        }

        if (keyFrameRequested.getAndSet(false)) {
            try {
                Bundle parameters = new Bundle();
                parameters.putInt(MediaCodec.PARAMETER_KEY_REQUEST_SYNC_FRAME, 0);
                codec.setParameters(parameters);
                Ln.v("Key frame requested");
            } catch (Exception e) {
                Ln.w("Could not request a key frame", e);
            }
        }
    }

    /**
     * Pumps encoded frames to {@code sink} until the encoder reports end of stream or
     * {@code running} goes false.
     *
     * @return the reason the loop ended, for the caller's log
     */
    public String pump(PacketSink sink, AtomicBoolean running) throws IOException {
        MediaCodec.BufferInfo bufferInfo = new MediaCodec.BufferInfo();
        long lastActivityMs = SystemClock.uptimeMillis();

        while (running.get()) {
            applyPendingParameters();

            int index;
            try {
                index = codec.dequeueOutputBuffer(bufferInfo, DEQUEUE_TIMEOUT_US);
            } catch (IllegalStateException e) {
                throw new IOException("The encoder stopped unexpectedly: " + Ln.describe(e), e);
            }

            if (index == MediaCodec.INFO_TRY_AGAIN_LATER) {
                // A DeX desktop with nothing moving produces no frames at all. A heartbeat lets the
                // host tell an idle desktop apart from a stalled pipeline.
                long now = SystemClock.uptimeMillis();
                if (now - lastActivityMs >= 1000) {
                    lastActivityMs = now;
                    sink.writeHeartbeat(SystemClock.elapsedRealtimeNanos() / 1000);
                }
                continue;
            }

            if (index == MediaCodec.INFO_OUTPUT_FORMAT_CHANGED) {
                Ln.v("Encoder output format: " + codec.getOutputFormat());
                continue;
            }

            if (index < 0) {
                continue;
            }

            try {
                ByteBuffer buffer = codec.getOutputBuffer(index);
                if (buffer != null && bufferInfo.size > 0) {
                    buffer.position(bufferInfo.offset);
                    buffer.limit(bufferInfo.offset + bufferInfo.size);

                    boolean isConfig =
                            (bufferInfo.flags & MediaCodec.BUFFER_FLAG_CODEC_CONFIG) != 0;
                    boolean isKeyFrame =
                            (bufferInfo.flags & MediaCodec.BUFFER_FLAG_KEY_FRAME) != 0;

                    sink.writeFrame(buffer, bufferInfo.presentationTimeUs, isConfig, isKeyFrame);
                    lastActivityMs = SystemClock.uptimeMillis();
                }

                if ((bufferInfo.flags & MediaCodec.BUFFER_FLAG_END_OF_STREAM) != 0) {
                    return "the encoder signalled end of stream";
                }
            } finally {
                codec.releaseOutputBuffer(index, false);
            }
        }

        return "the agent was asked to stop";
    }

    @Override
    public void close() {
        try {
            codec.stop();
        } catch (Exception e) {
            Ln.v("Encoder stop failed: " + Ln.describe(e));
        }

        try {
            codec.release();
        } catch (Exception e) {
            Ln.v("Encoder release failed: " + Ln.describe(e));
        }

        if (inputSurface != null) {
            inputSurface.release();
        }
    }
}
