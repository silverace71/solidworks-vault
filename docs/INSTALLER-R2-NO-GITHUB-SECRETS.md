# SWVAULT installer releases to R2 without GitHub R2 secrets

This patch was prepared against the `solidworks-vault-main(1).zip` repository provided on November 26, 2026. It includes your newer Cloudflare/R2 CAD server; **do not replace or roll back that server**. These changes affect only the public GitHub release workflow, the installer download Worker, and two new **installer-only** Linux scripts.

## What "no secrets" actually means

- **GitHub:** No user-defined `R2_*` secrets, no Cloudflare credentials and no GitHub personal access token are needed. The workflow uses GitHub's automatically provided `GITHUB_TOKEN` for releases. It requires repository workflow permission `contents: write` (declared in YAML). Your team configuration remains a repository **variable** (`TEAM_JSON`), not a secret. Treat that value as public; do not place credentials in it.
- **Linux server:** R2 writes still require authentication. The server already stores R2 credentials in `server/linux/data/r2-rclone.conf`, protected on disk. Reuse that file **only if its access token is allowed to write to the new installer bucket**. Prefer a separate R2 bucket-scoped token kept in a second root-only local rclone config. This is **not a GitHub secret** and must never be committed.
- **Windows users:** Need no R2 key. They download via the unchanged `/swvault-invites/download` route and existing SWVAULT updater. The Worker and R2 installer domain have public read-only access.
- **For other teams:** They supply their own `TEAM_JSON`, GitHub repository name, bucket, Worker configuration and server-local R2 credentials; nobody gets access to your private keys or bucket.

## Pipeline

1. A meaningful client change (or next release version change) reaches GitHub `main`.
2. GitHub builds/tests ZIP and EXE, validates their embedded version, publishes a SHA-256 `manifest.json` and a GitHub release. A failed build/manifest **blocks the release**.
3. Once per hour, the *installer-only* Linux timer queries the latest **public** release in the configured repo. It never downloads or deploys `swvault-server.tar.gz`.
4. If that release is newer than `/installer/latest`, the Linux script downloads the manifest, ZIP and EXE, validates names, sizes, SHA-256 and embedded ZIP version.
5. Linux uploads ZIP/EXE/manifest to its dedicated installer R2 bucket using a local rclone config; it reads all of them back and checks R2's public URLs and byte ranges.
6. **Only after all R2 checks pass** does Linux call its existing local `swvault-admin.sh publish-installer` API. Existing Windows clients then discover the update from `/swvault-invites/installer/latest` and download bytes via the Worker/R2 route. Failed steps keep the previously published version unchanged.

GitHub releases can briefly precede R2 publication by up to the hourly poll interval. This is intentional: it protects installed clients from half-uploaded releases.

## 1. Install repository patch with GitHub Desktop (Windows)

Extract this patch ZIP, copy the included folders/files into your **existing** `C:\\SWVAULT-Build\\solidworks-vault` repository root, and review changes in GitHub Desktop. **Do not replace the entire project**. Commit and push once reviewed. The patch does not modify CAD / Gitea / R2-transfer code.

The workflow still requires GitHub repository variable `TEAM_JSON`, containing your correct public team settings. `TEAM_ICON_PNG_B64` is optional. No R2 GitHub secrets are needed. The GitHub repo must be **public** to allow the server to download its release artifacts without a GitHub personal access token. If you must keep the repo private, use a securely stored GitHub token on the Linux server or the prior GitHub Actions R2-secret approach.

**Versioning:** Your uploaded repository has `version.txt = 1.4`, while the currently installed client is `1.4.1`. The workflow computes patch version from Git commit counts. Check the next calculated version and GitHub tags before publishing: it must be **higher** than the version installed on users' PCs (e.g. 1.4.2 or 1.5.0). Changes to only `.github/` or `docs/` are designed *not* to trigger a client release. Do not republish different bytes under the same version.

## 2. Prepare the dedicated installer bucket (Cloudflare dashboard)

- Keep your CAD bucket `swvault-lfs` **private**.
- Create **installer-only** bucket `swvault-installers` and connect public-read Custom Domain `downloads.lynxmotorsports.racing` (or each team's custom domain).
- Do not upload credentials, private drawings, or private configuration into the public installer bucket.
- For the current manually built 1.4.1, finish your existing direct Cloudflare dashboard upload of both ZIP and EXE under `releases/1.4.1/`. Those are separate from the future automated releases. Confirm the ZIP hash matches `/swvault-invites/installer/latest` before deploying the Worker.

## 3. One-time Cloudflare Worker deployment

Worker JS: `.github/cloudflare/swvault-installer-r2-worker.js`.

Add Worker route `vault.lynxmotorsports.racing/swvault-invites/download*` in the Cloudflare zone. Do not route `/swvault-invites/installer/latest`, which remains served by your Linux invite service. Only enable this route after current 1.4.1 files are public and verified.

Configure Worker Settings -> Variables:

| Name | Current LYNX example | Other teams |
| --- | --- | --- |
| `R2_PUBLIC_BASE` | `https://downloads.lynxmotorsports.racing` | Their own installer R2 Custom Domain |
| `TEAM_SLUG` | `LYNXFSAE` | Their safe team slug |

These variables are public non-secret values. No Worker API key is required to **read public installer files**. Do not bind or expose the private CAD bucket.

## 4. Install the Linux installer-only sync scripts

Once you have committed the files in the repository, **also copy these new scripts to your live server** under `/opt/swvault/server/linux/` (they do not deploy themselves):

- `server/linux/installer-r2-sync.sh`
- `server/linux/enable-installer-r2-sync.sh`

The Linux server already has `rclone`, `jq`, `curl` and the admin CLI from SWVAULT setup. Confirm via `command -v rclone jq curl`.

Create a private, **root-owned local config**, not a GitHub file:

```bash
sudo nano /opt/swvault/server/linux/data/installer-r2-sync.env
```

For this team:

```ini
GITHUB_RELEASE_REPO=YOUR_GITHUB_OWNER/YOUR_PUBLIC_REPO
INSTALLER_R2_BUCKET=swvault-installers
INSTALLER_R2_PUBLIC_BASE=https://downloads.lynxmotorsports.racing
INSTALLER_R2_RCLONE_CONFIG=/opt/swvault/server/linux/data/r2-rclone.conf
INSTALLER_REQUIRED=false
```

`INSTALLER_REQUIRED=false` is the safe initial default. After testing the update on an existing 1.2.9 client, you can change this to `true` to mark future releases as required (they still require user action to install).

Apply file permissions:

```bash
sudo chown root:root /opt/swvault/server/linux/data/installer-r2-sync.env && sudo chmod 600 /opt/swvault/server/linux/data/installer-r2-sync.env
```

**R2 access note:** Your existing `data/r2-rclone.conf` may only have permission to write to `swvault-lfs`. If the installer sync says it cannot access `swvault-installers`, create a new installer-only R2 API token scoped to that bucket and store it locally in `data/r2-installer-rclone.conf`. Set `INSTALLER_R2_RCLONE_CONFIG` to that local path. Example *format only*, not actual credentials (keep file mode 600):

```ini
[r2]
type = s3
provider = Cloudflare
access_key_id = YOUR_INSTALLER_BUCKET_ACCESS_KEY_ID
secret_access_key = YOUR_INSTALLER_BUCKET_SECRET_ACCESS_KEY
endpoint = https://YOUR_CLOUDFLARE_ACCOUNT_ID.r2.cloudflarestorage.com
region = auto
acl = private
```

These credentials **must never be copied into GitHub Desktop**. You need them only once on the Linux machine. Do not change existing Gitea LFS credentials while setting this up.

Run a dry run after the first *newer* GitHub release exists:

```bash
cd /opt/swvault/server/linux && sudo bash ./installer-r2-sync.sh --dry-run
```

This downloads and hashes the GitHub release, but does **not** modify R2 or the published version. If GitHub latest is not newer than SWVAULT's current version, it exits without transferring.

To synchronize immediately:

```bash
cd /opt/swvault/server/linux && sudo bash ./installer-r2-sync.sh
```

After a successful manual sync, enable hourly automatic checking:

```bash
cd /opt/swvault/server/linux && sudo bash ./enable-installer-r2-sync.sh
```

To check service logs or run it manually later:

```bash
sudo journalctl -u swvault-installer-r2-sync.service -n 100 --no-pager
```

```bash
sudo systemctl start swvault-installer-r2-sync.service
```

**Do not run** `sudo ./swvault-admin.sh auto-update on ...` on the Cloudflare deployment. The uploaded `swvault-admin.sh` intentionally disables legacy server-file auto-updates there. This patch uses a distinct `swvault-installer-r2-sync.timer` and never deploys server code.

## 5. Test downloads and automatic updates

From **Windows PowerShell**, once the Worker is active:

```powershell
curl.exe -sSI 'https://vault.lynxmotorsports.racing/swvault-invites/download?format=exe'
```

Expect `X-SwVault-Installer-Source: r2`. If `origin-fallback`, check R2 paths, release metadata and manifest first.

```powershell
curl.exe -sS -D - -o NUL -H 'Range: bytes=0-1023' 'https://vault.lynxmotorsports.racing/swvault-invites/download?format=zip'
```

Expect HTTP 206 and a matching `Content-Range`.

```powershell
(Invoke-RestMethod 'https://vault.lynxmotorsports.racing/swvault-invites/installer/latest') | Format-List
```

To compare the installer ZIP SHA-256, download via the same URL and check against `.sha256` in `/installer/latest`:

```powershell
curl.exe -fL -o "$env:TEMP\\swvault-r2.zip" 'https://vault.lynxmotorsports.racing/swvault-invites/download?format=zip'; (Get-FileHash "$env:TEMP\\swvault-r2.zip" -Algorithm SHA256).Hash.ToLowerInvariant()
```

Finally test **one** real 1.2.9 update from its tray menu. Confirm the new client still points at the correct vault URL.

## Failure safety and limitations

- GitHub Actions does **not** have R2 write access; creating a GitHub release doesn't mean R2 is finished until the server sync succeeds.
- If Linux cannot reach the public GitHub repo, has insufficient R2 permissions, or cannot verify every uploaded object, it stops before advertising the new version.
- The installer-sync service **does not update server code**, run migrations, manage the Cloudflare Tunnel, modify CAD files, or alter the existing LFS bucket.
- The Worker may fall back to the home server for file downloads when the R2 asset is missing/mismatched; test for the `X-SwVault-Installer-Source` header.
- If the installed Linux version differs from the uploaded source, confirm the exact `swvault-admin.sh publish-installer` interface before enabling the timer.
- Public releases are downloadable by anyone. Do not publish team-private secrets in installer packages, `TEAM_JSON`, EXEs or ZIPs.
