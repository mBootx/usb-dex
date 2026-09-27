#!/usr/bin/env bash
# Builds the DexStream device agent into a dex-bearing jar.
#
# The agent is a plain Java program run by app_process on the phone, not an APK, so it needs no
# Gradle and no manifest: javac against the platform android.jar, then d8 to produce classes.dex,
# then a jar containing that dex. Keeping the build to two SDK tools makes it reproducible and quick
# enough to run on every CI push.
set -euo pipefail

cd "$(dirname "$0")"

out="build"
classes="$out/classes"
jar="$out/dexstream-agent.jar"

# --- Locate the Android SDK -------------------------------------------------------------------
sdk="${ANDROID_HOME:-${ANDROID_SDK_ROOT:-}}"
if [[ -z "$sdk" ]]; then
  for candidate in "$HOME/Android/Sdk" "$HOME/Library/Android/sdk" /usr/local/lib/android/sdk; do
    if [[ -d "$candidate" ]]; then
      sdk="$candidate"
      break
    fi
  done
fi

if [[ -z "$sdk" || ! -d "$sdk" ]]; then
  echo "error: the Android SDK was not found." >&2
  echo "Set ANDROID_HOME, or run ./verify.sh to type-check without the SDK." >&2
  exit 1
fi

# Highest installed platform: the agent targets old API levels at runtime but compiles against the
# newest jar so that APIs guarded by a Build.VERSION check still resolve.
android_jar="${ANDROID_JAR:-}"
if [[ -z "$android_jar" ]]; then
  android_jar=$(find "$sdk/platforms" -maxdepth 2 -name android.jar 2>/dev/null | sort -V | tail -1)
fi

if [[ -z "$android_jar" || ! -f "$android_jar" ]]; then
  echo "error: no android.jar found under $sdk/platforms." >&2
  echo "Install a platform with: sdkmanager 'platforms;android-34'" >&2
  exit 1
fi

d8="${D8:-}"
if [[ -z "$d8" ]]; then
  d8=$(find "$sdk/build-tools" -maxdepth 2 -name 'd8' -type f 2>/dev/null | sort -V | tail -1)
fi

if [[ -z "$d8" || ! -x "$d8" ]]; then
  echo "error: d8 was not found under $sdk/build-tools." >&2
  echo "Install build tools with: sdkmanager 'build-tools;34.0.0'" >&2
  exit 1
fi

echo "android.jar: $android_jar"
echo "d8:          $d8"

# --- Compile ---------------------------------------------------------------------------------
rm -rf "$out"
mkdir -p "$classes"

mapfile -t sources < <(find src/main/java -name '*.java' | sort)
echo "Compiling ${#sources[@]} files..."

# Source and target 8: d8 desugars, and an older bytecode level keeps the agent loadable on the
# oldest Android release DexStream supports. javac warns that -source 8 without a matching
# bootclasspath is unsafe; that warning is expected here because android.jar *is* the bootclasspath.
javac \
  -source 8 \
  -target 8 \
  -nowarn \
  -classpath "$android_jar" \
  -bootclasspath "$android_jar" \
  -d "$classes" \
  "${sources[@]}" 2>&1 | grep -v 'bootstrap class path' || true

if ! compgen -G "$classes/com/dexstream/agent/*.class" > /dev/null; then
  echo "error: compilation produced no classes." >&2
  exit 1
fi

# --- Convert to dex and package --------------------------------------------------------------
echo "Converting to dex..."
mapfile -t class_files < <(find "$classes" -name '*.class' | sort)
"$d8" --release --min-api 24 --lib "$android_jar" --output "$out" "${class_files[@]}"

if [[ ! -f "$out/classes.dex" ]]; then
  echo "error: d8 produced no classes.dex." >&2
  exit 1
fi

# app_process reads the dex straight out of the jar, so the jar needs nothing but classes.dex.
( cd "$out" && jar --create --file "$(basename "$jar")" classes.dex )

echo
echo "Built $jar ($(du -h "$jar" | cut -f1))"
echo "Push it with: adb push $jar /data/local/tmp/dexstream-agent.jar"
