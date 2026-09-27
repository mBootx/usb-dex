# Getting a DeX desktop to capture

This is the part of the problem that is genuinely uncertain, so it gets its own page. The short version:
**DexStream can capture a DeX desktop, but it cannot reliably start one**, and whether capture works at
all depends on your Android version.

## Why there is no official route

Samsung shipped *DeX for PC*, a Windows application that did exactly what DexStream does, and
**discontinued it**. Support was dropped with Android 11 / One UI 3.1, the app was withdrawn, and its
host protocol was never published. There is no Samsung SDK for streaming DeX to a PC, no documented
wire protocol, and no supported API for a PC to ask a phone to enter DeX mode.

What does exist is the Android framework's own display machinery, which a process running as the shell
user can reach. That is what DexStream uses, and it is why the interesting constraints are Android's
rather than Samsung's.

## What DeX actually is, from the host's point of view

DeX is a second logical display on the phone. When it is running, `dumpsys display` shows an extra
display alongside the built-in panel — often named something containing "DeX", sometimes only
identifiable as a non-default virtual or external display. Capture the right display and you have the
DeX desktop; inject input into that display's id and you can drive it.

DexStream therefore ranks the displays it finds rather than matching one name: an explicit DeX label
first, then a display it created itself, then any non-default external or virtual display, then the
phone's own screen. The chosen display and the reason are shown in the metrics bar, so you can see
whether you are looking at DeX or at a mirror of the phone.

## Route 1: start DeX on the phone (works today)

The reliable route. Start DeX by any normal means and DexStream will find and capture it:

- **USB-C to HDMI, or a DeX dock**, into any monitor or TV. Choose DeX, not Screen mirroring. The
  monitor has to stay connected, because the display exists only while it is.
- **DeX on a wireless display**, from quick settings, onto a Miracast-capable TV.

This is the recommended configuration. It needs no hidden APIs on the activation side at all.

## Route 2: let DexStream create a desktop display (depends on your Android version)

With **Settings → Create a desktop display on the device when DeX is not running** enabled, the agent
creates a virtual display of its own and captures that. Android can be told to treat a secondary display
as a freeform desktop, which gives a desktop-like environment even where Samsung's own DeX is not
involved.

Whether this works depends on what the platform still exposes to the shell user:

| Android | Situation |
| --- | --- |
| 10 – 13 | `SurfaceControl.createDisplay` exists. Capture and display creation both work from the shell user. This is the well-trodden path. |
| 14+ | `SurfaceControl.createDisplay` was **removed**. The replacement is a `VirtualDisplayConfig` with `setDisplayIdToMirror`, which is hidden but reachable by reflection. Whether a shell-user process is allowed to use it for mirroring varies. |

The agent probes, in order:

1. **`SurfaceControl` display mirroring.** Cheapest: composition happens once for both the panel and us.
2. **A mirroring virtual display** via `VirtualDisplayConfig.Builder.setDisplayIdToMirror`.
3. **A brand new virtual display**, which is what Route 2 uses — this creates a desktop rather than
   mirroring one.

It logs which one succeeded, or an `UnsupportedOperationException` naming each one it tried and why it
failed. That log line is the answer for your specific device, and it is the first thing to look at.

## Route 3: not implemented, and why

Two things that sound like solutions are not:

- **Asking Samsung's framework to enable DeX.** `SemDesktopModeManager` exists on One UI and DexStream
  queries it to *report* whether DeX is running, because that makes the UI message accurate. It exposes
  no documented, permitted way for a shell-user process to *enable* desktop mode, and inventing one
  from undocumented internals would be guesswork that breaks on the next One UI update. DexStream reads
  the state and reports `UNKNOWN` when it cannot, rather than pretending.
- **Rooting the device.** Out of scope. Everything DexStream does runs with ADB's privileges and no
  more.

## If capture fails entirely

If all three strategies fail on your device, the honest answer is that a shell-user process cannot
capture a display on that build, and no amount of host-side work changes it. The options then are:

- Use Route 1, which needs the least from the platform.
- Check whether *Disable permission monitoring* in Developer options changes the outcome — on some
  builds it affects what the shell user may do.
- Report the agent's log. The strategy list is designed to be extended, and a fourth strategy for a
  specific One UI version is a small change in
  [`ScreenCapture.java`](../agent/src/main/java/com/dexstream/agent/ScreenCapture.java).
