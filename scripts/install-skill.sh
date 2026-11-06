#!/usr/bin/env bash
# Installs the "bingeride-workout" Claude skill.
#   scripts/install-skill.sh          -> copies it to ~/.claude/skills (Claude Code, any directory)
#   scripts/install-skill.sh --zip    -> also builds dist/bingeride-workout.zip to upload in claude.ai
#                                        (Settings > Capabilities > Skills > Upload; needs code execution on)
set -euo pipefail
cd "$(dirname "$0")/.."
mkdir -p "$HOME/.claude/skills"
# mywahoo-workout is the skill's name from before the app was renamed.
rm -rf "$HOME/.claude/skills/bingeride-workout" "$HOME/.claude/skills/mywahoo-workout"
cp -r skills/bingeride-workout "$HOME/.claude/skills/"
echo "Installed: $HOME/.claude/skills/bingeride-workout"
if [[ "${1:-}" == "--zip" ]]; then
  mkdir -p dist
  rm -f dist/bingeride-workout.zip
  (cd skills && python3 -c "import shutil; shutil.make_archive('../dist/bingeride-workout', 'zip', '.', 'bingeride-workout')")
  echo "Built: dist/bingeride-workout.zip"
fi
