#!/usr/bin/env bash
# Creates local sensitive, root-only config archive. Do NOT commit/upload this file.
set -euo pipefail
umask 077
cd "$(dirname "$0")"
[[ $EUID -eq 0 ]] || { echo 'Run with sudo.' >&2; exit 1; }
[[ $# == 1 && "$1" == /* ]] || { echo 'Usage: sudo ./export-private-config.sh /mounted-independent-disk/backup' >&2; exit 1; }
output="$1"
[[ -d "$output" ]] || { echo 'Backup destination must already exist.' >&2; exit 1; }
output="$(realpath "$output")"
[[ "$output" != "$(pwd)"* ]] || { echo 'Do not store inside the repository.' >&2; exit 1; }
items=()
for item in .env .env.r2.production team.json data/admin-token data/admin-credentials.txt \
 data/cloudflare/tunnel-token data/r2-rclone.conf data/tailscale data/invites \
 data/gitea/gitea/conf/app.ini; do
 [[ ! -e "$item" ]] || items+=("$item")
done
((${#items[@]})) || { echo 'No config to back up.' >&2; exit 1; }
archive="$output/swvault-PRIVATE-config-$(date -u +%Y%m%dT%H%M%SZ).tar.gz"
tar -czf "$archive" "${items[@]}"
chmod 600 "$archive"
echo "Stored sensitive configuration archive at $archive (mode 600)."
echo 'Keep off GitHub. Encrypt at rest. This does not include repositories or LFS objects.'
