#!/usr/bin/env bash
# Install only the installer/R2 sync timer; does not enable legacy auto-update.
set -euo pipefail
[[ ${EUID:-$(id -u)} -eq 0 ]] || { echo 'Run with sudo.' >&2; exit 1; }
cd "$(dirname "${BASH_SOURCE[0]}")"
source ./lib.sh
[[ -f "$DATA_DIR/installer-r2-sync.env" ]] || die 'Create data/installer-r2-sync.env first (see docs)'
[[ -f "$SWVAULT_DIR/installer-r2-sync.sh" ]] || die 'Missing installer-r2-sync.sh'
cat >/etc/systemd/system/swvault-installer-r2-sync.service <<EOF
[Unit]
Description=SWVAULT installer-only sync from public GitHub Releases to R2
After=network-online.target docker.service
Wants=network-online.target

[Service]
Type=oneshot
ExecStart=/bin/bash $SWVAULT_DIR/installer-r2-sync.sh
TimeoutStartSec=2h
EOF
cat >/etc/systemd/system/swvault-installer-r2-sync.timer <<EOF
[Unit]
Description=Poll GitHub Releases and sync SWVAULT installer assets to R2

[Timer]
OnCalendar=hourly
Persistent=true
RandomizedDelaySec=5m

[Install]
WantedBy=timers.target
EOF
systemctl daemon-reload
systemctl enable --now swvault-installer-r2-sync.timer
say 'Installer-only sync timer enabled. Manually run: sudo systemctl start swvault-installer-r2-sync.service'
systemctl list-timers swvault-installer-r2-sync.timer --no-pager
