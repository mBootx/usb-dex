package com.dexstream.agent;

import java.lang.reflect.Method;
import java.util.ArrayList;
import java.util.List;

/**
 * Reads display geometry from {@code DisplayManagerGlobal}, which is the only way to learn a
 * display's layer stack and its real logical size from outside the system server.
 *
 * <p>{@code DisplayManagerGlobal.getDisplayInfo(int)} returns an {@code android.view.DisplayInfo},
 * a hidden class whose fields are read reflectively. The layer stack is the value screen capture
 * needs: it identifies the set of surfaces a display composites, and mirroring works by pointing a
 * new display at the same stack.
 */
public final class DisplayBridge {

    /** Everything the agent needs to know about one display. */
    public static final class Info {
        public int displayId;
        public int logicalWidth;
        public int logicalHeight;
        public int layerStack;
        public int rotation;
        public int densityDpi;
        public float refreshRate;
        public int flags;
        public String name = "";

        @Override
        public String toString() {
            return "display " + displayId + " \"" + name + "\" " + logicalWidth + "x" + logicalHeight
                    + " @" + refreshRate + "Hz rotation=" + rotation + " layerStack=" + layerStack;
        }
    }

    private static Object globalInstance;
    private static Method getDisplayInfoMethod;
    private static Method getDisplayIdsMethod;

    private DisplayBridge() {
    }

    private static synchronized void ensureInitialised() throws Exception {
        if (globalInstance != null) {
            return;
        }

        Class<?> type = Reflect.findClass("android.hardware.display.DisplayManagerGlobal");
        if (type == null) {
            throw new UnsupportedOperationException(
                    "android.hardware.display.DisplayManagerGlobal is missing on this build");
        }

        Method getInstance = Reflect.findMethod(type, "getInstance", new Class<?>[0]);
        globalInstance = Reflect.invoke(getInstance, null);
        getDisplayInfoMethod = Reflect.findMethod(type, "getDisplayInfo", new Class<?>[]{int.class});
        getDisplayIdsMethod = Reflect.findMethodByArity(type, "getDisplayIds", 0);

        if (getDisplayIdsMethod == null) {
            // Android 14 added an overload taking a boolean; match by arity as a fallback.
            getDisplayIdsMethod = Reflect.findMethodByArity(type, "getDisplayIds", 1);
        }
    }

    /** Lists the ids of every display the system knows about. */
    public static int[] getDisplayIds() throws Exception {
        ensureInitialised();

        Object result = getDisplayIdsMethod.getParameterCount() == 1
                ? Reflect.invoke(getDisplayIdsMethod, globalInstance, Boolean.TRUE)
                : Reflect.invoke(getDisplayIdsMethod, globalInstance);

        if (result instanceof int[]) {
            return (int[]) result;
        }

        throw new UnsupportedOperationException(
                "getDisplayIds returned " + (result == null ? "null" : result.getClass().getName()));
    }

    /** Reads one display's geometry. */
    public static Info getInfo(int displayId) throws Exception {
        ensureInitialised();

        Object displayInfo = Reflect.invoke(getDisplayInfoMethod, globalInstance, displayId);
        if (displayInfo == null) {
            throw new IllegalArgumentException("No such display: " + displayId);
        }

        Info info = new Info();
        info.displayId = displayId;
        info.logicalWidth = Reflect.readInt(displayInfo, "logicalWidth", 0);
        info.logicalHeight = Reflect.readInt(displayInfo, "logicalHeight", 0);
        info.layerStack = Reflect.readInt(displayInfo, "layerStack", displayId);
        info.rotation = Reflect.readInt(displayInfo, "rotation", 0);
        info.densityDpi = Reflect.readInt(displayInfo, "logicalDensityDpi", 0);
        info.refreshRate = Reflect.readFloat(displayInfo, "refreshRate", 60f);
        info.flags = Reflect.readInt(displayInfo, "flags", 0);

        Object name = Reflect.readObject(displayInfo, "name");
        if (name != null) {
            info.name = name.toString();
        }

        return info;
    }

    /** Reads every display, skipping any that cannot be queried. */
    public static List<Info> getAll() throws Exception {
        List<Info> displays = new ArrayList<>();
        for (int id : getDisplayIds()) {
            try {
                displays.add(getInfo(id));
            } catch (Exception e) {
                Ln.w("Could not read display " + id, e);
            }
        }

        return displays;
    }

    /**
     * Picks the display to capture.
     *
     * <p>Mirrors the host's ranking so both ends agree: an explicitly requested id wins, then a
     * display whose name identifies it as DeX or as one the agent created, then any non-default
     * display, then the built-in panel.
     */
    public static Info select(int requestedDisplayId) throws Exception {
        List<Info> displays = getAll();
        if (displays.isEmpty()) {
            throw new IllegalStateException("The system reported no displays");
        }

        if (requestedDisplayId >= 0) {
            for (Info info : displays) {
                if (info.displayId == requestedDisplayId) {
                    return info;
                }
            }
            throw new IllegalArgumentException(
                    "Requested display " + requestedDisplayId + " is not present");
        }

        Info best = null;
        for (Info info : displays) {
            String lower = info.name.toLowerCase(java.util.Locale.ROOT);
            boolean looksLikeDex = lower.contains("dex")
                    || lower.contains("desktop")
                    || info.name.equals(Protocol.AGENT_DISPLAY_NAME);

            if (looksLikeDex) {
                if (best == null || !isDexLike(best)) {
                    best = info;
                } else if (pixels(info) > pixels(best)) {
                    best = info;
                }
                continue;
            }

            if (best == null) {
                best = info;
            } else if (!isDexLike(best) && info.displayId != 0 && best.displayId == 0) {
                best = info;
            }
        }

        return best;
    }

    private static boolean isDexLike(Info info) {
        String lower = info.name.toLowerCase(java.util.Locale.ROOT);
        return lower.contains("dex") || lower.contains("desktop");
    }

    private static long pixels(Info info) {
        return (long) info.logicalWidth * info.logicalHeight;
    }
}
