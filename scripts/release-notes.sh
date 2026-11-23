#!/usr/bin/env bash
# Prints the CHANGELOG.md section for a version, e.g.: scripts/release-notes.sh 0.1.0
set -euo pipefail
version="$1"
notes=$(awk -v v="$version" '
  $0 ~ "^## \\[" v "\\]" { on = 1; next }
  on && /^## \[/ { exit }
  on { print }
' "$(dirname "$0")/../CHANGELOG.md")
if [[ -z "${notes//[[:space:]]/}" ]]; then
  echo "No CHANGELOG.md section for $version" >&2
  exit 1
fi
printf '%s\n\nInstall: `dotnet tool install -g AgentBlackbox --version %s` or download a binary below.\n' "$notes" "$version"
