# Building

## What you need

| To build | Requires |
| --- | --- |
| Everything, including the app and both artifacts | Windows, .NET 8 SDK |
| Core, USB, media, tests | Any OS, .NET 8 SDK |
| The device agent | Any OS, JDK 17+, Android SDK (platform + build-tools) |
| The agent type-check only | Any OS, JDK 17+ |

## On Windows

```
dotnet build DexStream.sln -c Release
dotnet test DexStream.sln -c Release
```

The portable executable — the primary deliverable, one self-contained file with no prerequisites:

```
dotnet publish src/DexStream.App/DexStream.App.csproj -c Release -o artifacts/portable
```

The publish properties (`win-x64`, self-contained, single file, compressed) are set in the project, so
no extra flags are needed. The result is `artifacts/portable/DexStream.exe`.

The installer, a per-user MSI that needs no elevation:

```
dotnet tool install --global wix --version 5.0.2
wix build installer/DexStream.wxs -define "PublishDir=<full path to artifacts/portable/>" -define "Version=0.1.0.0" -out artifacts/DexStream.msi
```

`PublishDir` must be an absolute path ending in a separator.

## On Linux or macOS

The Windows Desktop SDK is not part of the .NET SDK package on these platforms, so the WPF app cannot
be built. Everything else can:

```
dotnet build DexStream.Linux.slnf -c Release
dotnet test tests/DexStream.Core.Tests -c Release
```

The app's C# can still be type-checked, which catches most mistakes without a Windows machine:

```
dotnet build tools/DexStream.App.TypeCheck/DexStream.App.TypeCheck.csproj -c Release
```

That project compiles the app's source against the WPF reference assemblies, which NuGet supplies
anywhere. It cannot compile the XAML, so the markup compiler's generated members are stood in for by
`XamlGeneratedStubs*.cs`. If it fails while the Windows build passes, those stubs have drifted from the
`x:Name`s in the XAML — the error says which name is missing.

## The device agent

```
agent/verify.sh     # type-check against signature-only Android stubs; needs only a JDK
agent/build.sh      # the real build; needs the Android SDK
```

`build.sh` finds `android.jar` and `d8` under `$ANDROID_HOME`, or set them explicitly:

```
ANDROID_JAR=/path/to/android.jar D8=/path/to/d8 agent/build.sh
```

The output is `agent/build/dexstream-agent.jar`. **Build it before the app**: when the jar exists, the
app project embeds it so the single executable is self-contained. Without it the app still builds and
falls back to a jar next to the executable, and the UI says which it used.

```
agent/build.sh && dotnet publish src/DexStream.App/DexStream.App.csproj -c Release -o artifacts/portable
```

`verify.sh` exists because the stubs let a signature or syntax mistake be caught with nothing but a JDK.
It never produces a runnable artifact — the shipped agent always comes from `build.sh` and the real
`android.jar`.

## Iterating on the agent without rebuilding the host

Push a freshly built jar and let the app pick it up from disk:

```
agent/build.sh
copy agent\build\dexstream-agent.jar <folder containing DexStream.exe>
```

A jar next to the executable takes precedence over nothing — the embedded copy wins — so for this to
work, publish a build made without the jar present, or run the app from `bin/` where the repository
lookup finds `agent/build/` automatically.

## Continuous integration

`.github/workflows/build.yml` has three jobs:

- **core** (Linux): builds the solution filter, runs the tests, type-checks the app's C#.
- **windows**: builds the whole solution, runs every test, publishes the portable executable and builds
  the MSI. Both are uploaded as artifacts, so neither is a claim about a script nobody ran.
- **agent** (Linux): the stub type-check, then the real `javac` + `d8` build, uploading the jar.

## Layout

```
src/DexStream.Core     platform-neutral protocol, parsing and maths — where the tests live
src/DexStream.Usb      SetupAPI and WinUSB
src/DexStream.Media    Media Foundation and Direct3D 11
src/DexStream.App      WPF shell and session orchestration
agent/                 the Android-side agent, its stubs and its build scripts
tests/                 xUnit suites
tools/                 the app type-check project
installer/             the WiX package
docs/                  this documentation
```
