# DexStream

Stream Samsung DeX from a USB-connected Galaxy device to a Windows desktop, at the highest resolution
and refresh rate the device offers.

USB only. No wireless fallback, no network path, no ADB server, no administrator rights.

> **Read [docs/VERIFICATION.md](docs/VERIFICATION.md) before you rely on any of this.** The project was
> written without access to a Windows machine or a Galaxy device. The protocol layer is covered by 224
> tests that run on every push and the whole thing compiles in CI, but nothing has been run against real
> hardware. That page says exactly what is proven and what is not.

## What it does

- **Finds the phone itself.** DexStream speaks the ADB wire protocol directly to the bulk endpoints of
  the device's ADB interface over WinUSB. It does not shell out to `adb.exe` and does not start an ADB
  server, so "USB only" is a property of the design rather than a promise.
- **Finds the DeX desktop.** DeX is a second logical display on the phone, labelled inconsistently
  across One UI versions. DexStream ranks the displays it finds and tells you which one it chose and
  why.
- **Streams at the device's maximum.** The display's mode list is read from the device and the largest
  resolution, then the highest refresh rate at that resolution, is requested. Caps are available in
  settings.
- **Decodes on the GPU.** A Media Foundation transform hands back Direct3D 11 textures; conversion,
  scaling and letterboxing are one fixed-function blit; presentation is a flip-model swap chain with
  tearing allowed and a one-frame latency limit. No frame is ever queued.
- **Sends mouse and keyboard back.** Over the same USB connection. Typed characters go as text so the
  device's own layout, dead keys and IME apply.
- **Measures its own latency honestly.** End to end, from the device's capture timestamp to the moment
  the frame is on screen, using a clock offset derived from the control-channel round trip. The design
  target is under 20 ms; the number shown is measured, not estimated.

## Requirements

- A Galaxy device with DeX: Galaxy S, Note, Z Fold or Tab S flagship, Galaxy S8 or newer, Android 10+.
  See [docs/SUPPORTED-DEVICES.md](docs/SUPPORTED-DEVICES.md).
- Windows 10 1809 or later, or Windows 11, x64.
- A GPU with Direct3D 11 video decode — which is every discrete GPU and every Intel iGPU since Haswell.
- A USB cable that carries data. USB 3 is better than USB 2.0, but USB 2.0 works.

## Getting started

1. On the phone: enable **Developer options** (tap Build number seven times), turn on **USB debugging**,
   and set the USB mode to **Transferring files**.
2. Start DeX — connect the phone to a monitor with a USB-C to HDMI cable or a DeX dock, or use DeX on a
   wireless display. (Or let DexStream create a desktop display instead; see
   [docs/DEX-ACTIVATION.md](docs/DEX-ACTIVATION.md).)
3. Connect the phone to the PC and run `DexStream.exe`.
4. Tap **Allow** on the phone's "Allow USB debugging?" prompt. The screen has to be unlocked.
5. Streaming starts automatically. If it does not, press **Start streaming**.

If something goes wrong, open the **Diagnostics** pane — it carries both the app's log and the device
agent's own output, and the failing stage is almost always named there. Then see
[docs/TROUBLESHOOTING.md](docs/TROUBLESHOOTING.md).

### If the ADB server is running

Only one process can hold the USB interface. Run `adb kill-server` and close Android Studio or Samsung
Smart Switch. DexStream releases the interface as soon as it stops, so you can switch back and forth.

## Deliverables

Both are built and uploaded by every green CI run:

- **`DexStream.exe`** — portable, self-contained, one file, no installer and no prerequisites. The
  device agent is embedded in it.
- **`DexStream.msi`** — per-user install with a Start menu shortcut. No elevation required.

## How it works

Briefly: DexStream authenticates to `adbd` over USB, pushes a small Java agent to `/data/local/tmp`,
starts it with `app_process` as the shell user, and connects to the abstract socket it opens. The agent
captures a display into a `MediaCodec` encoder tuned for latency rather than file size and streams
encoded frames back; the host decodes them on the GPU and presents them without buffering. A second
socket carries input the other way.

The full picture, including the ADB handshake, the wire format, why there are two sockets, and how the
latency figure is derived, is in [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md).

## Documentation

| | |
| --- | --- |
| [VERIFICATION.md](docs/VERIFICATION.md) | What is proven, how, and what still needs hardware. **Start here.** |
| [DRIVERS.md](docs/DRIVERS.md) | USB driver setup and the ADB server conflict. |
| [SUPPORTED-DEVICES.md](docs/SUPPORTED-DEVICES.md) | Models, resolutions, USB and Windows requirements. |
| [DEX-ACTIVATION.md](docs/DEX-ACTIVATION.md) | Getting a DeX desktop to capture, and why it is the uncertain part. |
| [TROUBLESHOOTING.md](docs/TROUBLESHOOTING.md) | Symptom by symptom. |
| [ARCHITECTURE.md](docs/ARCHITECTURE.md) | Design, protocols and the reasoning behind them. |
| [BUILDING.md](docs/BUILDING.md) | Building on Windows, on Linux or macOS, and the agent. |

## Building

```
agent/build.sh                                  # build the device agent first, so it gets embedded
dotnet build DexStream.sln -c Release           # Windows
dotnet test  DexStream.sln -c Release
```

On Linux or macOS the WPF app cannot be built, because the .NET SDK package there omits the Windows
Desktop SDK. Everything else can, and the app's C# can still be type-checked. See
[docs/BUILDING.md](docs/BUILDING.md).

## Known limitations

- **Capturing DeX depends on your Android version.** `SurfaceControl.createDisplay` was removed in
  Android 14 and its replacement needs a hidden API whose availability to the shell user varies. The
  agent probes three strategies and logs which one worked. This is the biggest open question and it is a
  question about Android, not about this code.
- **DeX cannot be started from the PC.** Samsung's own DeX for PC was discontinued and its protocol was
  never published. Start DeX on the phone, or let DexStream create a display of its own.
- **No audio.** Video and input only.
- **x64 only.** An ARM64 build needs a second runtime identifier in the publish profile.
- **Secure surfaces appear black.** Android blanks capture of DRM-protected content. This is by design
  in the platform, not a bug here.

## Licence and relationship to Samsung

DexStream is not affiliated with, endorsed by, or supported by Samsung. "Samsung DeX" and "Galaxy" are
Samsung's trademarks. Nothing here is derived from Samsung software; it is built on the Android
framework APIs available to the ADB shell user, which is the same footing as `adb` itself.
