#!/usr/bin/env bash
# Verify that a real Gitea LFS batch response gives a signed, direct Cloudflare R2 GET URL.
# For large vaults, make only a one-byte range GET. Do not print presigned URLs (bearer credentials).
set -euo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/lib.sh"
[[ $EUID -eq 0 ]] || die 'Run as root: sudo ./verify-r2-direct.sh [OID SIZE]'
[[ -s "$ADMIN_TOKEN_FILE" ]] || die 'Missing admin token. Run setup first.'
command -v jq >/dev/null || die 'jq not installed'
OID="${1:-}"; SIZE="${2:-}"
if [[ -z "$OID" || -z "$SIZE" ]]; then
    command -v sqlite3 >/dev/null || {
        warn 'sqlite3 is needed for automatic sample selection; supply an LFS SHA-256 OID and size manually.'
        exit 2
    }
    db="$(find "$DATA_DIR/gitea" -type f -name '*.db' -print -quit 2>/dev/null || true)"
    [[ -n "$db" ]] || die 'Could not locate the Gitea SQLite database; pass OID and SIZE manually.'
    pair="$(sqlite3 -separator ' ' "$db" 'SELECT oid, size FROM lfs_meta_object WHERE size > 0 LIMIT 1;' 2>/dev/null || true)"
    [[ -n "$pair" ]] || die 'No Git LFS objects found yet. Upload a small file then rerun, or provide OID and SIZE.'
    read -r OID SIZE <<< "$pair"
fi
[[ "$OID" =~ ^[a-fA-F0-9]{64}$ ]] || die 'Expected a 64-hex-character Git LFS OID'
[[ "$SIZE" =~ ^[0-9]+$ ]] || die 'Expected a file size in bytes'
url="http://127.0.0.1:3000/$(env_get ORG)/$(env_get REPO).git/info/lfs/objects/batch"
body="$(jq -n --arg oid "$OID" --argjson size "$SIZE" '{operation:"download",transfers:["basic"],objects:[{oid:$oid,size:$size}]}')"
response="$(curl -fsS --max-time 20 -u "$(env_get ADMIN_USER):$(cat "$ADMIN_TOKEN_FILE")" \
    -H 'Accept: application/vnd.git-lfs+json' -H 'Content-Type: application/vnd.git-lfs+json' \
    --data "$body" "$url")" || die 'LFS batch request failed; verify the admin token and repository.'
href="$(jq -r '.objects[0].actions.download.href // empty' <<< "$response")"
[[ -n "$href" ]] || die "No LFS download action returned: $(jq -c '.objects[0].error // {}' <<< "$response")"
account="$(env_get R2_ACCOUNT_ID)"
if [[ -z "$account" && -f "$SWVAULT_DIR/.env.r2.production" ]]; then
    account="$(sed -n 's/^R2_ACCOUNT_ID=//p' "$SWVAULT_DIR/.env.r2.production" | tail -1)"
fi
[[ "$href" == "https://${account}.r2.cloudflarestorage.com/"* ]] || die 'FAIL: LFS download still points through the SWVAULT server, not R2.'
[[ "$href" == *'X-Amz-Signature='* || "$href" == *'X-Amz-Algorithm='* ]] || die 'FAIL: R2 download URL is not signed.'
say "PASS: batch action points directly to private R2 for OID ${OID:0:12}... (never showing signed URL)"
# Fetch a single byte to verify the actual signed URL works, not merely its hostname.
status="$(curl -sSL -r 0-0 --max-time 30 --max-filesize 1024 -o /dev/null -w '%{http_code}' "$href")" || die 'FAIL: signed R2 URL did not respond to a one-byte range GET.'
[[ "$status" == 206 ]] || die "FAIL: R2 expected HTTP 206 for a one-byte range request, got $status"
say 'PASS: a client can download bytes directly from R2; Cloudflare Tunnel is not in the file path.'
echo 'NOTE: this verifies direct downloads. Separately pilot a 500 MB client multipart UPLOAD.'
