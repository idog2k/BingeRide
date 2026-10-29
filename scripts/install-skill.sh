#!/usr/bin/env bash
# Installs the "mywahoo-workout" Claude skill.
#   scripts/install-skill.sh          -> copies it to ~/.claude/skills (Claude Code, any directory)
#   scripts/install-skill.sh --zip    -> also builds dist/mywahoo-workout.zip to upload in claude.ai
#                                        (Settings > Capabilities > Skills > Upload; needs code execution on)
set -euo pipefail
cd "$(dirname "$0")/.."
mkdir -p "$HOME/.claude/skills"
rm -rf "$HOME/.claude/skills/mywahoo-workout"
cp -r skills/mywahoo-workout "$HOME/.claude/skills/"
echo "Installed: $HOME/.claude/skills/mywahoo-workout"
if [[ "${1:-}" == "--zip" ]]; then
  mkdir -p dist
  rm -f dist/mywahoo-workout.zip
  (cd skills && python3 -c "import shutil; shutil.make_archive('../dist/mywahoo-workout', 'zip', '.', 'mywahoo-workout')")
  echo "Built: dist/mywahoo-workout.zip"
fi
