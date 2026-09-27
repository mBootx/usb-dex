package com.dexstream.agent;

import android.os.Looper;

import java.lang.reflect.Constructor;
import java.lang.reflect.Field;
import java.lang.reflect.Method;

/**
 * Makes the framework usable from a bare {@code app_process} with no application context.
 *
 * <p>Two things bite here. MediaCodec and the clipboard service reach for the current
 * {@code ActivityThread} and its {@code Application} on some builds, One UI among them, and get a
 * null-pointer failure when there is none. And several framework helpers post to the main
 * {@link Looper}, which a raw process does not have.</p>
 *
 * <p>Everything below is best effort: if a step fails the agent carries on, because on most builds
 * none of it is needed. Failures are logged at verbose level so a support report can show whether a
 * workaround was the difference.</p>
 */
public final class Workarounds {

    private Workarounds() {
    }

    /** Applies every workaround. Safe to call more than once. */
    public static void apply() {
        prepareMainLooper();
        fillActivityThread();
    }

    private static void prepareMainLooper() {
        try {
            if (Looper.getMainLooper() == null) {
                Looper.prepareMainLooper();
                Ln.v("Prepared a main Looper for the agent process.");
            }
        } catch (Exception e) {
            Ln.v("Could not prepare a main Looper: " + Ln.describe(e));
        }
    }

    /**
     * Installs a minimal {@code ActivityThread} so that
     * {@code ActivityThread.currentApplication()} and friends do not fail.
     */
    private static void fillActivityThread() {
        try {
            Class<?> activityThreadClass = Reflect.findClass("android.app.ActivityThread");
            if (activityThreadClass == null) {
                return;
            }

            Field current = activityThreadClass.getDeclaredField("sCurrentActivityThread");
            current.setAccessible(true);
            if (current.get(null) != null) {
                return;
            }

            Constructor<?> constructor = activityThreadClass.getDeclaredConstructor();
            constructor.setAccessible(true);
            Object activityThread = constructor.newInstance();
            current.set(null, activityThread);

            // AppBindData carries the ApplicationInfo that some services read; an empty one is enough
            // to get past the null checks.
            Class<?> bindDataClass = Reflect.findClass("android.app.ActivityThread$AppBindData");
            if (bindDataClass != null) {
                Constructor<?> bindConstructor = bindDataClass.getDeclaredConstructor();
                bindConstructor.setAccessible(true);
                Object bindData = bindConstructor.newInstance();

                Field appInfoField = bindDataClass.getDeclaredField("appInfo");
                appInfoField.setAccessible(true);
                Class<?> appInfoClass = Reflect.findClass("android.content.pm.ApplicationInfo");
                if (appInfoClass != null) {
                    Object appInfo = appInfoClass.getDeclaredConstructor().newInstance();
                    Field packageName = appInfoClass.getField("packageName");
                    packageName.set(appInfo, "com.android.shell");
                    appInfoField.set(bindData, appInfo);
                }

                Field boundApplication = activityThreadClass.getDeclaredField("mBoundApplication");
                boundApplication.setAccessible(true);
                boundApplication.set(activityThread, bindData);
            }

            Ln.v("Installed a stand-in ActivityThread.");
        } catch (Exception e) {
            Ln.v("Could not install a stand-in ActivityThread: " + Ln.describe(e));
        }
    }

    /** Reads the device's marketing model, used in the stream header. */
    public static String getDeviceName() {
        try {
            Class<?> build = Reflect.findClass("android.os.Build");
            if (build != null) {
                Field model = build.getField("MODEL");
                Object value = model.get(null);
                if (value != null) {
                    return value.toString();
                }
            }
        } catch (Exception e) {
            Ln.v("Could not read Build.MODEL: " + Ln.describe(e));
        }

        return "Android device";
    }

    /** Reads a system property through the hidden {@code SystemProperties} class. */
    public static String getSystemProperty(String key, String fallback) {
        try {
            Class<?> type = Reflect.findClass("android.os.SystemProperties");
            Method get = Reflect.findMethod(type, "get", new Class<?>[]{String.class, String.class});
            Object value = Reflect.invoke(get, null, key, fallback);
            return value == null ? fallback : value.toString();
        } catch (Exception e) {
            return fallback;
        }
    }
}
