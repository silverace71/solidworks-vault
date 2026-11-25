# SWVAULT 1.4.0 — release verification and deployment gates

## Implemented in source

- Private R2 S3 storage for Gitea LFS objects, signed direct R2 downloads, and refreshed download batches.
- Authenticated upload coordinator with per-user Gitea repository write checks, short-lived signed R2 UploadPart URLs, direct multi-part uploads in the Windows LFS client, verification of SHA-256/size on the VM, cold copy and native Gitea LFS registration.
- Hosted `cloudflared` instance in the same network namespace on the new VM; normal requests through Caddy.
- **ONE-TIME ONLY 1.2.9 -> 1.4** export/import and explicitly armed client URL migration. A fresh VM is not required for 1.4.x updates.
- 1.2.9 updater publishing switch, client registry backups, preservation of vault ID and local CAD folder, one-time idle restart after successful URL change.
- Separate R2 and local backups. Upload finalization implies VM->R2 canonical write once after VM cold copy; this is not zero-bandwidth on check-in.

## Automated checks performed when assembling the ZIP

- Bash syntax for Linux scripts, Python compilation, Python transfer-service mock tests.
- YAML parse, static reference checks, ZIP structure and absence of production credentials/backup data.
- GitHub workflow includes Windows .NET SDK 10 test/build steps and server Python checks.

## Not verified here — a real deployment MUST pass these tests

- Compile Windows .NET 10 projects, SOLIDWORKS COM add-in registration/install under real SOLIDWORKS.
- Real Gitea 1.27.3 + private R2 multipart/upload/finalize registration with 500 MiB+, parallel retries, corrupted data rejection and Git LFS signed GET.
- The final export from an actual 1.2.9 Gitea SQLite database, import to a different Linux VM, all user permissions/tokens/locks/history/invites.
- Old updater recognizes/signs/publishes 1.4 package and the Windows agent migrates an actual Credential Manager token/registry/Git mirror without lost local CAD edits.
- Restore independent snapshots and cold backups.

**DO NOT ARM** `arm-129-cutover.sh` or decommission old server unless every applicable pilot check has succeeded and users are frozen. An explicit admin gate is intentionally required.

### Important limitations

- File body upload is direct to R2, but the upload coordinator downloads it once and Gitea uploads its canonical LFS copy once. This is intentional to use supported Gitea code instead of forging metadata.
- R2 is not free beyond its monthly storage/operation allowances. Cold storage and independent backups require real disk space.
- Staged incomplete transfers may persist; configure R2 lifecycle cleanup and monitor storage.
- Old 1.2.9 agent **does not automatically install** an update by force; admins must track and manually update any remaining clients.
- The browser download API still streams through the VM.
