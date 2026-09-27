# Troubleshooting

Open the **Diagnostics** pane first. It shows the app's own log and relays the device agent's stderr, so
whatever failed is almost always named there. The same content goes to
`%LOCALAPPDATA%\DexStream\dexstream.log`, and **Copy** puts it on the clipboard for a bug report.

## No device is found

DexStream says *No device connected* and the Start button stays disabled.

1. **Is USB debugging on?** Settings → Developer options → USB debugging. Some One UI updates turn it
   back off.
2. **Is the USB mode right?** Pull down the notification shade, tap the USB notification, choose
   **Transferring files**. Charging-only mode publishes no ADB interface.
3. **Is it a data cable?** Many bundled cables are charge-only. Try the cable that came with the phone,
   or any cable known to work for file transfer.
4. **Does Device Manager show an ADB interface?** See [DRIVERS.md](DRIVERS.md). If it shows only
   *Samsung Mobile MTP Device*, the phone is not publishing ADB.

## "Another process already owns the ADB interface"

Exactly one process can hold the interface. Run `adb kill-server`, and close Android Studio, Samsung
Smart Switch, scrcpy or any phone-management tool. DexStream releases the interface when it stops, so
you can alternate freely.

## The phone never shows the authorization prompt

- The screen must be **unlocked** for the dialog to appear.
- If you have previously tapped *Deny*, the phone remembers. Developer options → **Revoke USB debugging
  authorizations**, then reconnect.
- If DexStream reused an existing `adbkey` that the phone already rejected, turn off **Settings → Reuse
  the existing ADB key** to force a fresh key and a fresh prompt.

## "No DeX display was found"

DexStream found the phone but no DeX desktop. DeX is not running.

Start it on the phone, by any of:

- Connect the phone to a monitor with a USB-C to HDMI cable or a DeX dock, and choose DeX rather than
  Screen mirroring.
- Use **DeX on a wireless display** from the quick settings panel, if your TV supports it.

Or let DexStream provide the desktop instead: turn on **Settings → Create a desktop display on the
device when DeX is not running**. Whether that works depends on your Android version — see
[DEX-ACTIVATION.md](DEX-ACTIVATION.md).

Or, to just mirror the phone screen, turn off **Only stream the DeX desktop**.

## "The device agent did not start listening"

The agent was pushed and launched but never opened its socket. The agent's own output in the
diagnostics pane says why. The usual causes:

- **Capture is not permitted on this build.** The agent logs an `UnsupportedOperationException` naming
  the strategies it tried. This is the known hard case on Android 14 and later; see
  [DEX-ACTIVATION.md](DEX-ACTIVATION.md).
- **`InputEvent.setDisplayId` is unavailable**, so the agent refuses to start rather than send your
  mouse clicks to the phone's own screen. The message says so.
- **A stale agent is still running.** DexStream kills any previous agent before starting, but a reboot
  of the phone clears anything it missed.
- **The encoder rejected the resolution.** Cap the resolution in settings — 2560 px is a safe value —
  and try again.

## The picture is black, or only the wallpaper appears

The capture produced empty frames. Two known causes:

- **A secure surface is on screen.** Android blanks capture of DRM-protected content: Netflix, a
  banking app's screen, and some password fields. Frames resume when it goes away.
- **The capture display is powered off.** If DeX was stopped after the session started, the display is
  gone. Stop and start the stream.

## "No Media Foundation decoder could be activated"

Windows has no decoder for the codec in use.

- **HEVC**: install the *HEVC Video Extension* from the Microsoft Store, or set **Settings → Video
  codec → H.264**. H.264 is decodable on every supported GPU and needs nothing extra.
- **H.264 missing**: this means the Media Feature Pack is absent, which happens on Windows "N"
  editions. Install it from Windows Update → Optional features.

The diagnostics pane lists the decoders it found, which is usually enough to see what is missing.

## Latency is higher than expected

Check the metrics bar. The **Latency** figure is measured end to end — from the moment the device's
encoder stamped the frame to the moment it was presented — using a clock offset derived from the
control-channel round trip. **Control RTT** isolates the USB path.

- **Is this USB 2.0?** Roughly 250 Mbit/s and noticeably worse jitter. Use a USB 3 cable and port.
- **Is the bitrate too high for the link?** Set an explicit bitrate in settings; 25 Mbit/s at 1080p or
  40 Mbit/s at 1440p is ample for desktop content.
- **Is the decoder a software one?** The diagnostics pane says which decoder was activated. A software
  decoder adds several milliseconds and a lot of CPU.
- **Is tearing allowed?** The startup log line says whether the swap chain got `AllowTearing`. Without
  it, every present waits for the next vertical blank, which adds up to one refresh interval.

## Frames are dropped

*Dropped* in the metrics bar counts frames the decoder produced that never reached the screen, plus
frames discarded while waiting for the first key frame after a reset. A steady trickle right after
connecting is normal. A continuing stream of drops means either the decoder is returning system-memory
frames (see above) or the display driver is refusing presents — the log will carry an HRESULT.

## The phone's screen stays off after stopping

DexStream turns the panel back on when the session closes, but if the app was killed rather than closed,
the panel can be left off. Press the power button twice, or unplug and replug the cable. Turn off
**Settings → Turn the phone's screen off while streaming** to avoid it entirely.

## Mouse or keyboard do nothing

- **Click the stream first.** Keyboard input follows WPF focus, and a fresh window may not have it.
- **Check the log for an injection failure.** `InputInjector` logs each failure with the reason.
- If pointer position is offset, the stream geometry and the device's display size have diverged;
  restarting the stream re-reads both. Please report it with the log, as the rescaling is meant to
  handle that case.

## Reporting a problem

Include:

1. The phone's model and Android/One UI version.
2. Whether DeX was started on the phone, and how.
3. The diagnostics pane contents (**Copy**), which include the agent's own log.
4. Whether USB 2.0 or USB 3.
