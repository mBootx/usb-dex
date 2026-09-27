#!/usr/bin/env bash
# Type-checks the device agent against signature-only Android stubs.
#
# This needs nothing but a JDK, so a signature or syntax mistake in the agent is caught here rather
# than at release time. It does not produce a runnable artifact: use build.sh for that, which
# compiles against the real android.jar and converts the result with d8.
set -euo pipefail

cd "$(dirname "$0")"
out="build/verify"
rm -rf "$out"
mkdir -p "$out"

mapfile -t sources < <(find stubs src/main/java -name '*.java' | sort)

echo "Type-checking ${#sources[@]} files against the Android stubs..."
javac \
  -Xlint:all \
  -implicit:none \
  -d "$out" \
  "${sources[@]}"

echo "OK: the agent type-checks."
