#!/usr/bin/env bash
# Run ONLY on the original SWVAULT 1.2.9 VM, after publishing the 1.4 update.
# Stops writers and leaves them STOPPED to avoid split-brain. Never moves/deletes data.
set -euo pipefail
[[ $EUID == 0 ]] || { echo 'Use sudo.' >&2; exit 1; }
cd "$(dirname "${BASH_SOURCE[0]}")"
[[ -s .env && -d data/gitea && -f team.json && -f docker-compose.yml ]] || { echo 'Not in the original SWVAULT server/linux directory.' >&2; exit 1; }
[[ ! -f data/r2-transfer/transfers.db ]] || { echo 'This looks like a 1.4 VM; do not export it as 1.2.9.' >&2; exit 1; }
output="${1:-}"
[[ "$output" == /* && "$output" == *.tar.gz ]] || { echo 'Usage: sudo ./export-129-final.sh /separate-disk/private/swvault-129-final.tar.gz' >&2; exit 1; }
[[ ! -e "$output" ]] || { echo 'Destination already exists; refusing to overwrite.' >&2; exit 1; }
mkdir -p "$(dirname "$output")"
[[ "$(df -P "$(dirname "$output")" | tail -1 | awk '{print $1}')" != "$(df -P data/gitea | tail -1 | awk '{print $1}')" ]] || {
  echo 'Use an independent disk for the final snapshot.' >&2; exit 1; }
need="$(du -sk data/gitea data/invites 2>/dev/null | awk '{sum+=$1} END {print sum}')"
avail="$(df -Pk "$(dirname "$output")" | tail -1 | awk '{print $4}')"
(( avail > need + 2097152 )) || { echo 'Need source size plus 2 GiB available on backup disk.' >&2; exit 1; }
printf '\nFINAL SWVAULT 1.2.9 EXPORT\nOld users must stop writing NOW. Old Gitea will remain OFFLINE after export.\n'
read -r -p 'Type FREEZE_129 to stop all writes and create the final migration snapshot: ' ok </dev/tty
[[ "$ok" == FREEZE_129 ]] || exit 1
umask 077
stopped=no
complete=no
recover() {
  rc=$?
  if [[ "$complete" != yes && "$stopped" == yes ]]; then
    echo 'Export failed. Restarting original writers so work can resume.' >&2
    docker compose -f docker-compose.yml up -d gitea invites >/dev/null 2>&1 || true
    systemctl enable --now swvault-watchdog.timer swvault-autoupdate.timer swvault-backup.timer >/dev/null 2>&1 || true
    rm -f "$output"
  fi
  exit "$rc"
}
trap recover EXIT
# Prevent the original watchdog/auto-updater from restarting old writers.
systemctl disable --now swvault-watchdog.timer swvault-autoupdate.timer swvault-autoupdate.path >/dev/null 2>&1 || true
systemctl stop swvault-watchdog.service swvault-autoupdate.service swvault-backup.timer swvault-backup.service >/dev/null 2>&1 || true
docker compose -f docker-compose.yml stop invites gitea
stopped=yes
names=(.env team.json data/gitea)
[[ ! -d data/invites ]] || names+=(data/invites)
[[ ! -f data/admin-token ]] || names+=(data/admin-token)
for custom in team-icon.png invites/web/logo.svg invites/web/logo-light.svg; do
  [[ ! -f "$custom" ]] || names+=("$custom")
done
# Original Tailscale device keys intentionally excluded. Transfer via encrypted/offline storage.
tar -czf "$output" "${names[@]}"
tar -tzf "$output" >/dev/null
chmod 600 "$output"
sha256sum "$output" > "$output.sha256"
chmod 600 "$output.sha256"
complete=yes
trap - EXIT
printf '\nExport completed: %s\n' "$output"
echo 'Original Gitea/invites are STOPPED. Do not restart writable services after importing on the new VM.'
echo 'If migration is abandoned BEFORE new writes, restore the old server: sudo docker compose up -d gitea invites'
