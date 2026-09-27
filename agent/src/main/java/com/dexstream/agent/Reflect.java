package com.dexstream.agent;

import java.lang.reflect.Field;
import java.lang.reflect.InvocationTargetException;
import java.lang.reflect.Method;

/**
 * Reflection helpers for the framework internals the agent needs.
 *
 * <p>Screen capture and input injection are not part of the public Android SDK, so they are reached
 * through reflection. The signatures also move between releases, which is why every lookup here
 * takes a list of candidates and reports which one matched instead of assuming one shape: an agent
 * that fails on a new One UI release should say what it could not find, not crash opaquely.
 */
public final class Reflect {

    private Reflect() {
    }

    /** Loads a class, returning null when it does not exist on this build. */
    public static Class<?> findClass(String name) {
        try {
            return Class.forName(name);
        } catch (ClassNotFoundException e) {
            Ln.v("Class not present on this build: " + name);
            return null;
        }
    }

    /**
     * Finds the first declared method matching {@code name} and any of the supplied parameter lists.
     *
     * @return the accessible method, or null when none of the candidates exist
     */
    public static Method findMethod(Class<?> type, String name, Class<?>[]... parameterCandidates) {
        if (type == null) {
            return null;
        }

        for (Class<?>[] parameters : parameterCandidates) {
            try {
                Method method = type.getDeclaredMethod(name, parameters);
                method.setAccessible(true);
                return method;
            } catch (NoSuchMethodException ignored) {
                // Try the next candidate signature.
            }
        }

        // Fall back to matching on name and arity alone, which covers releases that changed a
        // parameter type without changing the call's meaning.
        for (Class<?>[] parameters : parameterCandidates) {
            for (Method method : type.getDeclaredMethods()) {
                if (method.getName().equals(name) && method.getParameterCount() == parameters.length) {
                    method.setAccessible(true);
                    Ln.v("Matched " + type.getSimpleName() + "." + name + " by arity: " + method);
                    return method;
                }
            }
        }

        return null;
    }

    /** Finds a method by name and arity only. */
    public static Method findMethodByArity(Class<?> type, String name, int arity) {
        if (type == null) {
            return null;
        }

        for (Method method : type.getDeclaredMethods()) {
            if (method.getName().equals(name) && method.getParameterCount() == arity) {
                method.setAccessible(true);
                return method;
            }
        }

        return null;
    }

    public static Object invoke(Method method, Object target, Object... arguments) throws Exception {
        if (method == null) {
            throw new NoSuchMethodException("Method not available on this Android build");
        }

        try {
            return method.invoke(target, arguments);
        } catch (InvocationTargetException e) {
            Throwable cause = e.getCause();
            if (cause instanceof Exception) {
                throw (Exception) cause;
            }
            throw e;
        }
    }

    /** Reads an instance field, returning {@code fallback} when it is missing. */
    public static int readInt(Object target, String fieldName, int fallback) {
        if (target == null) {
            return fallback;
        }

        try {
            Field field = target.getClass().getField(fieldName);
            field.setAccessible(true);
            return field.getInt(target);
        } catch (Exception e) {
            Ln.v("Field " + fieldName + " unavailable: " + Ln.describe(e));
            return fallback;
        }
    }

    /** Reads an instance field, returning {@code fallback} when it is missing. */
    public static float readFloat(Object target, String fieldName, float fallback) {
        if (target == null) {
            return fallback;
        }

        try {
            Field field = target.getClass().getField(fieldName);
            field.setAccessible(true);
            return field.getFloat(target);
        } catch (Exception e) {
            Ln.v("Field " + fieldName + " unavailable: " + Ln.describe(e));
            return fallback;
        }
    }

    public static Object readObject(Object target, String fieldName) {
        if (target == null) {
            return null;
        }

        try {
            Field field = target.getClass().getField(fieldName);
            field.setAccessible(true);
            return field.get(target);
        } catch (Exception e) {
            Ln.v("Field " + fieldName + " unavailable: " + Ln.describe(e));
            return null;
        }
    }

    /** Reads a public static int constant, returning {@code fallback} when absent. */
    public static int readStaticInt(Class<?> type, String fieldName, int fallback) {
        if (type == null) {
            return fallback;
        }

        try {
            Field field = type.getDeclaredField(fieldName);
            field.setAccessible(true);
            return field.getInt(null);
        } catch (Exception e) {
            return fallback;
        }
    }
}
