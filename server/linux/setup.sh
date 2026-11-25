#!/usr/bin/env bash
# SWVAULT 1.4 Cloudflare/R2 fresh VM setup. Existing 1.2.9 requires one-time import guide.
# Ask every question first, validate private R2 access, then apply fresh-VM changes.
# Create a remotely managed tunnel and published hostname in Cloudflare BEFORE running this.
set -euo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/lib.sh"
[[ $EUID -eq 0 ]] || die "Run as root: sudo ./setup.sh"
[[ "$(uname -s)" == Linux ]] || die "Linux only"
[[ -r /dev/tty ]] || die "Interactive terminal required"
umask 077

ask_value() {
    local key="$1" text="$2" default="$3" answer
    if [[ "${SWVAULT_WIZARD:-no}" == yes ]]; then
        answer="$(env_get "$key")"
        [[ -n "$answer" || "$key" == SW_VERSION ]] || die "Wizard value $key is missing; rerun install.sh"
        printf '%s' "$answer"
        return
    fi
    read -r -p "$text [$default]: " answer </dev/tty
    printf '%s' "${answer:-$default}"
}
ask_secret() {
    local text="$1" answer
    if [[ "${SWVAULT_WIZARD:-no}" == yes ]]; then
        case "$text" in
            Cloudflare*) answer="$(env_get CF_TUNNEL_TOKEN)" ;;
            'R2 Access Key ID'*) answer="$(env_get R2_ACCESS_KEY_ID)" ;;
            'R2 Secret Access Key'*) answer="$(env_get R2_SECRET_ACCESS_KEY)" ;;
            *) die "Unknown wizard secret request: $text" ;;
        esac
        [[ -n "$answer" ]] || die "Wizard secret missing: $text"
        printf '%s' "$answer"
        return
    fi
    read -r -s -p "$text: " answer </dev/tty; echo >&2
    printf '%s' "$answer"
}
valid_host() { [[ "$1" =~ ^[a-zA-Z0-9]([a-zA-Z0-9.-]*[a-zA-Z0-9])?$ && "$1" == *.* && "$1" != *..* ]]; }
install_packages() {
    if command -v apt-get >/dev/null; then
        apt-get update -qq && DEBIAN_FRONTEND=noninteractive apt-get install -y -qq "$@"
    elif command -v dnf >/dev/null; then
        dnf install -y -q "$@"
    else
        die "Please install missing prerequisites: $*"
    fi
}
random_secret() { head -c 32 /dev/urandom | base64 | tr '+/' '-_' | tr -d '=\n'; }

printf '\nSWVAULT Cloudflare + private R2 setup\n'
printf 'Documentation: CLOUDFLARE-SETUP.md\n'
printf 'Prepare the Tunnel token, R2 account ID, bucket name, Access Key ID and Secret Access Key.\n'
printf 'Secrets are typed locally and are NOT sent to ChatGPT.\n\n'

# 1. Collect ALL settings and credentials before installing, stopping or rewriting anything.
TEAM_NAME="$(ask_value TEAM_NAME 'Team name' "$(env_get TEAM_NAME || true)")"
TEAM_NAME="${TEAM_NAME:-FSAE}"
default_org="$(printf '%s' "$TEAM_NAME" | tr '[:upper:]' '[:lower:]' | tr -cs 'a-z0-9' '-' | sed 's/^-*//; s/-*$//')"
ORG="$(ask_value ORG 'Gitea organization' "$(env_get ORG || true)")"; ORG="${ORG:-${default_org:-team}}"
REPO="$(ask_value REPO 'Vault repository' "$(env_get REPO || true)")"; REPO="${REPO:-cad}"
ADMIN_USER="$(ask_value ADMIN_USER 'Admin username' "$(env_get ADMIN_USER || true)")"; ADMIN_USER="${ADMIN_USER:-${SUDO_USER:-admin}}"
LOCAL_ROOT="$(ask_value LOCAL_ROOT 'Default Windows workspace path' "$(env_get LOCAL_ROOT || true)")"; LOCAL_ROOT="${LOCAL_ROOT:-C:\\SWVault\\${TEAM_NAME}}"
SW_VERSION="$(ask_value SW_VERSION 'SOLIDWORKS year (empty disables check)' "$(env_get SW_VERSION || true)")"
BACKUPS_ENABLED="$(ask_value BACKUPS_ENABLED 'Enable scheduled backups? (yes/no)' "$(env_get BACKUPS_ENABLED)")"
BACKUPS_ENABLED="${BACKUPS_ENABLED:-yes}"
[[ "$BACKUPS_ENABLED" == yes || "$BACKUPS_ENABLED" == no ]] || die 'BACKUPS_ENABLED must be yes or no'
if [[ "$BACKUPS_ENABLED" == yes ]]; then
    BACKUP_DIR="$(ask_value BACKUP_DIR 'Independent backup destination' "$(env_get BACKUP_DIR || true)")"
    BACKUP_DIR="${BACKUP_DIR:-/var/backups/swvault}"
else
    BACKUP_DIR=''
fi
EMAIL_DOMAINS="$(ask_value EMAIL_DOMAINS 'Allowed member email domains (none disables verification)' "$(env_get EMAIL_DOMAINS || true)")"; EMAIL_DOMAINS="${EMAIL_DOMAINS:-none}"
CF_DOMAIN="$(ask_value CF_DOMAIN 'Cloudflare domain' "$(env_get CF_DOMAIN || true)")"; CF_DOMAIN="${CF_DOMAIN:-lynxmotorsports.racing}"
VAULT_HOSTNAME="$(ask_value VAULT_HOSTNAME 'Public SWVAULT hostname (MUST already be routed in Cloudflare)' "$(env_get CF_PUBLIC_HOST || true)")"
VAULT_HOSTNAME="${VAULT_HOSTNAME:-vault.$CF_DOMAIN}"
R2_BUCKET="$(ask_value R2_BUCKET 'EXISTING private R2 bucket name' "$(env_get R2_BUCKET || true)")"; R2_BUCKET="${R2_BUCKET:-swvault-lfs}"
R2_ACCOUNT_ID="$(ask_value R2_ACCOUNT_ID 'R2 Account ID (32 hex chars)' "$(env_get R2_ACCOUNT_ID || true)")"
[[ "$ORG" =~ ^[a-z0-9][a-z0-9._-]*$ ]] || die "Invalid organization name"
[[ "$REPO" =~ ^[a-zA-Z0-9][a-zA-Z0-9._-]*$ ]] || die "Invalid repository"
[[ "$ADMIN_USER" =~ ^[A-Za-z0-9][A-Za-z0-9._-]*$ ]] || die "Invalid admin username"
valid_host "$CF_DOMAIN" || die "Invalid domain name"
valid_host "$VAULT_HOSTNAME" || die "Invalid public hostname"
[[ "$VAULT_HOSTNAME" == *".$CF_DOMAIN" ]] || die "Hostname must end in .$CF_DOMAIN"
[[ "$R2_ACCOUNT_ID" =~ ^[0-9a-fA-F]{32}$ ]] || die "R2 Account ID must have 32 hexadecimal characters"
[[ "$R2_BUCKET" =~ ^[a-z0-9][a-z0-9.-]{1,61}[a-z0-9]$ ]] || die "Invalid R2 bucket name"
[[ -z "$SW_VERSION" || "$SW_VERSION" =~ ^20[0-9]{2}$ ]] || die "SOLIDWORKS year must be 20XX or empty"
if [[ "$BACKUPS_ENABLED" == yes ]]; then
    [[ "$BACKUP_DIR" == /* ]] || die 'Backup directory must be absolute'
    [[ "$BACKUP_DIR" != "$SWVAULT_DIR" && "$BACKUP_DIR" != "$SWVAULT_DIR"/* ]] || die 'Backup directory must be outside SWVAULT'
fi

if [[ -n "$(env_get CF_TUNNEL_TOKEN)" ]]; then
    if [[ "${SWVAULT_WIZARD:-no}" == yes ]]; then reuse=y
    else read -r -p 'Reuse saved Cloudflare Tunnel token? [Y/n]: ' reuse </dev/tty; fi
    [[ "${reuse:-y}" =~ ^[Yy] ]] && CF_TUNNEL_TOKEN="$(env_get CF_TUNNEL_TOKEN)" || CF_TUNNEL_TOKEN="$(ask_secret 'Cloudflare Tunnel token (eyJ...)')"
else
    CF_TUNNEL_TOKEN="$(ask_secret 'Cloudflare Tunnel token (eyJ...)')"
fi
[[ "$CF_TUNNEL_TOKEN" == eyJ* ]] || die 'The Tunnel token must start with eyJ (paste the token, not the whole docker command)'
if [[ -n "$(env_get R2_ACCESS_KEY_ID)" && -n "$(env_get R2_SECRET_ACCESS_KEY)" ]]; then
    if [[ "${SWVAULT_WIZARD:-no}" == yes ]]; then reuse=y
    else read -r -p 'Reuse saved R2 access and secret keys? [Y/n]: ' reuse </dev/tty; fi
    if [[ "${reuse:-y}" =~ ^[Yy] ]]; then
        R2_ACCESS_KEY_ID="$(env_get R2_ACCESS_KEY_ID)"
        R2_SECRET_ACCESS_KEY="$(env_get R2_SECRET_ACCESS_KEY)"
    else
        R2_ACCESS_KEY_ID="$(ask_secret 'R2 Access Key ID')"
        R2_SECRET_ACCESS_KEY="$(ask_secret 'R2 Secret Access Key')"
    fi
else
    R2_ACCESS_KEY_ID="$(ask_secret 'R2 Access Key ID')"
    R2_SECRET_ACCESS_KEY="$(ask_secret 'R2 Secret Access Key')"
fi
[[ -n "$R2_ACCESS_KEY_ID" && -n "$R2_SECRET_ACCESS_KEY" ]] || die 'Missing R2 credentials'
# Minimal environment-file safety; never accept special interpolation syntax in credential values.
for val in "$CF_TUNNEL_TOKEN" "$R2_ACCESS_KEY_ID" "$R2_SECRET_ACCESS_KEY"; do
    [[ "$val" != *'$'* && "$val" != *$'\n'* && "$val" != *$'\r'* ]] || die 'Credential cannot contain $ or newline for this installer'
done

SMTP_HOST="$(env_get SMTP_HOST)"; SMTP_PORT="$(env_get SMTP_PORT)"; SMTP_USER="$(env_get SMTP_USER)"; SMTP_PASSWORD="$(env_get SMTP_PASSWORD)"; SMTP_FROM="$(env_get SMTP_FROM)"
if [[ "${SWVAULT_WIZARD:-no}" == yes ]]; then
    [[ "$(env_get SMTP_ENABLED)" == yes || "$(env_get SMTP_ENABLED)" == no ]] || die 'Wizard SMTP_ENABLED must be yes or no'
    if [[ "$(env_get SMTP_ENABLED)" == no ]]; then
        SMTP_HOST=''; SMTP_PORT=''; SMTP_USER=''; SMTP_PASSWORD=''; SMTP_FROM=''
        [[ "$EMAIL_DOMAINS" == none ]] || die 'SMTP is disabled but allowed-email-domain verification is enabled; restart setup to enable SMTP or set email domains to none.'
    fi
fi
if [[ "${SWVAULT_WIZARD:-no}" != yes && "$EMAIL_DOMAINS" != none && -z "$SMTP_HOST" ]]; then
    echo 'For verification emails, use a dedicated Gmail app password (NOT your account password).'
    SMTP_HOST="$(ask_value SMTP_HOST 'SMTP host' smtp.gmail.com)"
    SMTP_PORT="$(ask_value SMTP_PORT 'SMTP port' 587)"
    SMTP_USER="$(ask_value SMTP_USER 'Sender email address' '')"
    SMTP_FROM="$SMTP_USER"
    SMTP_PASSWORD="$(ask_secret 'SMTP app password')"
fi

existing=no; legacy=no
if [[ -d "$DATA_DIR/tailscale" || -s "$ADMIN_TOKEN_FILE" || -f "$DATA_DIR/gitea/gitea/conf/app.ini" ]]; then existing=yes; fi
# Fresh wizard setup may be interrupted after containers initialize Gitea but
# before an admin token is written. That state is resumable, not legacy data.
if [[ "${SWVAULT_WIZARD:-no}" == yes && "$(env_get SWVAULT_SETUP_STAGE)" == in_progress && ! -s "$ADMIN_TOKEN_FILE" ]]; then
    existing=no
fi
if [[ "$existing" == yes && "$(env_get CF_DEPLOYMENT)" != cloudflare ]]; then legacy=yes; fi
if [[ "$legacy" == yes ]]; then
    die "This release REQUIRES a new VM for the one-time 1.2.9 -> 1.4 move. Do not run setup on the old production VM; see docs/1.2.9-TO-1.4-MIGRATION.md."
fi
if [[ "$existing" == yes ]]; then
    # Changing repository identity during a storage migration would create a new empty vault.
    for key in ORG REPO ADMIN_USER; do
        prior="$(env_get "$key")"
        [[ -z "$prior" || "$prior" == "${!key}" ]] || die "$key is already '$prior'; changing it would break existing clients. Keep the original value."
    done
fi
printf '\nREVIEW (no changes have been made yet)\n'
printf '  Public URL: https://%s\n  Gitea: %s/%s\n  R2: %s (private, signed direct downloads)\n' "$VAULT_HOSTNAME" "$ORG" "$REPO" "$R2_BUCKET"
printf '  Scheduled backups: %s (%s)\n  Existing SWVAULT data: %s (Tailscale legacy: %s)\n' "$BACKUPS_ENABLED" "${BACKUP_DIR:-disabled}" "$existing" "$legacy"
printf '  Cloudflare Free: direct R2 multipart uploads and signed downloads bypass the 100 MB tunnel limit for 1.4 clients; unmodified clients do not.\n'
if [[ "$legacy" == yes ]]; then
    printf '  WARNING: production clients use a hostname-derived vault ID. Existing clients need a migration/re-enrollment plan.\n'
    printf '  WARNING: check-ins MUST be paused; old Tailscale route will be turned off after cutover.\n'
    read -r -p 'Type MIGRATE to allow backup, R2 copying, and hostname cutover: ' approval </dev/tty
    [[ "$approval" == MIGRATE ]] || die 'No changes were made.'
elif [[ "$existing" == yes ]]; then
    if [[ "${SWVAULT_WIZARD:-no}" == yes && "${SWVAULT_WIZARD_APPROVED:-no}" == yes ]]; then
        approval=RECONFIGURE
    else
        read -r -p 'Type RECONFIGURE to update your existing Cloudflare SWVAULT configuration: ' approval </dev/tty
    fi
    [[ "$approval" == RECONFIGURE ]] || die 'No changes were made.' 
else
    if [[ "${SWVAULT_WIZARD:-no}" == yes && "${SWVAULT_WIZARD_APPROVED:-no}" == yes ]]; then
        approval=INSTALL
    else
        read -r -p 'Type INSTALL to deploy a NEW SWVAULT server: ' approval </dev/tty
    fi
    [[ "$approval" == INSTALL ]] || die 'No changes were made.' 
fi
# Only new 1.4 clients bypass the Cloudflare 100 MB limit using direct R2.
printf '1.4 supports signed R2 multipart uploads for large CAD files; pilot testing is required.\n'
if [[ "${SWVAULT_WIZARD:-no}" == yes && "${SWVAULT_WIZARD_APPROVED:-no}" == yes ]]; then
    upload_ack=I_ACCEPT_PILOT_TESTING
else
    read -r -p 'Type I_ACCEPT_PILOT_TESTING to continue: ' upload_ack </dev/tty
fi
[[ "$upload_ack" == I_ACCEPT_PILOT_TESTING ]] || die 'Cancelled: a real pilot test is required.'

# 2. Preflight: no existing data modification before credential verification.
say 'Installing prerequisites and validating R2 bucket credentials'
missing=()
for tool in curl jq rsync rclone sqlite3; do command -v "$tool" >/dev/null || missing+=("$tool"); done
((${#missing[@]} == 0)) || install_packages "${missing[@]}"
if ! command -v docker >/dev/null || ! docker compose version >/dev/null 2>&1; then
    say 'Installing Docker Engine and Compose from the official package repository (no curl|sh)'
    if ! command -v apt-get >/dev/null; then
        die 'Docker Engine with Compose plugin must be installed manually on this distribution; see https://docs.docker.com/engine/install/'
    fi
    . /etc/os-release
    case "$ID" in ubuntu|debian) ;; *) die 'Official automated Docker installation supports Ubuntu/Debian only.' ;; esac
    apt-get update -qq
    DEBIAN_FRONTEND=noninteractive apt-get install -y -qq ca-certificates curl
    install -m 0755 -d /etc/apt/keyrings
    curl -fsSL "https://download.docker.com/linux/$ID/gpg" -o /etc/apt/keyrings/docker.asc
    chmod a+r /etc/apt/keyrings/docker.asc
    dpkg_arch="$(dpkg --print-architecture)"
    release="${VERSION_CODENAME:-}"
    [[ -n "$release" ]] || die 'Cannot determine distribution codename for Docker apt repository.'
    printf 'deb [arch=%s signed-by=/etc/apt/keyrings/docker.asc] https://download.docker.com/linux/%s %s stable\n' "$dpkg_arch" "$ID" "$release" > /etc/apt/sources.list.d/docker-swvault.list
    apt-get update -qq
    DEBIAN_FRONTEND=noninteractive apt-get install -y -qq docker-ce docker-ce-cli containerd.io docker-buildx-plugin docker-compose-plugin
fi
command -v docker >/dev/null && docker compose version >/dev/null 2>&1 || die 'Docker installation did not provide docker compose.' 
systemctl enable --now docker >/dev/null
rclone_conf_temp="$(mktemp)"
chmod 600 "$rclone_conf_temp"
# Remove transient credentials even when validation fails before the main rollback trap exists.
trap 'rm -f "$rclone_conf_temp"' EXIT

cat > "$rclone_conf_temp" <<EOF
[r2]
type = s3
provider = Cloudflare
access_key_id = $R2_ACCESS_KEY_ID
secret_access_key = $R2_SECRET_ACCESS_KEY
endpoint = https://$R2_ACCOUNT_ID.r2.cloudflarestorage.com
region = auto
acl = private
EOF
if ! rclone --config "$rclone_conf_temp" lsf "r2:$R2_BUCKET" --max-depth 1 >/dev/null; then
    die 'R2 validation failed; check bucket, Account ID, region/jurisdiction, and credentials. No services changed.'
fi
say 'R2 credentials and bucket access verified'

rollback_path=''; cutover_started=no; timers_paused=no
cleanup_and_rollback() {
    local code=$?
    trap - EXIT
    [[ -z "${rclone_conf_temp:-}" ]] || rm -f "$rclone_conf_temp"
    if (( code != 0 )) && [[ "$cutover_started" == yes && -n "$rollback_path" ]]; then
        warn 'A step failed. Attempting legacy Tailscale rollback.'
        cp "$rollback_path/old.env" "$ENV_FILE" || true
        [[ ! -f "$rollback_path/old.team.json" ]] || cp "$rollback_path/old.team.json" "$TEAM_FILE" || true
        docker compose -f "$SWVAULT_DIR/docker-compose.yml" down --remove-orphans >/dev/null 2>&1 || true
        docker compose -f "$SWVAULT_DIR/docker-compose.tailscale-legacy.yml" up -d --remove-orphans || true
    fi
    if (( code != 0 )) && [[ "$timers_paused" == yes ]]; then
        systemctl enable --now swvault-watchdog.timer >/dev/null 2>&1 || true
        if [[ "$BACKUPS_ENABLED" == yes ]]; then
            systemctl enable --now swvault-backup.timer >/dev/null 2>&1 || true
        else
            systemctl disable --now swvault-backup.timer >/dev/null 2>&1 || true
        fi
    fi
    exit "$code"
}
trap cleanup_and_rollback EXIT

if [[ "$existing" == yes ]]; then
    # These OLD systemd jobs invoke this updated script and could restart new Compose while
    # the old Gitea service is intentionally stopped for migration.
    say 'Pausing existing watchdog, backup and auto-update jobs during migration'
    systemctl stop swvault-watchdog.timer swvault-backup.timer swvault-watchdog.service swvault-backup.service >/dev/null 2>&1 || true
    systemctl stop swvault-autoupdate.timer swvault-autoupdate.path swvault-autoupdate.service >/dev/null 2>&1 || true
    timers_paused=yes
    if [[ "$BACKUPS_ENABLED" == yes ]]; then
        say 'Backing up current server before changing existing containers'
        mkdir -p "$BACKUP_DIR"
        rollback_path="$BACKUP_DIR/cloudflare-rollback-$(date +%Y%m%d-%H%M%S)"
        mkdir -p "$rollback_path"
        [[ -f "$ENV_FILE" ]] && cp -p "$ENV_FILE" "$rollback_path/old.env" || die 'Existing install is missing .env'
        [[ ! -f "$TEAM_FILE" ]] || cp -p "$TEAM_FILE" "$rollback_path/old.team.json"
        if [[ "$legacy" == yes ]]; then
            COMPOSE_FILE="$SWVAULT_DIR/docker-compose.tailscale-legacy.yml" "$SWVAULT_DIR/../backup/backup-gitea.sh" "$SWVAULT_DIR" "$BACKUP_DIR"
        else
            "$SWVAULT_DIR/../backup/backup-gitea.sh" "$SWVAULT_DIR" "$BACKUP_DIR"
        fi
        [[ -n "$(find "$BACKUP_DIR/daily" -maxdepth 1 -name 'gitea-*.tar.gz' -size +0c -print -quit)" ]] || die 'No verified Gitea dump; aborting'
        say "Snapshot complete; rollback settings at $rollback_path"
    else
        warn 'Scheduled backups are OFF; no automated full-server snapshot will be created on this rerun.'
    fi
    if [[ "$legacy" == yes ]]; then
    # An old auto-updater might replace Cloudflare files with a Tailscale-only release.
    systemctl disable --now swvault-autoupdate.timer swvault-autoupdate.path >/dev/null 2>&1 || true
    say 'Stopping Gitea and invites for consistent R2 migration (Tailscale namespace stays up)'
    cutover_started=yes
    docker compose -f "$SWVAULT_DIR/docker-compose.tailscale-legacy.yml" stop invites gitea
    # Source storage is the OLD local Gitea data. Keep all local objects for rollback.
    # The Gitea CLI uses env-provided S3 credentials, not secret-valued CLI flags.
    say 'Copying existing LFS objects to R2. This can transfer 100+ GB and may take a while.'
    docker compose -f "$SWVAULT_DIR/docker-compose.tailscale-legacy.yml" run --rm --no-deps -u git \
        -e "AWS_ACCESS_KEY_ID=$R2_ACCESS_KEY_ID" -e "AWS_SECRET_ACCESS_KEY=$R2_SECRET_ACCESS_KEY" \
        gitea gitea migrate-storage --type lfs --storage minio \
        --minio-endpoint "$R2_ACCOUNT_ID.r2.cloudflarestorage.com" \
        --minio-bucket "$R2_BUCKET" --minio-location auto --minio-base-path lfs/ \
        --minio-use-ssl --minio-checksum-algorithm md5 --minio-bucket-lookup-type path
    say 'LFS migration command completed; local LFS objects have NOT been deleted'
    fi
fi

# 3. Write settings and start the Cloudflare-first server.
say 'Writing root-only configuration and Tunnel token'
for kv in TEAM_NAME ORG REPO ADMIN_USER LOCAL_ROOT SW_VERSION BACKUP_DIR BACKUPS_ENABLED EMAIL_DOMAINS CF_DOMAIN VAULT_HOSTNAME R2_BUCKET R2_ACCOUNT_ID R2_ACCESS_KEY_ID R2_SECRET_ACCESS_KEY CF_TUNNEL_TOKEN; do
    env_set "$kv" "${!kv}"
done
env_set CF_PUBLIC_HOST "$VAULT_HOSTNAME"
env_set CF_DEPLOYMENT cloudflare
if [[ -n "$SMTP_HOST" ]]; then
    for kv in SMTP_HOST SMTP_PORT SMTP_USER SMTP_FROM; do env_set "$kv" "${!kv}"; done
    env_set SMTP_PASSWORD "${SMTP_PASSWORD//\$/\$\$}"
else
    for kv in SMTP_HOST SMTP_PORT SMTP_USER SMTP_FROM SMTP_PASSWORD; do env_set "$kv" ''; done
fi
for key in GITEA_SECRET_KEY GITEA_INTERNAL_TOKEN GITEA_LFS_JWT_SECRET GITEA_OAUTH_JWT_SECRET; do
    [[ -n "$(env_get "$key")" ]] || env_set "$key" "$(random_secret)"
done
mkdir -p "$DATA_DIR/cloudflare" "$DATA_DIR/gitea" "$DATA_DIR/invites" "$DATA_DIR/r2-transfer" "$DATA_DIR/cold-lfs"
chown 1000:1000 "$DATA_DIR/r2-transfer" "$DATA_DIR/cold-lfs"
chmod 700 "$DATA_DIR/r2-transfer" "$DATA_DIR/cold-lfs"
chmod 700 "$DATA_DIR" "$DATA_DIR/cloudflare"
chown 1000:1000 "$DATA_DIR/gitea"
(umask 077; printf '%s' "$CF_TUNNEL_TOKEN" > "$DATA_DIR/cloudflare/tunnel-token")
(umask 077; cp "$rclone_conf_temp" "$DATA_DIR/r2-rclone.conf")
# cloudflared runs as UID/GID 65532 and must be able to read its mounted token file.
chown 0:65532 "$DATA_DIR/cloudflare/tunnel-token"
chmod 640 "$DATA_DIR/cloudflare/tunnel-token"
chmod 600 "$DATA_DIR/r2-rclone.conf"
touch "$ADMIN_TOKEN_FILE"
chown 0:1000 "$ADMIN_TOKEN_FILE"
chmod 640 "$ADMIN_TOKEN_FILE"

say 'Starting cloudflared, Gitea, Caddy, invites and the R2 upload coordinator'
if [[ "$legacy" == yes ]]; then
    docker compose -f "$SWVAULT_DIR/docker-compose.tailscale-legacy.yml" down --remove-orphans
fi
compose pull -q --ignore-buildable
compose up -d --build --remove-orphans
wait_gitea 300
cutover_started=no

# ------------------------------------------------------------------ 4. Gitea
say "Starting Gitea with Cloudflare/R2"
compose up -d
wait_gitea 300

if [[ ! -s "$ADMIN_TOKEN_FILE" ]]; then
    say "Creating admin account '$ADMIN_USER'"
    admin_password="$(gen_password)"
    if ! gitea_cli admin user create --admin --username "$ADMIN_USER" --password "$admin_password" \
            --email "$ADMIN_USER@noreply.localhost" --must-change-password=false >/dev/null 2>&1; then
        # Already exists (an earlier, interrupted setup): make the password known again.
        gitea_cli admin user change-password --username "$ADMIN_USER" --password "$admin_password" --must-change-password=false >/dev/null
    fi
    token="$(gitea_cli admin user generate-access-token --username "$ADMIN_USER" \
        --token-name "swvault-admin-$(date +%Y%m%d%H%M%S)" --scopes all --raw | tr -d '\r\n')"
    [[ -n "$token" ]] || die "Could not create the admin token."
    (umask 077; printf '%s' "$token" > "$ADMIN_TOKEN_FILE")
    chown 0:1000 "$ADMIN_TOKEN_FILE"
    chmod 640 "$ADMIN_TOKEN_FILE"
    (umask 077; printf 'SwVault admin sign-in\n  User name: %s\n  Password:  %s\n' "$ADMIN_USER" "$admin_password" > "$DATA_DIR/admin-credentials.txt")
fi

say "Creating organization '$ORG', teams and repository '$REPO'"
API_OK_CODES="409 422"
api POST orgs "$(jq -n --arg o "$ORG" --arg n "$TEAM_NAME" '{username: $o, full_name: $n, visibility: "private"}')" >/dev/null
# repo.code = files; repo.issues = review requests (Designers can request/answer, Viewers can read).
for spec in "Designers:write:Can check files out and in" "Viewers:read:Can open and download files only"; do
    IFS=: read -r name perm description <<<"$spec"
    team_json="$(jq -n --arg n "$name" --arg p "$perm" --arg d "$description" \
        '{name: $n, description: $d, permission: $p, includes_all_repositories: true, can_create_org_repo: false,
          units: ["repo.code", "repo.issues"], units_map: {"repo.code": $p, "repo.issues": $p}}')"
    id="$(team_id "$ORG" "$name")"
    if [[ -z "$id" ]]; then
        api POST "orgs/$ORG/teams" "$team_json" >/dev/null
    else
        api PATCH "teams/$id" "$team_json" >/dev/null   # older setups: add review requests
    fi
done
api POST "orgs/$ORG/repos" "$(jq -n --arg r "$REPO" '{name: $r, private: true, default_branch: "main", auto_init: false}')" >/dev/null
# No force pushes or deleting main: the vault's history can't be rewritten, only added to.
if ! api GET "repos/$ORG/$REPO/branch_protections" | jq -e '.[] | select(.rule_name == "main")' >/dev/null; then
    api POST "repos/$ORG/$REPO/branch_protections" '{"rule_name": "main", "enable_push": true}' >/dev/null
fi
# Labels for review requests (SwVault creates missing ones too; see Core/Client/Reviews.cs).
existing_labels="$(api GET "repos/$ORG/$REPO/labels?limit=100" | jq -r '.[].name')"
for spec in "review:#6f42c1" "review: design:#0366d6" "review: simulation:#0e8a16" "review: drawing:#d93f0b" \
            "review: approved:#2cbe4e" "review: changes requested:#fbca04" "review: cancelled:#8b949e"; do
    name="${spec%:*}"; color="${spec##*:}"
    grep -qxF "$name" <<<"$existing_labels" && continue
    api POST "repos/$ORG/$REPO/labels" "$(jq -n --arg n "$name" --arg c "$color" '{name: $n, color: $c, description: "SwVault review requests"}')" >/dev/null
done
API_OK_CODES=""

write_team_json
chmod 644 "$TEAM_FILE"
if [[ "$existing" == yes ]]; then
    if ! "$SWVAULT_DIR/verify-r2-direct.sh"; then
        warn 'Direct R2 GET check did not pass. DO NOT ask clients to switch hostnames until repaired.'
        warn 'Existing local LFS copies and rollback compose are preserved.'
    fi
fi

if [[ -n "$(env_get SMTP_HOST)" && -z "$(env_get EMAIL_TESTED)" ]]; then
    if [[ "${SWVAULT_WIZARD:-no}" == yes ]]; then
        test_to="${SWVAULT_WIZARD_EMAIL_TEST:-}"
    else
        read -r -p "Send a test email to check the email settings? Address (Enter to skip): " test_to </dev/tty
    fi
    if [[ -n "$test_to" ]]; then
        if "$SWVAULT_DIR/swvault-admin.sh" test-email "$test_to"; then env_set EMAIL_TESTED yes; fi
    fi
fi


# ------------------------------------------------------------------ 5. keep it running
say 'Configuring the watchdog and optional nightly backup schedule'
backup_script="$(cd "$SWVAULT_DIR/../backup" && pwd)/backup-gitea.sh"
chmod +x "$backup_script" "$SWVAULT_DIR/swvault-admin.sh" "$SWVAULT_DIR/setup.sh"
if [[ "$BACKUPS_ENABLED" == yes ]]; then
    mkdir -p "$BACKUP_DIR"
    chmod 700 "$BACKUP_DIR"
cat > /etc/systemd/system/swvault-backup.service <<EOF
[Unit]
Description=SwVault nightly backup
After=docker.service

[Service]
Type=oneshot
ExecStart=$backup_script $SWVAULT_DIR $BACKUP_DIR
EOF
cat > /etc/systemd/system/swvault-backup.timer <<EOF
[Unit]
Description=SwVault nightly backup

[Timer]
OnCalendar=*-*-* 03:15
Persistent=true
RandomizedDelaySec=10m

[Install]
WantedBy=timers.target
EOF
else
    # Never remove existing backup files, but disable scheduled jobs if opted out.
    systemctl disable --now swvault-backup.timer >/dev/null 2>&1 || true
    say 'Nightly backups disabled (existing archives, if any, remain untouched)'
fi
cat > /etc/systemd/system/swvault-watchdog.service <<EOF
[Unit]
Description=SwVault health watchdog (restarts the server if it stops answering)
After=docker.service

[Service]
Type=oneshot
ExecStart=$SWVAULT_DIR/swvault-admin.sh watchdog
EOF
cat > /etc/systemd/system/swvault-watchdog.timer <<EOF
[Unit]
Description=SwVault health watchdog

[Timer]
OnBootSec=5min
OnUnitActiveSec=5min

[Install]
WantedBy=timers.target
EOF
systemctl daemon-reload
if [[ "$BACKUPS_ENABLED" == yes ]]; then
    systemctl enable --now swvault-backup.timer >/dev/null
else
    systemctl disable --now swvault-backup.timer >/dev/null 2>&1 || true
fi
systemctl enable --now swvault-watchdog.timer >/dev/null

if [[ "$(env_get KEEP_AWAKE)" != no ]]; then
    if [[ "${SWVAULT_WIZARD:-no}" == yes ]]; then
        awake=y
    else
        read -r -p "Stop this PC from sleeping/suspending (recommended for a server)? [Y/n]: " awake </dev/tty
    fi
    if [[ "${awake:-y}" =~ ^[Yy] ]]; then
        systemctl mask sleep.target suspend.target hibernate.target hybrid-sleep.target >/dev/null 2>&1 || true
        if [[ -f /etc/systemd/logind.conf ]] && ! grep -q '^HandleLidSwitch=ignore' /etc/systemd/logind.conf; then
            printf '\n[Login]\nHandleLidSwitch=ignore\nHandleLidSwitchExternalPower=ignore\n' >> /etc/systemd/logind.conf
        fi
        env_set KEEP_AWAKE yes
    else
        env_set KEEP_AWAKE no
    fi
fi


# ------------------------------------------------------------------ 6. check from the internet
say "Checking https://$VAULT_HOSTNAME from the internet (the public name can take a few minutes to appear)"
deadline=$((SECONDS + 45))
reachable=no
while (( SECONDS < deadline )); do
    if curl -fsS -m 15 "https://$VAULT_HOSTNAME/api/healthz" >/dev/null 2>&1; then reachable=yes; break; fi
    sleep 5
done

echo
if [[ "$reachable" == yes ]]; then
    say "SwVault server is up at https://$VAULT_HOSTNAME"
else
    warn "https://$VAULT_HOSTNAME isn't reachable yet. Check the tunnel public hostname route points to http://localhost:8080, then: ./swvault-admin.sh status"
fi
cat <<EOF

$( [[ -f "$DATA_DIR/admin-credentials.txt" ]] \
    && echo "Your admin sign-in is in $DATA_DIR/admin-credentials.txt (only root can read it). To pick your own password: sudo ./swvault-admin.sh set-password $ADMIN_USER" \
    || echo "Your admin user name is '$ADMIN_USER', with the password you chose (sudo ./swvault-admin.sh set-password $ADMIN_USER changes it)." )

Next steps:
  1. Copy $TEAM_FILE to your Windows build PC, build the team installer and publish it
     to this server (asks for your admin sign-in):
       .\\scripts\\package.ps1 -TeamConfig team.json -Publish
  2. Install it on your own PC first and sign in as '$ADMIN_USER'. That sets up the vault
     (you become its admin and approver).
  3. Invite your team: SOLIDWORKS > SwVault tab > Invite People (or the tray icon), then
     Copy message and paste it into your team chat. People join with the link and code.
     (Or from here: sudo ./swvault-admin.sh invite --uses 20)
  4. Import existing files from SOLIDWORKS: SwVault tab > Import Folder.

Backups go to $BACKUP_DIR nightly. Keep it on a separate disk and verify restores.
R2 direct-download validation: sudo ./verify-r2-direct.sh
SWVAULT 1.4 Windows agents use private R2 multipart upload for CAD files >= 8 MiB.
Original 1.2.9 clients DO NOT have this capability; do not point them at the new Tunnel.
For the one-time 1.2.9 migration use the separate export/import/arm scripts and the guide.
Test a real 500 MB check-in and complete history/backup restore before arming the migration.
EOF
