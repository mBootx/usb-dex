# Supported devices

## Requirements

- A Samsung Galaxy device with **DeX**, which means a Galaxy S, Note, Z Fold or Tab S flagship from the
  Galaxy S8 (2017) onwards.
- **Android 10 or later.** The agent uses `MediaCodec` with a `Surface` input and framework display
  APIs that behave predictably from Android 10 on.
- **USB debugging** enabled, and a cable that carries data.

DeX itself is a Samsung feature. On a non-Samsung Android device DexStream will still mirror a display
if capture works, but there is no DeX desktop to stream.

## Recognised models

The app reads `ro.product.model` after connecting and reports whether that model is known to ship DeX.
The catalog matches on model-code prefix rather than listing every regional SKU, so `SM-S938B`,
`SM-S938U` and `SM-S938N` are all recognised as a Galaxy S25 Ultra.

| Family | Model codes | Marketing name |
| --- | --- | --- |
| Galaxy S25 | `SM-S931`, `SM-S936`, `SM-S937`, `SM-S938` | S25, S25+, S25 Edge, S25 Ultra |
| Galaxy S24 | `SM-S921`, `SM-S926`, `SM-S928` | S24, S24+, S24 Ultra |
| Galaxy S23 | `SM-S911`, `SM-S916`, `SM-S918` | S23, S23+, S23 Ultra |
| Galaxy S22 | `SM-S901`, `SM-S906`, `SM-S908` | S22, S22+, S22 Ultra |
| Galaxy S21 | `SM-G991`, `SM-G996`, `SM-G998` | S21, S21+, S21 Ultra |
| Galaxy S20 | `SM-G981`, `SM-G986`, `SM-G988` | S20, S20+, S20 Ultra |
| Galaxy S10 and older | `SM-G97`, `SM-G96`, `SM-G95` | S10, S9, S8 |
| Galaxy Note | `SM-N95` … `SM-N98` | Note8 through Note20 |
| Galaxy Z Fold | `SM-F926` … `SM-F958` | Fold3 through Fold7 |
| Galaxy Tab S | `SM-X8`, `SM-X9`, `SM-T9` | Tab S series |

A model that is not listed is reported as "DeX support unknown" and DexStream tries anyway. That is
deliberate: the list is a convenience, not a gate, and a newer Galaxy will work before this table is
updated.

Add a model by editing `KnownModels` in
[`src/DexStream.Core/Device/SamsungDeviceCatalog.cs`](../src/DexStream.Core/Device/SamsungDeviceCatalog.cs).

## Resolution and refresh rate

DexStream reads the DeX display's mode list from the device and picks the largest resolution, then the
highest refresh rate available at that resolution. In practice:

- **DeX on a monitor** (USB-C to HDMI, or a DeX dock) exposes the resolution the monitor negotiated,
  commonly 1920×1080 or 3840×2160 at 60 Hz. DeX itself caps at 60 Hz on most devices even where the
  phone's own panel runs at 120 Hz.
- **A display DexStream creates** can be given any size the encoder accepts, which is where refresh
  rates above 60 Hz are actually reachable.
- **Mirroring the phone panel**, the fallback when DeX is not running, follows the panel: typically
  1440×3120 at up to 120 Hz on an S24/S25 Ultra.

The resolution and refresh rate the app settles on are shown in the metrics bar. A cap can be set in
settings, which is worth doing on USB 2.0 — see [TROUBLESHOOTING.md](TROUBLESHOOTING.md).

## USB

| Connection | Practical ceiling | Notes |
| --- | --- | --- |
| USB 3.x (SuperSpeed) | Comfortably above any useful bitrate | What the Galaxy S25 Ultra supports with the right cable. |
| USB 2.0 (high speed) | Roughly 250–280 Mbit/s of bulk throughput | Plenty for a 40 Mbit/s stream. Latency is higher and jitter is worse. |

A USB 2.0 cable in a USB 3 port still negotiates USB 2.0. Many bundled charging cables have no data
pairs at all, in which case no ADB interface appears.

## Windows

- Windows 10 version 1809 or later, or Windows 11.
- x64. There is no ARM64 build yet; the publish profile would need a second runtime identifier.
- A GPU supporting Direct3D 11.0 with video decode — every discrete GPU and every Intel iGPU since
  Haswell. H.264 decode is universal; HEVC needs either a reasonably recent GPU or the HEVC Video
  Extension from the Microsoft Store.
