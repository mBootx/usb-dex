package com.dexstream.agent;

import android.os.SystemClock;
import android.view.InputDevice;
import android.view.InputEvent;
import android.view.KeyCharacterMap;
import android.view.KeyEvent;
import android.view.MotionEvent;

import java.lang.reflect.Method;

/**
 * Injects keyboard, mouse and scroll events into a specific display.
 *
 * <p>Injection goes through {@code InputManager.injectInputEvent}, which is hidden. It moved to
 * {@code InputManagerGlobal} in Android 14, so both entry points are probed. Targeting a display
 * other than the built-in one additionally needs {@code InputEvent.setDisplayId}, also hidden:
 * without it every event would land on the phone's own screen instead of the DeX desktop, so its
 * absence is treated as a hard failure rather than degraded silently.
 */
public final class InputInjector {

    /** {@code InputManager.INJECT_INPUT_EVENT_MODE_ASYNC}: fire and forget, no waiting for the app. */
    private static final int INJECT_MODE_ASYNC = 0;

    private final Object inputManager;
    private final Method injectInputEvent;
    private final Method setDisplayId;
    private final int displayId;
    private final KeyCharacterMap keyCharacterMap;

    private long pointerDownTime;
    private int lastButtonState;

    private InputInjector(
            Object inputManager,
            Method injectInputEvent,
            Method setDisplayId,
            int displayId) {
        this.inputManager = inputManager;
        this.injectInputEvent = injectInputEvent;
        this.setDisplayId = setDisplayId;
        this.displayId = displayId;
        this.keyCharacterMap = KeyCharacterMap.load(KeyCharacterMap.VIRTUAL_KEYBOARD);
    }

    /** Builds an injector bound to {@code displayId}. */
    public static InputInjector create(int displayId) throws Exception {
        Object manager = null;
        Method inject = null;

        // Android 14 moved the singleton to InputManagerGlobal; earlier releases have it on
        // InputManager. Try the newer one first so a future removal of the old one is harmless.
        for (String className : new String[]{
                "android.hardware.input.InputManagerGlobal",
                "android.hardware.input.InputManager"}) {
            Class<?> type = Reflect.findClass(className);
            if (type == null) {
                continue;
            }

            Method getInstance = Reflect.findMethod(type, "getInstance", new Class<?>[0]);
            if (getInstance == null) {
                continue;
            }

            Method candidate = Reflect.findMethod(
                    type, "injectInputEvent", new Class<?>[]{InputEvent.class, int.class});
            if (candidate == null) {
                continue;
            }

            try {
                manager = Reflect.invoke(getInstance, null);
                inject = candidate;
                Ln.v("Using " + className + " for input injection");
                break;
            } catch (Exception e) {
                Ln.v("Could not obtain " + className + ": " + Ln.describe(e));
            }
        }

        if (manager == null || inject == null) {
            throw new UnsupportedOperationException(
                    "Neither InputManagerGlobal nor InputManager exposes injectInputEvent on this "
                            + "build, so DexStream cannot forward mouse and keyboard input");
        }

        Method setDisplayId =
                Reflect.findMethod(InputEvent.class, "setDisplayId", new Class<?>[]{int.class});
        if (setDisplayId == null && displayId != 0) {
            throw new UnsupportedOperationException(
                    "InputEvent.setDisplayId is not available, so input cannot be routed to display "
                            + displayId + " (it would go to the phone's own screen instead)");
        }

        return new InputInjector(manager, inject, setDisplayId, displayId);
    }

    private boolean inject(InputEvent event) {
        try {
            if (setDisplayId != null) {
                Reflect.invoke(setDisplayId, event, displayId);
            }

            Object result = Reflect.invoke(injectInputEvent, inputManager, event, INJECT_MODE_ASYNC);
            return !(result instanceof Boolean) || (Boolean) result;
        } catch (Exception e) {
            Ln.w("Input injection failed", e);
            return false;
        } finally {
            if (event instanceof MotionEvent) {
                ((MotionEvent) event).recycle();
            }
        }
    }

    /** Injects a key down or up. */
    public boolean injectKey(int action, int keyCode, int repeat, int metaState) {
        long now = SystemClock.uptimeMillis();
        KeyEvent event = new KeyEvent(
                now,
                now,
                action,
                keyCode,
                repeat,
                metaState,
                KeyCharacterMap.VIRTUAL_KEYBOARD,
                0,
                KeyEvent.FLAG_FROM_SYSTEM,
                InputDevice.SOURCE_KEYBOARD);
        return inject(event);
    }

    /** Presses and releases a key, used for the navigation shortcuts. */
    public boolean tapKey(int keyCode) {
        return injectKey(KeyEvent.ACTION_DOWN, keyCode, 0, 0)
                && injectKey(KeyEvent.ACTION_UP, keyCode, 0, 0);
    }

    /**
     * Injects text by translating it into key events with the virtual keyboard's character map.
     *
     * <p>Android has no API for injecting a string, and mapping host key codes on the host side would
     * ignore the device's own layout. Characters the virtual keyboard cannot produce are reported and
     * skipped rather than silently dropped.
     */
    public boolean injectText(String text) {
        KeyEvent[] events = keyCharacterMap.getEvents(text.toCharArray());
        if (events == null) {
            Ln.w("The virtual keyboard cannot produce \"" + text + "\"");
            return false;
        }

        boolean allSucceeded = true;
        for (KeyEvent event : events) {
            allSucceeded &= inject(event);
        }

        return allSucceeded;
    }

    /**
     * Injects a pointer event.
     *
     * @param action      one of the {@code Protocol.POINTER_*} values
     * @param x           x in display pixels
     * @param y           y in display pixels
     * @param pressure    0..1
     * @param buttonState Android button bitmask currently held
     */
    public boolean injectPointer(
            int action, int x, int y, float pressure, int buttonState, int actionButton) {
        long now = SystemClock.uptimeMillis();
        int motionAction = toMotionAction(action);

        if (motionAction == MotionEvent.ACTION_DOWN) {
            pointerDownTime = now;
        } else if (pointerDownTime == 0) {
            // A move or up that arrives before any down (for example after a reconnect) would be
            // rejected by the input pipeline; anchor it to now so it is still delivered.
            pointerDownTime = now;
        }

        MotionEvent.PointerProperties[] properties = new MotionEvent.PointerProperties[1];
        properties[0] = new MotionEvent.PointerProperties();
        properties[0].id = 0;
        properties[0].toolType = MotionEvent.TOOL_TYPE_MOUSE;

        MotionEvent.PointerCoords[] coords = new MotionEvent.PointerCoords[1];
        coords[0] = new MotionEvent.PointerCoords();
        coords[0].x = x;
        coords[0].y = y;
        coords[0].pressure = pressure;
        coords[0].size = 1;

        MotionEvent event = MotionEvent.obtain(
                pointerDownTime,
                now,
                motionAction,
                1,
                properties,
                coords,
                0,
                buttonState,
                1f,
                1f,
                InputDevice.SOURCE_MOUSE,
                0,
                0,
                InputDevice.SOURCE_MOUSE);

        if (actionButton != 0) {
            setActionButton(event, actionButton);
        }

        if (motionAction == MotionEvent.ACTION_UP || motionAction == MotionEvent.ACTION_CANCEL) {
            pointerDownTime = 0;
        }

        lastButtonState = buttonState;
        return inject(event);
    }

    /** Injects a scroll wheel event at the given position. */
    public boolean injectScroll(int x, int y, float horizontal, float vertical, int buttonState) {
        long now = SystemClock.uptimeMillis();

        MotionEvent.PointerProperties[] properties = new MotionEvent.PointerProperties[1];
        properties[0] = new MotionEvent.PointerProperties();
        properties[0].id = 0;
        properties[0].toolType = MotionEvent.TOOL_TYPE_MOUSE;

        MotionEvent.PointerCoords[] coords = new MotionEvent.PointerCoords[1];
        coords[0] = new MotionEvent.PointerCoords();
        coords[0].x = x;
        coords[0].y = y;
        coords[0].setAxisValue(MotionEvent.AXIS_HSCROLL, horizontal);
        coords[0].setAxisValue(MotionEvent.AXIS_VSCROLL, vertical);

        MotionEvent event = MotionEvent.obtain(
                now,
                now,
                MotionEvent.ACTION_SCROLL,
                1,
                properties,
                coords,
                0,
                buttonState,
                1f,
                1f,
                InputDevice.SOURCE_MOUSE,
                0,
                0,
                InputDevice.SOURCE_MOUSE);

        return inject(event);
    }

    /** The button bitmask most recently reported by the host. */
    public int getLastButtonState() {
        return lastButtonState;
    }

    private static int toMotionAction(int protocolAction) {
        switch (protocolAction) {
            case Protocol.POINTER_DOWN:
                return MotionEvent.ACTION_DOWN;
            case Protocol.POINTER_UP:
                return MotionEvent.ACTION_UP;
            case Protocol.POINTER_CANCEL:
                return MotionEvent.ACTION_CANCEL;
            case Protocol.POINTER_HOVER_ENTER:
                return MotionEvent.ACTION_HOVER_ENTER;
            case Protocol.POINTER_HOVER_MOVE:
                return MotionEvent.ACTION_HOVER_MOVE;
            case Protocol.POINTER_HOVER_EXIT:
                return MotionEvent.ACTION_HOVER_EXIT;
            default:
                return MotionEvent.ACTION_MOVE;
        }
    }

    /**
     * Sets {@code MotionEvent.mActionButton}, which identifies which button caused a press or
     * release. It has no public setter, and without it secondary and middle clicks are delivered as
     * a press with no identified button, which many apps ignore.
     */
    private static void setActionButton(MotionEvent event, int actionButton) {
        try {
            Method setter = Reflect.findMethod(
                    MotionEvent.class, "setActionButton", new Class<?>[]{int.class});
            if (setter != null) {
                Reflect.invoke(setter, event, actionButton);
            }
        } catch (Exception e) {
            Ln.v("Could not set the action button: " + Ln.describe(e));
        }
    }
}
