#!/usr/bin/env bash
# Staged production migration: moves only Git LFS STORAGE, not public hostname.
# All secrets are entered interactively; working Funnel remains the endpoint.
set -euo pipefail
umask 077
cd "$(dirname "$0")"
[[ $EUID -eq 0 ]] || { echo 'Use sudo to run this script.' >&2; exit 1; }
[[ -r /dev/tty ]] || { echo 'An interactive terminal is required.' >&2; exit 1; }
[[ -s .env && -d data/gitea && -f docker-compose.tailscale-legacy.yml ]] || { echo 'Legacy data, .env, or compose file missing.' >&2; exit 1; }
[[ ! -e .env.r2.production ]] || { echo 'R2 overlay is already configured; do NOT rerun migration. Check the guide.' >&2; exit 1; }
for bin in docker rclone rsync sha256sum; do command -v "$bin" >/dev/null || { echo "Missing required command: $bin" >&2; exit 1; }; done
docker compose version >/dev/null
read -r -p 'Private R2 bucket name [swvault-lfs]: ' bucket </dev/tty
bucket="${bucket:-swvault-lfs}"
read -r -p 'R2 Account ID (32 hex digits): ' account </dev/tty
read -r -p 'R2 Access Key ID: ' access </dev/tty
read -r -s -p 'R2 Secret Access Key (hidden): ' secret </dev/tty; echo >&2
read -r -p 'Independent backup directory, on ANOTHER DISK: ' backup </dev/tty
[[ "$bucket" =~ ^[a-z0-9][a-z0-9.-]{1,61}[a-z0-9]$ ]] || { echo 'Invalid R2 bucket name' >&2; exit 1; }
[[ "$account" =~ ^[[:xdigit:]]{32}$ && -n "$access" && -n "$secret" ]] || { echo 'Invalid or missing credentials' >&2; exit 1; }
[[ "$backup" == /* && "$backup" != /opt/swvault* && "$backup" != "$(pwd)"* && "$backup" != /tmp/* ]] || { echo 'Use an absolute independent backup path.' >&2; exit 1; }
[[ "$access" != *'$'* && "$secret" != *'$'* && "$secret" != *$'\n'* && "$secret" != *$'\r'* ]] || { echo 'Unsupported special characters in env credentials.' >&2; exit 1; }
[[ -z "$(sed -n 's/^CF_DEPLOYMENT=//p' .env | tail -1)" ]] || { echo 'This is not a legacy-only installation.' >&2; exit 1; }
config="$(mktemp)"; envtemp="$(mktemp)"
cleanup() { rm -f "$config" "$envtemp"; }
trap cleanup EXIT
cat > "$config" <<EOC
[r2]
type = s3
provider = Cloudflare
access_key_id = $access
secret_access_key = $secret
endpoint = https://$account.r2.cloudflarestorage.com
region = auto
acl = private
EOC
chmod 600 "$config"
echo 'Preflight: listing R2 bucket with supplied credentials...'
rclone --config "$config" lsf "r2:$bucket" --max-depth 1 >/dev/null || { echo 'R2 credentials/bucket not accessible; nothing changed.' >&2; exit 1; }
cat > "$envtemp" <<EOC
R2_BUCKET=$bucket
R2_ACCOUNT_ID=$account
R2_ACCESS_KEY_ID=$access
R2_SECRET_ACCESS_KEY=$secret
EOC
chmod 600 "$envtemp"
# Verifies compose overlay can expand, before stopping production.
docker compose --env-file .env --env-file "$envtemp" -f docker-compose.tailscale-legacy.yml -f docker-compose.r2-legacy.yml config -q
[[ -d "$backup" ]] || { echo "Backup directory $backup does not exist. Mount a backup disk first." >&2; exit 1; }
backup="$(realpath "$backup")"
[[ "$(df -P "$backup" | tail -1 | awk '{print $1}')" != "$(df -P data/gitea | tail -1 | awk '{print $1}')" ]] || { echo 'Backup must be on a different filesystem.' >&2; exit 1; }
need="$(du -sk data/gitea | awk '{print $1}')"
avail="$(df -Pk "$backup" | tail -1 | awk '{print $4}')"
(( avail > need + 1048576 )) || { echo 'Not enough backup capacity to duplicate Gitea data plus 1 GiB headroom.' >&2; exit 1; }
echo
printf 'Storage-only migration to R2 bucket %s\nExisting Funnel, Gitea ROOT_URL, team.json and Windows client URLs stay unchanged.\nIndependent backup: %s\n\n' "$bucket" "$backup"
read -r -p 'STOP ALL CHECK-INS, take a hypervisor snapshot, then type STORAGE_ONLY: ' confirm </dev/tty
[[ "$confirm" == STORAGE_ONLY ]] || { echo 'Cancelled without changes.'; exit 1; }
# Prevent legacy watchdog/auto-update timers from restarting or overwriting files mid-copy.
systemctl stop swvault-watchdog.timer swvault-watchdog.service swvault-backup.timer swvault-backup.service >/dev/null 2>&1 || true
systemctl disable --now swvault-autoupdate.timer swvault-autoupdate.path >/dev/null 2>&1 || true
stamp="$(date -u +%Y%m%dT%H%M%SZ)"
safe="$backup/swvault-pre-r2-$stamp"
mkdir -p "$safe"
# Stop writers first for a filesystem-consistent SQLite/LFS copy.
docker compose -f docker-compose.tailscale-legacy.yml stop invites gitea
successful=no
recover() {
  local rc=$?
  if [[ "$successful" != yes ]]; then
    echo 'Cutover not complete. Attempting to start the ORIGINAL legacy stack.' >&2
    docker compose -f docker-compose.tailscale-legacy.yml up -d >/dev/null 2>&1 || true
  fi
  exit "$rc"
}
trap 'cleanup; recover' EXIT
echo 'Backing up stopped server (including entire historical LFS store)...'
rsync -a data/gitea/ "$safe/gitea/"
if [[ -d data/invites ]]; then rsync -a data/invites/ "$safe/invites/"; fi
cp -p .env "$safe/old.env"
[[ ! -f team.json ]] || cp -p team.json "$safe/old.team.json"
[[ ! -d data/tailscale ]] || rsync -a data/tailscale/ "$safe/tailscale/"
[[ ! -f data/admin-token ]] || cp -p data/admin-token "$safe/old-admin-token"
# Compare the LFS file tree (offline) prior to migration.
[[ ! -d data/gitea/git/lfs ]] || { diff -q <(cd data/gitea/git/lfs && find . -type f | sort) <(cd "$safe/gitea/git/lfs" && find . -type f | sort) >/dev/null || { echo 'Backup LFS filenames mismatch' >&2; exit 1; }; }
echo "Offline backup created: $safe"
echo 'Copying DB-referenced Gitea LFS objects to R2. Original local objects are retained.'
docker compose -f docker-compose.tailscale-legacy.yml run --rm --no-deps -u git \
    -e "AWS_ACCESS_KEY_ID=$access" -e "AWS_SECRET_ACCESS_KEY=$secret" gitea \
    gitea migrate-storage --type lfs --storage minio \
    --minio-endpoint "$account.r2.cloudflarestorage.com" \
    --minio-bucket "$bucket" --minio-location auto --minio-base-path lfs/ \
    --minio-use-ssl --minio-checksum-algorithm md5 --minio-bucket-lookup-type path
cp "$envtemp" .env.r2.production
cp "$config" data/r2-rclone.conf
chmod 600 .env.r2.production data/r2-rclone.conf
# Even if this start fails, the original local data are intact for manual rollback.
echo 'Restarting EXISTING Tailscale stack with R2 LFS storage enabled.'
docker compose --env-file .env --env-file .env.r2.production -f docker-compose.tailscale-legacy.yml -f docker-compose.r2-legacy.yml up -d
successful=yes
systemctl start swvault-backup.timer swvault-watchdog.timer >/dev/null 2>&1 || true
trap cleanup EXIT
echo
printf 'DONE. Old Funnel URL is unchanged. Offline backup: %s\n' "$safe"
echo 'Before reopening writes: test LFS signed downloads, >100 MB check-in, locks, version history and full restore.'
echo 'Revert only during a maintenance window, and ONLY if no new R2-only check-ins exist.'
