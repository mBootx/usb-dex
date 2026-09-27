package com.dexstream.agent;

import android.os.SystemClock;
import android.view.KeyEvent;

import java.io.DataInputStream;
import java.io.DataOutputStream;
import java.io.EOFException;
import java.io.IOException;
import java.io.InputStream;
import java.io.OutputStream;
import java.nio.charset.StandardCharsets;
import java.util.concurrent.atomic.AtomicBoolean;

/**
 * Reads control messages from the host and applies them.
 *
 * <p>Runs on its own thread so that input latency does not depend on the encoder loop. Everything it
 * needs from the encoder ({@code requestKeyFrame}, {@code setBitrate}) is deferred through atomics
 * rather than called across threads, because MediaCodec's parameter calls are not safe to interleave
 * with a dequeue on another thread.</p>
 */
public final class ControlChannel implements Runnable {

    /** Android's {@code KeyEvent.KEYCODE_*} values for the navigation shortcuts. */
    private static final int KEYCODE_BACK = 4;
    private static final int KEYCODE_HOME = 3;
    private static final int KEYCODE_APP_SWITCH = 187;
    private static final int KEYCODE_NOTIFICATION = 83;

    private final DataInputStream input;
    private final DataOutputStream output;
    private final InputInjector injector;
    private final VideoEncoder encoder;
    private final PacketSink videoSink;
    private final AtomicBoolean running;

    /** The real size of the display being driven, used to rescale host coordinates. */
    private volatile int displayWidth;
    private volatile int displayHeight;

    public ControlChannel(
            InputStream input,
            OutputStream output,
            InputInjector injector,
            VideoEncoder encoder,
            PacketSink videoSink,
            AtomicBoolean running,
            int displayWidth,
            int displayHeight) {
        this.input = new DataInputStream(input);
        this.output = new DataOutputStream(output);
        this.injector = injector;
        this.encoder = encoder;
        this.videoSink = videoSink;
        this.running = running;
        this.displayWidth = displayWidth;
        this.displayHeight = displayHeight;
    }

    /** Updates the display size used for coordinate rescaling after a geometry change. */
    public void setDisplaySize(int width, int height) {
        this.displayWidth = width;
        this.displayHeight = height;
    }

    @Override
    public void run() {
        try {
            readHandshake();

            while (running.get()) {
                int type = input.read();
                if (type < 0) {
                    Ln.i("The host closed the control channel.");
                    break;
                }

                handle(type);
            }
        } catch (EOFException e) {
            Ln.i("The control channel ended.");
        } catch (IOException e) {
            if (running.get()) {
                Ln.w("Control channel error", e);
            }
        } catch (Exception e) {
            Ln.e("Unexpected control channel failure", e);
        } finally {
            running.set(false);
        }
    }

    private void readHandshake() throws IOException {
        int magic = input.readInt();
        if (magic != Protocol.CONTROL_MAGIC) {
            throw new IOException(
                    String.format("Bad control handshake magic 0x%08X; expected DEXC", magic));
        }

        int version = input.readUnsignedShort();
        input.readUnsignedShort(); // flags, reserved

        if (version != Protocol.VERSION) {
            throw new IOException(
                    "Host speaks control protocol version " + version
                            + " but this agent implements " + Protocol.VERSION);
        }

        Ln.v("Control channel handshake accepted.");
    }

    private void handle(int type) throws IOException {
        switch (type) {
            case Protocol.CONTROL_KEY_EVENT:
                handleKeyEvent();
                break;
            case Protocol.CONTROL_TEXT:
                handleText();
                break;
            case Protocol.CONTROL_POINTER_EVENT:
                handlePointerEvent();
                break;
            case Protocol.CONTROL_SCROLL:
                handleScroll();
                break;
            case Protocol.CONTROL_REQUEST_KEY_FRAME:
                encoder.requestKeyFrame();
                break;
            case Protocol.CONTROL_SET_BITRATE:
                encoder.setBitrate(input.readInt());
                break;
            case Protocol.CONTROL_SET_CLIPBOARD:
                handleSetClipboard();
                break;
            case Protocol.CONTROL_SET_DISPLAY_POWER:
                handleDisplayPower();
                break;
            case Protocol.CONTROL_PING:
                handlePing();
                break;
            case Protocol.CONTROL_SHUTDOWN:
                Ln.i("The host asked the agent to shut down.");
                running.set(false);
                break;
            case Protocol.CONTROL_SYSTEM_ACTION:
                handleSystemAction();
                break;
            default:
                // An unknown type means the stream is out of sync: the message length is unknown, so
                // there is no safe way to skip it.
                throw new IOException(
                        String.format("Unknown control message type 0x%02X; the channel is out of sync", type));
        }
    }

    private void handleKeyEvent() throws IOException {
        int action = input.readUnsignedByte();
        int keyCode = input.readInt();
        int repeat = input.readInt();
        int metaState = input.readInt();

        int motionAction = action == 1 ? KeyEvent.ACTION_UP : KeyEvent.ACTION_DOWN;
        injector.injectKey(motionAction, keyCode, repeat, metaState);
    }

    private void handleText() throws IOException {
        String text = readText();
        if (text != null) {
            injector.injectText(text);
        }
    }

    private void handlePointerEvent() throws IOException {
        int action = input.readUnsignedByte();
        input.readLong(); // pointer id; a single mouse pointer is all the host sends today
        int x = input.readInt();
        int y = input.readInt();
        int statedWidth = input.readUnsignedShort();
        int statedHeight = input.readUnsignedShort();
        float pressure = input.readUnsignedShort() / 65535f;
        int actionButton = input.readInt();
        int buttons = input.readInt();

        int[] scaled = rescale(x, y, statedWidth, statedHeight);
        injector.injectPointer(action, scaled[0], scaled[1], pressure, buttons, actionButton);
    }

    private void handleScroll() throws IOException {
        int x = input.readInt();
        int y = input.readInt();
        int statedWidth = input.readUnsignedShort();
        int statedHeight = input.readUnsignedShort();
        float horizontal = input.readShort() / 32767f;
        float vertical = input.readShort() / 32767f;
        int buttons = input.readInt();

        int[] scaled = rescale(x, y, statedWidth, statedHeight);
        injector.injectScroll(scaled[0], scaled[1], horizontal, vertical, buttons);
    }

    private void handleSetClipboard() throws IOException {
        input.readLong(); // sequence number, echoed by nothing today
        boolean paste = input.readUnsignedByte() != 0;
        String text = readText();

        if (text == null) {
            return;
        }

        if (!SystemBridge.setClipboard(text)) {
            sendError("The device clipboard could not be set on this build.");
            return;
        }

        if (paste) {
            // KEYCODE_PASTE is 279; sending it is more reliable than trying to find a focused view.
            injector.tapKey(279);
        }
    }

    private void handleDisplayPower() throws IOException {
        boolean on = input.readUnsignedByte() != 0;
        if (!SystemBridge.setBuiltInDisplayPower(on)) {
            sendError("The phone's screen power state could not be changed on this build.");
        }
    }

    private void handlePing() throws IOException {
        long sequence = input.readLong();
        long hostTimestampUs = input.readLong();

        // The device clock the host compares against is the same one MediaCodec stamps frames with,
        // so that a frame's presentation time and a pong are directly comparable.
        long deviceUs = SystemClock.elapsedRealtimeNanos() / 1000;

        synchronized (output) {
            output.writeByte(Protocol.REPLY_PONG);
            output.writeLong(sequence);
            output.writeLong(hostTimestampUs);
            output.writeLong(deviceUs);
            output.flush();
        }
    }

    private void handleSystemAction() throws IOException {
        int action = input.readUnsignedByte();
        switch (action) {
            case Protocol.ACTION_BACK:
                injector.tapKey(KEYCODE_BACK);
                break;
            case Protocol.ACTION_HOME:
                injector.tapKey(KEYCODE_HOME);
                break;
            case Protocol.ACTION_RECENTS:
                injector.tapKey(KEYCODE_APP_SWITCH);
                break;
            case Protocol.ACTION_NOTIFICATIONS:
                injector.tapKey(KEYCODE_NOTIFICATION);
                break;
            default:
                Ln.v("Ignoring unknown system action " + action);
                break;
        }
    }

    /**
     * Maps coordinates from the frame size the host is rendering to the display's real size.
     *
     * <p>The two differ whenever the encoded frame was scaled down, so the host reports the size it
     * used and the agent rescales. Without this, input would be offset by exactly the scale factor.
     */
    private int[] rescale(int x, int y, int statedWidth, int statedHeight) {
        int width = displayWidth;
        int height = displayHeight;

        if (statedWidth <= 0 || statedHeight <= 0 || width <= 0 || height <= 0) {
            return new int[]{x, y};
        }

        if (statedWidth == width && statedHeight == height) {
            return new int[]{x, y};
        }

        long scaledX = (long) x * width / statedWidth;
        long scaledY = (long) y * height / statedHeight;

        return new int[]{
                (int) Math.max(0, Math.min(width - 1, scaledX)),
                (int) Math.max(0, Math.min(height - 1, scaledY)),
        };
    }

    private String readText() throws IOException {
        int length = input.readInt();
        if (length < 0 || length > Protocol.MAX_TEXT_BYTES) {
            throw new IOException(
                    "Text message length " + length + " is outside the accepted range");
        }

        byte[] body = new byte[length];
        input.readFully(body);
        return new String(body, StandardCharsets.UTF_8);
    }

    /** Reports a problem back to the host so it can surface it rather than silently doing nothing. */
    private void sendError(String message) {
        try {
            byte[] body = message.getBytes(StandardCharsets.UTF_8);
            synchronized (output) {
                output.writeByte(Protocol.REPLY_ERROR);
                output.writeInt(body.length);
                output.write(body);
                output.flush();
            }
        } catch (IOException e) {
            Ln.v("Could not report an error to the host: " + Ln.describe(e));
        }
    }
}
