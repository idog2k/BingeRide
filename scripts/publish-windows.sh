#!/usr/bin/env bash
# Builds MyWahoo.exe (self-contained, single file) and copies it to the Windows side.
# Usage: scripts/publish-windows.sh [target dir, default %LOCALAPPDATA%\Programs\MyWahoo]
set -euo pipefail
cd "$(dirname "$0")/.."
export PATH="$HOME/.dotnet:$PATH" DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1

win_user=$(/mnt/c/Windows/System32/cmd.exe /c "echo %USERNAME%" 2>/dev/null | tr -d '\r')
target="${1:-/mnt/c/Users/$win_user/AppData/Local/Programs/MyWahoo}"

dotnet publish src/MyWahoo.Windows -c Release -r win-x64 --self-contained \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true \
  -p:DebugType=none -o publish/win-x64

mkdir -p "$target"
cp publish/win-x64/MyWahoo.exe "$target/"
echo "Published: $target/MyWahoo.exe ($(du -h "$target/MyWahoo.exe" | cut -f1))"
