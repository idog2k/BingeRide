#!/usr/bin/env bash
# Builds BingeRide.exe (self-contained, single file) and copies it to the Windows side.
# Usage: scripts/publish-windows.sh [target dir, default %LOCALAPPDATA%\Programs\BingeRide]
set -euo pipefail
cd "$(dirname "$0")/.."
export PATH="$HOME/.dotnet:$PATH" DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1

win_user=$(/mnt/c/Windows/System32/cmd.exe /c "echo %USERNAME%" 2>/dev/null | tr -d '\r')
target="${1:-/mnt/c/Users/$win_user/AppData/Local/Programs/BingeRide}"

dotnet publish src/BingeRide.Windows -c Release -r win-x64 --self-contained \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true \
  -p:DebugType=none -o publish/win-x64

mkdir -p "$target"
cp publish/win-x64/BingeRide.exe "$target/"
echo "Published: $target/BingeRide.exe ($(du -h "$target/BingeRide.exe" | cut -f1))"

# The app used to be called MyWahoo: remove the old exe so a leftover shortcut can't start it.
legacy="$(dirname "$target")/MyWahoo"
if [[ -z "${1:-}" && -e "$legacy/MyWahoo.exe" ]]; then
  if rm -f "$legacy/MyWahoo.exe" 2>/dev/null; then
    rmdir "$legacy" 2>/dev/null || true
    echo "Removed the old $legacy/MyWahoo.exe"
  else
    echo "MyWahoo.exe (the old name) is still running: close it, then delete $legacy"
  fi
fi
