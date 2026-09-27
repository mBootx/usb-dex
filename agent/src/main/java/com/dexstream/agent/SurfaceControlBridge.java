package com.dexstream.agent;

import android.graphics.Rect;
import android.os.IBinder;
import android.view.Surface;

import java.lang.reflect.Method;

/**
 * Reflective access to {@code android.view.SurfaceControl}.
 *
 * <p>Up to and including Android 13, the cheapest way to mirror a display is to ask SurfaceFlinger
 * for a virtual display, point it at an encoder input surface and attach it to an existing display's
 * layer stack. Android 14 removed {@code createDisplay} from SurfaceControl, so
 * {@link #isDisplayCreationAvailable()} reports whether this path exists and
 * {@link ScreenCapture} falls back to a DisplayManager virtual display when it does not.
 */
public final class SurfaceControlBridge {

    private static Class<?> surfaceControlClass;
    private static Method createDisplay;
    private static Method destroyDisplay;
    private static Method setDisplaySurface;
    private static Method setDisplayProjection;
    private static Method setDisplayLayerStack;
    private static Method openTransaction;
    private static Method closeTransaction;

    private static boolean initialised;

    private SurfaceControlBridge() {
    }

    private static synchronized void ensureInitialised() {
        if (initialised) {
            return;
        }

        initialised = true;
        surfaceControlClass = Reflect.findClass("android.view.SurfaceControl");
        if (surfaceControlClass == null) {
            return;
        }

        createDisplay = Reflect.findMethod(
                surfaceControlClass, "createDisplay", new Class<?>[]{String.class, boolean.class});
        destroyDisplay = Reflect.findMethod(
                surfaceControlClass, "destroyDisplay", new Class<?>[]{IBinder.class});
        setDisplaySurface = Reflect.findMethod(
                surfaceControlClass, "setDisplaySurface", new Class<?>[]{IBinder.class, Surface.class});
        setDisplayProjection = Reflect.findMethod(
                surfaceControlClass,
                "setDisplayProjection",
                new Class<?>[]{IBinder.class, int.class, Rect.class, Rect.class});
        setDisplayLayerStack = Reflect.findMethod(
                surfaceControlClass, "setDisplayLayerStack", new Class<?>[]{IBinder.class, int.class});
        openTransaction = Reflect.findMethod(surfaceControlClass, "openTransaction", new Class<?>[0]);
        closeTransaction = Reflect.findMethod(surfaceControlClass, "closeTransaction", new Class<?>[0]);
    }

    /** True when this build still exposes the SurfaceControl display-mirroring path. */
    public static boolean isDisplayCreationAvailable() {
        ensureInitialised();
        return createDisplay != null
                && setDisplaySurface != null
                && setDisplayProjection != null
                && setDisplayLayerStack != null;
    }

    public static IBinder createDisplay(String name, boolean secure) throws Exception {
        ensureInitialised();
        Object token = Reflect.invoke(createDisplay, null, name, secure);
        if (!(token instanceof IBinder)) {
            throw new UnsupportedOperationException("SurfaceControl.createDisplay returned no token");
        }
        return (IBinder) token;
    }

    public static void destroyDisplay(IBinder token) {
        ensureInitialised();
        if (destroyDisplay == null || token == null) {
            return;
        }

        try {
            Reflect.invoke(destroyDisplay, null, token);
        } catch (Exception e) {
            Ln.w("Could not destroy the capture display", e);
        }
    }

    /**
     * Binds {@code token} to {@code surface} and projects {@code source} from the display that owns
     * {@code layerStack} into {@code destination}, inside a single SurfaceFlinger transaction so the
     * display never exists in a half-configured state.
     */
    public static void configureDisplay(
            IBinder token,
            Surface surface,
            int layerStack,
            Rect source,
            Rect destination,
            int orientation) throws Exception {
        ensureInitialised();

        boolean opened = false;
        try {
            if (openTransaction != null) {
                Reflect.invoke(openTransaction, null);
                opened = true;
            }

            Reflect.invoke(setDisplaySurface, null, token, surface);
            Reflect.invoke(setDisplayProjection, null, token, orientation, source, destination);
            Reflect.invoke(setDisplayLayerStack, null, token, layerStack);
        } finally {
            if (opened) {
                try {
                    Reflect.invoke(closeTransaction, null);
                } catch (Exception e) {
                    Ln.w("Could not close the SurfaceFlinger transaction", e);
                }
            }
        }
    }
}
