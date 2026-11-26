#!/usr/bin/env bash
# Read-only checks. Does not arm migration, change R2, or publish releases.
set -euo pipefail
cd "$(dirname "$0")"
source ./lib.sh
printf '\n=== SWVAULT services ===\n'
compose ps
printf '\n=== Public HTTP local routes ===\n'
curl -fsS --max-time 10 http://127.0.0.1:8080/swvault-transfer/health
printf '\n=== Private R2 canonical files ===\n'
bucket="$(env_get R2_BUCKET)"
[[ -s data/r2-rclone.conf ]] || die 'Missing R2 rclone config'
rclone --config data/r2-rclone.conf size "r2:$bucket/lfs"
printf '\n=== Signed LFS download ===\n'
# This checks a real signed GET against an existing object, never prints the URL.
bash ./verify-r2-direct.sh
printf '\nWARNING: This does NOT test a Windows check-in, web redirect, or cold-mirror restore.\n'
