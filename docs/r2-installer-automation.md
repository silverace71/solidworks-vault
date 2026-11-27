# SWVAULT installer delivery using Cloudflare R2

This changes **installer ZIP/EXE delivery**, not the Gitea LFS CAD storage backend. It does not replace any server files.

## What happens after setup

1. Push a real client change to `main` (or manually dispatch the release workflow). The existing version calculation and Windows tests/build run on GitHub Actions.
2. GitHub Actions uploads **both** installer files to the `swvault-installers` R2 bucket. It reads each back and byte-compares it with the original, checks the public URL's size and first 1024 bytes, and verifies the public JSON manifest. A failure blocks creation of the GitHub release.
3. GitHub publishes the release. The **existing server auto-update service**, when enabled, discovers that GitHub release and updates `/swvault-invites/installer/latest` and the installer stored locally on the server. It normally runs on the server's own configured schedule. This is a separate system from GitHub Actions.
4. A one-time Cloudflare Worker intercepts file downloads from `/swvault-invites/download`, finds the currently published version, and streams its ZIP or EXE from the R2 custom domain; old Windows updater URLs and invite codes continue to work. The original server remains the fallback if an asset is missing.

The Worker never stores R2 credentials. It needs the public custom domain for *installer objects only*; do not publish your CAD/LFS bucket.

## Before starting

- Verify the `main` branch contains **your actual current 1.4.1+ source**, not an old backup. The original source archive used to prepare these changes predates your deployed R2 migration, so **do not overwrite your existing `server/` folder**. The ZIP includes only GitHub automation and the Worker.
- Check `version.txt` and existing `v*` GitHub tags. The source snapshot has `version.txt` set to `1.2`, while your running Windows client is `1.4.1`. Before triggering a new client build, bump `version.txt` to `1.5` (or another appropriate higher MAJOR.MINOR) if it is still `1.2`. The workflow now rejects versions below the latest tagged GitHub release, but **cannot know** a version you published only manually to your server.
- Create R2 bucket `swvault-installers`. In Cloudflare R2 > bucket > Settings > Custom Domains, add `downloads.lynxmotorsports.racing`, and wait until it is active. This bucket must allow public reads through that domain. Upload no passwords, tokens, or private documents to it. Keep `swvault-lfs` private.
- Confirm that you own/administer the `lynxmotorsports.racing` zone and can edit Cloudflare Workers routes.

## One-time GitHub Actions settings

In the **same GitHub repository**: Settings > Secrets and variables > Actions.

Add these **Secrets** (use a NEW, bucket-scoped Cloudflare R2 API token with Object Read & Write for `swvault-installers` only):

| Name | Value |
| --- | --- |
| `R2_ACCOUNT_ID` | Cloudflare R2 account ID (not the Gitea R2 bucket key) |
| `R2_ACCESS_KEY_ID` | Access key ID of the R2 bucket-scoped token |
| `R2_SECRET_ACCESS_KEY` | Secret access key of that R2 token |

Add these **Variables**:

| Name | Value |
| --- | --- |
| `R2_PUBLIC_BASE` | `https://downloads.lynxmotorsports.racing` |
| `R2_BUCKET` | `swvault-installers` (optional; this is the default) |
| `TEAM_JSON` | Your current team config JSON; confirm `vaultUrl` is correct |
| `TEAM_ICON_PNG_B64` | Optional team tray icon base64, if already used |

**Do not put access keys in repository files, `.github/workflows/release.yml`, or GitHub Desktop commits.** GitHub secret values are hidden from the build logs. Credentials for this installer bucket should be separate from credentials used by Gitea for CAD storage.

## One-time Worker deployment

Use `.github/cloudflare/swvault-installer-r2-worker.js` (also supplied as a separate `.js` download). In Cloudflare dashboard:

1. Workers & Pages > Create Worker > `swvault-installer-r2`.
2. Edit code: paste this `.js`, deploy.
3. Settings > Domains & Routes > Add **Route** (not Custom Domain): `vault.lynxmotorsports.racing/swvault-invites/download*`, in the `lynxmotorsports.racing` zone. Ensure the vault hostname is proxied through Cloudflare.
4. The `/swvault-invites/installer/latest` endpoint stays on your Linux server. No route needed for it.

Only enable the route **after 1.4.1 files are uploaded and publicly downloadable**. You do not need to redeploy the Worker for future versions because it reads the published version dynamically.

If you already copied the earlier Worker, replace it with this newer one. This version accepts old manually uploaded releases that lack `manifest.json`; later GitHub releases have a SHA-256 manifest and are checked against the server's release metadata.

## Switch from manual 1.4.1 to automatic releases

For **your current 1.4.1**, finish manually uploading BOTH of these exact files to R2:

- `releases/1.4.1/SwVault-LYNXFSAE-1.4.1.zip`
- `releases/1.4.1/SwVault-LYNXFSAE-1.4.1-Setup.exe`

Compare the ZIP's local SHA-256 with `https://vault.lynxmotorsports.racing/swvault-invites/installer/latest` before enabling the Worker. Use Windows PowerShell:

```powershell
(Get-FileHash 'C:\SWVAULT-Build\solidworks-vault\dist\SwVault-LYNXFSAE-1.4.1.zip' -Algorithm SHA256).Hash.ToLowerInvariant()
(Invoke-RestMethod 'https://vault.lynxmotorsports.racing/swvault-invites/installer/latest').sha256
```

Both must match; otherwise, publish the correct version locally on Linux first. Do **not** re-run `scripts/package.ps1 -Publish` over your slow Cloudflare upload connection; use the existing local `swvault-admin.sh publish-installer` route if needed. The new GitHub action is for future releases.

Now enable the existing *server-side* GitHub updater, which your server's `swvault-admin.sh status` previously showed as OFF. From the physical server or SSH:

```bash
cd /opt/swvault/server/linux && sudo ./swvault-admin.sh auto-update on YOUR_GITHUB_OWNER/YOUR_REPO
```

It will request a GitHub fine-grained PAT on the terminal. Follow the scopes in your installed server's docs. This step is required for the server to advertise new versions to old clients. GitHub Actions uploads installers immediately on release; the server advertises the new version only **after its auto-update job polls GitHub**. To synchronize sooner after a successful GitHub release:

```bash
cd /opt/swvault/server/linux && sudo ./swvault-admin.sh auto-update now
```

Do not run `auto-update now` until you have confirmed the server's current code, GitHub repository and branches match the deployed release: the updater can install **server-side files** from the release as well as client installers.

## Test commands (one line each)

**1. Check the latest version metadata:**

```powershell
Invoke-RestMethod 'https://vault.lynxmotorsports.racing/swvault-invites/installer/latest' | Format-List
```

**2. Download headers should include `X-SwVault-Installer-Source: r2`:**

```powershell
curl.exe -sSI 'https://vault.lynxmotorsports.racing/swvault-invites/download?format=exe'
```

**3. Test resuming the update ZIP (should respond `206 Partial Content`):**

```powershell
curl.exe -sS -D - -o NUL -H 'Range: bytes=0-1023' 'https://vault.lynxmotorsports.racing/swvault-invites/download?format=zip'
```

**4. Download and check the full ZIP against the advertised SHA-256:**

```powershell
curl.exe -fL -o "$env:TEMP\swvault-r2-test.zip" 'https://vault.lynxmotorsports.racing/swvault-invites/download?format=zip'; (Get-FileHash "$env:TEMP\swvault-r2-test.zip" -Algorithm SHA256).Hash.ToLowerInvariant()
```

**5. Check the newest release artifacts in the R2 bucket:** under `releases/<version>/` verify the ZIP, EXE, and `manifest.json`. `manifest.json` is created for new GitHub releases, not the manual 1.4.1 upload.

**6. Test a real existing 1.2.9 user:** tray icon > Check for updates > install. Verify the version and the server URL still match; the new ZIP should come from R2.

## Failure modes

- GitHub Actions fails in `upload-r2`: the ZIP/EXE are not published as a GitHub release. Check the Actions logs for the failing upload, content verification, or public hostname check. Usually credentials or public custom-domain setup.
- `X-SwVault-Installer-Source: origin-fallback`: either the R2 asset is missing, the domain is not serving it, or the new manifest does not match the server's published hash/size. Check paths and metadata before disabling fallback.
- The Worker downloads fast but `/installer/latest` is old: the **Linux server's automatic update service is still off or hasn't synchronized**. R2 uploads do not automatically change the server's version metadata.
- Update ZIP hash mismatch: do not force installation. Confirm the file bytes and release metadata belong to the same build.
- Browser shows older version: the Linux invite service has not published a newer installer yet; Worker intentionally uses the published server version for safety.
- Push changes only to `.github/` or `docs/`: the original release workflow considers these documentation/automation-only, so no new Windows client is built. This avoids releasing a new client merely because the delivery pipeline was configured.

## Deployment caveat

These files were developed from the **supplied SwVault-main.zip source snapshot**, which is older than your running server's `r2_transfer` / Cloudflare Tunnel deployment. Compare changes in GitHub Desktop before committing; do not replace newer workflows or server files blindly. No live R2 credentials were available, so end-to-end verification against your Cloudflare account must be done by your first GitHub Actions run and test downloads.
