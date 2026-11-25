# SWVAULT 1.4 — Cloudflare Tunnel + private R2 + fresh Linux VM

**Primary deployment guide:** [Fresh 1.4 VM setup](docs/1.4-FRESH-VM-SETUP.md).

**One-time migration from original SWVAULT 1.2.9:** [1.2.9 -> 1.4](docs/1.2.9-TO-1.4-MIGRATION.md) and [Windows rollout](docs/1.4-WINDOWS-ROLLOUT.md). **Nobody needs 1.3.** New hardware/VM is required ONLY for this major storage/control-plane migration, not for ordinary updates from 1.4 onward.

For `lynxmotorsports.racing`, prepare in Cloudflare:

1. An active DNS zone for `lynxmotorsports.racing`.
2. A remotely managed tunnel (e.g. `swvault-14`) with a public app route `vault.lynxmotorsports.racing` -> `http://localhost:8080`; copy the connector token beginning `eyJ`, not the account password.
3. A **private** R2 Standard bucket (e.g. `swvault-lfs`). Create an R2 **bucket-scoped** Object Read & Write S3 API key/secret and note the R2 Account ID. Do not enable public bucket domains or share keys with Windows clients.
4. A separate independent disk for local cold objects and backups.

The fresh VM setup is interactive and collects all domain, Tunnel, R2 and vault values **before deployment**:

```bash
cd /opt/swvault/server/linux
sudo ./setup.sh
```

- New 1.4 Windows clients upload large LFS file bodies in parallel 32 MiB **direct R2 multipart PUTs**. Only auth, status, and short-lived signed URL control calls use the Cloudflare Tunnel.
- Downloads use Gitea-issued signed **direct R2 GET** URLs.
- VM verifies SHA-256, writes a local cold copy, and registers the object via Gitea's official LFS PUT, which uploads the canonical LFS object to R2 again **server-side**. This means VM bandwidth is reduced versus every workstation transferring through it, **not zero during check-in**. The Git commit is not allowed until that registration is done.
- Cloudflare's 100 MB proxied HTTP request limit does not apply to the direct R2 multipart part requests; ordinary small API operations still use Tunnel.
- The existing Python web browser's `/swvault/api/download` endpoint remains VM-streamed. Use the 1.4 desktop client's R2 download path for large restores.

**Release maturity:** this is a source release with unit/integration-boundary tests, not a proven production deployment. Gitea/R2 upload registration, the Windows add-in in SOLIDWORKS, snapshot import, and automatic credential migration must be verified on a separate pilot VM and workstation before the final freeze.

No Cloudflare/R2 secret is included in the GitHub source ZIP. See [release readiness](RELEASE-READINESS.md) and [configuration/recovery guide](docs/1.4-CONFIGURATION-AND-BACKUPS.md).
