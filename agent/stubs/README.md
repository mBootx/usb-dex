# Android API stubs

These are signature-only stand-ins for the public Android classes the agent uses. They exist so the
agent can be type-checked with nothing but a JDK — no Android SDK, no Gradle — which is what
`verify.sh` does and what makes a syntax or signature mistake visible immediately rather than at
release time.

They are **never** used to produce the shipped agent. `build.sh` compiles against the real
`android.jar` from the platform SDK and converts the result with `d8`.

Only public SDK surface belongs here. Everything the agent reaches reflectively (SurfaceControl,
DisplayManagerGlobal, InputManager, IClipboard, Samsung's SemDesktopModeManager) is deliberately
absent, because reflection is compiled against no signature at all.
