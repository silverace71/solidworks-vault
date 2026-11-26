#!/usr/bin/env bash
# One-time SWVAULT 1.4 object-key validation hotfix. Does not touch data, .env, or team.json.
set -euo pipefail
if [[ $(id -u) -ne 0 ]]; then echo 'Run as root: sudo bash fix-live-server.sh' >&2; exit 1; fi
APP=/opt/swvault/server/linux
cd "$APP"
for f in invites/browser.py r2-transfer/service.py; do
  if [[ ! -f "$f" ]]; then echo "Missing $APP/$f" >&2; exit 1; fi
done
BACKUP="/root/swvault-before-r2-path-fix-$(date +%Y%m%d-%H%M%S)"
mkdir -m 700 "$BACKUP"
cp -p invites/browser.py "$BACKUP/browser.py"
cp -p r2-transfer/service.py "$BACKUP/service.py"
python3 - <<'PY'
from pathlib import Path
files=[Path('invites/browser.py'), Path('r2-transfer/service.py')]
old="not key.endswith('/' + oid) or"
new="key != f'lfs/{oid[:2]}/{oid[2:4]}/{oid[4:]}' or"
texts=[]
for path in files:
    s=path.read_text()
    if s.count(old)!=1 and s.count(new)!=1:
        raise SystemExit(f'Unexpected source in {path}; refusing partial patch')
    texts.append(s)
for path,s in zip(files,texts):
    if old in s:
        path.write_text(s.replace(old,new,1))
    print(f'Canonical LFS key check present: {path}')
PY
python3 -m py_compile invites/browser.py r2-transfer/service.py
docker compose config -q
docker compose build r2-transfer
docker compose up -d --no-deps --force-recreate invites r2-transfer
printf '\nSaved old files to: %s\n' "$BACKUP"
printf 'R2 coordinator health:\n'
curl -fsS --max-time 10 http://127.0.0.1:8080/swvault-transfer/health || true
printf '\nRetest the authenticated browser download. Do not rerun setup/import.\n'
