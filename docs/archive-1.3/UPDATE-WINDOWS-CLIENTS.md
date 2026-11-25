# Guide 3 — Upgrade Windows clients through the existing Tailscale/Funnel updater

**Goal:** New hostname without deleting anyone's workspace, `.git` mirror, local state DB, saved SOLIDWORKS files or open check-outs. Keep original Tailscale URL online during the entire rollout. This is not a forced background installation.

## Build/release preflight

- Storage-only R2 migration completed and verified; both Cloudflare and original Funnel endpoints reach the **same** Gitea instance. Large upload origin tested with >100 MB files.
- Admin test PC backed up `C:\Users\USER\AppData\Local\SwVault\` and `C:\SWVault\FSAE` (or real local root); no checked-out changes lost. A separate VM snapshot exists.
- The new `server/linux/team.json` generated with `PREVIOUS_VAULT_URL` and `LFS_UPLOAD_ORIGIN` includes these fields (replace placeholders):

```json
{
  "name": "FSAE",
  "vaultUrl": "https://vault.lynxmotorsports.racing/fsae/cad.git",
  "previousVaultUrl": "https://YOUR-OLD-TAILSCALE-HOST.ts.net/fsae/cad.git",
  "lfsUploadOrigin": "https://YOUR-OLD-TAILSCALE-HOST.ts.net",
  "localRoot": "C:\\SWVault\\FSAE",
  "admin": "YOUR-EXISTING-ADMIN",
  "downloadAllOnJoin": true
}
```

`previousVaultUrl` must exactly match the old registry URL (apart from optional `.git`/case normalization), including owner and repo. It authorizes an **opt-in** migration shipped in the team installer, not an arbitrary server-provided redirect.

## Step 1 — Produce Windows packages (on Windows)

Install .NET 10 SDK and Inno Setup 6, plus any prerequisites documented in README. Copy `server/linux/team.json` into repo root as **untracked** `team.json` (don't embed invite codes in publicly distributed packages). Use a SemVer version **greater** than what users have installed (for example `1.3.0` instead of `1.2.9`):

```powershell
pwsh -File .\scripts\package.ps1 -Version 1.3.0 -TeamConfig .\team.json -Notes 'New hostname; private R2 downloads' 
```

You should get `dist\SwVault-FSAE-1.3.0.zip` and, with Inno Setup 6, `dist\SwVault-FSAE-1.3.0-Setup.exe`. This archive is source code and **does not** contain prebuilt binaries. Build artifacts are created locally and excluded from Git.

## Step 2 — Pilot installation on one Windows PC

Before any teamwide publishing, copy/pilot the ZIP/EXE on a **test workstation** with a backed-up profile and representative existing checkouts. Close SOLIDWORKS and save work. Install manually and restart the Agent. Observe the event/log folder (`%LOCALAPPDATA%\SwVault\logs`). The migration code:

1. Reads `previousVaultUrl` and `vaultUrl` from the newly installed `team.json`.
2. Looks for **one** existing registration with the old URL; checks that the owner/repo path is unchanged.
3. Reads existing token from Windows Credential Manager and uses it to authenticate to the new Gitea `/api/v1/user` endpoint (HTTPS). On failure: no registration change.
4. Saves the credential against the new host (without deleting the old), saves `vaults.json.before-cloudflare-TIMESTAMP.bak`, changes ONLY `RemoteUrl`, retaining the previous `Id`, `LocalRoot`, vault mirror/state and open-checkout metadata.
5. Next sync reconfigures Git origin URL from the updated registration. Importantly, users should **not click Add Vault for the new URL**; the expected result is the *existing* vault displaying the new URL.

Use SOLIDWORKS: Check Out → modify and Check In a small file and then a >100 MB sample, Get Latest on another workstation, file lock and historical revision recovery. Confirm LFS download GET points to R2; upload PUT points to the tested Tailscale hostname. If the old credentials aren't available or the new hostname rejects them, stop rollout and troubleshoot; do NOT add a duplicate vault under the same workspace.

## Step 3 — Publish using the OLD Funnel updater endpoint

Old users check for updates on their old Tailscale server. With the **dual Funnel bridge** running, it maps to the **same invitations/installer service** as the new Cloudflare route. Copy the newly built ZIP/EXE to the SWVAULT VM:

```powershell
scp .\dist\SwVault-FSAE-1.3.0.zip admin@YOUR-VM:/tmp/
scp .\dist\SwVault-FSAE-1.3.0-Setup.exe admin@YOUR-VM:/tmp/
```

On the VM (replace filenames and path if necessary):

```bash
cd /opt/swvault/server/linux
sudo ./swvault-admin.sh publish-installer /tmp/SwVault-FSAE-1.3.0.zip \
  --setup /tmp/SwVault-FSAE-1.3.0-Setup.exe \
  --required --notes 'Cloudflare vault URL and direct R2 downloads'
```

`--required` is a repeated update notification. By default, the **old** Agent checks after about one minute and every **four hours**, and the user must choose *Install update*, approve elevation and close SOLIDWORKS. It is not a forced or unattended upgrade. Separately distribute the Setup EXE and ask each team member to confirm they migrated. Make a user-by-user completion list; check normal users, approvers and admins.

## Step 4 — Troubleshooting and rollback

- No update prompt: check old `https://YOUR-OLD-TAILSCALE-HOST.ts.net/swvault-invites` route, installer version (must be higher than installed), and `TeamInvites` access. The old URL must continue working throughout rollout.
- No URL migration: check that the new `team.json` is installed, `previousVaultUrl` matches exactly, old Windows Credential Manager token exists and `/api/v1/user` recognizes it on the new endpoint. The agent logs only status, not credentials.
- Check-in 413: `lfsUploadOrigin` missing, too-old Agent DLL, old Funnel unavailable or action URL rewritten incorrectly. Recheck both routes and test with no real production work first.
- Restore registration if necessary: close Agent and SOLIDWORKS, back up current profile, restore the timestamped `vaults.json.before-cloudflare-*.bak`, reinstall old team installer or old `team.json`, and restart. Do NOT delete workspace/state or force re-import.
- Preserve the old Tailscale route until every laptop has migrated, and preserve a fallback installer on a separate medium.
