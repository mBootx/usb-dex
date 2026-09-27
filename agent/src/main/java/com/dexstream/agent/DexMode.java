package com.dexstream.agent;

import java.lang.reflect.Method;

/**
 * Reports whether Samsung DeX is running, using Samsung's own framework class when present.
 *
 * <p>{@code com.samsung.android.desktopmode.SemDesktopModeManager} exists only on One UI and is not
 * documented, so every access is probed and failures are reported as "unknown" rather than as "off".
 * The agent never depends on this: display selection works from display geometry alone, and this
 * only improves the message the desktop app shows the user.</p>
 *
 * <p>There is no supported way for a shell-user process to <em>start</em> DeX. Samsung's own
 * "DeX for PC" application was discontinued and its host protocol was never published, so the user
 * starts DeX on the phone, or DexStream creates a desktop-mode display of its own. See
 * {@code docs/DEX-ACTIVATION.md}.</p>
 */
public final class DexMode {

    public enum State {
        /** DeX is running. */
        ENABLED,

        /** DeX is not running. */
        DISABLED,

        /** This build does not expose Samsung's DeX classes, or they could not be read. */
        UNKNOWN,
    }

    private DexMode() {
    }

    /** Queries Samsung's desktop-mode state. */
    public static State query() {
        Class<?> managerClass =
                Reflect.findClass("com.samsung.android.desktopmode.SemDesktopModeManager");
        if (managerClass == null) {
            return State.UNKNOWN;
        }

        try {
            Class<?> serviceManager = Reflect.findClass("android.os.ServiceManager");
            Method getService =
                    Reflect.findMethod(serviceManager, "getService", new Class<?>[]{String.class});
            Object binder = Reflect.invoke(getService, null, "desktopmode");
            if (binder == null) {
                return State.UNKNOWN;
            }

            // The state object exposes an enabled flag; its exact accessor differs across One UI
            // versions, so try the documented shapes and give up quietly.
            Class<?> stateClass =
                    Reflect.findClass("com.samsung.android.desktopmode.SemDesktopModeState");
            if (stateClass == null) {
                return State.UNKNOWN;
            }

            int enabledConstant = Reflect.readStaticInt(stateClass, "ENABLED", 4);
            Method getInstance = Reflect.findMethod(managerClass, "getInstance", new Class<?>[0]);
            if (getInstance == null) {
                return State.UNKNOWN;
            }

            Object manager = Reflect.invoke(getInstance, null);
            Method getState = Reflect.findMethodByArity(managerClass, "getDesktopModeState", 0);
            if (manager == null || getState == null) {
                return State.UNKNOWN;
            }

            Object state = Reflect.invoke(getState, manager);
            if (state == null) {
                return State.DISABLED;
            }

            int enabled = Reflect.readInt(state, "enabled", Integer.MIN_VALUE);
            if (enabled == Integer.MIN_VALUE) {
                Method getEnabled = Reflect.findMethodByArity(state.getClass(), "getEnabled", 0);
                if (getEnabled != null) {
                    Object value = Reflect.invoke(getEnabled, state);
                    if (value instanceof Integer) {
                        enabled = (Integer) value;
                    }
                }
            }

            if (enabled == Integer.MIN_VALUE) {
                return State.UNKNOWN;
            }

            return enabled == enabledConstant ? State.ENABLED : State.DISABLED;
        } catch (Exception e) {
            Ln.v("Could not read the Samsung DeX state: " + Ln.describe(e));
            return State.UNKNOWN;
        }
    }
}
