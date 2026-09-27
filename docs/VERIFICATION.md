# What has been verified, and what has not

DexStream was written without access to a Windows machine or a Galaxy device. That shapes what can
honestly be claimed about it, so this page says exactly which parts are proven, how they were proven,
and which parts still need a first run on real hardware.

Read this before filing a bug, and before assuming any figure in the README is measured.

## Proven by tests that run on every push

These run in CI on Linux and Windows, and can be run locally with `dotnet test`.

| Area | How it is verified |
| --- | --- |
| ADB message framing | Round-trip encode/parse, plus the four-character command codes checked against their ASCII values, and rejection of a corrupted magic field or an oversized payload. |
| Android public-key format | The packed 524-byte `RSAPublicKey` struct is compared byte-for-byte against output from an independent Python implementation of `android_pubkey.c`, for a pinned key. `n0inv` is additionally checked against its defining property, `n · n0inv ≡ -1 (mod 2³²)`. |
| ADB token signing | The signature is compared against one produced by `openssl pkeyutl -sign -pkeyopt digest:sha1`, which is the exact call adbd verifies against. |
| CNXN/AUTH handshake | Run end to end against an in-memory fake adbd that rebuilds the modulus from the packed struct the host sends and verifies the host's signature with it — the same check a phone makes before showing the trust prompt. Both the already-trusted path and the "device asks for the public key" path are covered. |
| Stream multiplexing and flow control | Distinct stream ids, write acknowledgement, refusal of an unknown service, and a file pushed through the sync service byte-for-byte including the multi-chunk case. |
| `dumpsys display` parsing | Parsed against representative Samsung output: logical display ids rather than list positions, the active mode's refresh rate resolved from the mode list, flags, power state, and the maximum resolution and refresh rate. |
| DeX display selection | The ranking across a named DeX display, an agent-created display, an unlabelled secondary display and the phone panel, including ties and powered-off displays. |
| DexStream wire protocol | Header and packet round-trips, rejection of a bad magic, a version mismatch and implausible geometry, device-name truncation without splitting a UTF-8 character or a surrogate pair, and a stream delivered one byte at a time. |
| Control message encoding | Byte-exact expectations for every message type, and the fixed-point clamping for pressure and scroll. |
| Input mapping | Win32 virtual keys to Android keycodes, including that Home maps to `MOVE_HOME` and not `KEYCODE_HOME`, and the meta-state bits pinned against `android.view.KeyEvent`. |
| Viewport geometry | Letterbox and pillarbox maths, integer scaling, coordinate mapping and clamping, degenerate sizes. |
| Metrics | Percentiles, the sliding rate meter, and the clock synchroniser's offset derivation including drift recovery. |
| Settings | Round-trip, a corrupt file falling back to defaults, and atomic replacement. |

224 tests. `dotnet test tests/DexStream.Core.Tests`.

## Proven to compile

- **Every C# project**, on Windows CI. The Linux leg builds everything except the WPF app, because
  the Linux .NET SDK package does not ship the Windows Desktop SDK.
- **The device agent**, twice: `agent/verify.sh` type-checks it against signature-only Android stubs
  with nothing but a JDK, and `agent/build.sh` compiles it against the real platform `android.jar`
  and converts it with `d8`.
- **Both deliverables are produced by CI**: the single-file executable and the MSI are build
  artifacts of every green run, so neither is a claim about a script that was never executed. The
  Windows job takes the agent jar from the agent job and refuses to continue if it is absent, so a
  published executable always has the agent embedded.

The Media Foundation and Direct3D 11 code is written against API signatures read out of the Vortice
assemblies by reflection rather than from memory, so the names, overloads and struct layouts are
correct. Interface IIDs come from `Type.GUID` rather than being hardcoded.

## Not verified: needs a Windows machine

Compiling is not running. None of the following has been executed:

- **WinUSB transport.** Opening the ADB interface, the pipe policies, and the bulk read and write
  loops. The pipe policy combination (`ALLOW_PARTIAL_READS` on, `AUTO_FLUSH` off) is what gives the
  bulk IN pipe byte-stream semantics; if it is wrong, ADB framing desynchronises immediately and
  loudly rather than subtly.
- **Media Foundation decode.** Type negotiation, the D3D11 device handover, and the
  `ProcessInput`/`ProcessOutput` loop.
- **Direct3D presentation.** Swap chain creation, the video processor, and `VideoProcessorBlt`.
- **The WPF shell**, including whether mouse capture and the airspace split behave as intended.
- **The MSI**, beyond the fact that WiX builds it.

## Not verified: needs a Galaxy device

- **Whether DeX can be captured at all on a given One UI build.** This is the single biggest open
  question, and it is a question about Android, not about this code. Screen capture from the shell
  user has changed twice: `SurfaceControl.createDisplay` was removed in Android 14, and its
  replacement needs a hidden `VirtualDisplayConfig` method. The agent probes three strategies in
  order and logs which one worked, so the first run on a device will say. See
  [DEX-ACTIVATION.md](DEX-ACTIVATION.md).
- **Input injection into a non-default display.** Requires `InputEvent.setDisplayId`, which is hidden.
  Its absence is treated as a hard failure rather than silently sending input to the phone's own
  screen.
- **Whether a Samsung encoder accepts the requested resolution and refresh rate.**
- **The latency figure.** The design targets under 20 ms end to end and the app measures it — the
  clock synchroniser exists precisely so the number displayed is real rather than estimated — but no
  measurement has been taken. Treat the 20 ms target as a design goal, not a result.
- **The Samsung DeX state query**, which uses One UI classes that are not documented.

## How to finish the verification

1. On a Windows 10 or 11 machine, run the portable executable with a Galaxy device attached and USB
   debugging on. Open the diagnostics pane before connecting.
2. If it fails, the pane and `%LOCALAPPDATA%\DexStream\dexstream.log` will name the stage. The agent's
   own stderr is relayed into the same pane, so a capture failure on the device side appears there
   too.
3. The most informative single line is the one the agent logs saying which capture strategy
   succeeded, or the `UnsupportedOperationException` explaining why none did.
