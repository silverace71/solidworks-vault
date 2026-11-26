#!/usr/bin/env bash
# Explicit, scheduled rate-limited local mirror of canonical R2 LFS objects.
# Copy-only, never deletes remote/local objects. Does not replace independent backups.
set -euo pipefail
[[ ${EUID:-$(id -u)} -eq 0 ]] || { echo 'Run with sudo.' >&2; exit 1; }
cd "$(dirname "${BASH_SOURCE[0]}")"
source ./lib.sh
command -v rclone >/dev/null || die 'Install rclone first.'
[[ -s data/r2-rclone.conf ]] || die 'Missing private data/r2-rclone.conf'
bucket="$(env_get R2_BUCKET)"
[[ "$bucket" =~ ^[a-z0-9][a-z0-9.-]{1,61}[a-z0-9]$ ]] || die 'Invalid or missing R2 bucket'
# 600 KiB/sec = ~4.92 megabits/sec; all mirror downloads share one rclone process.
root="${SWVAULT_COLD_MIRROR_PATH:-$SWVAULT_DIR/data/cold-r2-mirror}"
mkdir -p -m 700 "$root"
chmod 700 "$root"
exec 9>"$SWVAULT_DIR/data/cold-mirror.lock"
flock -n 9 || { echo 'Mirror already running'; exit 0; }
echo "Mirroring canonical LFS objects from private R2 to $root at <= 5 Mbps."
rclone --config "$SWVAULT_DIR/data/r2-rclone.conf" copy "r2:$bucket/lfs" "$root" \
  --transfers 1 --checkers 2 --bwlimit 600k --stats 30s --log-level INFO
