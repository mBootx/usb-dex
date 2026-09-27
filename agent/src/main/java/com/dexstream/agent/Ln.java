package com.dexstream.agent;

import android.util.Log;

/**
 * Logging for a process started through {@code app_process}.
 *
 * <p>Output goes to stderr as well as logcat. The host reads the agent's stderr from the ADB shell
 * stream it used to launch the agent, which means a crash or a capture failure shows up in the
 * desktop app's diagnostics pane instead of being invisible on the phone.
 */
public final class Ln {

    private static final String TAG = "DexStream";

    private static boolean verbose;

    private Ln() {
    }

    public static void setVerbose(boolean value) {
        verbose = value;
    }

    public static boolean isVerbose() {
        return verbose;
    }

    public static void v(String message) {
        if (verbose) {
            Log.v(TAG, message);
            System.err.println("[V] " + message);
        }
    }

    public static void i(String message) {
        Log.i(TAG, message);
        System.err.println("[I] " + message);
    }

    public static void w(String message) {
        Log.w(TAG, message);
        System.err.println("[W] " + message);
    }

    public static void w(String message, Throwable error) {
        Log.w(TAG, message, error);
        System.err.println("[W] " + message + ": " + describe(error));
    }

    public static void e(String message, Throwable error) {
        Log.e(TAG, message, error);
        System.err.println("[E] " + message + ": " + describe(error));
        if (verbose && error != null) {
            error.printStackTrace(System.err);
        }
    }

    /** Renders a throwable including its cause chain, which reflection failures always have. */
    public static String describe(Throwable error) {
        if (error == null) {
            return "(no exception)";
        }

        StringBuilder text = new StringBuilder();
        Throwable current = error;
        int depth = 0;
        while (current != null && depth < 6) {
            if (depth > 0) {
                text.append(" <- ");
            }
            text.append(current.getClass().getSimpleName());
            if (current.getMessage() != null) {
                text.append('(').append(current.getMessage()).append(')');
            }
            current = current.getCause();
            depth++;
        }

        return text.toString();
    }
}
