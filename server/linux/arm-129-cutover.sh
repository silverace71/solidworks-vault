#!/usr/bin/env bash
# Manual final approval of the ONE-TIME 1.2.9 -> 1.4 hostname cutover.
set -euo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/lib.sh"
[[ $EUID == 0 ]] || die 'Use sudo.'
[[ "$(env_get CF_DEPLOYMENT)" == cloudflare ]] || die 'Only for new 1.4 Cloudflare VM.'
old="$(env_get PREVIOUS_VAULT_URL)"
[[ "$old" == https://*.ts.net/* ]] || die 'No source 1.2.9 Tailscale URL was imported.'
[[ ! -e "$DATA_DIR/r2-transfer/migration-ready.json" ]] || die 'Cutover already armed.'
[[ -s "$ADMIN_TOKEN_FILE" ]] || die 'Admin token missing.'
"$SWVAULT_DIR/verify-r2-direct.sh"
curl -fsS --max-time 20 'http://127.0.0.1:3200/swvault-transfer/health' | jq -e '.direct_r2_uploads == true' >/dev/null || die 'Upload coordinator unhealthy'
head="$(api GET "repos/$(env_get ORG)/$(env_get REPO)/branches/main" | jq -r '.commit.id // empty')"
[[ "$head" =~ ^[0-9a-f]{40,64}$ ]] || die 'Migrated repo main is missing.'
printf '\nOLD FROZEN VAULT: %s\nNEW VAULT: %s\nNEW main commit: %s\n' "$old" "$(vault_url)" "$head"
cat <<'NOTE'
Before arming: confirm original 1.2.9 writes remain STOPPED, the backup is verified,
1.4 Windows pilot sign-in works, a >100MB file checks in DIRECTLY to R2,
get-latest uses direct signed R2 GET, and a complete cold-storage copy exists.
NOTE
read -r -p 'Type PILOT_PASSED_AND_OLD_FROZEN to allow client auto-migration: ' approval </dev/tty
[[ "$approval" == PILOT_PASSED_AND_OLD_FROZEN ]] || die 'Cutover not armed.'
mkdir -p "$DATA_DIR/r2-transfer"
ready="$(mktemp "$DATA_DIR/r2-transfer/.ready.XXXXXX")"
jq -n --arg source "$old" --arg target "$(vault_url)" --arg head "$head" \
  '{ready:true,source:$source,target:$target,importedHead:$head}' > "$ready"
chown 1000:1000 "$ready"; chmod 600 "$ready"
mv "$ready" "$DATA_DIR/r2-transfer/migration-ready.json"
echo 'ARMED. Updated 1.4 Agents switch after their next migration check and restart.'
