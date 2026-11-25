# SWVAULT 1.4 — one-line fresh-VM deployment

**Scope:** NEW Ubuntu/Debian Linux VM only. Original SWVAULT 1.2.9 VM remains untouched until you deliberately run the optional old-VM export helper. These files extend the 1.4 source snapshot provided previously. Review the patch/diff before merging into your public GitHub repository.

## First: merge these files into GitHub

Upload the patch ZIP *with paths preserved* to `https://github.com/silverace71/solidworks-vault` or copy the included files into a local clone and commit them. The required files are:

- `install.sh` (new bootstrap installer, repository root)
- `server/linux/setup.sh` (updated to accept validated wizard inputs, use official Docker packages, and test SMTP)
- `server/linux/import-129-fresh-vm.sh` (updated to use the backup disk for archive extraction and recover new-VM state on failure)
- `tools/old-vm-export.sh` (optional, copy to the ORIGINAL 1.2.9 VM)
- the guides in `docs/`

**The one-liner below will return 404 until `install.sh` is actually committed to the repository's `main` branch.** If using a different default branch, change `main` in the URL. The live public GitHub repository was not accessible to the authoring environment for comparison, so review the patch against your current HEAD before merging; the installer refuses to run if expected 1.4 files are absent.

## One line to paste into a terminal on the NEW VM

```bash
curl -fsSLo /tmp/swvault-install.sh https://raw.githubusercontent.com/silverace71/solidworks-vault/main/install.sh && sudo bash /tmp/swvault-install.sh
```

This downloads a script over HTTPS, then runs it with root privileges. Review the file in GitHub first, preferably pin to a reviewed commit rather than always trusting `main`. The script also shows the exact cloned commit and requires its final eight hexadecimal characters before deploying.

Dry run (still prompts for all values, but makes no SWVAULT deployment changes):

```bash
curl -fsSLo /tmp/swvault-install.sh https://raw.githubusercontent.com/silverace71/solidworks-vault/main/install.sh && sudo bash /tmp/swvault-install.sh --dry-run
```

Use the **same one-liner to resume** after Ctrl+C, SSH disconnection, or a failed package download. Do not clear `/opt/swvault/server/linux/data` or `.env` to “try again.” Once deploying starts, changes are not transactionally atomic, but the wizard preserves state for retry and never modifies the original VM.

## Prepare these values BEFORE running

| Prompt | Where to find or prepare it |
|---|---|
| Team, ORG, REPO, admin username | Use the EXACT original 1.2.9 values if importing history. From original VM: `grep -E '^(TEAM_NAME|ORG|REPO|ADMIN_USER|LOCAL_ROOT)=' /opt/swvault/server/linux/.env` |
| Windows workspace | Keep the original root path if moving existing Windows clients. |
| New domain | `lynxmotorsports.racing`; hostname normally `vault.lynxmotorsports.racing` |
| Tunnel token | Cloudflare Dashboard → Networking → Tunnels → create a remotely managed tunnel `swvault` → select Docker and copy **only** the token starting `eyJ...`. |
| Tunnel published route | `vault.lynxmotorsports.racing` → HTTP `http://localhost:8080`. Do not point directly to Gitea or use the VM LAN IP when using the included shared network namespace. |
| R2 bucket name | Cloudflare → R2 → create private Standard bucket (e.g., `swvault-lfs`). Avoid public bucket access. |
| R2 account ID | From R2 Overview / S3 endpoint (`<ACCOUNT_ID>.r2.cloudflarestorage.com`). Standard bucket only for the supplied config. |
| R2 S3 credentials | R2 → API Tokens → Object Read & Write → limit to SWVAULT bucket. Copy Access Key ID and Secret Access Key while shown. |
| Backup destination | Mount an independent filesystem/disk, e.g. `/mnt/swvault-backups`. The wizard refuses `/` root drive for backups. |
| Email | SMTP server/port, sender email, SMTP login, app password and address for a test email; or explicitly choose `no` email. For Gmail, use a dedicated app password, not the normal Gmail login password. |
| Migration | Choose 0=no import; 1=local final 1.2.9 export; 2=remote final export over SSH; 3=local CAD folder; 4=remote CAD folder. |

Cloudflare links: https://developers.cloudflare.com/cloudflare-one/networks/connectors/cloudflare-tunnel/get-started/create-remote-tunnel/ and https://developers.cloudflare.com/r2/get-started/s3/ . Docker installation details: https://docs.docker.com/engine/install/ubuntu/ .

### Prepare the independent backup disk

Attach and mount your backup disk inside the VM and confirm that it is a **different filesystem** before setup. A sample check:

```bash
lsblk -f
df -h / /mnt/swvault-backups
```

If you do not have an independent backup disk yet, stop and attach one rather than using a directory on the same OS disk.

### Email configuration

Select `yes` at the SMTP prompt if the team will use member verification or notifications. For Gmail, a sending account generally requires two-step verification and an app password. Typical settings:

- SMTP host `smtp.gmail.com`
- Port `587` (STARTTLS) or `465` (implicit TLS)
- SMTP user and From address: your actual sending mailbox
- Password: dedicated SMTP app password (entered hidden). The wizard does not send it to GitHub.

The installer attempts a test message after Gitea/invites start and records the result. If email fails, check `sudo ./swvault-admin.sh test-email youraddress@example.com` on the new VM. You can explicitly choose `no` if email must be configured later; manual invitation links will still work.

## Migration choices

**Full original SWVAULT 1.2.9 export (`.tar.gz` and `.sha256`):** Preserves Gitea accounts, Git commits, history, locks, LFS objects and invitation data. Best option for moving the actual vault rather than just current CAD files. The wizard verifies archive SHA-256 and separately asks for `IMPORT_129` before replacing the NEW VM's test data. If you decline this step, it stages the archive without importing. This step is not safe while users continue making changes on the old VM; freeze it only at the final cutover window.

**Plain CAD folder:** `rsync` copies the folder to the independent staging filesystem, preserving the directory tree. **It does not populate the vault automatically.** Build/install the Windows client, then use the SWVAULT/SOLIDWORKS **Import Folder** workflow on a designated workstation. This creates proper Git LFS metadata and checks-in the files; copying into `data/gitea` would not. To use Windows files, share/mount the source folder into the VM read-only first (e.g. a CIFS mount), then give the wizard the mounted Linux path.

**Another VM:** choose option 2/4 and provide `user@private-LAN-IP` and the absolute remote archive/folder path. Install `openssh-server` and `rsync` on the remote machine, set up SSH authentication/host-key trust, and verify the private network route. If your Proxmox nodes have 100Gb networking, the guest VMs need their **own** interfaces/bridges to access that network; a Proxmox migration interface is not automatically visible inside a VM.

## After installation: acceptance tests

```bash
cd /opt/swvault/server/linux
sudo ./swvault-admin.sh status
sudo docker compose ps
sudo docker compose logs --tail=100 gitea cloudflared r2-transfer proxy invites
sudo ./verify-r2-direct.sh
```

`verify-r2-direct.sh` needs at least one LFS object. On a new empty vault, upload a test CAD file first. Verify **both** >100MB direct multipart uploads from the new 1.4 Windows client and signed direct-R2 downloads before inviting users. The upload coordinator has not been live-tested against your Cloudflare account; do not assume these tests passed simply because setup exited successfully.

After a successful full 1.2.9 import and Windows pilot, manually run:

```bash
sudo /opt/swvault/server/linux/arm-129-cutover.sh
```

This is never run by the installer. Keep the original 1.2.9 VM frozen for rollback until all users have migrated and backup restores have been tested.

## Recovery and security

- Ctrl+C before `DEPLOY` stops without changing any SWVAULT services.
- Interrupted or failed post-`DEPLOY` runs leave `.env` and data in place; rerun the same installer, choose `resume`. Package downloads, image updates and Gitea initialization are not fully transactional; check logs before promoting to production.
- Full-history import snapshots the NEW VM's pre-import state onto the independent backup filesystem and attempts to restore it on error. The original VM is not modified by the new-VM installer.
- Root-only secrets live in `/opt/swvault/server/linux/.env`, Cloudflare token mount and R2 configuration. Never commit these or unredacted export archives to public GitHub.
- R2 storage over the free allowance incurs charges even when direct R2 downloads have zero egress charges.
- Do **not** disable Defender or antivirus to install the Windows client; investigate any detection first.

## Recommended minimal-downtime schedule for a production 1.2.9 vault

The safest order is **not** to freeze 1.2.9 as soon as the 1.4 VM is created:

1. Run the one-liner on the new VM and choose import **`0`** (fresh/test). Finish Cloudflare, R2, SMTP, and a Windows 1.4 test-client upload/download pilot.
2. Build/test the 1.4 Windows installer. Publish it through the old vault's update feed, or distribute the EXE. Updated users keep using the old 1.2.9 URL until the approved cutover. The old updater prompts; it does not silently force installation.
3. At the **final maintenance window**, pause check-ins, run `tools/old-vm-export.sh` on the old VM, and allow it to send the final archive over the private Proxmox-connected network. It leaves the old vault stopped.
4. On the new VM, import the received archive manually (change `newuser` to the SSH transfer user's Linux login):

   ```bash
   sudo /opt/swvault/server/linux/import-129-fresh-vm.sh /home/newuser/swvault-transfer/swvault-129-final.tar.gz /mnt/swvault-backups
   ```

5. Run R2 signed-download verification and pilot a real 500MB+ check-in from one updated Windows client. Inspect Gitea history, file locks and email delivery.
6. Only after that, deliberately run `sudo /opt/swvault/server/linux/arm-129-cutover.sh`. Restart updated Windows Agents as appropriate and monitor them switching. Keep the old VM snapshot and full LFS copies for recovery.

The `0` choice doesn't prevent a later full-history import; it intentionally defers the destructive **new-VM test database replacement** to the final cutover window.
