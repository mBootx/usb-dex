package com.dexstream.agent;

import java.io.DataOutputStream;
import java.io.IOException;
import java.io.OutputStream;
import java.nio.ByteBuffer;
import java.nio.charset.StandardCharsets;

/**
 * Writes the DexStream video channel: the fixed stream header followed by framed packets.
 *
 * <p>All writes go through one synchronised object because the encoder thread writes frames while
 * the control thread can write clipboard and log packets on the same socket.
 */
public final class PacketSink {

    private final DataOutputStream output;
    private final byte[] packetHeader = new byte[Protocol.PACKET_HEADER_SIZE];
    private byte[] copyBuffer = new byte[256 * 1024];

    public PacketSink(OutputStream stream) {
        this.output = new DataOutputStream(stream);
    }

    /** Writes the 92-byte stream header. Must be the first thing on the channel. */
    public synchronized void writeStreamHeader(
            String deviceName, int codec, int width, int height, int refreshRateMilliHz, int displayId)
            throws IOException {
        byte[] header = new byte[Protocol.STREAM_HEADER_SIZE];
        int offset = 0;

        offset = putInt(header, offset, Protocol.VIDEO_MAGIC);
        offset = putShort(header, offset, Protocol.VERSION);
        offset = putShort(header, offset, (short) 0);

        byte[] name = truncateUtf8(deviceName, Protocol.DEVICE_NAME_SIZE - 1);
        System.arraycopy(name, 0, header, offset, name.length);
        offset += Protocol.DEVICE_NAME_SIZE;

        offset = putInt(header, offset, codec);
        offset = putInt(header, offset, width);
        offset = putInt(header, offset, height);
        offset = putInt(header, offset, refreshRateMilliHz);
        putInt(header, offset, displayId);

        output.write(header);
        output.flush();
    }

    /** Writes one encoded access unit or codec-configuration blob. */
    public synchronized void writeFrame(
            ByteBuffer payload, long presentationTimeUs, boolean isConfig, boolean isKeyFrame)
            throws IOException {
        int length = payload.remaining();
        byte type = isConfig ? Protocol.PACKET_CONFIG : Protocol.PACKET_FRAME;
        byte flags = 0;
        if (isConfig) {
            flags |= Protocol.FLAG_CODEC_CONFIG;
        }
        if (isKeyFrame) {
            flags |= Protocol.FLAG_KEY_FRAME;
        }

        writeHeader(type, flags, length, presentationTimeUs);

        if (payload.hasArray()) {
            output.write(payload.array(), payload.arrayOffset() + payload.position(), length);
            payload.position(payload.position() + length);
        } else {
            if (copyBuffer.length < length) {
                copyBuffer = new byte[length];
            }
            payload.get(copyBuffer, 0, length);
            output.write(copyBuffer, 0, length);
        }

        output.flush();
    }

    /** Writes a zero-length keep-alive so the host can distinguish idle from stalled. */
    public synchronized void writeHeartbeat(long timestampUs) throws IOException {
        writeHeader(Protocol.PACKET_HEARTBEAT, (byte) 0, 0, timestampUs);
        output.flush();
    }

    /** Reports that the capture geometry changed; the body is a fresh stream header. */
    public synchronized void writeDisplayChanged(
            String deviceName, int codec, int width, int height, int refreshRateMilliHz, int displayId,
            long timestampUs) throws IOException {
        byte[] body = new byte[Protocol.STREAM_HEADER_SIZE];
        int offset = 0;
        offset = putInt(body, offset, Protocol.VIDEO_MAGIC);
        offset = putShort(body, offset, Protocol.VERSION);
        offset = putShort(body, offset, (short) 0);
        byte[] name = truncateUtf8(deviceName, Protocol.DEVICE_NAME_SIZE - 1);
        System.arraycopy(name, 0, body, offset, name.length);
        offset += Protocol.DEVICE_NAME_SIZE;
        offset = putInt(body, offset, codec);
        offset = putInt(body, offset, width);
        offset = putInt(body, offset, height);
        offset = putInt(body, offset, refreshRateMilliHz);
        putInt(body, offset, displayId);

        writeHeader(Protocol.PACKET_DISPLAY_CHANGED, (byte) 0, body.length, timestampUs);
        output.write(body);
        output.flush();
    }

    /** Sends UTF-8 text copied on the device. */
    public synchronized void writeClipboard(String text, long timestampUs) throws IOException {
        byte[] body = text.getBytes(StandardCharsets.UTF_8);
        if (body.length > Protocol.MAX_TEXT_BYTES) {
            Ln.w("Clipboard text of " + body.length + " bytes is too large to forward");
            return;
        }

        writeHeader(Protocol.PACKET_CLIPBOARD, (byte) 0, body.length, timestampUs);
        output.write(body);
        output.flush();
    }

    private void writeHeader(byte type, byte flags, int length, long presentationTimeUs)
            throws IOException {
        packetHeader[0] = type;
        packetHeader[1] = flags;
        packetHeader[2] = 0;
        packetHeader[3] = 0;
        putInt(packetHeader, 4, length);
        putLong(packetHeader, 8, presentationTimeUs);
        output.write(packetHeader);
    }

    private static int putShort(byte[] buffer, int offset, short value) {
        buffer[offset] = (byte) (value >>> 8);
        buffer[offset + 1] = (byte) value;
        return offset + 2;
    }

    private static int putInt(byte[] buffer, int offset, int value) {
        buffer[offset] = (byte) (value >>> 24);
        buffer[offset + 1] = (byte) (value >>> 16);
        buffer[offset + 2] = (byte) (value >>> 8);
        buffer[offset + 3] = (byte) value;
        return offset + 4;
    }

    private static int putLong(byte[] buffer, int offset, long value) {
        for (int i = 0; i < 8; i++) {
            buffer[offset + i] = (byte) (value >>> (56 - (8 * i)));
        }
        return offset + 8;
    }

    /**
     * Encodes {@code text} into at most {@code maxBytes} of UTF-8 without splitting a character.
     * Splitting one would put a replacement character on the wire and, worse, make the byte count
     * disagree with the field width.
     */
    static byte[] truncateUtf8(String text, int maxBytes) {
        if (text == null) {
            return new byte[0];
        }

        byte[] encoded = text.getBytes(StandardCharsets.UTF_8);
        if (encoded.length <= maxBytes) {
            return encoded;
        }

        int end = maxBytes;
        // Walk back off any continuation byte (10xxxxxx) so the result ends on a character boundary.
        while (end > 0 && (encoded[end] & 0xC0) == 0x80) {
            end--;
        }

        byte[] truncated = new byte[end];
        System.arraycopy(encoded, 0, truncated, 0, end);
        return truncated;
    }
}
