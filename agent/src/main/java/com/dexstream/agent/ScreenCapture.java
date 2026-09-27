package com.dexstream.agent;

import android.graphics.Rect;
import android.hardware.display.VirtualDisplay;
import android.os.IBinder;
import android.view.Surface;

import java.lang.reflect.Constructor;
import java.lang.reflect.Method;

/**
 * Feeds a display's contents into an encoder input surface.
 *
 * <p>Three strategies are attempted in order, because the framework API for this has changed twice
 * and the agent has to run on whatever the phone happens to be:</p>
 *
 * <ol>
 *   <li>{@link Strategy#SURFACE_CONTROL} — ask SurfaceFlinger for a display, point it at our surface
 *       and attach it to the target display's layer stack. Available up to Android 13 and the
 *       cheapest of the three, because composition happens once for both the panel and us.</li>
 *   <li>{@link Strategy#MIRRORING_VIRTUAL_DISPLAY} — a DisplayManager virtual display configured
 *       with {@code setDisplayIdToMirror}. This is the Android 14+ replacement; the builder method
 *       is hidden, so it is reached reflectively.</li>
 *   <li>{@link Strategy#NEW_VIRTUAL_DISPLAY} — a plain public-API virtual display. This does not
 *       mirror anything: it is a <em>new</em> display, which is what the agent wants when asked to
 *       create a desktop for DeX rather than to capture an existing one.</li>
 * </ol>
 *
 * <p>Which strategy succeeded is reported through {@link #getStrategy()} and logged, so a support
 * report says what actually happened on that device instead of leaving it to guesswork.</p>
 */
public final class ScreenCapture implements AutoCloseable {

    public enum Strategy {
        SURFACE_CONTROL,
        MIRRORING_VIRTUAL_DISPLAY,
        NEW_VIRTUAL_DISPLAY,
    }

    /** Virtual display flags from android.hardware.display.DisplayManager. */
    private static final int VIRTUAL_DISPLAY_FLAG_PUBLIC = 1 << 0;
    private static final int VIRTUAL_DISPLAY_FLAG_PRESENTATION = 1 << 1;
    private static final int VIRTUAL_DISPLAY_FLAG_SECURE = 1 << 2;
    private static final int VIRTUAL_DISPLAY_FLAG_OWN_CONTENT_ONLY = 1 << 3;
    private static final int VIRTUAL_DISPLAY_FLAG_AUTO_MIRROR = 1 << 4;
    private static final int VIRTUAL_DISPLAY_FLAG_SUPPORTS_TOUCH = 1 << 6;
    private static final int VIRTUAL_DISPLAY_FLAG_TRUSTED = 1 << 10;

    private final Strategy strategy;
    private final IBinder surfaceControlToken;
    private final VirtualDisplay virtualDisplay;
    private final int captureWidth;
    private final int captureHeight;
    private final int createdDisplayId;

    private ScreenCapture(
            Strategy strategy,
            IBinder surfaceControlToken,
            VirtualDisplay virtualDisplay,
            int captureWidth,
            int captureHeight,
            int createdDisplayId) {
        this.strategy = strategy;
        this.surfaceControlToken = surfaceControlToken;
        this.virtualDisplay = virtualDisplay;
        this.captureWidth = captureWidth;
        this.captureHeight = captureHeight;
        this.createdDisplayId = createdDisplayId;
    }

    public Strategy getStrategy() {
        return strategy;
    }

    public int getCaptureWidth() {
        return captureWidth;
    }

    public int getCaptureHeight() {
        return captureHeight;
    }

    /**
     * The id of the display the agent created, or -1 when it is mirroring an existing one. The host
     * needs this to route input to the right display.
     */
    public int getCreatedDisplayId() {
        return createdDisplayId;
    }

    /**
     * Starts mirroring {@code source} into {@code target}.
     *
     * @param source the display to capture
     * @param target the encoder's input surface
     * @param width  encoded frame width, already rounded to the encoder's alignment
     * @param height encoded frame height
     */
    public static ScreenCapture mirror(DisplayBridge.Info source, Surface target, int width, int height)
            throws Exception {
        Exception surfaceControlFailure = null;

        if (SurfaceControlBridge.isDisplayCreationAvailable()) {
            try {
                return mirrorWithSurfaceControl(source, target, width, height);
            } catch (Exception e) {
                surfaceControlFailure = e;
                Ln.w("SurfaceControl mirroring failed; trying a mirroring virtual display", e);
            }
        } else {
            Ln.i("SurfaceControl.createDisplay is not available on this build (expected on Android 14+).");
        }

        try {
            return mirrorWithVirtualDisplay(source, target, width, height);
        } catch (Exception e) {
            if (surfaceControlFailure != null) {
                e.addSuppressed(surfaceControlFailure);
            }
            throw new UnsupportedOperationException(
                    "Could not capture display " + source.displayId + ". Neither SurfaceControl "
                            + "mirroring nor a mirroring virtual display is available to the shell user "
                            + "on this build. See docs/TROUBLESHOOTING.md. Last error: "
                            + Ln.describe(e), e);
        }
    }

    private static ScreenCapture mirrorWithSurfaceControl(
            DisplayBridge.Info source, Surface target, int width, int height) throws Exception {
        IBinder token = SurfaceControlBridge.createDisplay(Protocol.AGENT_DISPLAY_NAME, false);

        try {
            // The projection source is the display's own logical bounds; swapped when the display is
            // rotated 90 or 270 degrees, because logicalWidth/Height already follow the rotation but
            // the layer stack's content does not.
            Rect sourceRect = new Rect(0, 0, source.logicalWidth, source.logicalHeight);
            Rect destinationRect = new Rect(0, 0, width, height);

            SurfaceControlBridge.configureDisplay(
                    token, target, source.layerStack, sourceRect, destinationRect, 0);

            Ln.i("Capturing display " + source.displayId + " via SurfaceControl (layerStack "
                    + source.layerStack + ") at " + width + "x" + height);
            return new ScreenCapture(Strategy.SURFACE_CONTROL, token, null, width, height, -1);
        } catch (Exception e) {
            SurfaceControlBridge.destroyDisplay(token);
            throw e;
        }
    }

    /**
     * Creates a virtual display that mirrors {@code source}.
     *
     * <p>{@code VirtualDisplayConfig.Builder.setDisplayIdToMirror} and the
     * {@code createVirtualDisplay(VirtualDisplayConfig)} overload are both hidden, so the whole
     * chain is built reflectively.
     */
    private static ScreenCapture mirrorWithVirtualDisplay(
            DisplayBridge.Info source, Surface target, int width, int height) throws Exception {
        Class<?> configClass = Reflect.findClass("android.hardware.display.VirtualDisplayConfig");
        Class<?> builderClass = Reflect.findClass("android.hardware.display.VirtualDisplayConfig$Builder");

        if (configClass == null || builderClass == null) {
            throw new UnsupportedOperationException(
                    "android.hardware.display.VirtualDisplayConfig is not available on this build");
        }

        Constructor<?> builderConstructor =
                builderClass.getConstructor(String.class, int.class, int.class, int.class);
        builderConstructor.setAccessible(true);
        Object builder = builderConstructor.newInstance(
                Protocol.AGENT_DISPLAY_NAME, width, height, Math.max(source.densityDpi, 160));

        Method setFlags = Reflect.findMethod(builderClass, "setFlags", new Class<?>[]{int.class});
        Method setSurface = Reflect.findMethod(builderClass, "setSurface", new Class<?>[]{Surface.class});
        Method setDisplayIdToMirror =
                Reflect.findMethod(builderClass, "setDisplayIdToMirror", new Class<?>[]{int.class});
        Method build = Reflect.findMethod(builderClass, "build", new Class<?>[0]);

        if (setDisplayIdToMirror == null) {
            throw new UnsupportedOperationException(
                    "VirtualDisplayConfig.Builder.setDisplayIdToMirror is not available, so this build "
                            + "cannot mirror an existing display from the shell user");
        }

        int flags = VIRTUAL_DISPLAY_FLAG_PUBLIC
                | VIRTUAL_DISPLAY_FLAG_AUTO_MIRROR
                | VIRTUAL_DISPLAY_FLAG_TRUSTED;
        Reflect.invoke(setFlags, builder, flags);
        Reflect.invoke(setSurface, builder, target);
        Reflect.invoke(setDisplayIdToMirror, builder, source.displayId);

        Object config = Reflect.invoke(build, builder);
        VirtualDisplay display = createVirtualDisplay(config);

        Ln.i("Capturing display " + source.displayId
                + " via a mirroring virtual display at " + width + "x" + height);
        return new ScreenCapture(
                Strategy.MIRRORING_VIRTUAL_DISPLAY, null, display, width, height, displayIdOf(display));
    }

    /**
     * Creates a brand new display rather than mirroring one, for the case where DeX is not running
     * and the user asked DexStream to provide a desktop of its own.
     */
    public static ScreenCapture createNewDisplay(
            Surface target, int width, int height, int densityDpi) throws Exception {
        Class<?> managerClass = Reflect.findClass("android.hardware.display.DisplayManagerGlobal");
        if (managerClass == null) {
            throw new UnsupportedOperationException("DisplayManagerGlobal is not available");
        }

        Class<?> configClass = Reflect.findClass("android.hardware.display.VirtualDisplayConfig");
        Class<?> builderClass = Reflect.findClass("android.hardware.display.VirtualDisplayConfig$Builder");

        int flags = VIRTUAL_DISPLAY_FLAG_PUBLIC
                | VIRTUAL_DISPLAY_FLAG_PRESENTATION
                | VIRTUAL_DISPLAY_FLAG_OWN_CONTENT_ONLY
                | VIRTUAL_DISPLAY_FLAG_SUPPORTS_TOUCH
                | VIRTUAL_DISPLAY_FLAG_TRUSTED;

        if (configClass != null && builderClass != null) {
            Constructor<?> builderConstructor =
                    builderClass.getConstructor(String.class, int.class, int.class, int.class);
            builderConstructor.setAccessible(true);
            Object builder = builderConstructor.newInstance(
                    Protocol.AGENT_DISPLAY_NAME, width, height, densityDpi);

            Reflect.invoke(
                    Reflect.findMethod(builderClass, "setFlags", new Class<?>[]{int.class}), builder, flags);
            Reflect.invoke(
                    Reflect.findMethod(builderClass, "setSurface", new Class<?>[]{Surface.class}),
                    builder,
                    target);

            Object config = Reflect.invoke(
                    Reflect.findMethod(builderClass, "build", new Class<?>[0]), builder);
            VirtualDisplay display = createVirtualDisplay(config);

            Ln.i("Created a new " + width + "x" + height + " display for DeX-style desktop use");
            return new ScreenCapture(
                    Strategy.NEW_VIRTUAL_DISPLAY, null, display, width, height, displayIdOf(display));
        }

        throw new UnsupportedOperationException(
                "This build does not expose VirtualDisplayConfig, so DexStream cannot create a display");
    }

    private static VirtualDisplay createVirtualDisplay(Object config) throws Exception {
        Class<?> globalClass = Reflect.findClass("android.hardware.display.DisplayManagerGlobal");
        Method getInstance = Reflect.findMethod(globalClass, "getInstance", new Class<?>[0]);
        Object global = Reflect.invoke(getInstance, null);

        // The DisplayManagerGlobal overload takes (Context, MediaProjection, VirtualDisplayConfig,
        // VirtualDisplay.Callback, Executor) on recent releases and slightly different shapes on
        // older ones, so it is matched by name and arity and filled with nulls where the agent has
        // nothing meaningful to pass.
        for (Method method : globalClass.getDeclaredMethods()) {
            if (!method.getName().equals("createVirtualDisplay")) {
                continue;
            }

            Class<?>[] parameters = method.getParameterTypes();
            Object[] arguments = new Object[parameters.length];
            boolean configPlaced = false;

            for (int i = 0; i < parameters.length; i++) {
                if (parameters[i].isInstance(config)) {
                    arguments[i] = config;
                    configPlaced = true;
                } else {
                    arguments[i] = null;
                }
            }

            if (!configPlaced) {
                continue;
            }

            method.setAccessible(true);
            Object result = Reflect.invoke(method, global, arguments);
            if (result instanceof VirtualDisplay) {
                return (VirtualDisplay) result;
            }
        }

        throw new UnsupportedOperationException(
                "No usable DisplayManagerGlobal.createVirtualDisplay overload was found");
    }

    private static int displayIdOf(VirtualDisplay display) {
        try {
            if (display != null && display.getDisplay() != null) {
                return display.getDisplay().getDisplayId();
            }
        } catch (Exception e) {
            Ln.v("Could not read the created display's id: " + Ln.describe(e));
        }

        return -1;
    }

    /**
     * Re-points the capture at a new encoder surface, which is needed after the encoder is recreated
     * following a resolution change. Only the SurfaceControl path can do this in place.
     */
    public void setSurface(DisplayBridge.Info source, Surface surface) throws Exception {
        if (strategy == Strategy.SURFACE_CONTROL) {
            SurfaceControlBridge.configureDisplay(
                    surfaceControlToken,
                    surface,
                    source.layerStack,
                    new Rect(0, 0, source.logicalWidth, source.logicalHeight),
                    new Rect(0, 0, captureWidth, captureHeight),
                    0);
            return;
        }

        if (virtualDisplay != null) {
            virtualDisplay.setSurface(surface);
            return;
        }

        throw new UnsupportedOperationException("This capture strategy cannot change its surface");
    }

    @Override
    public void close() {
        if (surfaceControlToken != null) {
            SurfaceControlBridge.destroyDisplay(surfaceControlToken);
        }

        if (virtualDisplay != null) {
            try {
                virtualDisplay.release();
            } catch (Exception e) {
                Ln.w("Could not release the virtual display", e);
            }
        }
    }
}
