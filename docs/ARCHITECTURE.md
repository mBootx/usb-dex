# Architecture

## The shape of the problem

Streaming a phone display to a PC over USB needs four things: a transport, something on the device that
can see the display, a codec, and a way back for input. Samsung publishes no SDK for any of it, so all
four are built here on top of what Android exposes to the ADB shell user.

```
Windows                                         │ USB cable │                      Galaxy device
                                                              
┌──────────────────────────────┐                             ┌────────────────────────────────┐
│ DexStream.App (WPF)          │                             │ dexstream-agent.jar            │
│  ┌────────────────────────┐  │                             │  (app_process, shell user)     │
│  │ StreamSurface (HWND)   │  │                             │                                │
│  └───────────▲────────────┘  │                             │  ┌──────────────────────────┐  │
│              │ present       │                             │  │ ScreenCapture            │  │
│  ┌───────────┴────────────┐  │                             │  │  → display → Surface     │  │
│  │ D3D11VideoPipeline     │  │      video: DEXS packets    │  └────────────┬─────────────┘  │
│  │  MF decode → VP blt    │◄─┼─────────────────────────────┼───────────────┘                │
│  └────────────────────────┘  │  localabstract:dexstream #1 │  ┌──────────────────────────┐  │
│                              │                             │  │ VideoEncoder (MediaCodec)│  │
│  ┌────────────────────────┐  │      control: input etc.    │  └──────────────────────────┘  │
│  │ DexControlChannel      │──┼─────────────────────────────┼──►┌──────────────────────────┐ │
│  └────────────────────────┘  │  localabstract:dexstream #2 │   │ InputInjector            │ │
│                              │                             │   └──────────────────────────┘ │
│  ┌────────────────────────┐  │      shell: agent stderr    │                                │
│  │ AdbConnection          │──┼─────────────────────────────┼── diagnostics                  │
│  └───────────┬────────────┘  │                             └────────────────────────────────┘
│  ┌───────────┴────────────┐  │                                              ▲
│  │ WinUsbAdbTransport     │  │   bulk IN / bulk OUT                         │
│  └────────────────────────┘  ├──────────────────────────────────────────────┘
└──────────────────────────────┘                                          adbd
```

## Projects

| Project | Target | Contents |
| --- | --- | --- |
| `DexStream.Core` | `net8.0` | ADB protocol, DexStream wire protocol, display discovery, input mapping, geometry, metrics, settings. No OS dependency, which is what makes it testable. |
| `DexStream.Usb` | `net8.0-windows` | SetupAPI enumeration, WinUSB bulk transport, hot-plug watcher, ADB key storage. |
| `DexStream.Media` | `net8.0-windows` | Media Foundation decode and Direct3D 11 presentation. |
| `DexStream.App` | `net8.0-windows` | WPF shell, session orchestration, input capture. |
| `agent` | Android, plain javac + d8 | Display capture, encoding, input injection. |

Core deliberately has no Windows dependency. Everything with a right answer that can be checked — key
encoding, framing, parsing, coordinate maths — lives there and is covered by tests that run anywhere.

## Transport: ADB over USB, implemented here

DexStream does not shell out to `adb.exe` and does not talk to an ADB server. It speaks the ADB wire
protocol itself, straight to the bulk endpoints of the device's ADB interface.

The reasons are practical. There is no process to install, start or version-match. There is no
localhost TCP socket, so "USB only" is a property of the design rather than a promise. And the host
controls its own flow control, which matters when one stream carries 40 Mbit/s of video.

The ADB interface is a vendor-specific USB interface with class `0xFF`, subclass `0x42`, protocol
`0x01`, with one bulk IN and one bulk OUT endpoint. Windows binds WinUSB to it via
`android_winusb.inf`, and DexStream finds it by the device interface GUID that INF assigns.

### Handshake

adbd will not talk to an unknown host. The exchange is:

1. Host → `CNXN(version, maxdata, "host::features=…")`
2. Device → `AUTH(TOKEN, 20 random bytes)`
3. Host → `AUTH(SIGNATURE, sign(token))` — PKCS#1 v1.5 with the token treated as a SHA-1 digest,
   because adbd verifies with `RSA_verify(NID_sha1, token, 20, …)`
4. If the device does not know the key: Host → `AUTH(RSAPUBLICKEY, base64(struct) + " user@host\0")`,
   and the phone shows its trust prompt
5. Device → `CNXN(version, maxdata, "device::ro.product.name=…")`

Step 4 is the fiddly part. adbd does not accept a SubjectPublicKeyInfo blob; it expects the packed
struct from `libcrypto_utils/android_pubkey.c`: the word count, `n0inv` (`-1/n[0] mod 2³²`), the
modulus little-endian, `R² mod n`, and the exponent. Getting any field wrong produces a silent
rejection, which is why it is checked against an independent implementation in the tests.

DexStream prefers the key `adb` already uses, so a device that has authorized that key connects with no
prompt at all.

### Streams

ADB multiplexes any number of streams over the two endpoints. Every message carries the sender's stream
id in `arg0` and the recipient's in `arg1`, and a `WRTE` must be acknowledged with `OKAY` before the
next one on that stream.

`AdbConnection` runs one reader loop that demultiplexes to per-stream bounded channels, and — this is
the part that matters — it sends `OKAY` only *after* the payload has been queued. That turns the
channel's capacity into real backpressure on the device: if the host stops draining video, the device's
encoder blocks rather than the host's memory growing without bound. A single stalled consumer cannot
wedge the shared reader loop either, because a stream that is not drained within a timeout is dropped
and closed instead.

DexStream opens three streams: `shell:` to launch the agent and read its stderr, and
`localabstract:dexstream` twice, for video and control.

## Why two sockets rather than the shell's stdout

The legacy `shell:` service merges the command's stderr into the same stream as its stdout. A single log
line from the agent would land in the middle of the H.264 bitstream and corrupt it. So the agent opens
an abstract socket and the host connects to it twice; the first connection is video, the second is
control, and the order is how the agent tells them apart. The shell stream then carries nothing but
diagnostics, which is exactly what the app shows in its diagnostics pane.

## Device side

The agent is a plain Java program started with `app_process`, not an APK. It runs as the shell user, so
it has ADB's privileges and nothing more — no root, no installed package, nothing left behind but a jar
in `/data/local/tmp`.

Capture has to cope with the framework having moved twice, so three strategies are probed in order and
the one that worked is logged. See [DEX-ACTIVATION.md](DEX-ACTIVATION.md) for what each needs.

Encoding is configured for interactive latency rather than file size: constant bitrate so the encoder
never saves bits for a later frame, no B-frames so no frame waits for one that comes after it, a
one-frame latency hint where the platform has it, realtime priority, and a deliberately long key-frame
interval — a periodic IDR is a big packet and shows up as a recurring latency spike — with IDRs
requested on demand instead.

## Wire protocol

Big-endian throughout, so the Java side needs no byte swapping: `DataOutputStream` and
`DataInputStream` are already big-endian.

**Video channel.** A fixed 92-byte header (`DEXS`, version, device name, codec four-character code,
width, height, refresh rate in milli-hertz, display id), then a stream of packets:

```
u8 type   u8 flags   u16 reserved   u32 length   u64 presentationTimeUs
```

Types cover codec configuration, a frame, a geometry change, a heartbeat, clipboard text and a log
line. The heartbeat exists so an idle DeX desktop — which produces no frames at all — can be told apart
from a stalled pipeline.

**Control channel.** A `DEXC` handshake, then variable-length messages for key and pointer events,
text, scroll, bitrate, clipboard, screen power, navigation shortcuts and a round-trip probe. Pointer
coordinates are sent with the frame size the host is rendering, so the agent can rescale them to the
display's real size when the capture was scaled down.

## Host video path

One dedicated thread does read, decode and present with no queue between the stages, because every
queue is latency the user feels as input lag.

Decode is a Media Foundation transform handed DexStream's own Direct3D 11 device, so decoded frames
come back as GPU textures rather than being copied through system memory. Conversion from NV12,
scaling and letterboxing are one `VideoProcessorBlt` — fixed-function silicon on every modern GPU —
so there are no shaders, no vertex buffers and no runtime HLSL. The swap chain is flip-model with
tearing allowed and a one-frame latency limit; the default three-frame queue is the wrong trade here.

Decode and present are one component on purpose. The transform still owns the texture it hands back, so
converting and presenting it immediately and releasing it is both the cheapest option and the lowest
latency one.

## Measuring latency honestly

A frame carries the device's monotonic timestamp. The host's clock is unrelated to it, so comparing
them directly would be meaningless. The control channel therefore carries a ping/pong: the device
reports its clock, the host assumes the device handled the ping halfway through the round trip, and
keeps the estimate from the *smallest* round trip observed — the sample least distorted by queuing.
That offset converts a frame's timestamp into host time, and the difference from the present time is
the end-to-end figure in the metrics bar.

The estimate is discarded every couple of minutes so it can follow clock drift instead of being pinned
by one lucky early sample. Until an estimate exists, latency reads as blank rather than as a
nonsensical number.

## Input

Windows virtual-key codes are mapped to Android keycodes in Core, so the table is testable without a
UI. Two details matter:

- **Printable characters are sent as text, not keycodes.** Android has no inject-text API, so the agent
  converts the string with the virtual keyboard's `KeyCharacterMap`. Doing it this way means the
  device's own layout, dead keys and IME decide what gets typed. Keycodes are used for keys with no
  text equivalent, and for shortcuts like Ctrl+C where the device needs the real key.
- **Home maps to `MOVE_HOME`, not `KEYCODE_HOME`.** Mapping it to the Android home button would send
  the DeX desktop to the launcher on every text-navigation keypress.

Mouse input is read from the hosted window's message procedure rather than from WPF, because a child
HWND consumes mouse messages before WPF sees them. Keyboard, text and the wheel stay on the WPF side,
where focus lives. Buttons are captured on press so a drag that leaves the window still delivers the
release, instead of leaving the button stuck down on the device.

## Failure handling

Every stage names its own failure in terms a user can act on: the ADB server holding the USB interface,
the phone not having authorized the key, DeX not running, the agent unable to capture, no HEVC decoder
installed. `DexStreamSession.Describe` turns an exception into one sentence for the status line, and the
full detail goes to the diagnostics pane and the log file.

Reconnection is handled by treating device removal as authoritative: the coordinator disposes the
session whatever else it was doing. Rotation and a DeX resolution change end the current encoding pass,
the device sends a new header, and the pipeline rebuilds at the new geometry without dropping the
session.
