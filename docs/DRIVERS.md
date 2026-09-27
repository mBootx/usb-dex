# USB driver setup

DexStream talks to the phone's ADB interface directly over WinUSB. It does not use `adb.exe`, and it
does not install anything. What it needs is that Windows has bound **WinUSB** to the phone's ADB
interface, which is what Samsung's and Google's Android USB drivers both do.

## On the phone, once

1. **Settings → About phone → Software information** and tap **Build number** seven times to unlock
   Developer options.
2. **Settings → Developer options → USB debugging**, on.
3. Connect the cable. Set the USB mode to **Transferring files / Android Auto** (MTP) — in charging-only
   mode the phone publishes no ADB interface at all.
4. The first time DexStream connects, the phone shows **Allow USB debugging?**. Tick *Always allow from
   this computer* and tap **Allow**. The screen must be unlocked for the prompt to appear.

## On Windows

Windows 10 and 11 usually bind the right driver as soon as a Galaxy device is plugged in with USB
debugging on. Check in **Device Manager**: with the phone attached you should see

```
Universal Serial Bus devices
    SAMSUNG Android ADB Interface
```

or, with Google's driver,

```
Android Device
    Android ADB Interface
```

Either is fine. DexStream finds the interface by its device interface class GUID
(`{F72FE0D4-CBCB-407D-8814-9ED673D0DD6B}`), which `android_winusb.inf` and every driver derived from it
assign to the ADB interface.

### If no ADB interface appears

Install one of these, then unplug and replug the cable:

- **Samsung Android USB Driver for Windows** — Samsung's own package, from the Samsung developer
  site. Recommended for Galaxy devices.
- **Google USB Driver** — ships with Android Studio under *SDK Manager → SDK Tools → Google USB
  Driver*, and is also downloadable standalone from developer.android.com.

Neither requires anything from DexStream afterwards.

### If the device shows as "Samsung Mobile MTP Device" only

The phone is in a USB mode with no ADB interface. Pull down the notification shade, tap the USB
notification, and choose **Transferring files**. Then check that USB debugging is still enabled — some
One UI updates reset it.

## Administrator rights

None are needed to run DexStream. WinUSB device access is granted to the interactive user, the MSI is a
per-user install, and everything the app writes lives under `%LOCALAPPDATA%\DexStream`.

Installing a *driver* does need administrator rights, but that is a one-off step outside DexStream.

## The ADB server conflict

Only one process may hold a WinUSB interface at a time. If the ADB server is running it will have
claimed the interface at startup, and DexStream's open fails with a sharing violation. The error message
says so explicitly.

Close whatever started it:

```
adb kill-server
```

Android Studio, Visual Studio's Android tooling, Samsung Smart Switch, scrcpy and most phone-management
utilities all start an ADB server. DexStream does not, and it releases the interface as soon as it stops,
so you can switch back and forth freely.

## Which ADB key is used

DexStream prefers the key `adb` already uses, at `%USERPROFILE%\.android\adbkey`. If your device has
authorized that key before, DexStream connects without prompting. If there is no such key, DexStream
generates its own under `%LOCALAPPDATA%\DexStream\adbkey`, and the phone asks for authorization once.

You can turn the reuse off in **Settings → Reuse the existing ADB key**, which forces DexStream's own
key and therefore one authorization prompt.
