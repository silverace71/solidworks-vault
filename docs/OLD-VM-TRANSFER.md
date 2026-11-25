# SWVAULT 1.2.9 old-VM export and transfer — separate instructions

The **new** VM installer works without touching the old one. Use this optional helper only if you want to move all users, history, Git LFS data, and locks from the original **1.2.9** production VM.

## Prepare first

1. Get everyone upgraded to the tested 1.4 Windows client while their **original 1.2.9 server** is still running. Users should save/check in all their work before final freeze.
2. Put both VMs on a guest-accessible fast Proxmox network. Verify they can SSH over a private IP. E.g. on the old VM `ip route get NEW_VM_FAST_IP`, then `ssh newuser@NEW_VM_FAST_IP`. **Do not assume Proxmox's migration network is automatically available to guests.**
3. On the NEW VM, install OpenSSH server and rsync, create an encrypted transfer destination owned by the SSH user:

```bash
sudo apt-get install -y openssh-server rsync
mkdir -p -m 700 ~/swvault-transfer
```

4. On the OLD VM, prepare root's SSH key if missing and authorize it on the new VM:

```bash
sudo -i
install -d -m 700 /root/.ssh
test -s /root/.ssh/id_ed25519 || ssh-keygen -t ed25519 -f /root/.ssh/id_ed25519 -N ''
ssh-copy-id newuser@NEW_VM_FAST_IP
ssh newuser@NEW_VM_FAST_IP 'test -w swvault-transfer && command -v rsync'
exit
```

5. Attach a separate backup disk to the old VM, mount it, and ensure it has enough space for the entire original LFS history. Example backup archive: `/mnt/swvault-old-backups/swvault-129-final.tar.gz`. Do not point it at the original VM's OS disk. The archive contains sensitive `.env` secrets and must never be published.

## One-line export helper (after you commit `tools/old-vm-export.sh` to GitHub)

```bash
curl -fsSLo /tmp/swvault-old-export.sh https://raw.githubusercontent.com/silverace71/solidworks-vault/main/tools/old-vm-export.sh && sudo bash /tmp/swvault-old-export.sh
```

The helper confirms the old directory, private archive path, remote SSH destination, remote writable directory, and free backup space **before** asking for `FREEZE_129`. After confirmation it stops Gitea and invites, creates a consistent `.tar.gz` + SHA-256 file, transfers them via encrypted SSH/`rsync`, and checks the remote file's hash. It intentionally **leaves the old vault stopped** on success; do not restart it for write traffic after new-VM cutover.

If the transfer itself fails, keep the old VM frozen and rerun `rsync` with the existing archive manually, or resume by copying both files to the new VM:

```bash
sudo rsync -a --partial --append-verify /mnt/swvault-old-backups/swvault-129-final.tar.gz{,.sha256} newuser@NEW_VM_FAST_IP:swvault-transfer/
```

On the new VM, locate the received archive: `~/swvault-transfer/swvault-129-final.tar.gz`. Run the new installer and select `1` (local full export) and provide the corresponding **absolute** Linux path, or run the existing import script after verifying archive and settings. Choose **the same original organization, repository, and admin username** when configuring the new server.

## Important timing

A final export freezes old writes. To avoid downtime before the new server is configured, you can perform a preliminary non-final VM snapshot/copy for storage planning, but **do not** treat it as the final database export while users continue changing files. The official final export is performed once the updated Windows clients and new server have passed pilot checks. The new-VM import backs up any test vault state before replacing it.

For Windows rollout, reference the repository's `docs/1.4-WINDOWS-ROLLOUT.md` and `docs/1.2.9-TO-1.4-MIGRATION.md`. The original 1.2.9 update channel prompts users to install the new client; it does not silently install it. Do not arm `arm-129-cutover.sh` until everyone who needs automatic URL migration has installed the new Agent.

### Preferred option if the 1.4 VM is already running

Run the new-VM one-liner with import choice `0` while the original is still live. Later freeze/export from the original, transfer the archive to `~/swvault-transfer`, and run the **new-VM import command** in the new-VM guide. You do not need to reinstall or clear the new VM; the import script safely snapshots its temporary/test state first.
