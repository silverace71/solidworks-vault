# Guide 2 — Build a new SWVAULT vault backed by private R2, then import existing CAD files

For a **fresh** SWVAULT server and repository, with `lynxmotorsports.racing`. This does not overwrite an existing vault or migrate its locks/metadata. If you want to preserve an existing repository's history, permissions and checkouts, use `MIGRATE-EXISTING-TO-R2.md`, not this guide.

## 1. Prepare Cloudflare and a backup disk

1. Confirm `lynxmotorsports.racing` is an **Active** site in Cloudflare's dashboard.
2. R2 → Buckets → create private **Standard** bucket `swvault-lfs` (or your chosen unique bucket). Do not enable `r2.dev` or public access.
3. R2 → Overview → record **Account ID** (S3 endpoint `https://ACCOUNT.r2.cloudflarestorage.com`). R2 → Manage R2 API Tokens → create **Object Read & Write** credentials scoped to *that* bucket. Copy **Access Key ID** and **Secret Access Key** into your password manager. Secret is displayed once.
4. Tunnels → Create → Cloudflared → remotely managed tunnel `swvault` → choose Docker → copy **token beginning `eyJ`**. Set published application route: `vault.lynxmotorsports.racing` → **HTTP** `http://localhost:8080`.
5. Provide a separate backup disk that can retain R2 object copies + Gitea DB/repo data; storage and operation charges still apply to R2 beyond free allowances.

You do **not** need a Cloudflare Global API Key, Google/Gmail password, Workers or CDN Cache Everything rule. Keep those credentials out of Git and chat.

## 2. Install SWVAULT on a fresh VM

Copy the release ZIP to the VM, unzip and run the interactive installer. It asks for settings and secrets before deploying.

```bash
cd /tmp
unzip SWVAULT-GitHub-Ready-R2-Release.zip
sudo mkdir -p /opt/swvault
sudo cp -a SwVault-main/. /opt/swvault/
cd /opt/swvault/server/linux
sudo ./setup.sh
sudo ./swvault-admin.sh status
sudo docker compose logs --tail=80 cloudflared proxy gitea
```

Defaults: new hostname `vault.lynxmotorsports.racing`, existing private R2 bucket, Gitea on the VM, container-local `cloudflared` + Caddy. Choose stable team name, `ORG`, `REPO`, admin, `LOCAL_ROOT` (for example `C:\SWVault\FSAE`), email domain policy and independent backup path. Store admin credentials from `data/admin-credentials.txt` somewhere safe and change the admin password.

**The Cloudflare-only installer is suitable for initial connectivity and small uploads, but not for production CAD files >100 MB.** It warns and requires acknowledgement. Before importing large files, first configure and verify a non-Cloudflare large-upload route in step 3. This is not fixed by enabling dictionary compression.

## 3. Provide a working large-file upload path before bulk import

Two alternatives:

**Recommended interim route: new Tailscale Funnel running beside Cloudflare on the VM.** Prepare a Tailscale auth key/valid Funnel entitlement and configure the existing `tailscale-serve.json` for the node's `TS_CERT_DOMAIN`. Create the Tailscale state on a **test VM** using the original Tailscale Compose deployment, or carefully provision it for the optional `tailscale-bridge` container. Preserve `data/tailscale/`. Add `TS_HOSTNAME` and (if needed for first enrollment) `TS_AUTHKEY` to root-only `.env`. Then:

```bash
cd /opt/swvault/server/linux
sudo docker compose -f docker-compose.yml -f docker-compose.tailscale-bridge.yml config --services
sudo docker compose -f docker-compose.yml -f docker-compose.tailscale-bridge.yml up -d
sudo docker compose -f docker-compose.yml -f docker-compose.tailscale-bridge.yml logs --tail=70 tailscale-bridge
```

Validate the `https://NODE.TAILNET.ts.net/api/healthz` endpoint and try uploading a **500 MB** file while Cloudflare's hostname remains configured for the app. Add this line to `.env` and generate fresh `team.json`:

```dotenv
LFS_UPLOAD_ORIGIN=https://NODE.TAILNET.ts.net
```

```bash
sudo ./swvault-admin.sh client-config
```

This origin MUST route to exactly the same private Gitea instance, and it should be tested on a cloned VM because dual-network-namespace configuration has not been live validated. If Tailscale Funnel cannot be used, configure your own **DNS-only direct HTTPS hostname** outside Cloudflare's proxy with valid TLS and secure network access instead. Cloudflare Tunnel itself does not allow Gitea HTTP uploads above 100 MB per request.

**Direct R2 multipart staging alone is not a valid Git LFS check-in.** The current release does not register such staged objects in Gitea. Do not import to R2 separately and assume those files appear in SWVAULT.

## 4. Build Windows installer from GitHub source

On a Windows machine with .NET 10 SDK and Inno Setup 6 (for an EXE), transfer `server/linux/team.json` securely to the repository root as `team.json` (this team config is not secret but can contain an invite code: don't commit it). The package embeds the new domain and optional upload origin.

```powershell
pwsh -File .\scripts\package.ps1 -Version 1.3.0 -TeamConfig .\team.json
```

Install the EXE on an admin **test PC first**, sign in, confirm Get Latest / Git locking, then distribute to team. `scripts/package.ps1` generates a ZIP even when Inno Setup is missing. Do not assume a ZIP of C# *source* is a ready Windows executable; this repository ZIP is for GitHub/version tracking and building.

## 5. Import an existing folder of CAD files

1. Place the CAD folder on the admin workstation outside OneDrive/Dropbox, with a **backup copy**. Identify reference/assembly relationships; verify SOLIDWORKS version compatibility and permissions.
2. Point SWVAULT's local root at the new vault workspace (e.g. `C:\SWVault\FSAE`) when creating it, and sign in as the designated admin FIRST to initialize its vault configuration. Coordinate import/checkout while other users remain idle.
3. Use the SWVAULT SOLIDWORKS tab → **Import Folder** (or repository import workflow described in `docs/user-guide.md`). Keep the workstation awake and imported source available until checksums and references are verified. For >100 MB individual CAD files, the updated Windows client must have the verified `lfsUploadOrigin` set and the Funnel/direct origin must be reachable. Existing imported files will be hashed and stored as LFS objects, not automatically deduplicated against unrelated stored versions.
4. Do NOT run a generic `rclone copy` into `r2:swvault-lfs/lfs/` to substitute for importing: Gitea needs the corresponding LFS metadata and Git pointer commits. A raw object upload is not a SWVAULT revision.
5. After import, check assembly references, file count, total size, historical revisions, lock/check-in/check-out, initial 100+ GB Get Latest on a second PC and SHA-256 integrity. Use a smaller sample first.

**If the CAD files are actually in the prior SWVAULT repository**, do not create a new blank repository. The correct procedure is to migrate the existing Gitea database and LFS store (Guide 1), preserving history and identities.

## 6. Backups and ongoing operations

```bash
cd /opt/swvault/server/linux
sudo ./verify-r2-direct.sh   # requires at least one LFS object first
sudo ./config-inventory.sh
sudo ./export-private-config.sh /mnt/independent-backup
sudo ../backup/backup-gitea.sh "$PWD" /mnt/independent-backup
```

Back up both the VM's Gitea DB/repositories and the **entire R2 LFS object store**. Verify restoration to a separate VM using a test bucket. Rotate scoped R2 credentials if exposed; don't put `.env`, `data/`, tunnel tokens, private config archives, or admin tokens into GitHub.
