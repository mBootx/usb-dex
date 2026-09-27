package com.dexstream.agent;

import android.net.LocalServerSocket;
import android.net.LocalSocket;
import android.os.SystemClock;

import java.io.IOException;
import java.util.concurrent.atomic.AtomicBoolean;

/**
 * The DexStream device agent.
 *
 * <p>Launched by the host with {@code app_process}, it listens on an abstract socket, accepts a video
 * connection and a control connection, captures a display, encodes it and injects the input the host
 * sends back. It runs as the shell user, so it has ADB's privileges and no more.</p>
 *
 * <p>Two sockets rather than the shell's own stdout because the legacy {@code shell:} service merges
 * stderr into the same stream: a single log line would corrupt the video bitstream. The shell stream
 * carries only the agent's diagnostics.</p>
 */
public final class Main {

    /** How often to check whether the captured display changed size or rotated. */
    private static final long GEOMETRY_POLL_MS = 500;

    private Main() {
    }

    public static void main(String[] args) {
        // The default handler prints to a logcat tag nobody is watching; route it to stderr so the
        // host sees the reason a session died.
        Thread.setDefaultUncaughtExceptionHandler(
                (thread, error) -> Ln.e("Agent thread " + thread.getName() + " failed", error));

        Options options = Options.parse(args);
        Ln.setVerbose(options.verbose);
        Ln.i("DexStream agent starting: " + options);

        Workarounds.apply();

        try {
            run(options);
            Ln.i("DexStream agent stopped.");
        } catch (Throwable error) {
            Ln.e("DexStream agent failed", error);
            // A non-zero exit code lets the host distinguish a crash from a clean stop.
            System.exit(1);
        }
    }

    private static void run(Options options) throws Exception {
        DexMode.State dexState = DexMode.query();
        Ln.i("Samsung DeX state: " + dexState);

        LocalServerSocket server = new LocalServerSocket(Protocol.SOCKET_NAME);
        Ln.i("Listening on localabstract:" + Protocol.SOCKET_NAME);

        LocalSocket video = null;
        LocalSocket control = null;
        try {
            // The host opens the video channel first and the control channel second; the order is how
            // the two connections are told apart, since an abstract socket carries no other identity.
            video = server.accept();
            control = server.accept();
            Ln.i("Host connected on both channels.");
            stream(options, video, control);
        } finally {
            closeQuietly(control);
            closeQuietly(video);
            closeQuietly(server);
        }
    }

    private static void stream(Options options, LocalSocket video, LocalSocket control)
            throws Exception {
        PacketSink sink = new PacketSink(video.getOutputStream());
        String deviceName = Workarounds.getDeviceName();
        AtomicBoolean sessionAlive = new AtomicBoolean(true);

        DisplayBridge.Info source = resolveSource(options);
        Ln.i("Capturing " + source);

        boolean screenTurnedOff = false;
        Thread controlThread = null;
        ControlChannel controlChannel = null;
        boolean headerWritten = false;

        try {
            // Each pass builds an encoder and a capture for the display's current geometry. A rotation
            // or a DeX resolution change ends the pass, the host is told, and the next pass rebuilds
            // at the new size.
            while (sessionAlive.get()) {
                try (VideoEncoder encoder =
                             VideoEncoder.create(options, source.logicalWidth, source.logicalHeight);
                     ScreenCapture capture = startCapture(options, source, encoder)) {

                    int inputDisplayId = capture.getCreatedDisplayId() >= 0
                            ? capture.getCreatedDisplayId()
                            : source.displayId;

                    if (capture.getCreatedDisplayId() >= 0) {
                        // A display the agent created has its own geometry; re-read it so input
                        // coordinates and the stream header describe the same thing.
                        source = DisplayBridge.getInfo(capture.getCreatedDisplayId());
                    }

                    int refreshMilliHz = Math.round(source.refreshRate * 1000);

                    if (!headerWritten) {
                        sink.writeStreamHeader(
                                deviceName,
                                options.codec,
                                encoder.getWidth(),
                                encoder.getHeight(),
                                refreshMilliHz,
                                inputDisplayId);
                        headerWritten = true;
                    } else {
                        sink.writeDisplayChanged(
                                deviceName,
                                options.codec,
                                encoder.getWidth(),
                                encoder.getHeight(),
                                refreshMilliHz,
                                inputDisplayId,
                                SystemClock.elapsedRealtimeNanos() / 1000);
                    }

                    if (controlThread == null) {
                        InputInjector injector = InputInjector.create(inputDisplayId);
                        controlChannel = new ControlChannel(
                                control.getInputStream(),
                                control.getOutputStream(),
                                injector,
                                encoder,
                                sink,
                                sessionAlive,
                                source.logicalWidth,
                                source.logicalHeight);
                        controlThread = new Thread(controlChannel, "dexstream-control");
                        controlThread.setDaemon(true);
                        controlThread.start();
                    } else {
                        controlChannel.setDisplaySize(source.logicalWidth, source.logicalHeight);
                    }

                    if (options.turnScreenOff && !screenTurnedOff) {
                        screenTurnedOff = SystemBridge.setBuiltInDisplayPower(false);
                    }

                    AtomicBoolean passAlive = new AtomicBoolean(true);
                    Thread watcher = startGeometryWatcher(source, sessionAlive, passAlive);

                    try {
                        String reason = encoder.pump(sink, passAlive);
                        Ln.i("Encoding pass ended because " + reason);
                    } finally {
                        passAlive.set(false);
                        watcher.interrupt();
                        watcher.join(1000);
                    }

                    if (!sessionAlive.get()) {
                        break;
                    }

                    // The pass ended but the session did not, so the geometry changed: re-read it.
                    DisplayBridge.Info updated = DisplayBridge.getInfo(source.displayId);
                    Ln.i("Display geometry changed to " + updated);
                    source = updated;
                }
            }
        } finally {
            if (screenTurnedOff) {
                SystemBridge.setBuiltInDisplayPower(true);
            }

            sessionAlive.set(false);
            if (controlThread != null) {
                controlThread.interrupt();
            }
        }
    }

    private static void closeQuietly(LocalSocket socket) {
        if (socket != null) {
            try {
                socket.close();
            } catch (IOException e) {
                Ln.v("Could not close a local socket: " + Ln.describe(e));
            }
        }
    }

    private static void closeQuietly(LocalServerSocket socket) {
        if (socket != null) {
            try {
                socket.close();
            } catch (IOException e) {
                Ln.v("Could not close the local server socket: " + Ln.describe(e));
            }
        }
    }

    /** Chooses which display to capture, honouring the option to create a desktop of our own. */
    private static DisplayBridge.Info resolveSource(Options options) throws Exception {
        DisplayBridge.Info selected = DisplayBridge.select(options.displayId);

        boolean isDexLike = selected.name.toLowerCase(java.util.Locale.ROOT).contains("dex")
                || selected.name.toLowerCase(java.util.Locale.ROOT).contains("desktop");

        if (options.displayId < 0 && !isDexLike && selected.displayId == 0 && options.createDesktopDisplay) {
            Ln.i("No DeX display found; DexStream will create a "
                    + options.desktopWidth + "x" + options.desktopHeight + " desktop display.");
        }

        return selected;
    }

    private static ScreenCapture startCapture(
            Options options, DisplayBridge.Info source, VideoEncoder encoder) throws Exception {
        boolean shouldCreateDesktop = options.createDesktopDisplay
                && options.displayId < 0
                && source.displayId == 0
                && !source.name.toLowerCase(java.util.Locale.ROOT).contains("dex");

        if (shouldCreateDesktop) {
            try {
                return ScreenCapture.createNewDisplay(
                        encoder.getInputSurface(),
                        encoder.getWidth(),
                        encoder.getHeight(),
                        options.desktopDensity);
            } catch (Exception e) {
                Ln.w("Could not create a desktop display; falling back to mirroring the phone screen", e);
            }
        }

        return ScreenCapture.mirror(
                source, encoder.getInputSurface(), encoder.getWidth(), encoder.getHeight());
    }

    /**
     * Ends the current encoding pass when the captured display changes size or rotates, so the
     * encoder can be rebuilt at the new geometry.
     */
    private static Thread startGeometryWatcher(
            DisplayBridge.Info source, AtomicBoolean sessionAlive, AtomicBoolean passAlive) {
        Thread watcher = new Thread(() -> {
            while (passAlive.get() && sessionAlive.get()) {
                try {
                    Thread.sleep(GEOMETRY_POLL_MS);
                } catch (InterruptedException e) {
                    return;
                }

                try {
                    DisplayBridge.Info current = DisplayBridge.getInfo(source.displayId);
                    if (current.logicalWidth != source.logicalWidth
                            || current.logicalHeight != source.logicalHeight
                            || current.rotation != source.rotation) {
                        Ln.i("Detected a display change: " + current);
                        passAlive.set(false);
                        return;
                    }
                } catch (Exception e) {
                    // The display disappeared: DeX was stopped, or the dock was unplugged.
                    Ln.i("The captured display is gone: " + Ln.describe(e));
                    sessionAlive.set(false);
                    passAlive.set(false);
                    return;
                }
            }
        }, "dexstream-geometry");

        watcher.setDaemon(true);
        watcher.start();
        return watcher;
    }
}
