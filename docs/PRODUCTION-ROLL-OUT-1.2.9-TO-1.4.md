# SWVAULT 1.4 production rollout: original 1.2.9 clients -> Cloudflare/R2 VM

**Status: source patch tested locally, live Windows/Gitea/R2 end-to-end pilot required.** Do not arm cutover based only on CI. This guide targets your existing imported `cudenver/lynxcad` repository and `vault.lynxmotorsports.racing`. Check the actual configuration on your VM; don't substitute these example paths blindly.

## What this patch changes

- Windows 1.4 clients with `directR2Uploads=true` now use the private R2 multipart coordinator for **all** LFS uploads, not only LFS objects 8 MB and larger. The client imposes an approximate shared **5 megabits/sec (625,000 bytes/sec)** cap across parallel R2 part uploads **per running Windows Agent process**, not across the entire team.
- Browser download button: for authenticated **Git LFS-backed** content, it retrieves the Git LFS pointer (at most 4097 bytes), requests an authorized Gitea LFS batch action, validates the signed R2 hostname, then sends HTTP 302 to private R2. Browser does **not** stream the CAD body through your VM. Normal links to R2 may save under an object ID rather than the original CAD filename: test this before distributing broadly. Very small legacy Git blobs remain served from the VM; larger non-LFS blobs are blocked until converted to LFS.
- Caddy rejects external *native Git LFS PUT body* requests. The **server-side** Gitea registration by the coordinator uses internal loopback and remains available.
- An optional hourly R2-to-server local mirror copies canonical LFS objects at about **4.9 Mbps**, never deleting existing files. This mirrors file bytes, not Git metadata, users, or secrets. Do **not** treat same-disk cold storage as an independent backup.
- GitHub CI runs Windows build/tests plus Linux/Python unit tests on push and pull request. Existing nightly release packaging remains separate.
- Windows Agent continues retrying the 1.2.9 migration readiness marker until the approved cutover (the earlier ~16h45 limit is removed).
- Existing Gitea and R2 coordinator still **pull a new staged R2 object to the VM for SHA-256 verification and then upload a canonical copy to R2 through internal Gitea**. This is a known 1.4 limitation: clients do not send large file bodies to the VM, but Gitea finalization still consumes VM bandwidth. The local cold copy is also generated during that finalization, *not* solely by the background mirror. Fully removing these VM transfers requires a redesigned LFS metadata registration path and further validation. **No unattended production guarantee is implied.**

## Phase 0: safe baseline and rollback checkpoint

1. Confirm source code for this patch matches your deployed 1.4 before applying; don't run a fresh installer or import a second time.
2. Keep the original 1.2.9 VM and its complete export archive **unchanged and frozen**. Never allow check-ins against both machines after the snapshot.
3. On the 1.4 machine, copy the relevant scripts and app files to a root-owned checkpoint (before overwriting):

   ```bash
   sudo -i
   cd /opt/swvault/server/linux
   checkpoint="/root/swvault-pre-prod-$(date +%Y%m%d-%H%M%S)"
   mkdir -m 700 "$checkpoint"
   cp -a invites Caddyfile.cloudflare docker-compose.yml swvault-admin.sh "$checkpoint/"
   cp -p .env team.json "$checkpoint/"
   echo "$checkpoint"
   ```

4. Do **not** post `.env`, R2 API tokens, the old server export, or admin credentials in GitHub.
5. Ensure disk space for `data/cold-r2-mirror` if enabling the hourly job. It may eventually duplicate every canonical LFS object, in addition to the existing `data/cold-lfs` copies.

## Phase 1: apply production patch to the NEW VM (no reinstall or second import)

On your GitHub/Windows development machine, commit the source changes only after review; deploy only the *changed server-side files* to the **new** Linux computer using SCP/WinSCP or a private clone. Root access is expected because `/opt/swvault` is protected.

At minimum transfer these files, retaining existing `data/`, `.env` and `team.json`:

```
server/linux/invites/browser.py
server/linux/docker-compose.yml
server/linux/Caddyfile.cloudflare
server/linux/swvault-admin.sh
server/linux/cold-mirror-r2.sh
server/linux/enable-cold-mirror-timer.sh
server/linux/production-readiness.sh
```

Then on the new server:

```bash
sudo -i
cd /opt/swvault/server/linux
python3 -m py_compile invites/browser.py
chmod 755 swvault-admin.sh cold-mirror-r2.sh enable-cold-mirror-timer.sh production-readiness.sh
# docker-compose.yml adds R2_ACCOUNT_ID to 'invites' so it can validate the signed URL host
# Restarts the web service/proxy only. Does not restart Gitea or re-run the importer.
docker compose up -d --no-deps --force-recreate invites proxy
```

Note: if using `docker compose` with another deployed override, use the *same* compose options that your installed system uses. After deployment, inspect logs:

```bash
docker compose ps
docker compose logs --tail=60 invites proxy r2-transfer
curl -fsS http://127.0.0.1:8080/swvault-transfer/health
./production-readiness.sh
```

### Browser acceptance test

Log into `https://vault.lynxmotorsports.racing/swvault/` as a regular imported team member, click download on an actual LFS-backed CAD file and inspect browser Developer Tools → Network. Expect the authenticated request to `/swvault/api/download?...` to answer **302**, with its `Location` hostname equal to `YOUR_ACCOUNT_ID.r2.cloudflarestorage.com`. The browser should then receive the file directly from R2. Do **not** paste, share or log the full signed URL. Verify the file bytes and filename. Test historical version downloads. Non-LFS Git files are exceptions; test if any user-facing CAD revisions are not LFS-backed.

## Phase 2: optional slow local mirror and backup decision

Install `rclone` and verify the existing root-only R2 configuration, then choose whether you want **an extra same-machine mirror** of canonical LFS files:

```bash
sudo -i
cd /opt/swvault/server/linux
rclone --config data/r2-rclone.conf size r2:swvault-lfs/lfs
./cold-mirror-r2.sh       # optional first manual copy at <=~5 Mbps
./enable-cold-mirror-timer.sh   # optional, hourly
systemctl status swvault-cold-mirror.timer
```

To stop future hourly copies:

```bash
sudo systemctl disable --now swvault-cold-mirror.timer
```

`data/cold-r2-mirror` holds LFS **only**. To recover a whole vault you also require consistent Gitea DB/Git repositories, `data/invites/`, correct server identity/secrets and the update feed. Enable SWVAULT's metadata backup separately if possible. Because your server uses a single physical disk, keep another encrypted off-machine backup; mirroring to that same disk cannot protect against disk failure.

## Phase 3: prepare Windows build on WINDOWS SERVER

1. Install and update .NET 10 SDK, Git for Windows, and Inno Setup 6. The included wizard can attempt installing missing tools through `winget` if Windows Server has it.
2. Clone `https://github.com/silverace71/solidworks-vault` into a normal, non-system folder on your Windows build server. **Review security scanner flags; do not disable Defender or whitelist unverified code.**
3. Get the new VM's final `team.json` only (never `.env`):
   ```bash
   sudo cp /opt/swvault/server/linux/team.json /home/silver/swvault-team-1.4.json
   sudo chown silver:silver /home/silver/swvault-team-1.4.json
   ```
   Transfer it to your Windows Server. Verify `vaultUrl` is your new Cloudflare URL; `previousVaultUrl` is the **exact** original 1.2.9 `https://...ts.net/...git` repository URL; `localRoot` matches the existing users' workspaces; and `directR2Uploads` is `true`.
4. In PowerShell from your checked-out repo, use the new wizard, **without publication first**:

   ```powershell
   Set-Location C:\path\to\solidworks-vault
   .\scripts\windows-build-and-publish.ps1 -Version 1.4.1 -TeamConfig C:\path\to\swvault-team-1.4.json -InstallMissingTools
   ```

   This runs `dotnet restore`, `dotnet test`, `dotnet build`, and packages a self-contained Windows installer. It fails without publishing if any of these steps fails. Check `dist\*-Setup.exe` and record SHA-256.

5. Before distributing a build, investigate your previous Windows Defender warning and validate the source/installer. Ideally code-sign using a trusted certificate and distribute the exact SHA-256 you tested.

## Phase 4: pilot ONE Windows PC

1. Create a restore point or backup for its existing SWVAULT registry/profile and local CAD workspace. Save or check in all uncommitted CAD work; close SOLIDWORKS.
2. Install the new Setup EXE on that PC. Installing a 1.4 build does not arm the server-side cutover; while the readiness marker is missing it should keep the existing 1.2.9 registration.
3. Verify the new 1.4 Agent/update watcher starts and that files and vault identity remain intact.
4. Do a controlled test using a throwaway **500 MB+** new file on a test branch/vault before production cutover: check part PUTs target R2's `*.r2.cloudflarestorage.com` hostname and **no large request body targets `vault.lynxmotorsports.racing`**. A single PC's combined client upload should average about 5 Mbps. At that cap, a 500 MB upload takes at least ~13 minutes; this is intentional.
5. Verify the check-in actually commits (not merely finishes uploading staged R2 parts), produces the expected LFS OID, and a new Windows machine can check out the exact same bytes. Also test file locks and historical revisions.
6. Check server `docker compose logs --tail=100 r2-transfer` for errors. Observe that finalization still transfers file bytes between R2/VM/Gitea; it is **not eliminated** in 1.4.
7. If any part fails, **do not arm cutover**, distribute the known-working 1.2.9 build/manual rollback instead.

## Phase 5: distribute update to the rest of the team

Original 1.2.9 Agents already poll the old server about 1 minute after startup and every 4 hours. The `Required` flag **prompts repeatedly**; it does **not** silently force-install. Users need to accept, close SOLIDWORKS, and approve UAC, unless you deploy the EXE with your own managed Windows mechanism.

**Your original server was deliberately frozen during import.** First test whether its `https://<old>.ts.net/swvault-invites/installer/latest` feed still responds. If the old Gitea and invite services are stopped, publishing an installer through the old feed **will not work**. Do not simply restart old Gitea and allow check-ins; that would create two live diverging vaults.

- **If old update service is safely running/readable:** after one-client pilot, run the wizard with:

  ```powershell
  .\scripts\windows-build-and-publish.ps1 -Version 1.4.1 -TeamConfig C:\path\to\swvault-team-1.4.json -PublishToOld -Required
  ```

  The wizard asks you to type `PUBLISH` and then prompts for your old-vault administrator credential. It publishes the ZIP and Setup EXE to the **old** feed while embedding the **new** URL and old `previousVaultUrl`. Do not share these credentials.

- **If old update service is offline:** distribute the tested Setup EXE using your Windows Server's GPO/Intune/software-management solution, or give users the signed/hash-verified EXE manually. Auto-update prompts from 1.2.9 cannot work when their old update feed is unreachable. Deploying software centrally may be automated, but exact silent install options should be validated against the generated Inno Setup installer on a test Windows machine.

Document each user's new client version. Don't delete/move their working folders manually; the 1.4 client migration preserves the vault registration ID and workspace when its checks pass.

## Phase 6: arm one-time URL cutover

ONLY AFTER all clients can install 1.4 and the new VM passes your tests, run the explicitly gated cutover script on the **new** VM:

```bash
sudo -i
cd /opt/swvault/server/linux
./arm-129-cutover.sh
```

This announces migration readiness for the **exact** old/new URLs and lets updated Agents authenticate against the new Gitea. Clients retry while running (no longer stop after 201 attempts); restarting the Agent is still a useful recovery step. It preserves the existing vault ID and local workspace. It does not mean users' uncommitted local changes will magically be uploaded; protect those first.

Test sign-in, locks, check-in, check-out, search and historical file access from at least two users. Keep the old 1.2.9 archive and server available for rollback until the new vault operates reliably; **do not** allow writes on both sides.

## Phase 7: ongoing updates and GitHub Actions

1. Commit the changes to a reviewed branch in your public GitHub repo, then open a pull request against `main`.
2. `.github/workflows/ci.yml` builds Windows and tests the Linux/Python service on **every pull request and push**; add the CI jobs as required status checks in GitHub repository branch protection/rulesets.
3. `.github/workflows/release.yml` is the existing nightly release workflow. It can generate a GitHub release containing Windows ZIP/Setup EXE and server files. It is **not** a comprehensive live R2/SOLIDWORKS acceptance test. Check release signatures/checksums before team-wide distribution.
4. Repository **Actions variable** `TEAM_JSON` must contain the correct new-VM client config (`team.json`), without any R2 or tunnel secrets. Optional `TEAM_ICON_PNG_B64` sets your custom team icon.
5. For ongoing server updates, separately approve the new-vault server updater (`sudo ./swvault-admin.sh auto-update on silverace71/solidworks-vault`) **only after testing** how this deployment applies changes. An automatic GitHub release should not silently override root-owned production config or an imported vault.
6. New 1.4 Agents use the same update watcher as before: checks at startup + approximately every four hours; users accept the upgrade. CI passing does not prove live compatibility.
7. For live integration tests, use a dedicated staging vault and optionally a *self-hosted runner* with restricted staging credentials. Never put production R2 write credentials in a public repo or run untrusted PR code on a runner with production access.

## Final sign-off criteria

- GitHub Windows solution builds and tests pass, and 1.4 Agent/add-in starts on a clean supported Windows PC.
- A 500 MB+ file uploads **directly** in parts to R2 without Gitea-proxied client upload, then is registered, checked out and SHA-256 verified.
- R2 browser download returns 302 on LFS CAD file; actual file bytes come from R2; historical version works.
- All original users, permissions, Git history, locks and LFS inventories match the exported vault.
- Local cold copy exists for newly checked-in files and the optional rate-limited canonical R2 mirror works.
- The original server's update feed is available (or managed software distribution is ready), and updated Agents migrate preserved workspaces after readiness is armed.
- A **tested independent restore** exists for database, repositories, server configuration and all LFS objects.
- Only after these checks should the old VM be permanently retired.
