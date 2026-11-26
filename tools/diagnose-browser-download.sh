#!/usr/bin/env bash
set -euo pipefail
cd /opt/swvault/server/linux
PUBLIC_URL='https://vault.lynxmotorsports.racing/swvault/api/download?path=Top%20Level%20Assembly%2FSteering.SLDASM'
LOCAL_URL='http://127.0.0.1:8080/swvault/api/download?path=Top%20Level%20Assembly%2FSteering.SLDASM'
echo 'Testing unauthenticated download route (401 is EXPECTED).'
echo 'This never downloads or exposes vault data.'
for mode in local public; do
  if [[ $mode == local ]]; then url=$LOCAL_URL; else url=$PUBLIC_URL; fi
  printf '%s: ' "$mode"
  curl --ipv4 --silent --show-error --max-time 15 --output /dev/null \
    --write-out 'HTTP %{http_code}\n' "$url" || true
done
echo
echo 'Now click Download while logged in, then review the last 2 minutes:'
echo 'docker compose logs --since=2m --tail=90 invites proxy cloudflared'
echo
cat <<'MSG'
Interpretation:
- public 502, local 401 (unauthenticated): Cloudflare Tunnel / origin routing issue.
- public 401, local 401: public routing works for this path; investigate authenticated handler.
- After an authenticated click, no "request reached Python" log: request did not reach invites.
- A "download rejected before redirect" log: Python/Gitea/R2 authorization failure, inspect status.
- A "redirected LFS" log: SWVAULT issued a direct R2 redirect; inspect the next browser request.
MSG
