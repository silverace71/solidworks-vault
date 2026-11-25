# SWVAULT 1.4 installer — choices and recovery

This note covers the **new VM** one-line installer from the patch; it is not for running on the old 1.2.9 production VM.

## Scheduled backup choice

At **Enable nightly scheduled backups?**, choose `yes` or `no` (no blank answers):

| Choice | Setup behavior | After installation |
|---|---|---|
| `no` | No backup directory is requested. Existing backup files are retained. | `swvault-backup.timer` is disabled; Gitea/R2 still operate normally. |
| `yes` | A backup directory is requested. If missing, the wizard offers to create the folder **after DEPLOY**. | Nightly backup service and timer are enabled. |

The backup folder must be on an independent disk that is **already mounted**. For example, mount `/mnt/backups` first and enter `/mnt/backups/swvault`. The script can create `swvault`, but **cannot make an unmounted path into a separate disk**.

## Import behavior with backups off

- **Fresh vault (`0`)** — no import staging path required.
- **CAD folder (`3`/`4`)** — select a writable staging path; the installer can create it. The folder is copied for later **Windows Import Folder**, not inserted directly into Gitea.
- **Full 1.2.9 export (`1`/`2`)** — select an import staging path on an independent mounted disk with room for the archive, extraction, and one-time pre-import rollback snapshot. This safety step remains necessary even when nightly backups are `no`. It does not enable ongoing backups.

## Interrupted setup — what to select

Rerun the same installer one-liner. If prior setup exists, the menu appears:

- **resume** — continue from saved plan/credentials and pinned source commit. Use when a command failed, network dropped, or setup was interrupted after `DEPLOY`.
- **restart** — ask all setup questions again with existing non-secret values as defaults; replace configuration only after a new review and confirmation. It **never deletes** Gitea DB, files, LFS objects, import completion markers or R2 bucket data. Do **not** use it expecting a totally empty new vault.
- **quit** — exit without changes.

If you need a truly blank vault, provision a *new* VM or perform a separately reviewed data reset; that is not what `restart` does.

Review any saved root-only `.env` checkpoints in `/var/lib/swvault-installer/config-checkpoints/` if reconfiguration fails. Never upload these secret-bearing files to GitHub. The installer source is downloaded from your GitHub repository: inspect the proposed commit before running it as root.

## Smoke test before deployment

```bash
curl -fsSLo /tmp/swvault-install.sh https://raw.githubusercontent.com/silverace71/solidworks-vault/main/install.sh
sudo bash /tmp/swvault-install.sh --dry-run
```

No backup/import folders are created by `--dry-run`, and no Docker services are changed. After you merge the patch into the repository, you can deploy with `sudo bash /tmp/swvault-install.sh`.
