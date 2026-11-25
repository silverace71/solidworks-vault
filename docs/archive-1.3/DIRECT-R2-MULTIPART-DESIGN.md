# Direct-R2 multipart check-in: design and acceptance criteria (NOT YET IMPLEMENTED)

This document is an engineering specification, **not a shipped production feature**. Today SWVAULT uses Gitea 1.27.3's Git LFS basic transfer adapter. Gitea signs direct R2 **GET** URLs, but for **PUT** it accepts the upload via its own Git LFS endpoint and stores the bytes in R2. The Cloudflare Tunnel 100 MB HTTP request limit therefore still applies to a Gitea upload over `vault.lynxmotorsports.racing`. There is no safe environment variable to make Gitea issue multipart signed upload URLs *and* create its own LFS object metadata automatically.

## Future architecture

1. Authorize object OID/size, user, repo, locks and policy through Gitea. User/password or token never travels to R2. Restrict operations to a named repo and an opaque staged object key.
2. Server creates a multipart upload in private R2 and signs short-lived `UploadPart` requests. Never hand R2 permanent credentials to clients. The R2 S3 endpoint (`https://ACCOUNT.r2.cloudflarestorage.com`) is called directly by Windows, **not** through Tunnel, Worker or a custom proxied domain.
3. Windows uses parallel 16–64 MiB parts, retries failures with exponential backoff, saves a resumable upload journal, limits memory pressure, validates a stable source file and produces a full-file SHA-256 matching the Git LFS OID.
4. Server validates the completed R2 object: authenticated user/repo session, exact length and SHA-256 **from a trusted verification flow**, not a client-supplied unverifiable header or multipart ETag. The server may need to stream-read the R2 object once, or use a verified checksum facility supported by R2. ETags are **not SHA-256**.
5. Once validated, make the object visible to Gitea using a **supported, tested registration implementation** (a tested Gitea fork/API or a replacement LFS service with equivalent semantics). Never write unsupported SQL to Gitea's metadata database while production Gitea is running.
6. Only then finalize Git pointer commit; provide idempotent retry, GC of abandoned sessions, key isolation, quota controls, audit log, and rollback if registration fails. Reuse existing Gitea permissions and lock discipline.
7. Run tests for 1 B, 100 MB, 500 MB, 4 GiB files; both incomplete/crashed multipart and resumed uploads; wrong OID, wrong size, unauthorized repo, replay, expired presigned part URL; SHA mismatches, network loss; existing LFS clients and browser history; and 100+ GB concurrent new user downloads.

**R2 behavior**: multipart uploads support large files (up to R2's service limits), a maximum of 10,000 parts, minimum 5 MiB per nonfinal part, and parallel/resumable part transfer. Standard presigned S3 single-GET/PUT URL examples don't by themselves provide this complete integration. References: https://developers.cloudflare.com/r2/objects/upload-objects/ and https://developers.cloudflare.com/r2/api/s3/presigned-urls/.

## Interim production deployment in this repository

Keep R2 as the **storage backend** and the **direct download endpoint**. Route large uploads to the same Gitea over a tested non-proxied HTTPS origin (`lfsUploadOrigin` in team.json, for example original working Tailscale Funnel). This bypasses Cloudflare Free's 100 MB **proxy** limit, but **does not** bypass the VM for uploads or provide multipart to R2. This distinction matters for bandwidth and cost planning.
