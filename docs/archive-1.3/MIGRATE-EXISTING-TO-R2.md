# Guide 1 — Existing SWVAULT 1.2.9 production → R2 → Cloudflare

**Read this before running any new `setup.sh`.** Designed for the existing working Tailscale/Funnel SWVAULT VM, your Gitea users/roles/locks/history, and `lynxmotorsports.racing`. The source bundle's **legacy `MIGRATE` mode has intentionally been disabled**. The safe path is *storage first*, *public hostname later*.

## What is actually supported

| Workflow | Route | Today |
|---|---|---|
| 100+ GB initial Get Latest | Authenticated LFS batch → short-lived signed private R2 URLs → Windows | Supported, verify on your VM |
| Check-in including >100 MB single CAD file | Existing `*.ts.net` Funnel → existing Gitea → R2 | Supported if your existing Funnel handles the tested sizes; check-in remains through VM |
| Check-in after hostname change | `vault.lynxmotorsports.racing` for metadata; **alternate non-proxied** Tailscale hostname for the upload action | Implemented in updated Windows client, **requires new client build and dual-endpoint pilot test** |
| Windows → direct R2 multipart check-in | Presigned UploadPart → server verification → Gitea LFS registration | **NOT yet implemented or production ready**; see `DIRECT-R2-MULTIPART-DESIGN.md` |
| Browser downloads | Browser → Python → Gitea | Still relayed by VM; use desktop client for bulk downloads |

Do not add a Cloudflare Cache Everything rule for private LFS, publish the bucket, or try to solve the HTTP 413 limit with compression. R2 S3 requests bypass the Tunnel, but **the standard Gitea LFS upload endpoint does not**. Cloudflare Free enforces 100 MB per proxied request.

## 0. Collect before you touch the server

- Cloudflare site active: `lynxmotorsports.racing`.
- Create *private Standard* R2 bucket (for example `swvault-lfs`). In R2 Overview record the **Account ID**. In Manage R2 API Tokens create **Object Read & Write** limited to that bucket and save **Access Key ID** + **Secret Access Key** locally in a password manager. Do not send secrets over chat.
- **Existing** Gitea organization/repository (`ORG`, `REPO`), admin user, `LOCAL_ROOT`, Tailscale hostname, team invites, SMTP, and any custom roles. Keep these exactly.
- Independent backup disk with capacity exceeding `du -sh /opt/swvault/server/linux/data/gitea` plus headroom, and a hypervisor snapshot.
- On the VM: Docker Compose, `rclone`, `rsync`, `sha256sum`. Check with `command -v rclone rsync docker`.

```bash
cd /opt/swvault/server/linux
sudo ./config-inventory.sh
sudo du -sh data/gitea data/gitea/git/lfs
sudo docker compose -f docker-compose.tailscale-legacy.yml ps
```

**Important:** If you copied this source over `/opt/swvault`, do so without deleting `data/`, `.env`, `team.json`, logos, installer archives, or `.gitignore`-excluded paths. Source code is NOT your data backup.

## 1. Storage-only maintenance window (no hostname change)

1. Tell members to **finish and check in work or retain checked-out files safely**, then stop all new check-ins. Back up the VM/snapshot and verify a restore to a clone before production.
2. Stage the new release source at the same existing location; copy the new `docker-compose.tailscale-legacy.yml`, `docker-compose.r2-legacy.yml`, `production-r2-migrate.sh`, and `lib.sh`, plus `verify-r2-direct.sh` and backup script. **Do not run `setup.sh`** on your existing VM. Never `rsync --delete` against production.
3. Mount a different filesystem, e.g. `/mnt/swvault-backup`. Verify it is present with `df -h /mnt/swvault-backup`.
4. Execute the guarded storage-only script; it asks for all R2 credentials *before* stopping services:

```bash
cd /opt/swvault/server/linux
sudo chmod +x production-r2-migrate.sh
sudo ./production-r2-migrate.sh
```

The script verifies bucket permissions, checks Compose syntax and backup space, requires you to type `STORAGE_ONLY`, stops Gitea/invites, makes an independent full offline copy of Git+DB+LFS/configs, runs Gitea's **native** LFS migration, then starts the **same Tailscale Funnel hostname** with the R2 storage overlay. Original local LFS objects are not deleted. R2 is not a versioned backup; keep a separate backup.

**New private local files:** `.env.r2.production` and `data/r2-rclone.conf`. Both contain R2 keys (mode 0600), are excluded from Git, and MUST NOT be shared in GitHub ZIPs. The script does not enable Cloudflare hostname changes.

## 2. Validate R2 while everyone still uses Tailscale

```bash
cd /opt/swvault/server/linux
sudo ./swvault-admin.sh status
sudo ./verify-r2-direct.sh
sudo docker compose --env-file .env --env-file .env.r2.production \
  -f docker-compose.tailscale-legacy.yml -f docker-compose.r2-legacy.yml ps
```

Run the tests with a *normal team user* and a clone of a real vault: a 1 KB file, a file larger than 100 MB, a 500 MB+ file if you use these, lock / check-out / check-in, newest and **historical** revisions, multiple simultaneous large Get Latest downloads. Confirm `verify-r2-direct.sh` checks the LFS batch action points at `*.r2.cloudflarestorage.com`. Verify checksum and old users/roles/invites/SMTP. If the R2 link returns 403, check bucket credentials / signed URL expiry; the client can refresh the download link.

**Rollback at this stage:** During another maintenance window, and only if *no new R2-only objects were written*, restore backed-up `.env`, Gitea data and old legacy Compose service; switch off the R2 overlay. If users checked in after the migration, rolling back local storage alone will lose visibility of newer objects; first reconcile/copy them from R2. Never delete R2 objects while assessing a rollback.

## 3. Set up a Cloudflare Tunnel on this same VM

Cloudflare dashboard → Zero Trust/Networking → Tunnels → Create Cloudflared (remotely managed) → name `swvault` → copy the **Docker token** beginning `eyJ` (not Cloudflare account password). Add published hostname `vault.lynxmotorsports.racing`, service **HTTP** `http://localhost:8080`. `cloudflared` and Caddy in this repository share the network namespace: **do not** point the origin at a LAN IP. Leave Cloudflare Access off for Git/LFS clients unless the client later supports Access service tokens.

**Do not cut production over immediately.** Clone the VM and test `docker-compose.yml` + `docker-compose.tailscale-bridge.yml` with the original Gitea data and R2 *test bucket* first. The optional bridge reuses your Tailscale state so pre-existing agents can still reach the old Funnel URL. One Tailscale identity MUST NOT run in two containers simultaneously.

During a planned cutover: take another full backup, pause check-ins, ensure you have offline console access, save the working `.env`/`team.json`, then stop the old Compose project and start the Cloudflare project with the bridge. Keep the entire `data/` directory, the `.env` secrets for Gitea, and the private R2 settings. The Cloudflare `docker-compose.yml` requires R2 keys in **`.env`**, not only `.env.r2.production`. The easiest safe approach is to stage a fresh copy of `.env` and explicitly append these four keys from your verified overlay **without printing them**. Also set:

```dotenv
CF_DEPLOYMENT=cloudflare
CF_DOMAIN=lynxmotorsports.racing
VAULT_HOSTNAME=vault.lynxmotorsports.racing
# Set actual previous repository URL (these are examples, not secrets):
PREVIOUS_VAULT_URL=https://OLD-TAILSCALE-HOST.ts.net/ORG/REPO.git
LFS_UPLOAD_ORIGIN=https://OLD-TAILSCALE-HOST.ts.net
```

Populate `data/cloudflare/tunnel-token` with the remotely managed Tunnel token, group-readable to container UID/GID 65532 (`root:65532`, mode 0640). Preserve `data/tailscale/` and `tailscale-serve.json`. From `server/linux`:

```bash
# Review first: should list cloudflared, gitea, invites, proxy, tailscale-bridge.
sudo docker compose -f docker-compose.yml -f docker-compose.tailscale-bridge.yml config --services
# With all users offline and old containers stopped:
sudo docker compose -f docker-compose.tailscale-legacy.yml \
  -f docker-compose.r2-legacy.yml --env-file .env --env-file .env.r2.production down
sudo docker compose -f docker-compose.yml -f docker-compose.tailscale-bridge.yml up -d
sudo ./swvault-admin.sh client-config
sudo ./verify-r2-direct.sh
```

**STOP before distributing installers** if either the old `*.ts.net` Funnel OR new `vault.lynxmotorsports.racing` cannot authenticate and serve the *same* Gitea repository. This dual route is an opt-in pilot requiring validation; the underlying container namespace arrangement is not yet tested on your live hypervisor. If a test fails, stop the new stack, restore original settings and restart the legacy stack with `.env.r2.production`. Use the console and never assume an automated rollback succeeded.

**Why the bridge:** New Windows clients use the Cloudflare URL for auth, Git pointers, and direct R2 GETs; the new `lfsUploadOrigin` field sends *only* LFS upload bodies through the old Funnel, bypassing the Cloudflare 100 MB request limit. Data still reaches Gitea/VM during uploads. This is NOT direct-to-R2 multipart.

## 4. Migrate Windows clients via the old Funnel's updater

Do this only after step 3 passes on a pilot VM/client. See [UPDATE-WINDOWS-CLIENTS.md](UPDATE-WINDOWS-CLIENTS.md). In short:

1. Ensure `team.json` built for the new installer includes `vaultUrl` = `https://vault.lynxmotorsports.racing/ORG/REPO.git`, `previousVaultUrl` = **exact old URL**, and `lfsUploadOrigin` = old Funnel HTTPS origin.
2. Build a **higher** version (e.g. `1.3.0`, or greater than installed assembly version) on a Windows machine with .NET 10/SOLIDWORKS interop. Test installation + automatic URL migration on one backup Windows profile first.
3. Copy the published ZIP to the VM and run `sudo ./swvault-admin.sh publish-installer <path-to-client.zip> --required --notes "Cloudflare URL migration"`; the new and OLD Funnel routes both serve the same invite/updater state. Users will see the update and choose Install Update. They must close SOLIDWORKS to complete installation.
4. Separately distribute the Setup EXE (from Inno Setup 6) as a fallback, and track who completed the update.
5. Leave the old Funnel running until every client has updated and confirmed both large uploads and direct R2 downloads. **Required** only means repeat reminders, not silent forced installs.

The new Agent only migrates one matching old registration after authenticating its existing saved Gitea token against the new URL. It **preserves `Id`, `LocalRoot`, mirror and state DB**, backs up `vaults.json`, and keeps the old credential. If validation fails, old registration is left unchanged. It does not migrate a different repository, multiple matching registrations or a user who has no saved token. Users with unusual profiles may need manual help.

## 5. Independent backups, checks, and recovery

```bash
cd /opt/swvault/server/linux
sudo ./config-inventory.sh                 # values redacted
sudo ./export-private-config.sh /mnt/swvault-backup
sudo ../backup/backup-gitea.sh "$PWD" /mnt/swvault-backup
```

Protect and encrypt the private config archive; it contains sensitive server credentials. Restoring requires the matching Gitea SQLite DB+repos, invites, `data/tailscale/`, Gitea secrets/keys, R2 data, `.env` and Cloudflare token. **Never copy .env or data into your public GitHub repository.** Test restoration to a clone and verify several historical LFS OIDs with SHA-256, not just the latest files.

## When can Tailscale be removed?

Only when the new client/server support authenticated direct-to-R2 uploads and Gitea LFS registration (plus failure tests) OR you provide another direct HTTPS origin not proxied by Cloudflare. The latter can be a DNS-only hostname with valid TLS, firewall restrictions and public IP/port forwarding; it will still use VM bandwidth. See the direct multipart design document for the future zero-relay implementation.
