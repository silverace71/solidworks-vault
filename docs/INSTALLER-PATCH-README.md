# SWVAULT fresh-VM one-line installer patch

This is an **additive patch** intended for https://github.com/silverace71/solidworks-vault (main branch), based on the SWVAULT 1.4.0 source archive supplied earlier in this conversation. It is NOT a rebuilt Windows client or a deployed public GitHub commit.

## Merge first

Copy the files in this patch into your local GitHub repository, preserving paths, review the diff, commit and push to `main`.

```bash
# On your Git development PC, in a clone of solidworks-vault:
# Copy the contents of this patch over the repo root, then:
git diff --check
git add install.sh server/linux/setup.sh server/linux/import-129-fresh-vm.sh tools/old-vm-export.sh docs/
git commit -m "Add resumable fresh-VM installer and old VM export helper"
git push origin main
```

## One-line NEW VM install (only AFTER merging)

```bash
curl -fsSLo /tmp/swvault-install.sh https://raw.githubusercontent.com/silverace71/solidworks-vault/main/install.sh && sudo bash /tmp/swvault-install.sh
```

See `docs/ONE-LINE-NEW-VM-INSTALL.md` for prerequisites, Cloudflare, R2, SMTP, optional backups, automatic backup/import-folder creation, local-folder/remote-VM import, and recovery.

### New installer options

- **Nightly backups are optional.** Choose `no` to disable the backup timer and avoid needing a backup directory. Choosing `yes` allows the wizard to create a missing **folder** on an already-mounted independent backup disk. Existing backup archives are never deleted.
- **Resume or restart:** when prior state exists, choose `resume`, `restart`, or `quit`. Resume keeps the approved source commit and settings; restart re-asks the questionnaire without wiping the vault data, retaining source history and taking a private config checkpoint before rewriting `.env`.
- **Import staging is separate from scheduled backups.** Full historical 1.2.9 imports require independent one-time rollback space even when nightly backups are disabled; ordinary CAD folder staging can live on another directory with sufficient space.
- `--dry-run` never deploys, including when saved wizard state exists.

## One-line OLD VM export (optional; freeze only when ready)

```bash
curl -fsSLo /tmp/swvault-old-export.sh https://raw.githubusercontent.com/silverace71/solidworks-vault/main/tools/old-vm-export.sh && sudo bash /tmp/swvault-old-export.sh
```

See `docs/OLD-VM-TRANSFER.md`. This script intentionally leaves the original 1.2.9 server frozen after producing the final snapshot to prevent diverging vault history.

## Important limitations

- The patch has been shell-syntax-checked and its interactive **dry-run** and cancel path tested. Docker/R2 SMTP/VM migration require a live pilot.
- The installer refuses to replace a preexisting SWVAULT deployment not created by this wizard; run on a NEW VM.
- For full history migration the old archive is a **sensitive private backup**, not a source file to upload to GitHub.
- Large R2 multipart client check-ins are supported by the 1.4 code path but were not verified against your live Cloudflare account in this environment.
- Once packages and services start, operations cannot be strictly atomic. The installer preserves data for resuming and keeps the original server separate; it does not promise instant rollback of every package operation.
