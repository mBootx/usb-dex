package com.dexstream.agent;

/**
 * The DexStream wire protocol constants, kept byte-for-byte in step with
 * {@code DexStream.Core/Protocol/DexProtocol.cs} on the host.
 *
 * <p>Every multi-byte field is big-endian, which is what {@link java.io.DataOutputStream} and
 * {@link java.io.DataInputStream} write and read natively, so the agent needs no byte swapping.
 */
public final class Protocol {

    /** ASCII {@code DEXS}, the first four bytes of the video channel. */
    public static final int VIDEO_MAGIC = 0x44455853;

    /** ASCII {@code DEXC}, the first four bytes of the control channel. */
    public static final int CONTROL_MAGIC = 0x44455843;

    /** Bumped whenever the framing changes; the host refuses a mismatch. */
    public static final short VERSION = 1;

    public static final int STREAM_HEADER_SIZE = 92;
    public static final int DEVICE_NAME_SIZE = 64;
    public static final int PACKET_HEADER_SIZE = 16;

    /** Abstract socket the agent listens on; the host connects with {@code localabstract:}. */
    public static final String SOCKET_NAME = "dexstream";

    /** Name given to displays the agent creates so the host can recognise them in dumpsys. */
    public static final String AGENT_DISPLAY_NAME = "DexStream Desktop";

    // Codec four-character codes.
    public static final int CODEC_H264 = 0x68323634; // "h264"
    public static final int CODEC_H265 = 0x68323635; // "h265"
    public static final int CODEC_AV1 = 0x61763031;  // "av01"

    // Video packet types.
    public static final byte PACKET_CONFIG = 1;
    public static final byte PACKET_FRAME = 2;
    public static final byte PACKET_DISPLAY_CHANGED = 3;
    public static final byte PACKET_HEARTBEAT = 4;
    public static final byte PACKET_CLIPBOARD = 5;
    public static final byte PACKET_LOG = 6;

    // Video packet flags.
    public static final byte FLAG_KEY_FRAME = 1;
    public static final byte FLAG_CODEC_CONFIG = 2;
    public static final byte FLAG_END_OF_STREAM = 4;

    // Control message types (host to device).
    public static final int CONTROL_KEY_EVENT = 1;
    public static final int CONTROL_TEXT = 2;
    public static final int CONTROL_POINTER_EVENT = 3;
    public static final int CONTROL_SCROLL = 4;
    public static final int CONTROL_REQUEST_KEY_FRAME = 5;
    public static final int CONTROL_SET_BITRATE = 6;
    public static final int CONTROL_SET_CLIPBOARD = 7;
    public static final int CONTROL_SET_DISPLAY_POWER = 8;
    public static final int CONTROL_PING = 9;
    public static final int CONTROL_SHUTDOWN = 10;
    public static final int CONTROL_SYSTEM_ACTION = 11;

    // Control replies (device to host).
    public static final byte REPLY_PONG = 1;
    public static final byte REPLY_ERROR = 2;

    // Pointer actions, mirroring MotionEvent's action constants that the host may send.
    public static final int POINTER_DOWN = 0;
    public static final int POINTER_UP = 1;
    public static final int POINTER_MOVE = 2;
    public static final int POINTER_CANCEL = 3;
    public static final int POINTER_HOVER_ENTER = 4;
    public static final int POINTER_HOVER_MOVE = 5;
    public static final int POINTER_HOVER_EXIT = 6;

    // System actions.
    public static final int ACTION_BACK = 1;
    public static final int ACTION_HOME = 2;
    public static final int ACTION_RECENTS = 3;
    public static final int ACTION_NOTIFICATIONS = 4;
    public static final int ACTION_TOGGLE_KEYBOARD = 5;

    /** Largest text or clipboard body the agent will accept, matching the host's limit. */
    public static final int MAX_TEXT_BYTES = 256 * 1024;

    private Protocol() {
    }
}
