> **SWVAULT 1.4.0 R2 direct-transfer source release.** Large Windows check-ins use signed multipart uploads directly to private Cloudflare R2; check-outs/downloads use signed R2 GETs. The Linux VM runs Gitea, authentication, locking, version history, SHA-256 verification and cold-storage backups. **Use a fresh VM only for the one-time original 1.2.9 -> 1.4 migration**; routine 1.4.x updates do not require a fresh VM. Start with [Cloudflare setup](CLOUDFLARE-SETUP.md) and the guides linked below.

# SwVault

SwVault keeps the team's SOLIDWORKS files on our vault server: check-out/check-in so only one person edits a file at a time, full history, releases and reviews. This page is how to set it up on your PC.

## What you need

- A Windows PC with **SOLIDWORKS** installed, and permission to approve one Windows administrator prompt.
- An **invite message** from a team admin. Its link opens a page with a download button; your invite code is built in.
- An internet connection. No VPN and no other software is needed.

## Install

1. **Close SOLIDWORKS.**
2. Open the link in your invite and click **Download SwVault**. Don't rename the file: your invite code is in its name.
3. Open the downloaded **SwVault Setup** and follow the wizard.
   - If Windows shows "Windows protected your PC", click **More info** → **Run anyway**.
   - Approve the Windows administrator prompt.
   - If setup says SOLIDWORKS is still running in the background, let it end SOLIDWORKS for you.

## Sign in

A SwVault window opens when setup finishes.

- **New to the team's vault:** choose **I'm new and have an invite**. The invite code is usually filled in already.
  1. If it asks for your school email, enter it, click **Email me a code**, and type in the 6-digit code from the email.
  2. Enter your name, and choose **General member** or **Subteam lead** (and which subteam).
  3. Choose a user name and password.
- **Already have an account:** choose **I have an account** and sign in.

The team's files then download to the vault folder, `C:\SWVault\<Team>`. The SwVault icon in the system tray (near the clock) tells you when they are all there.

Closed the window by accident? Click the SwVault tray icon. (Once you are signed in, a click on it opens your team's files on the web; right-click for everything else.)

## Check it works

1. Open SOLIDWORKS. There is a **SwVault** tab in the CommandManager and a **SwVault** task pane on the right.
   - Not there? Go to **Tools → Add-Ins**, tick **SwVault** in both columns, and click OK.
2. In the task pane, browse the vault and double-click a file to open it.

## Using it

- **Always open and save team files from the vault folder** (`C:\SWVault\<Team>`). Don't move it, and don't put it in OneDrive, Dropbox or Google Drive.
- Files you haven't checked out are **read-only**.
- **Check Out** before you edit. This locks the file so nobody else changes it at the same time.
- **Check In** when you're done, with a short comment about what changed. This uploads a new version and releases the lock.
- **Get Latest** downloads what teammates checked in.
- **Add to Vault** puts a new part, assembly or drawing in the vault in one step.

The [user guide](docs/user-guide.md) covers everything else: history, reviews, subsystems, releases and what to do when something looks wrong.

## Updates

When an admin publishes a new version, you get a notification and an **Install update...** item on the tray icon. Click it and close SOLIDWORKS when asked.

## Problems

| You see | Do this |
|---|---|
| The invite link doesn't work or has expired | Ask an admin for a new invite. |
| No SwVault tab in SOLIDWORKS | **Tools → Add-Ins**, tick **SwVault**. If it isn't listed, run SwVault Setup again. |
| Can't save a file | It's read-only because it isn't checked out. Check it out first. |
| "checked out by ..." | Ask that person to check it in. |
| Everything shows offline | Check your internet connection and that the SwVault tray icon is running. |

For anything else, open a terminal, run `swvault doctor`, and send the output to an admin.

To remove SwVault, uninstall it from Windows **Settings → Apps → Installed apps**. Your vault folder is kept.

## Admins and developers

- Setting up the server, building and publishing the installer, inviting people and pushing updates: [self-hosting guide](docs/self-hosting-linux.md) and [admin guide](docs/admin-guide.md).
- How it works: [architecture](docs/architecture.md).

Building from source needs Windows, the .NET 10 SDK, Git for Windows and (for the setup wizard) Inno Setup 6. The add-in builds against the SOLIDWORKS on the PC, or without one against the copies of its API libraries in `lib\solidworks`. A server can also build and publish `main` by itself every night: see [automatic updates](docs/self-hosting-linux.md#automatic-updates-from-github).

```powershell
dotnet build SwVault.slnx
dotnet test SwVault.slnx
.\scripts\package.ps1 -Version 1.4.0 -TeamConfig .\team.json -Publish   # setup wizard + update zip; team.json comes from the server
.\scripts\dev-gitea.ps1 -Setup        # local Gitea for testing (gitea.exe in C:\SwVaultDev\gitea)
.\scripts\dev-register-addin.ps1      # load your build in SOLIDWORKS (close it first)
```


## SWVAULT 1.4 — direct-to-R2 pilot release

This ZIP is the full **GitHub-ready source tree**, not a prebuilt Windows EXE. It implements multipart client-to-R2 uploads with four 32 MiB parts in parallel, private signed R2 downloads, server-side verified cold copies and supported Gitea registration, plus the **one-time 1.2.9 -> 1.4** migration.

- [Guide A — Fresh 1.4 Linux VM / Cloudflare / R2 / import existing CAD files](docs/1.4-FRESH-VM-SETUP.md)
- [Guide B — Import original 1.2.9 vault history and safely cut over](docs/1.2.9-TO-1.4-MIGRATION.md)
- [Guide C — Build Windows plugin and publish 1.4 using the original 1.2.9 updater](docs/1.4-WINDOWS-ROLLOUT.md)
- [Technical transfer architecture, limitations and acceptance criteria](docs/1.4-DIRECT-TRANSFER-TECHNICAL.md)
- [Configuration, secrets, cold storage, and independent backups](docs/1.4-CONFIGURATION-AND-BACKUPS.md)
- [What was verified vs what still requires live testing](RELEASE-READINESS.md)

**Bandwidth caveat:** user-upload payload goes from Windows to R2 rather than to the VM, but the VM still downloads each new object once to verify and cold-store it, and uploads the canonical LFS copy once to R2 through Gitea. Normal client downloads bypass the VM. This design is coded and locally tested, **not** verified against your live Cloudflare account, real Gitea 1.27.3 container, or a Windows SOLIDWORKS workstation. Do not retire the old 1.2.9 vault before pilot testing.

Do not publish production `.env`, `data/`, Gitea SQLite, R2 keys, Cloudflare Tunnel tokens, private user backups, or `team.json` with invite codes to GitHub.
