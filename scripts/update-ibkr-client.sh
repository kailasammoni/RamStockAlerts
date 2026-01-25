#!/usr/bin/env bash
set -euo pipefail

source_dir="${1:-}"
dest_dir="${2:-lib/ibkr/CSharpClient}"

if [[ -z "$source_dir" ]]; then
  echo "Usage: scripts/update-ibkr-client.sh <path-to-CSharpClient> [destination]" >&2
  exit 2
fi

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
src_path="$(cd "$source_dir" && pwd)"
dest_path="$repo_root/$dest_dir"
backup_path="${dest_path}.bak.$(date +%Y%m%d%H%M%S)"

if [[ ! -f "$src_path/CSharpAPI.csproj" ]]; then
  echo "Warning: CSharpAPI.csproj not found under $src_path. Make sure you passed the 'CSharpClient' folder." >&2
fi

if [[ -d "$dest_path" ]]; then
  mv "$dest_path" "$backup_path"
fi

mkdir -p "$dest_path"

rsync -a --delete \
  --exclude "bin" \
  --exclude "obj" \
  --exclude ".git" \
  --exclude ".vs" \
  "$src_path/" "$dest_path/"

echo "IBKR CSharpClient updated at $dest_path"
if [[ -d "$backup_path" ]]; then
  echo "Backup kept at $backup_path"
fi
