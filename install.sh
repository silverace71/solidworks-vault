#!/usr/bin/env bash
# SWVAULT 1.4+ fresh-VM installer. Put this file at repository root.
# Interactive, never runs migrations on the old VM; resumable after interruption.
set -Eeuo pipefail
umask 077
REPO_URL='https://github.com/silverace71/solidworks-vault.git'
DEST='/opt/swvault'
STATEDIR='/var/lib/swvault-installer'
TMP=''
changed=no
phase='preflight'
cleanup() {
  local rc=$?
  trap - EXIT INT TERM
  [[ -z "$TMP" ]] || rm -rf -- "$TMP"
  if (( rc != 0 )); then
    if [[ "$changed" == yes ]]; then
      printf '\nInterrupted at %s. Previous vault VM was not modified.\n' "$phase" >&2
      printf 'New VM state is retained for safe resume. Re-run the SAME command.\n' >&2
      printf 'Do not remove /opt/swvault/server/linux/data or the .env file.\n' >&2
    else
      printf '\nCancelled before deployment changes. Re-run whenever ready.\n' >&2
    fi
  fi
  exit "$rc"
}
trap cleanup EXIT
trap 'exit 130' INT
trap 'exit 143' TERM
fail(){ printf 'ERROR: %s\n' "$*" >&2; exit 1; }
info(){ printf '\n==> %s\n' "$*"; }
[[ $EUID -eq 0 ]] || fail 'Run with sudo on a NEW Ubuntu/Debian VM.'
[[ -r /dev/tty ]] || fail 'A real interactive terminal is required; unattended installs are deliberately refused.'
[[ "$(uname -s)" == Linux ]] || fail 'Linux only.'
[[ -r /etc/os-release ]] || fail 'Cannot identify Linux distribution.'
. /etc/os-release
[[ "$ID" == ubuntu || "$ID" == debian ]] || fail 'This one-shot installer supports Ubuntu/Debian only.' 
[[ "$(uname -m)" == x86_64 || "$(uname -m)" == aarch64 ]] || fail 'Use a 64-bit Linux VM.'
command -v flock >/dev/null || fail 'Install util-linux/flock first.'
mkdir -p "$STATEDIR"
chmod 700 "$STATEDIR"
exec 9>"$STATEDIR/installer.lock"
flock -n 9 || fail 'Another SWVAULT installer is running.'
# Recover matching config files if RESTART was interrupted while saving them.
if [[ -f "$STATEDIR/config-write.pending" ]]; then
  checkpoint="$(cat "$STATEDIR/config-write.pending")"
  [[ "$checkpoint" == "$STATEDIR/config-checkpoints/"* && -f "$checkpoint/old.env" ]] || fail 'Incomplete config checkpoint needs manual inspection; refusing to proceed.'
  cp -p -- "$checkpoint/old.env" "$DEST/server/linux/.env"
  if [[ -f "$checkpoint/old.migration.state" ]]; then
    cp -p -- "$checkpoint/old.migration.state" "$STATEDIR/migration.state"
  fi
  rm -f -- "$STATEDIR/config-write.pending"
  printf 'Recovered the previous complete configuration after interrupted RESTART save.\n'
fi
TMP="$(mktemp -d)"
chmod 700 "$TMP"
# The operator can explicitly opt in to a dry run to check validation and prompts.
DRY_RUN=no
[[ "${1:-}" != '--dry-run' ]] || DRY_RUN=yes
[[ $# == 0 || ( $# == 1 && $DRY_RUN == yes ) ]] || fail 'Usage: sudo bash install.sh [--dry-run]'
read_tty(){ local reply; IFS= read -r reply </dev/tty || exit 130; REPLY="$reply"; }
ask(){
  local name="$1" label="$2" def="${3:-}" pat="${4:-.+}" value
  while :; do
    if [[ -n "$def" ]]; then printf '%s [%s]: ' "$label" "$def" >/dev/tty; else printf '%s: ' "$label" >/dev/tty; fi
    read_tty; value="${REPLY:-$def}"
    [[ -n "$value" ]] || { printf 'A value is required. Use an explicit option such as none where allowed.\n' >/dev/tty; continue; }
    [[ "$value" =~ $pat ]] || { printf 'Invalid format. Please try again.\n' >/dev/tty; continue; }
    [[ "$value" != *$'\n'* && "$value" != *$'\r'* && "$value" != *'$'* ]] || { printf 'A dollar sign or newline is not supported in .env values.\n' >/dev/tty; continue; }
    printf -v "$name" '%s' "$value"
    break
  done
}
ask_secret(){
  local name="$1" label="$2" value
  while :; do
    printf '%s: ' "$label" >/dev/tty
    IFS= read -r -s value </dev/tty || exit 130
    printf '\n' >/dev/tty
    [[ -n "$value" ]] || { printf 'Cannot be blank.\n' >/dev/tty; continue; }
    [[ "$value" != *'$'* && "$value" != *$'\n'* && "$value" != *$'\r'* ]] || { printf 'Secret cannot include $ or a newline with this Compose deployment.\n' >/dev/tty; continue; }
    printf -v "$name" '%s' "$value"
    break
  done
}
choose(){
  local name="$1" prompt="$2" opts="$3"
  while :; do
    printf '%s (%s): ' "$prompt" "$opts" >/dev/tty
    read_tty
    [[ " $opts " == *" $REPLY "* ]] || { printf 'Pick one of the listed options.\n' >/dev/tty; continue; }
    printf -v "$name" '%s' "$REPLY"; break
  done
}
valid_email(){ [[ "$1" =~ ^[^@[:space:]]+@[^@[:space:]]+\.[^@[:space:]]+$ ]]; }
valid_hostname(){ [[ "$1" =~ ^[a-zA-Z0-9]([a-zA-Z0-9.-]*[a-zA-Z0-9])?$ && "$1" == *.* && "$1" != *..* ]]; }
mount_device(){ df -P "$1" | awk 'NR==2 {print $1}'; }

printf '\nSWVAULT NEW VM | Cloudflare + private R2 | interactive install\n'
cat <<'INTRO'
BEFORE proceeding, prepare:
  1. A NEW Ubuntu/Debian VM (not the old 1.2.9 production VM) with sudo,
     SSH, internet, 4+ GB RAM, and ample disk for services and imported files.
     Scheduled backups are OPTIONAL; if enabled, prepare an independent mounted
     filesystem. This wizard can create a folder ON that mounted filesystem.
  2. Cloudflare active DNS and a remotely managed Tunnel, published hostname
     vault.lynxmotorsports.racing -> http://localhost:8080.
     Save the long tunnel token from the Cloudflare Docker command.
  3. A PRIVATE, Standard Cloudflare R2 bucket and bucket-scoped Object Read/Write
     S3 Access Key ID + Secret Access Key, and your R2 account ID.
  4. SMTP hostname/port/sender, login, and app password if you want emails.
  5. MIGRATION CHOICE: (a) full old 1.2.9 export .tar.gz (users/history),
     or (b) plain CAD folder to stage for manual Windows Import Folder.
     Imports use their own staging directory, even if backups are OFF. A full
     1.2.9 history import still requires an INDEPENDENT disk for its temporary
     rollback snapshot; this is NOT an ongoing backup subscription/schedule.
     For old VM transfer: attach both VMs to a fast Proxmox VM network,
     confirm SSH connectivity over that IP, and run tools/old-vm-export.sh
     on the OLD VM only when ready to freeze the original vault.
  6. Keep the old VM read-only/frozen after FINAL export. Do not run two
     writable vaults against divergent copies of your repository.

No services are changed until you review the summary and type DEPLOY.
Ctrl+C aborts prompts. On rerun choose RESUME (saved values) or RESTART
(re-answer setup questions without deleting vault data). RESTART is NOT a wipe.
INTRO
if [[ -e "$DEST/server/linux/data/tailscale" ]]; then fail 'Legacy Tailscale data detected at destination; use an entirely new VM.'; fi
if [[ ! -e "$STATEDIR/owned-install" ]] && { [[ -e "$DEST/server/linux/.env" ]] || [[ -d "$DEST/server/linux/data/gitea" ]]; }; then
  fail 'An existing SWVAULT deployment is present but not owned by this wizard. Refusing to overwrite it.'
fi
# Keep completion state, saved credentials and runtime data separate.
# Restart means re-answer the wizard, NOT erase the existing vault.
RESUME=no
RESTART=no
if [[ -f "$STATEDIR/owned-install" && -f "$DEST/server/linux/.env" ]]; then
  printf '\nExisting wizard installation/state found.\n'
  printf '  resume  = continue with the previously saved settings\n'
  printf '  restart = re-answer questions and safely reconfigure (NO data deletion)\n'
  printf '  quit    = make no changes\n'
  choose RESUME_CHOICE 'Choose setup action' 'resume restart quit'
  case "$RESUME_CHOICE" in
    resume) RESUME=yes ;;
    restart) RESTART=yes ;;
    quit) exit 0 ;;
  esac
elif [[ -f "$STATEDIR/owned-install" ]]; then
  printf '\nAn earlier setup was interrupted before configuration was saved.\n'
  choose RESUME_CHOICE 'Start the setup questions again, or quit?' 'restart quit'
  [[ "$RESUME_CHOICE" == restart ]] || exit 0
  RESTART=yes
fi

# Use already saved non-secret values as defaults on RESTART; data is never wiped.
saved_default(){
  local key="$1" fallback="$2" prior=''
  if [[ "$RESTART" == yes && -r "$DEST/server/linux/.env" ]]; then
    prior="$(sed -n "s/^${key}=//p" "$DEST/server/linux/.env" | tail -n 1)"
  fi
  printf '%s' "${prior:-$fallback}"
}
# Do not accept blank answers. Optional choices have explicit 'none'.
if [[ "$RESUME" == no ]]; then
  ask TEAM_NAME 'Team name' "$(saved_default TEAM_NAME FSAE)" '^[A-Za-z0-9][A-Za-z0-9 _.-]{1,79}$'
  ask ORG 'Gitea organization (match original for migration)' "$(saved_default ORG fsae)" '^[a-z0-9][a-z0-9._-]*$'
  ask REPO 'Repository name (match original for migration)' "$(saved_default REPO cad)" '^[A-Za-z0-9][A-Za-z0-9._-]*$'
  ask ADMIN_USER 'Admin login (match original for migration)' "$(saved_default ADMIN_USER admin)" '^[A-Za-z0-9][A-Za-z0-9._-]*$'
  ask LOCAL_ROOT 'Windows workspace path (match original for migration)' "$(saved_default LOCAL_ROOT 'C:\SWVault\FSAE')" '^.{3,200}$'
  ask SW_VERSION 'SOLIDWORKS year or none' "$(saved_default SW_VERSION none)" '^(none|20[0-9]{2})$'
  ask CF_DOMAIN 'Cloudflare domain' "$(saved_default CF_DOMAIN lynxmotorsports.racing)" '^[A-Za-z0-9.-]+$'
  valid_hostname "$CF_DOMAIN" || fail 'Invalid domain.'
  ask VAULT_HOSTNAME 'New public SWVAULT hostname' "$(saved_default VAULT_HOSTNAME "vault.$CF_DOMAIN")" '^[A-Za-z0-9.-]+$'
  valid_hostname "$VAULT_HOSTNAME" && [[ "$VAULT_HOSTNAME" == *".$CF_DOMAIN" ]] || fail 'Hostname must belong to the domain.'
  ask R2_BUCKET 'Existing private R2 bucket' "$(saved_default R2_BUCKET swvault-lfs)" '^[a-z0-9][a-z0-9.-]{1,61}[a-z0-9]$'
  ask R2_ACCOUNT_ID 'Cloudflare R2 account ID (32 hex)' "$(saved_default R2_ACCOUNT_ID '')" '^[a-fA-F0-9]{32}$'
  ask_secret R2_ACCESS_KEY_ID 'R2 Access Key ID (hidden)'
  ask_secret R2_SECRET_ACCESS_KEY 'R2 Secret Access Key (hidden)'
  ask_secret CF_TUNNEL_TOKEN 'Cloudflare Tunnel token (hidden)'
  [[ "$CF_TUNNEL_TOKEN" == eyJ* ]] || fail 'Tunnel token must begin with eyJ.'
  ask EMAIL_DOMAINS 'Allowed email domains (none or comma-separated domains)' "$(saved_default EMAIL_DOMAINS none)" '^(none|[A-Za-z0-9.,-]+)$'
  choose SMTP_ENABLED 'Configure SMTP email now?' 'yes no'
  if [[ "$SMTP_ENABLED" == yes ]]; then
    ask SMTP_HOST 'SMTP server' 'smtp.gmail.com' '^[a-zA-Z0-9.-]+$'
    ask SMTP_PORT 'SMTP port (usually STARTTLS 587)' '587' '^[0-9]{2,5}$'
    (( SMTP_PORT >= 1 && SMTP_PORT <= 65535 )) || fail 'SMTP port invalid.'
    while :; do ask SMTP_USER 'SMTP login email (or login ID)' '' '^[^[:space:]]+$'; break; done
    while :; do ask SMTP_FROM 'Email From address' "$SMTP_USER" '^.{1,200}$'; valid_email "$SMTP_FROM" && break; printf 'Must be a valid email address.\n' >/dev/tty; done
    ask_secret SMTP_PASSWORD 'SMTP app password (hidden; not account password)'
    ask SMTP_TEST_TO 'Send a test email to' "$SMTP_FROM" '^[^[:space:]]+@[^[:space:]]+\.[^[:space:]]+$'
  else
    SMTP_HOST=''; SMTP_PORT=''; SMTP_USER=''; SMTP_FROM=''; SMTP_PASSWORD=''; SMTP_TEST_TO=''
    printf 'Email sending will be disabled. Invitations remain available through links/codes.\n'
  fi
  if [[ "$SMTP_ENABLED" == no && "$EMAIL_DOMAINS" != none ]]; then
    fail 'Email-domain verification requires SMTP. Choose SMTP=yes or set Allowed email domains to none.'
  fi
  choose BACKUPS_ENABLED 'Enable nightly scheduled backups? (independent disk recommended)' 'yes no'
  BACKUP_DIR=''; BACKUP_DIR_CREATE=no
  if [[ "$BACKUPS_ENABLED" == yes ]]; then
    ask BACKUP_DIR 'Backup directory (absolute path on an already-mounted independent disk)' "$(saved_default BACKUP_DIR /mnt/swvault-backups/swvault)" '^/.*$'
    BACKUP_DIR="$(realpath -m -- "$BACKUP_DIR")"
    [[ "$BACKUP_DIR" != "$DEST"/* && "$BACKUP_DIR" != "$DEST" ]] || fail 'Backup directory must be outside the install directory.'
    [[ ! -L "$BACKUP_DIR" ]] || fail 'Backup path cannot be a symbolic link.'
    if [[ -e "$BACKUP_DIR" ]]; then
      [[ -d "$BACKUP_DIR" ]] || fail 'Backup path exists but is not a directory.'
    else
      choose BACKUP_DIR_CREATE 'Backup directory does not exist. Create it AFTER you approve deployment?' 'yes no'
      [[ "$BACKUP_DIR_CREATE" == yes ]] || fail 'Create or choose an existing backup folder to enable backups.'
    fi
    parent="$BACKUP_DIR"
    while [[ ! -e "$parent" ]]; do parent="$(dirname "$parent")"; done
    [[ -d "$parent" && "$(mount_device "$parent")" != "$(mount_device /)" ]] || fail 'Backups require an independent mounted filesystem. Attach/mount the disk first, then let the installer create a folder on it, or choose no backups.'
  else
    printf 'Nightly backup service/timer will be disabled; existing backup files are NOT deleted.\n'
  fi
  choose KEEP_AWAKE 'Disable VM suspend/hibernate?' 'yes no'
  choose MIGRATION_MODE 'Import source: 0=fresh, 1=local full-export archive, 2=remote full-export archive, 3=local CAD folder, 4=remote CAD folder' '0 1 2 3 4'
  SOURCE_PATH=''; REMOTE_LOGIN=''
  MIGRATION_STAGE_DIR=''; MIGRATION_STAGE_CREATE=no
  if [[ "$MIGRATION_MODE" != 0 ]]; then
    if [[ "$BACKUPS_ENABLED" == yes ]]; then stage_default="$BACKUP_DIR/swvault-import-staging"; else stage_default='/srv/swvault-import-staging'; fi
    ask MIGRATION_STAGE_DIR 'Import staging directory (absolute path, enough free space for your files)' "$stage_default" '^/.*$'
    MIGRATION_STAGE_DIR="$(realpath -m -- "$MIGRATION_STAGE_DIR")"
    [[ "$MIGRATION_STAGE_DIR" != "$DEST" && "$MIGRATION_STAGE_DIR" != "$DEST"/* ]] || fail 'Import staging must be outside the SWVAULT deployment directory.'
    [[ ! -L "$MIGRATION_STAGE_DIR" ]] || fail 'Import staging path cannot be a symlink.'
    if [[ -e "$MIGRATION_STAGE_DIR" ]]; then
      [[ -d "$MIGRATION_STAGE_DIR" ]] || fail 'Import staging path exists but is not a directory.'
    else
      choose MIGRATION_STAGE_CREATE 'Import staging folder is missing. Create it AFTER DEPLOY approval?' 'yes no'
      [[ "$MIGRATION_STAGE_CREATE" == yes ]] || fail 'Choose an existing staging folder or authorize its creation.'
    fi
    if [[ "$MIGRATION_MODE" == 1 || "$MIGRATION_MODE" == 2 ]]; then
      parent="$MIGRATION_STAGE_DIR"
      while [[ ! -e "$parent" ]]; do parent="$(dirname "$parent")"; done
      [[ "$(mount_device "$parent")" != "$(mount_device /)" ]] || fail 'Full 1.2.9 history imports require independent disk space for a temporary rollback snapshot, EVEN with nightly backups off.'
    fi
    if [[ "$MIGRATION_MODE" == 1 || "$MIGRATION_MODE" == 3 ]]; then
      ask SOURCE_PATH 'Absolute LOCAL source path (archive or directory)' '' '^/.*$'
      SOURCE_PATH="$(realpath -m -- "$SOURCE_PATH")"
      [[ -e "$SOURCE_PATH" ]] || fail 'Source does not exist.'
      if [[ "$MIGRATION_MODE" == 3 ]]; then
        [[ "$SOURCE_PATH" != "$MIGRATION_STAGE_DIR" && "$SOURCE_PATH" != "$MIGRATION_STAGE_DIR"/* && "$MIGRATION_STAGE_DIR" != "$SOURCE_PATH"/* ]] || fail 'CAD source and staging cannot overlap; choose a separate staging folder.'
        [[ -d "$SOURCE_PATH" ]] || fail 'CAD source must be a directory.'
      fi
    else
      ask REMOTE_LOGIN 'SSH source user@private-VM-IP or user@hostname' '' '^[A-Za-z_][A-Za-z0-9_.-]*@[A-Za-z0-9][A-Za-z0-9.-]*$'
      ask SOURCE_PATH 'Absolute REMOTE path (avoid whitespace)' '' '^/[A-Za-z0-9_./-]+$'
    fi
    if [[ "$MIGRATION_MODE" == 1 || "$MIGRATION_MODE" == 2 ]]; then
      [[ "$SOURCE_PATH" == *.tar.gz ]] || fail 'Full export must be a .tar.gz produced by old-vm-export.sh.'
    fi
  fi
  if [[ "$RESTART" == yes && -f "$STATEDIR/import.complete" && ( "$MIGRATION_MODE" == 1 || "$MIGRATION_MODE" == 2 ) ]]; then
    fail 'A full-history import has already completed. Select 0 (keep existing vault); setup restart must not import the same history again.'
  fi
  if [[ -f "$DEST/server/linux/.env" && "$RESTART" == yes ]]; then
    for key in ORG REPO ADMIN_USER R2_BUCKET R2_ACCOUNT_ID; do
      current="$(sed -n "s/^${key}=//p" "$DEST/server/linux/.env" | tail -n 1)"
      [[ -z "$current" || "$current" == "${!key}" ]] || fail "Cannot change $key from '$current' on an initialized vault. RESTART keeps the existing database."
    done
  fi
  # Independent of the backup disk, the VM needs headroom for Docker and Gitea.
  root_avail="$(df -Pk / | awk 'NR==2 {print $4}')"
  (( root_avail >= 8388608 )) || fail 'New VM root disk needs at least 8 GiB free before deployment.'
  if [[ "$DRY_RUN" == no ]] && command -v ss >/dev/null && [[ ! -e "$STATEDIR/owned-install" ]]; then
    if ss -H -ltn 2>/dev/null | awk '{print $4}' | grep -Eq ':(3000|3100|8080)$'; then
      fail 'Ports 3000, 3100 or 8080 are already in use. This must be a clean VM or resolve the conflict first.'
    fi
  fi
  printf '\nREVIEW: VM=%s, URL=https://%s, repo=%s/%s, R2=%s, SMTP=%s\n' "$(hostname)" "$VAULT_HOSTNAME" "$ORG" "$REPO" "$R2_BUCKET" "$SMTP_ENABLED"
  printf 'Scheduled backups: %s; backup path: %s; create path: %s\n' "$BACKUPS_ENABLED" "${BACKUP_DIR:-disabled}" "$BACKUP_DIR_CREATE"
  printf 'Migration: %s; source: %s %s; staging: %s; create staging: %s\n' "$MIGRATION_MODE" "$REMOTE_LOGIN" "$SOURCE_PATH" "${MIGRATION_STAGE_DIR:-not needed}" "$MIGRATION_STAGE_CREATE"
  if [[ "$RESTART" == yes ]]; then
    printf 'RESTART only redoes setup questions. Existing vault data, original files and import markers stay intact.\n'
  fi
  printf 'Only NEW VM is changed; GitHub source commit is displayed before deployment.\n'
  printf 'Changes after DEPLOY can be safely resumed, but package downloads and service creation are not atomically reversible.\n'
  if [[ "$DRY_RUN" == yes ]]; then info 'DRY RUN passed collection and basic validation. No files changed.'; exit 0; fi
  printf 'Type DEPLOY to proceed (anything else cancels): ' >/dev/tty
  read_tty
  [[ "$REPLY" == DEPLOY ]] || exit 0
else
  # Resume old settings, including the backup choice and import staging plan.
  [[ -r "$STATEDIR/migration.state" ]] || fail 'Saved migration plan missing. Choose restart to re-enter configuration.'
  # shellcheck disable=SC1090
  source "$STATEDIR/migration.state"
fi

if [[ "$DRY_RUN" == yes ]]; then
  info 'DRY RUN: existing saved configuration was found; no deploy or service changes made.'
  exit 0
fi

# Directories are only created after DEPLOY (or on explicit resume of an approved plan).
phase='creating approved storage directories'
if [[ "${BACKUPS_ENABLED:-no}" == yes ]]; then
  [[ -n "${BACKUP_DIR:-}" && "$BACKUP_DIR" == /* ]] || fail 'Invalid saved backup configuration.'
  parent="$BACKUP_DIR"
  while [[ ! -e "$parent" ]]; do parent="$(dirname "$parent")"; done
  [[ "$(mount_device "$parent")" != "$(mount_device /)" ]] || fail 'Backup disk appears unmounted at DEPLOY time; refusing to create a misleading backup folder on the root disk.'
  mkdir -p -- "$BACKUP_DIR"
  chmod 700 "$BACKUP_DIR"
fi
if [[ "${MIGRATION_MODE:-0}" != 0 ]]; then
  [[ -n "${MIGRATION_STAGE_DIR:-}" && "$MIGRATION_STAGE_DIR" == /* ]] || fail 'Invalid saved import staging directory.'
  if [[ "$MIGRATION_MODE" == 1 || "$MIGRATION_MODE" == 2 ]]; then
    parent="$MIGRATION_STAGE_DIR"
    while [[ ! -e "$parent" ]]; do parent="$(dirname "$parent")"; done
    [[ "$(mount_device "$parent")" != "$(mount_device /)" ]] || fail 'Full history import requires an independently mounted rollback/staging disk.'
  fi
  mkdir -p -- "$MIGRATION_STAGE_DIR"
  chmod 700 "$MIGRATION_STAGE_DIR"
fi

phase='dependency installation'
changed=yes
info 'Installing basic prerequisites'
if command -v apt-get >/dev/null; then
  export DEBIAN_FRONTEND=noninteractive
  apt-get update -qq
  apt-get install -y -qq git curl ca-certificates rsync jq python3 tar openssh-client unzip rclone sqlite3
elif command -v dnf >/dev/null; then
  dnf install -y -q git curl ca-certificates rsync jq python3 tar openssh-clients unzip rclone sqlite
else
  fail 'Ubuntu/Debian or Fedora package manager required.'
fi

phase='retrieving source'
info "Fetching $REPO_URL"
git clone --depth 1 "$REPO_URL" "$TMP/repo"
[[ -s "$TMP/repo/server/linux/setup.sh" && -s "$TMP/repo/server/linux/import-129-fresh-vm.sh" && -f "$TMP/repo/server/linux/docker-compose.yml" ]] || fail 'Repository does not match expected SWVAULT 1.4 layout.'
[[ -f "$TMP/repo/server/linux/r2-transfer/service.py" ]] || fail 'Direct-R2 upload coordinator missing; refusing install.'
grep -q 'SWVAULT_WIZARD_APPROVED' "$TMP/repo/server/linux/setup.sh" || fail 'Public repository is missing wizard-aware setup.sh. Upload ALL patch files to GitHub first.'
grep -q 'swvault-import-stage' "$TMP/repo/server/linux/import-129-fresh-vm.sh" || fail 'Public repository is missing safe-import patch. Upload ALL patch files first.' 
commit="$(git -C "$TMP/repo" rev-parse HEAD)"
if [[ "$RESUME" == yes && -s "$STATEDIR/source-commit" ]]; then
  original_commit="$(cat "$STATEDIR/source-commit")"
  if [[ "$commit" != "$original_commit" ]]; then
    info 'Repository main changed since your previous attempt; resuming from the original reviewed commit.'
    git -C "$TMP/repo" fetch --depth 1 origin "$original_commit" || fail 'Cannot fetch original commit safely; contact an admin before replacing files.'
    git -C "$TMP/repo" checkout -q --detach "$original_commit"
    commit="$(git -C "$TMP/repo" rev-parse HEAD)"
    [[ "$commit" == "$original_commit" ]] || fail 'Pinned commit verification failed.'
  fi
fi
printf 'Source commit: %s\n' "$commit"
printf 'Review https://github.com/silverace71/solidworks-vault/commit/%s before deploying.\n' "$commit"
if [[ "$RESUME" == no ]]; then
  printf 'Type the last 8 characters of the commit to accept this exact checkout: ' >/dev/tty
  read_tty
  [[ "$REPLY" == "${commit: -8}" ]] || fail 'Source verification rejected; no SWVAULT files changed.'
fi

phase='copying deployment source'
# Preserve all runtime state and confidential files, even on interrupted reruns.
if [[ -d "$DEST" ]]; then
  stamp="$(date -u +%Y%m%dT%H%M%SZ)"
  mkdir -p "$STATEDIR/source-snapshots"
  tar --exclude='server/linux/data' --exclude='server/linux/.env' --exclude='server/linux/team.json' --exclude='.git' -czf "$STATEDIR/source-snapshots/preinstall-$stamp.tar.gz" -C "$DEST" . || true
fi
mkdir -p "$DEST"
rsync -a --exclude='.git' --exclude='server/linux/data/' --exclude='server/linux/.env' --exclude='server/linux/team.json' --exclude='server/linux/.env.r2.production' --exclude='server/linux/team-icon.png' --exclude='server/linux/invites/web/logo.svg' --exclude='server/linux/invites/web/logo-light.svg' "$TMP/repo/" "$DEST/"
printf '%s\n' "$commit" > "$STATEDIR/source-commit"
chmod +x "$DEST/server/linux/setup.sh"

if [[ "$RESUME" == no ]]; then
  phase='saving setup settings'
  install -d -m 700 "$DEST/server/linux"
  ENVFILE="$DEST/server/linux/.env"
  [[ ! -e "$ENVFILE" || "$RESTART" == yes ]] || fail 'Existing .env detected without restart consent.'
  # Keep previous config snapshots when restarting a wizard without deleting vault data.
  if [[ "$RESTART" == yes && -f "$ENVFILE" ]]; then
    checkpoint="$STATEDIR/config-checkpoints/restart-$(date -u +%Y%m%dT%H%M%SZ)-$$"
    mkdir -p "$checkpoint"
    chmod 700 "$STATEDIR/config-checkpoints" "$checkpoint"
    cp -p -- "$ENVFILE" "$checkpoint/old.env"
    if [[ -f "$STATEDIR/migration.state" ]]; then
      cp -p -- "$STATEDIR/migration.state" "$checkpoint/old.migration.state"
    fi
    chmod 600 "$checkpoint"/*
    # Marker is removed only when both files are fully replaced.
    printf '%s\n' "$checkpoint" > "$STATEDIR/config-write.pending"
    chmod 600 "$STATEDIR/config-write.pending"
  fi
  # Write root-only plan atomically; it will be sourced only from our private state directory.
  plan_tmp="$TMP/migration.state"
  printf 'MIGRATION_MODE=%q\nREMOTE_LOGIN=%q\nSOURCE_PATH=%q\nBACKUP_DIR=%q\nBACKUPS_ENABLED=%q\nBACKUP_DIR_CREATE=%q\nMIGRATION_STAGE_DIR=%q\nMIGRATION_STAGE_CREATE=%q\nSMTP_TEST_TO=%q\n' \
    "$MIGRATION_MODE" "$REMOTE_LOGIN" "$SOURCE_PATH" "$BACKUP_DIR" "$BACKUPS_ENABLED" "$BACKUP_DIR_CREATE" "$MIGRATION_STAGE_DIR" "$MIGRATION_STAGE_CREATE" "$SMTP_TEST_TO" > "$plan_tmp"
  chmod 600 "$plan_tmp"
  mv -- "$plan_tmp" "$STATEDIR/migration.state"
  touch "$STATEDIR/owned-install"
  # Only write secrets after explicit DEPLOY. Use temp + mv to avoid truncated .env.
  ENV_TMP="$TMP/pending.env"
  (
    umask 077
    # On RESTART, retain every unmodified internal secret (Gitea keys,
    # previous vault URL and imported identity) rather than generating new keys.
    if [[ "$RESTART" == yes && -s "$ENVFILE" ]]; then
      cp -p -- "$ENVFILE" "$ENV_TMP"
    else
      : > "$ENV_TMP"
    fi
    add_env(){
      local key="$1" value="$2" updated="$TMP/.updated-env"
      if grep -qE "^${key}=" "$ENV_TMP"; then
        KEY="$key" VALUE="$value" awk 'BEGIN {FS="="} $1==ENVIRON["KEY"] { print ENVIRON["KEY"] "=" ENVIRON["VALUE"]; next } {print}' "$ENV_TMP" > "$updated"
        mv -- "$updated" "$ENV_TMP"
      else
        printf '%s=%s\n' "$key" "$value" >> "$ENV_TMP"
      fi
    }
    add_env TEAM_NAME "$TEAM_NAME"; add_env ORG "$ORG"; add_env REPO "$REPO"
    add_env ADMIN_USER "$ADMIN_USER"; add_env LOCAL_ROOT "$LOCAL_ROOT"
    add_env SW_VERSION "${SW_VERSION/none/}"
    add_env CF_DOMAIN "$CF_DOMAIN"; add_env VAULT_HOSTNAME "$VAULT_HOSTNAME"
    add_env CF_PUBLIC_HOST "$VAULT_HOSTNAME"; add_env R2_BUCKET "$R2_BUCKET"
    add_env R2_ACCOUNT_ID "$R2_ACCOUNT_ID"; add_env R2_ACCESS_KEY_ID "$R2_ACCESS_KEY_ID"
    add_env R2_SECRET_ACCESS_KEY "$R2_SECRET_ACCESS_KEY"; add_env CF_TUNNEL_TOKEN "$CF_TUNNEL_TOKEN"
    add_env EMAIL_DOMAINS "$EMAIL_DOMAINS"; add_env SMTP_HOST "$SMTP_HOST"
    add_env SMTP_PORT "$SMTP_PORT"; add_env SMTP_USER "$SMTP_USER"
    add_env SMTP_PASSWORD "$SMTP_PASSWORD"; add_env SMTP_FROM "$SMTP_FROM"
    add_env BACKUP_DIR "$BACKUP_DIR"; add_env BACKUPS_ENABLED "$BACKUPS_ENABLED"
    add_env SMTP_ENABLED "$SMTP_ENABLED"; add_env KEEP_AWAKE "$KEEP_AWAKE"
    add_env SWVAULT_SETUP_STAGE in_progress
  )
  chmod 600 "$ENV_TMP"
  mv -- "$ENV_TMP" "$ENVFILE"
  rm -f -- "$STATEDIR/config-write.pending"
fi

phase='base SWVAULT setup'
info 'Running included setup.sh (R2 test, Docker, Gitea, Tunnel, email, backups)'
SWVAULT_WIZARD=yes SWVAULT_WIZARD_APPROVED=yes SWVAULT_WIZARD_EMAIL_TEST="${SMTP_TEST_TO:-}" bash "$DEST/server/linux/setup.sh"

phase='migration staging'
source "$STATEDIR/migration.state"
stage_dir="${MIGRATION_STAGE_DIR:-}"
if [[ "$MIGRATION_MODE" != 0 ]]; then
  [[ -n "$stage_dir" && -d "$stage_dir" ]] || fail 'Import staging directory is missing; choose RESTART to select another.'
fi
case "$MIGRATION_MODE" in
  0) info 'No file import selected. Create or import CAD files through Windows SWVAULT.' ;;
  1|2)
    dest_file="$stage_dir/$(basename "$SOURCE_PATH")"
    if [[ ! -s "$dest_file" || ! -s "$dest_file.sha256" ]]; then
      if [[ "$MIGRATION_MODE" == 1 ]]; then
        rsync -a --partial -- "$SOURCE_PATH" "$SOURCE_PATH.sha256" "$stage_dir/"
      else
        # Remote path restricted to POSIX filename characters at prompt time.
        rsync -a --partial -e ssh -- "$REMOTE_LOGIN:$SOURCE_PATH" "$REMOTE_LOGIN:$SOURCE_PATH.sha256" "$stage_dir/"
      fi
    fi
    [[ -s "$dest_file" && -s "$dest_file.sha256" ]] || fail 'Missing archive or checksum. Re-run after correcting source.'
    expected="$(awk 'NR==1 {print $1}' "$dest_file.sha256")"
    [[ "$expected" =~ ^[0-9a-fA-F]{64}$ ]] || fail 'Invalid archive checksum format.'
    actual="$(sha256sum "$dest_file" | awk '{print $1}')"
    [[ "$actual" == "$expected" ]] || fail 'Archive SHA-256 mismatch; do not import.'
    info "Archive SHA-256 verified: $dest_file"
    if [[ -f "$STATEDIR/import.complete" ]]; then
      info 'Prior completed import marker found; will not import again.'
    else
      printf '\nFull import replaces the NEW VM test Gitea database. Old VM remains untouched.\n'
      printf 'Type IMPORT_129 to run the repository migration script (or SKIP to stage only): ' >/dev/tty
      read_tty
      if [[ "$REPLY" == IMPORT_129 ]]; then
        "$DEST/server/linux/import-129-fresh-vm.sh" "$dest_file" "$stage_dir"
        touch "$STATEDIR/import.complete"
      else
        info 'Archive staged but NOT imported. Later run:'
        printf '  sudo %q %q %q\n' "$DEST/server/linux/import-129-fresh-vm.sh" "$dest_file" "$stage_dir"
      fi
    fi
    ;;
  3|4)
    dest_dir="$stage_dir/cad-folder"
    mkdir -p "$dest_dir"
    if [[ "$MIGRATION_MODE" == 3 ]]; then
      rsync -a --partial --checksum -- "$SOURCE_PATH/" "$dest_dir/"
    else
      rsync -a --partial --checksum -e ssh -- "$REMOTE_LOGIN:$SOURCE_PATH/" "$dest_dir/"
    fi
    info "CAD files staged at $dest_dir"
    printf 'IMPORTANT: Stage-only. Import this folder from the 1.4 Windows SWVAULT client via Import Folder.\n'
    printf 'Do NOT copy the staging folder into Gitea data or expect it to appear in history automatically.\n'
    ;;
esac
phase='complete'
sed -i 's/^SWVAULT_SETUP_STAGE=.*/SWVAULT_SETUP_STAGE=complete/' "$DEST/server/linux/.env"
info 'Installer finished. Follow the acceptance tests before moving users.'
printf 'Status: sudo /opt/swvault/server/linux/swvault-admin.sh status\n'
printf 'Direct-R2 test: sudo /opt/swvault/server/linux/verify-r2-direct.sh (needs a real LFS object)\n'
printf 'Cutover is NEVER automatic; after production pilot, use arm-129-cutover.sh manually.\n'
