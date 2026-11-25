#!/usr/bin/env bash
# One-time FULL SWVAULT 1.2.9 export. Run on the ORIGINAL VM only.
# Preflight remote SSH destination, freeze writers, generate checksum, rsync to new VM.
# Source system is left frozen after success to avoid diverging histories.
set -Eeuo pipefail
umask 077
[[ $EUID -eq 0 ]] || { echo 'Run as root/sudo on the OLD 1.2.9 VM.' >&2; exit 1; }
[[ -r /dev/tty ]] || { echo 'Interactive terminal required.' >&2; exit 1; }
failed=0
phase=preflight
frozen=no
completed=no
cleanup(){
  local rc=$?
  trap - EXIT INT TERM
  if (( rc != 0 )); then
    echo "Export interrupted during $phase." >&2
    if [[ "$frozen" == yes ]]; then
      echo 'Original writers are FROZEN. Inspect the old VM before restarting anything.' >&2
      echo 'Do not enable both old and new vaults for check-ins at the same time.' >&2
    else
      echo 'Old SWVAULT services have not been stopped by this script.' >&2
    fi
  fi
  exit "$rc"
}
trap cleanup EXIT
trap 'exit 130' INT
trap 'exit 143' TERM
ask(){ local name="$1" label="$2" def="${3:-}" val;
  while :; do
    printf '%s%s: ' "$label" "${def:+ [$def]}" >/dev/tty
    IFS= read -r val </dev/tty || exit 130
    val="${val:-$def}"
    [[ -n "$val" ]] || { echo 'Cannot be empty.' >/dev/tty; continue; }
    printf -v "$name" '%s' "$val"; break
  done
}
cat <<'INTRO'
SWVAULT 1.2.9 -> 1.4 FINAL EXPORT (old VM only)

Before running:
 - Users must finish their work. This script STOPS Gitea and invites.
 - You need an independent mounted backup disk on the OLD VM to fit
   Gitea data/LFS (plus extra free space). Do not use /tmp or the root disk.
 - Both VMs should be on the fast Proxmox guest-reachable network, with SSH
   allowed from this VM to the new VM. No public Internet transfer required.
 - On the NEW VM, create a private destination folder owned by your SSH user:
   mkdir -p -m 700 ~/swvault-transfer
 - If using a second fast NIC, verify 'ip route get <NEW_VM_FAST_IP>' goes
   through that NIC. The Proxmox live-migration network is not automatically
   attached to guest operating systems.
 - The archive includes PRIVATE credentials. It must remain root/private.
 - The old vault will stay STOPPED after successful export.

Ctrl+C before FREEZE_129 makes no changes to services.
INTRO
ask OLD_DIR 'Path of OLD server/linux' '/opt/swvault/server/linux'
[[ "$OLD_DIR" == /* && -f "$OLD_DIR/docker-compose.yml" && -f "$OLD_DIR/.env" && -f "$OLD_DIR/team.json" && -d "$OLD_DIR/data/gitea" ]] || {
  echo 'Cannot locate a complete original SWVAULT install.' >&2; exit 1;
}
[[ ! -e "$OLD_DIR/data/r2-transfer/transfers.db" ]] || { echo 'This looks like 1.4, refusing.' >&2; exit 1; }
ask BACKUP_PATH 'Absolute path for final archive on independent backup disk' '/mnt/swvault-backups/swvault-129-final.tar.gz'
[[ "$BACKUP_PATH" == /*.tar.gz ]] || { echo 'Must be an absolute .tar.gz path.' >&2; exit 1; }
[[ "$(basename "$BACKUP_PATH")" =~ ^[A-Za-z0-9_.-]+\.tar\.gz$ ]] || {
  echo 'Archive filename may contain only letters, digits, dots, underscores, hyphens.' >&2; exit 1;
}
[[ ! -e "$BACKUP_PATH" && ! -e "$BACKUP_PATH.sha256" ]] || { echo 'Existing export will not be overwritten. Pick a NEW filename.' >&2; exit 1; }
[[ -d "$(dirname "$BACKUP_PATH")" ]] || { echo 'Create and mount backup directory first.' >&2; exit 1; }
[[ "$(df -P "$(dirname "$BACKUP_PATH")" | awk 'NR==2{print $1}')" != "$(df -P "$OLD_DIR/data/gitea" | awk 'NR==2{print $1}')" ]] || {
  echo 'Backup must be an independent filesystem/disk.' >&2; exit 1;
}
ask NEW_SSH 'New VM SSH user@FAST-LAN-IP' ''
[[ "$NEW_SSH" =~ ^[a-zA-Z_][a-zA-Z0-9_.-]*@[a-zA-Z0-9][a-zA-Z0-9.-]*$ ]] || { echo 'Bad SSH destination.' >&2; exit 1; }
ask NEW_DIR 'Remote directory (under SSH user home, no spaces)' 'swvault-transfer'
[[ "$NEW_DIR" =~ ^[A-Za-z0-9_./-]+$ && "$NEW_DIR" != /* && "$NEW_DIR" != *..* ]] || { echo 'Use a simple relative folder under the remote home directory.' >&2; exit 1; }
command -v rsync >/dev/null || { echo 'Install rsync first: sudo apt-get install rsync' >&2; exit 1; }
command -v docker >/dev/null || { echo 'Docker missing.' >&2; exit 1; }
docker compose version >/dev/null || { echo 'Docker Compose missing.' >&2; exit 1; }
command -v ssh >/dev/null || { echo 'OpenSSH client missing.' >&2; exit 1; }
info(){ printf '\n==> %s\n' "$*"; }
info 'Testing new VM SSH and destination without touching old vault'
# Prefer an operator-configured SSH key. Do not disable host key validation.
ssh -o BatchMode=yes -o ConnectTimeout=10 "$NEW_SSH" "test -d '$NEW_DIR' && test -w '$NEW_DIR' && command -v rsync >/dev/null" || {
  echo 'SSH key auth/destination not ready. Set up authorized_keys and create target folder first.' >&2; exit 1;
}
info 'Checking old backup capacity'
need="$(du -sk "$OLD_DIR/data/gitea" "$OLD_DIR/data/invites" 2>/dev/null | awk '{s+=$1} END {print s+0}')"
avail="$(df -Pk "$(dirname "$BACKUP_PATH")" | awk 'NR==2{print $4}')"
(( avail > need + 2097152 )) || { echo 'Insufficient backup space (need current data size + 2GiB).' >&2; exit 1; }
info 'Review the source/destination and freeze warning'
printf 'Old source: %s\nPrivate archive: %s\nNew VM: %s:%s\n' "$OLD_DIR" "$BACKUP_PATH" "$NEW_SSH" "$NEW_DIR"
printf 'Type FREEZE_129 to STOP all old vault check-ins and export: ' >/dev/tty
IFS= read -r confirm </dev/tty || exit 130
[[ "$confirm" == FREEZE_129 ]] || exit 0
phase='freezing original vault'
cd "$OLD_DIR"
# Keep watchdogs from reopening Gitea during the snapshot.
systemctl disable --now swvault-watchdog.timer swvault-autoupdate.timer swvault-autoupdate.path >/dev/null 2>&1 || true
systemctl stop swvault-watchdog.service swvault-autoupdate.service swvault-backup.timer swvault-backup.service >/dev/null 2>&1 || true
docker compose stop invites gitea
frozen=yes
phase='archiving original vault'
names=(.env team.json data/gitea)
[[ ! -d data/invites ]] || names+=(data/invites)
[[ ! -f data/admin-token ]] || names+=(data/admin-token)
for f in team-icon.png invites/web/logo.svg invites/web/logo-light.svg; do [[ ! -f "$f" ]] || names+=("$f"); done
# Write partial archive, rename atomically only after tar validation.
tmpfile="$(dirname "$BACKUP_PATH")/.swvault-export-$$.partial"
trap 'rm -f "${tmpfile:-}"; cleanup' EXIT
(umask 077; tar -czf "$tmpfile" "${names[@]}")
tar -tzf "$tmpfile" >/dev/null
mv "$tmpfile" "$BACKUP_PATH"
sha256sum "$BACKUP_PATH" > "$BACKUP_PATH.sha256"
chmod 600 "$BACKUP_PATH" "$BACKUP_PATH.sha256"
phase='rsync to new VM'
info 'Copying archive over SSH (resumable; old VM stays frozen)'
rsync -a --partial --append-verify --info=progress2 -e ssh -- "$BACKUP_PATH" "$BACKUP_PATH.sha256" "$NEW_SSH:$NEW_DIR/"
# Verify remote checksum against copied bytes; path may differ from local.
base="$(basename "$BACKUP_PATH")"
expected="$(sha256sum "$BACKUP_PATH" | awk '{print $1}')"
remote_hash="$(ssh -o BatchMode=yes "$NEW_SSH" "sha256sum '$NEW_DIR/$base'" | awk '{print $1}')"
[[ "$remote_hash" == "$expected" ]] || { echo 'New VM archive checksum mismatch. DO NOT import.' >&2; exit 1; }
completed=yes
phase='complete'
cat <<'NEXT'

SUCCESS. Original SWVAULT 1.2.9 is intentionally FROZEN. Do not
restart it for writes after users start using the new vault.

On the NEW VM, run the installer and choose 'local full-export archive',
then provide the absolute path to this rsync destination archive.
Keep the checksum file alongside it. The wizard independently verifies it.

For a full rollback BEFORE any new-VM writes, coordinate everyone, then
on the old VM: sudo docker compose up -d gitea invites
NEXT
