#!/usr/bin/env bash
# Read-only configuration inventory. Never prints secret values.
set -euo pipefail
cd "$(dirname "$0")"
echo 'SWVAULT configuration file inventory (no credential contents):'
for item in .env .env.r2.production team.json data/gitea/gitea/conf/app.ini \
            data/admin-token data/admin-credentials.txt data/cloudflare/tunnel-token \
            data/r2-rclone.conf data/tailscale data/invites \
            docker-compose.yml docker-compose.tailscale-legacy.yml \
            docker-compose.r2-legacy.yml docker-compose.tailscale-bridge.yml; do
    if [[ -e "$item" ]]; then echo "  EXISTS: $item"; else echo "  absent: $item"; fi
done
if [[ -f .env ]]; then
  echo; echo 'Environment SETTING NAMES ONLY (values redacted):'
  sed -nE 's/^([A-Za-z_][A-Za-z_0-9]*)=.*/  \1=[REDACTED]/p' .env | sort -u
fi
if [[ -f .env.r2.production ]]; then
  echo 'R2 production overlay keys (redacted):'
  sed -nE 's/^([A-Za-z_][A-Za-z_0-9]*)=.*/  \1=[REDACTED]/p' .env.r2.production | sort -u
fi
