#!/usr/bin/env bash
# Nightly vault backup for the Docker deployments (server/docker and server/linux; the latter
# schedules it with a systemd timer). Run from cron as root, e.g.:
#   15 2 * * *  /opt/swvault/server/backup/backup-gitea.sh /opt/swvault/server/docker /mnt/backup
#
# Keeps 14 daily archives of the database/config/repositories, and mirrors the LFS content
# store (the big part) with rsync, which only copies new objects. Copy /mnt/backup offsite too.
set -euo pipefail

COMPOSE_DIR="${1:?compose directory}"
DEST="${2:?backup destination}"
STAMP="$(date +%Y%m%d-%H%M%S)"
mkdir -p "$DEST/daily" "$DEST/lfs"

cd "$COMPOSE_DIR"
if [[ -f .env.r2.production && "$(sed -n 's/^CF_DEPLOYMENT=//p' .env | tail -1)" != cloudflare ]]; then
  dc=(docker compose --env-file .env --env-file .env.r2.production -f docker-compose.tailscale-legacy.yml -f docker-compose.r2-legacy.yml)
else
  dc=(docker compose)
fi
# gitea dump: database, config, repositories (small: the vault repos only hold pointers + metadata).
"${dc[@]}" exec -T -u git gitea bash -c "cd /tmp && gitea dump --skip-lfs-data --type tar.gz --file /tmp/swvault-dump.tar.gz"
"${dc[@]}" cp gitea:/tmp/swvault-dump.tar.gz "$DEST/daily/gitea-$STAMP.tar.gz"
"${dc[@]}" exec -T -u git gitea rm -f /tmp/swvault-dump.tar.gz

# Server settings and identity (server/linux layout): .env with Gitea secrets, Tailscale node
# state (keeps the same https://... address after a restore), admin token, team.json. Small.
config=()
# data/invites/*.json: invites, member/lead profiles, projects/subsystems and their REs, which review emails went out (not the installer zip).
for f in .env .env.r2.production team.json data/admin-token data/admin-credentials.txt data/cloudflare/tunnel-token data/tailscale data/r2-rclone.conf data/r2-transfer/transfers.db data/invites/invites.json data/invites/people.json data/invites/review-emails.json data/invites/subsystems.json; do
  if [[ -e "$COMPOSE_DIR/$f" ]]; then config+=("$f"); fi
done
if ((${#config[@]} > 0)); then
  (umask 077; tar -czf "$DEST/daily/server-config-$STAMP.tar.gz" -C "$COMPOSE_DIR" "${config[@]}")
fi

# LFS objects are content-addressed and never change, so an additive rsync is a complete backup.
if [[ -d "$COMPOSE_DIR/data/gitea/git/lfs" ]]; then
  rsync -a "$COMPOSE_DIR/data/gitea/git/lfs/" "$DEST/lfs/"
fi
# New 1.4 multipart upload finalizations write a verified, immutable local copy.
if [[ -d "$COMPOSE_DIR/data/cold-lfs" ]]; then
  mkdir -p "$DEST/cold-lfs"
  rsync -a "$COMPOSE_DIR/data/cold-lfs/" "$DEST/cold-lfs/"
fi
# Canonical R2 objects MUST also be backed up independently. Never delete old objects.
if [[ -f "$COMPOSE_DIR/data/r2-rclone.conf" ]]; then
  command -v rclone >/dev/null || { echo "ERROR: rclone missing; R2 backup NOT done" >&2; exit 1; }
  bucket="$(sed -n 's/^R2_BUCKET=//p' "$COMPOSE_DIR/.env" | tail -1)"
  if [[ -z "$bucket" && -f "$COMPOSE_DIR/.env.r2.production" ]]; then
    bucket="$(sed -n 's/^R2_BUCKET=//p' "$COMPOSE_DIR/.env.r2.production" | tail -1)"
  fi
  [[ "$bucket" =~ ^[a-z0-9][a-z0-9.-]{1,61}[a-z0-9]$ ]] || { echo "ERROR: invalid R2 bucket" >&2; exit 1; }
  # NOTE: This consumes local backup disk capacity and cloud-to-host traffic.
  rclone --config "$COMPOSE_DIR/data/r2-rclone.conf" copy "r2:$bucket/lfs" "$DEST/lfs-r2/" --transfers 4 --checkers 8
fi

ls -1t "$DEST/daily"/gitea-*.tar.gz | tail -n +15 | xargs -r rm -f
ls -1t "$DEST/daily"/server-config-*.tar.gz 2>/dev/null | tail -n +15 | xargs -r rm -f
echo "Backup $STAMP complete."
