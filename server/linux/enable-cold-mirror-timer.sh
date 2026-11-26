#!/usr/bin/env bash
# Install hourly single-machine background copy service, opt-in only.
set -euo pipefail
[[ ${EUID:-$(id -u)} -eq 0 ]] || { echo 'Run with sudo.' >&2; exit 1; }
cd "$(dirname "${BASH_SOURCE[0]}")"
script="$(pwd)/cold-mirror-r2.sh"
[[ -x "$script" ]] || { echo "First run chmod 755 $script" >&2; exit 1; }
cat > /etc/systemd/system/swvault-cold-mirror.service <<UNIT
[Unit]
Description=SWVAULT private R2 to local cold mirror (limited to 5 Mbps)
After=network-online.target
Wants=network-online.target

[Service]
Type=oneshot
ExecStart=$script
TimeoutStartSec=0
Nice=10
IOSchedulingClass=idle
UNIT
cat > /etc/systemd/system/swvault-cold-mirror.timer <<'UNIT'
[Unit]
Description=Run SWVAULT cold mirror hourly

[Timer]
OnCalendar=hourly
RandomizedDelaySec=300
Persistent=true
Unit=swvault-cold-mirror.service

[Install]
WantedBy=timers.target
UNIT
systemctl daemon-reload
systemctl enable --now swvault-cold-mirror.timer
systemctl list-timers swvault-cold-mirror.timer
