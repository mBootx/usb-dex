package com.dexstream.agent;

import android.os.IBinder;

import java.lang.reflect.Method;

/**
 * Optional device features: turning the phone's panel off while streaming, and the clipboard.
 *
 * <p>None of these are required for streaming, and all of them are reached through hidden APIs that
 * Samsung sometimes replaces. Each call therefore reports success rather than throwing, so a
 * One UI change disables one convenience instead of breaking the session.
 */
public final class SystemBridge {

    /** {@code SurfaceControl.POWER_MODE_OFF}. */
    private static final int POWER_MODE_OFF = 0;

    /** {@code SurfaceControl.POWER_MODE_NORMAL}. */
    private static final int POWER_MODE_NORMAL = 2;

    private SystemBridge() {
    }

    /**
     * Turns the built-in panel on or off, leaving the streamed display running.
     *
     * @return true when the power mode was applied
     */
    public static boolean setBuiltInDisplayPower(boolean on) {
        Class<?> surfaceControl = Reflect.findClass("android.view.SurfaceControl");
        if (surfaceControl == null) {
            return false;
        }

        try {
            IBinder token = getBuiltInDisplayToken(surfaceControl);
            if (token == null) {
                Ln.v("No built-in display token; cannot change the panel's power state");
                return false;
            }

            Method setPowerMode = Reflect.findMethod(
                    surfaceControl,
                    "setDisplayPowerMode",
                    new Class<?>[]{IBinder.class, int.class});
            if (setPowerMode == null) {
                setPowerMode = Reflect.findMethod(
                        surfaceControl, "setPowerMode", new Class<?>[]{IBinder.class, int.class});
            }

            if (setPowerMode == null) {
                return false;
            }

            Reflect.invoke(setPowerMode, null, token, on ? POWER_MODE_NORMAL : POWER_MODE_OFF);
            Ln.i("Built-in display power set to " + (on ? "on" : "off"));
            return true;
        } catch (Exception e) {
            Ln.w("Could not change the built-in display power state", e);
            return false;
        }
    }

    private static IBinder getBuiltInDisplayToken(Class<?> surfaceControl) throws Exception {
        // Android 10+ : getPhysicalDisplayIds() then getPhysicalDisplayToken(long).
        Method getIds = Reflect.findMethod(surfaceControl, "getPhysicalDisplayIds", new Class<?>[0]);
        Method getToken =
                Reflect.findMethod(surfaceControl, "getPhysicalDisplayToken", new Class<?>[]{long.class});

        if (getIds != null && getToken != null) {
            Object ids = Reflect.invoke(getIds, null);
            if (ids instanceof long[] && ((long[]) ids).length > 0) {
                Object token = Reflect.invoke(getToken, null, ((long[]) ids)[0]);
                if (token instanceof IBinder) {
                    return (IBinder) token;
                }
            }
        }

        // Android 9 and earlier: getBuiltInDisplay(int).
        Method getBuiltIn =
                Reflect.findMethod(surfaceControl, "getBuiltInDisplay", new Class<?>[]{int.class});
        if (getBuiltIn != null) {
            Object token = Reflect.invoke(getBuiltIn, null, 0);
            if (token instanceof IBinder) {
                return (IBinder) token;
            }
        }

        return null;
    }

    /**
     * Replaces the device clipboard.
     *
     * @return true when the clipboard was set
     */
    public static boolean setClipboard(String text) {
        try {
            Class<?> serviceManager = Reflect.findClass("android.os.ServiceManager");
            Method getService =
                    Reflect.findMethod(serviceManager, "getService", new Class<?>[]{String.class});
            Object binder = Reflect.invoke(getService, null, "clipboard");
            if (binder == null) {
                return false;
            }

            Class<?> stubClass = Reflect.findClass("android.content.IClipboard$Stub");
            Method asInterface =
                    Reflect.findMethod(stubClass, "asInterface", new Class<?>[]{IBinder.class});
            Object clipboard = Reflect.invoke(asInterface, null, binder);
            if (clipboard == null) {
                return false;
            }

            Object clip = buildClipData(text);
            if (clip == null) {
                return false;
            }

            // setPrimaryClip's signature has gained parameters in almost every release (callingPackage,
            // attributionTag, userId, deviceId...). Match on name and arity, place the ClipData and the
            // package name, and let everything else default.
            for (Method method : clipboard.getClass().getMethods()) {
                if (!method.getName().equals("setPrimaryClip")) {
                    continue;
                }

                Class<?>[] parameters = method.getParameterTypes();
                Object[] arguments = new Object[parameters.length];
                boolean clipPlaced = false;

                for (int i = 0; i < parameters.length; i++) {
                    if (parameters[i].isInstance(clip)) {
                        arguments[i] = clip;
                        clipPlaced = true;
                    } else if (parameters[i] == String.class) {
                        arguments[i] = "com.android.shell";
                    } else if (parameters[i] == int.class) {
                        arguments[i] = 0;
                    } else {
                        arguments[i] = null;
                    }
                }

                if (!clipPlaced) {
                    continue;
                }

                method.setAccessible(true);
                Reflect.invoke(method, clipboard, arguments);
                return true;
            }

            Ln.v("No usable IClipboard.setPrimaryClip overload was found");
            return false;
        } catch (Exception e) {
            Ln.w("Could not set the device clipboard", e);
            return false;
        }
    }

    private static Object buildClipData(String text) throws Exception {
        Class<?> clipDataClass = Reflect.findClass("android.content.ClipData");
        if (clipDataClass == null) {
            return null;
        }

        Method newPlainText = Reflect.findMethod(
                clipDataClass,
                "newPlainText",
                new Class<?>[]{CharSequence.class, CharSequence.class});
        return Reflect.invoke(newPlainText, null, "dexstream", text);
    }
}
