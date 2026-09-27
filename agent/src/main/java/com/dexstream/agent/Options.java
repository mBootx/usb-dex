package com.dexstream.agent;

/**
 * Command-line options, passed by the host on the {@code app_process} command line as
 * {@code key=value} pairs.
 */
public final class Options {

    /** Logical display id to capture; -1 asks the agent to choose. */
    public int displayId = -1;

    /** Longest edge of the encoded frame in pixels; 0 keeps the display's native size. */
    public int maxSize;

    /** Target encoder bitrate in bits per second. */
    public int bitrate = 20_000_000;

    /** Requested frame rate in Hz. */
    public int maxFps = 60;

    /** Codec four-character code from {@link Protocol}. */
    public int codec = Protocol.CODEC_H264;

    /** Turn the phone's own panel off while streaming. */
    public boolean turnScreenOff;

    /** Create a desktop-mode display when no DeX display is present. */
    public boolean createDesktopDisplay;

    /** Width for a display the agent creates itself. */
    public int desktopWidth = 2560;

    /** Height for a display the agent creates itself. */
    public int desktopHeight = 1440;

    /** Density in dpi for a display the agent creates itself. */
    public int desktopDensity = 240;

    public boolean verbose;

    /**
     * Parses {@code key=value} arguments, ignoring anything unrecognised so that a newer host can
     * talk to an older agent without the agent refusing to start.
     */
    public static Options parse(String[] arguments) {
        Options options = new Options();

        for (String argument : arguments) {
            int separator = argument.indexOf('=');
            if (separator <= 0) {
                Ln.w("Ignoring malformed argument: " + argument);
                continue;
            }

            String key = argument.substring(0, separator);
            String value = argument.substring(separator + 1);

            try {
                switch (key) {
                    case "display_id":
                        options.displayId = Integer.parseInt(value);
                        break;
                    case "max_size":
                        options.maxSize = Integer.parseInt(value);
                        break;
                    case "bitrate":
                        options.bitrate = Integer.parseInt(value);
                        break;
                    case "max_fps":
                        options.maxFps = Integer.parseInt(value);
                        break;
                    case "codec":
                        options.codec = parseCodec(value);
                        break;
                    case "turn_screen_off":
                        options.turnScreenOff = Boolean.parseBoolean(value);
                        break;
                    case "create_desktop_display":
                        options.createDesktopDisplay = Boolean.parseBoolean(value);
                        break;
                    case "desktop_width":
                        options.desktopWidth = Integer.parseInt(value);
                        break;
                    case "desktop_height":
                        options.desktopHeight = Integer.parseInt(value);
                        break;
                    case "desktop_density":
                        options.desktopDensity = Integer.parseInt(value);
                        break;
                    case "verbose":
                        options.verbose = Boolean.parseBoolean(value);
                        break;
                    default:
                        Ln.v("Ignoring unknown option: " + key);
                        break;
                }
            } catch (NumberFormatException e) {
                Ln.w("Ignoring non-numeric value for " + key + ": " + value);
            }
        }

        return options;
    }

    private static int parseCodec(String value) {
        switch (value) {
            case "h265":
            case "hevc":
                return Protocol.CODEC_H265;
            case "av1":
            case "av01":
                return Protocol.CODEC_AV1;
            default:
                return Protocol.CODEC_H264;
        }
    }

    /** The MIME type MediaCodec needs for the selected codec. */
    public String mimeType() {
        switch (codec) {
            case Protocol.CODEC_H265:
                return "video/hevc";
            case Protocol.CODEC_AV1:
                return "video/av01";
            default:
                return "video/avc";
        }
    }

    @Override
    public String toString() {
        return "display_id=" + displayId
                + " max_size=" + maxSize
                + " bitrate=" + bitrate
                + " max_fps=" + maxFps
                + " codec=" + mimeType()
                + " turn_screen_off=" + turnScreenOff
                + " create_desktop_display=" + createDesktopDisplay;
    }
}
