# SWVAULT configuration and settings transfer map

**Purpose:** Move/redeploy settings without accidentally pushing credentials to GitHub or destroying existing vault identity. Do not confuse source deployment, state backup, R2 object storage and Windows profile data. These are separate parts of the system.

## Server files

| File/directory | Contents | Must transfer for restore? | Can publish to GitHub? |
|---|---|---|---|
| `server/linux/.env` | Domain, Gitea secret keys, Cloudflare and R2 credentials, SMTP, user/ORG/REPO and local-root settings | **Yes**, securely | **No** |
| `server/linux/.env.r2.production` | Stage-only legacy R2 credentials | During legacy storage-only stage | **No** |
| `server/linux/team.json` | Client team name, repository URL, optional old URL and upload route | **Yes** for Windows builds | Only if no invite code; generally keep private |
| `server/linux/data/gitea` | Gitea SQLite DB, Git repositories, local LFS legacy copies, identity | **Yes**, consistent snapshot | **No** |
| `server/linux/data/invites` | Invite codes, people, teams, settings, installer archives | **Yes** | **No** |
| `server/linux/data/admin-token` | Gitea admin service token | **Yes**, or recreate | **No** |
| `server/linux/data/admin-credentials.txt` | Generated admin password | If still needed | **No** |
| `server/linux/data/cloudflare/tunnel-token` | Remotely managed Tunnel connector token | **Yes**, or rotate in Cloudflare | **No** |
| `server/linux/data/tailscale` | Existing Funnel node identity/state | **Yes** to preserve old URL | **No** |
| `server/linux/data/r2-rclone.conf` | R2 backup credentials | **Yes**, or regenerate | **No** |
| `server/linux/data/invites/installer/` | ZIP/EXE offered by existing clients' updater | **Yes** for continuing old updates | **No** |
| `server/linux/docker-compose*.yml` and `Caddyfile.cloudflare` | Tracked deployment definitions | **Yes** (part of source) | **Yes** |
| `server/linux/tailscale-serve.json` | Funnel route template | **Yes** if using bridge | **Yes** (check for personalized values) |
| `/etc/systemd/system/swvault-*` | Backup/watchdog timers, possibly old server auto-update timers | Recreate after restore; inspect manually | No need |
| `/mnt/...` independent backup disk | Verified historical snapshot (Gitea + R2 contents + secrets) | **Yes**, independent of VM | **No** |

Read-only inventory (does not display secret values):

```bash
cd /opt/swvault/server/linux
sudo ./config-inventory.sh
```

Create a **private, unencrypted-internally** archive with mode 0600 to your independent backup disk:

```bash
sudo ./export-private-config.sh /mnt/independent-backup
```

The archive itself contains passwords and access tokens. Encrypt it before moving it or storing it offsite (for example using your organization-approved encrypted storage), and NEVER upload it to GitHub or a public issue. This config archive does **not** include Gitea repositories/LFS contents: back those up separately using a VM snapshot and the provided backup script. `rclone copy` from R2 to an independent backup disk is needed for remote object contents.

## Cross-VM restore process

1. Stop writers and take a verified offline VM/database snapshot. Securely copy the **source release** to the target VM first; leave the old VM powered down during final cutover, especially if reusing `data/tailscale` (a single Tailscale node identity must not be active on two machines).
2. Restore matching `data/gitea/` and `data/invites/` from the same consistent backup. Restore private-config archive under `/opt/swvault/server/linux` only after inspecting `tar -tzf ARCHIVE` for safe relative paths; run as root with appropriate restrictive permissions.
3. Recreate `.env`, `.env.r2.production` (legacy only), `data/r2-rclone.conf`, `data/cloudflare/tunnel-token`; keep Gitea's existing `SECRET_KEY`, `INTERNAL_TOKEN`, LFS and OAuth JWT secrets consistent with its database. Review absolute mount paths and backup paths for new VM.
4. Make sure the R2 bucket still contains its LFS objects. **DO NOT empty it** to redeploy services. Do not run fresh setup on restored production data.
5. Ensure existing Tailscale VM is off, then use the correct Compose stack: `docker-compose.tailscale-legacy.yml` + R2 legacy override for stage 1; `docker-compose.yml` + optional Tailscale bridge for Cloudflare stage. Run `docker compose config -q` and `verify-r2-direct.sh` first. Verify old and new endpoints, users and historical content.
6. Recreate backup/watchdog timers only once. Keep the source server updater disabled during staged Cloudflare migration; the Windows updater is managed separately via `publish-installer`.

## Windows local files

- `%LOCALAPPDATA%\SwVault\vaults.json` — vault identity (ID), remote URL and local root. The new Agent preserves the old ID while changing only the URL if `previousVaultUrl` matches and the new Gitea accepts the saved token.
- `%LOCALAPPDATA%\SwVault\vaults\<old-ID>\` — client Git mirror, state DB, local locks and other metadata. Do **not** delete/reinitialize it during URL migration.
- `%LOCALAPPDATA%\SwVault\logs\` — troubleshooting, not typically required to restore operations.
- Windows Credential Manager targets `SwVault:https://OLD-HOST` and `SwVault:https://NEW-HOST` — tokens are never stored in `vaults.json`. The migrator copies existing saved credentials only after verifying the destination server; don't attempt to edit Credentials Manager entries in JSON.
- The working CAD folder such as `C:\SWVault\FSAE` — preserve all checked-out/uncommitted files; backup separately and do not start a second vault in the same folder.
- Installed `bin\team.json` — bundled with each Windows package. Package after server URL is finalized and test against one backed-up workstation before publishing.

## GitHub version tracking / CI variables

Use the release ZIP of **source files**; in GitHub create a private repository, then import the unzipped `SwVault-main` folder (the ZIP itself is not a Git repository with history). Maintain branches, commits and releases. Review SOLIDWORKS interop redistributability/licensing before making the repository public.

For `.github/workflows/release.yml`, set GitHub Actions **repository variable** `TEAM_JSON` to the *safe* contents of `team.json`, with NO invite code. Optional variable `TEAM_ICON_PNG_B64` contains the custom logo. Do NOT put R2 keys, Cloudflare Tunnel token, Gitea admin token or `.env` in GitHub variables, Actions logs or repository files. The workflow uses a GitHub-provided token to create a release. `version.txt` now targets a 1.3 series; actual auto-generated patch depends on commit count. You can also build locally via `scripts/package.ps1 -Version 1.3.0 -TeamConfig team.json`.

**Do not enable the legacy server-side GitHub auto-update script during this production migration**; it previously could overwrite custom Cloudflare configuration. Publishing a reviewed *Windows client package* using `swvault-admin.sh publish-installer` is the supported rollout procedure. Once established, explicitly design a new reviewed server-release deployment process rather than silently pulling `main` into production.
