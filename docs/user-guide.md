# SwVault user guide

SwVault keeps the team's SOLIDWORKS files on the vault server and makes sure only one person edits a file at a time.

## Getting set up

1. Close SOLIDWORKS.
2. Open the link in your invite, click **Download SwVault**, and run the downloaded **SwVault Setup**. If Windows shows "Windows protected your PC", click **More info** → **Run anyway**. Approve the administrator prompt. If setup says SOLIDWORKS is still running in the background, let it end SOLIDWORKS for you.
3. In the SwVault window:
   - **New here?** Choose **I'm new and have an invite**. The code is usually filled in already. Enter your school email, click **Email me a code**, and type in the 6-digit code from the email. Then enter your name, say whether you're a **general member** or a **subteam lead** (and which subteam), and choose a user name and password.
   - **Already have an account?** Choose **I have an account** and sign in.
4. Your team's files download to the vault folder, for example `C:\SWVault\FSAE`. The SwVault icon in the system tray tells you when they're all there.
5. Open SOLIDWORKS. The **SwVault** tab and task pane are ready.

Closed the sign-in window? Click the SwVault tray icon, or click **Connect vault...** in the SwVault task pane.

**Switching accounts:** right-click the tray icon → **Switch account...** and sign in as the other account (a shared lab PC, or an admin's separate everyday account). A wrong password changes nothing. Files stay where they are; what the first account had checked out on this PC stays checked out to that account, so check in or undo first, or switch back later. In SOLIDWORKS, **Get Latest** on a file shows a **Cancel** button while it downloads; cancelling leaves your files as they were.

## The basics

- **Your vault folder**, for example `C:\SWVault\FSAE`, is the same path on every team PC. Always open and save vault files from there. Don't put it in OneDrive, Dropbox or Google Drive.
- **Files you haven't checked out are read-only.** SOLIDWORKS opens them read-only and won't let you save over them.
- **Check Out** before you edit. This locks the file for you. Everyone else sees "checked out by you" and can't check it out until you're done.
- **Check In** when you're done. This uploads your changes as a new version, releases the lock, and makes the file read-only again. Add a version note saying what changed and why; it becomes that version's entry in the history (left blank, the version is just called "Version N").
- **Get Latest** downloads newer versions that teammates checked in. The tray icon tells you when files you have were updated.

## In SOLIDWORKS

The **SwVault** tab (CommandManager) has the five everyday commands: **Check Out**, **Check In**, **Add to Vault**, **Get Latest** and **Request Review**. They act on the active document, or on the components you selected in an assembly's FeatureManager tree (right-click → SwVault). Everything else in the table is on the **Tools > SwVault** menu and in the **SwVault task pane** (right side): right-click a file or a folder there.

| Command | What it does |
|---|---|
| File Browser | Searches the whole vault as you type, with a thumbnail for every file. Typos and initials work ("frnt brkt" finds FrontBracket), and so do part numbers, materials, descriptions and version notes; narrow by a property with `name:value` (`material:6061`, `by:alice`, `state:released`, `rev:B`). Pick a file to see everything about it (status, custom properties, configurations, revisions, who approved the latest release, references, where used, the version timeline and its reviews). The **Reviews** tab lists every review request on the file with its status and discussion; **Request Review...** asks a lead, **Respond in Reviews...** opens your review inbox. **Open in SOLIDWORKS**, double-click or Enter opens it, getting the latest files first if needed. The task pane's **Browse...** button opens it with the pane's search text. |
| Add to Vault | Puts the active document in the vault. A window asks where it goes: pick a project, a subsystem or a folder inside one (or make a **New folder**), and check the file name. It is then saved there and checked in, together with any new files it references, after asking for a version note (it suggests "Added <file>"; blank is fine). |
| Check Out | Locks the file(s) and gets the latest version first. For an assembly or drawing, you tick which referenced files you also want to edit. |
| Check In | Lists what you changed or added under the file (including new parts in an assembly), asks for a version note (blank becomes "Version N"), uploads, releases. A preview image of each file is saved with the new version, so everyone sees thumbnails on the website and in the file browser, even for files they haven't downloaded. If SOLIDWORKS has no preview of a file, SwVault uses the one Windows Explorer shows. Tick **Keep checked out** to keep working. |
| Undo Check Out | Throws away your changes and releases the lock. |
| Get Latest | Downloads the newest versions of the file and everything it references. Open documents reload automatically. |
| History | A timeline of every version, newest first: big dots are versions (green when released), small dots are workflow changes, and a ring marks the version on your PC. Select one to see its saved preview, who, when, who approved it (for releases), its full version note and what changed since the version before (properties, references, configurations, size). **Open This Version** gets it and opens it in SOLIDWORKS. **Get This Version** puts an older version in your folder, read-only. Tick **With references as built** for an assembly exactly as it was built. **Roll Back** (after checking out) makes an old version the newest again when you check in. |
| Where Used | Assemblies and drawings that use this file. |
| Change State | Workflow: Submit for review → Approve (release) / Reject; Change request to edit a released file again. |
| Request Review | Asks a subteam lead for a **design**, **simulation** or **drawing** review of the file. You pick the lead and add a note. The lead reviews the version that's checked in. |
| Reviews | Your review inbox: **For me** (requests sent to you as a lead: open the file, **Approve**, **Request changes**, **Comment**), **My requests** (what you asked for and what the lead said) and **Copied to me (RE)** (requests on subsystems you're a responsible engineer of). |
| Projects | The projects, their subsystems and each subsystem's responsible engineers (REs). Add, rename or remove a project or subsystem, or ask to be an RE. Also the **Projects...** button in the task pane. |
| Invite People | Vault admins: makes an invite link and code to paste into the team chat. |
| Import Folder | Copies an existing folder of SOLIDWORKS files into the vault and fixes the references. Usually done once, by an admin. |
| Refresh / Vaults | Re-check the server, or connect to a vault. |

**Task pane:**
- One tree shows the vault: projects, subsystems and folders, with the files inside each folder. Each file has its SOLIDWORKS icon (assembly, part, drawing) with the state as a small badge, then its version and anything worth noticing: who has it checked out, *Outdated*, *Modified*, *New*, *Not local*, its workflow state and revision. Hover for the details. Assemblies come first, the top-level one at the top, then parts and drawings.
- Right-click a file for the commands, including **Move...** to put it in another folder. Its history goes along, and SwVault points the assemblies and drawings that use it at the new place (they are checked out, updated and checked back in for you). Ctrl+click or Shift+click picks several files for one command.
- Right-click a folder (or the empty space, for the top of the vault) for **New Subfolder...**, **Add Files Here...**, **Show in Explorer**, and the less-used windows: Projects, Import Folder, Reviews, Invite People. A new subfolder directly inside a subsystem appears on everyone's PC; anywhere else, teammates get it with the first file checked in to it.
- Filter to *My check-outs*, *Outdated*, *In review* or *New and modified*, or search file names and properties (part numbers, descriptions): the tree becomes a list of the matching files, each with its folder. Back on *Browse folders* the tree is as you left it.
- Double-click a file to open it. SwVault first offers to get the latest versions of everything it references.
- **Update Preview** (right-click a file) saves the thumbnail of a file that is already in the vault but shows no picture on the website or in the file browser (files added before 1.1.4, or imported). It doesn't make a new version.
- Files that someone moved or deleted from another PC disappear from your vault folder at the next refresh. A copy you changed without checking in is kept, and SwVault tells you.

**New files:** click **Add to Vault** (on the SwVault tab, or the button at the bottom of the task pane) and choose where the file goes; the window starts in the folder selected in the task pane. SwVault saves the file there and checks it in, including new parts an assembly uses. Parts and sub-assemblies it uses from **outside** the vault (your Desktop, OneDrive, the 3DEXPERIENCE cache...) are copied into the vault next to it (keeping their subfolders if they were in the assembly's folder), and the assembly is pointed at the copies, so teammates can open it. If the vault **already has** a file with the same name (say you built a new car's assembly from last year's battery pack), SwVault asks first: **Yes** uses the vault's file where it is and copies nothing (the assembly is closed and reopened to point at it; changes that are only in your outside copy are not included), **No** copies your outside file in as a new file. Virtual components stay inside their assembly. Vault files you haven't checked out are left as they are, even when SOLIDWORKS marks them as changed after a rebuild. The same happens on **Check In** when you've inserted an outside part into an open assembly. If a file with that name is already there, the copy gets a " (2)" suffix. Afterwards the file is read-only like every other vault file; check it out to keep editing. You can still save new files inside the vault folder yourself; they show as *New* until you add or check them in.

**STEP and other CAD formats:** the vault keeps any file, with versions, check-out and history like a part. For files that aren't open in SOLIDWORKS (a vendor's STEP model, a DXF for the laser cutter, a PDF datasheet) click **Add Files...** in the task pane, pick the files, and choose the folder they go in.

Double-clicking a file SOLIDWORKS can read opens it in SOLIDWORKS, the same as File > Open:

| Kind | Formats |
|---|---|
| Neutral 3D | STEP (`.step` `.stp`), IGES (`.igs` `.iges`), Parasolid (`.x_t` `.x_b`), ACIS (`.sat`), VDAFS, IFC, JT, 3D XML, IDF boards |
| Other CAD systems | Creo (`.prt` `.asm`, also numbered saves like `.prt.12`), NX, Inventor (`.ipt` `.iam`), Solid Edge (`.par` `.psm`), CATIA V5, Rhino (`.3dm`) |
| Meshes | STL, 3MF, OBJ, PLY, AMF, VRML |
| 2D | DXF, DWG |
| SOLIDWORKS extras | Templates, sheet formats, library features, form tools, blocks |

Anything else (PDF, spreadsheets, pictures) opens in its usual Windows program. The file browser and the web page can filter by these kinds.

Opening a STEP file gives you a new SOLIDWORKS part made from it; the STEP file in the vault isn't changed. To keep that part, use **Add to Vault**. To update the STEP itself, check it out, replace the file, and check it in. Some formats (CATIA, JT) need a SOLIDWORKS licence that includes them.

## On the web

The vault is also at **https://&lt;your server&gt;/swvault/**: sign in with your SwVault user name and password, on any computer or phone. Search works like the File Browser in SOLIDWORKS, and every file shows its thumbnail, properties, references, where it's used, its version history (download any version) and its reviews. Leads can approve or ask for changes right there. **Copy Link** gives a link to the file for chat or email.

The **Open in SOLIDWORKS**, **Get Latest**, **Check Out**, **Check In** and **Change State** buttons hand the file to SwVault on your PC (your browser asks the first time; tick "always allow"). With SOLIDWORKS open they work just like the task pane. Without it, Open starts SOLIDWORKS with the file, and Get Latest and Check Out still run.

## Reviews

- **Asking for a review:** check the file in, then **Request Review**. You'll get an email and a tray notification when the lead approves it or asks for changes. If they ask for changes, their feedback is in the email and under **Reviews → My requests**. Make the changes, check in, and request another review.
- **If you're a lead:** new requests arrive by email and as a tray notification, and wait under **Reviews → For me**. **Request changes** needs a comment saying what to change. **Approve** can have one too.
- **Your team role** (general member or subteam lead, and which subteam): tray icon → **My team role...**. Only leads appear in the Request Review list. Becoming a lead waits for a vault admin to approve it; until then you stay a general member and the window says the request is waiting.
- **Responsible engineers:** when a general member asks for a review of a file in a subsystem, that subsystem's REs are copied on it (email, tray notification, and **Reviews → Copied to me (RE)**). They can comment; the lead still approves.

## Projects, subsystems and responsible engineers

Each project (for example *2027 Car*) has subsystems (*Chassis*, *Front Suspension*...), and each subsystem is a folder in the vault, such as `FS27/Chassis`. Files in that folder (and its subfolders) belong to the subsystem; when subsystem folders are nested, the deepest one wins.

- **See them:** the **Projects...** button in the task pane, SwVault tab → **Projects**, or tray icon → **Projects and subsystems...**. The task pane also shows the active file's subsystem and its REs.
- **Add a project or subsystem:** anyone on the team can, from that window. The folder defaults to the name (a subsystem's goes inside its project's folder) and is created in your vault folder.
- **Folders inside a subsystem:** a new subsystem starts with *Custom Parts*, *COTS Parts* and *Fasteners* (change the list when you add it). Select a subsystem and click **Add folder...** for more, such as `COTS Parts/Bearings`. Everyone's vault folder gets these folders, even while they're empty. Files in them still belong to the subsystem.
- **Rename or remove one:** select it, then **Rename...** or **Remove**. Vault admins can, and so can whoever added it. Removing only takes it off this list: files and folders in the vault stay where they are.
- **Become a responsible engineer:** select the subsystem, then **I'm a responsible engineer of this**. A subsystem can have several REs. A vault admin approves each request (admins' own requests are approved straight away). You get a notification when it's approved. **Step down** (or **Withdraw my request**) any time.
- **As an RE** you're notified when someone checks in, adds or releases a file in your subsystem, and you're copied on general members' review requests for it.
- **Admins** see waiting lead and RE requests under tray icon → **Approvals (n waiting)...**, get a notification when a new one arrives, and can remove anyone as an RE.

Reviews are separate from release approval (**Change State → Approve**), which stamps revisions. A review is feedback from your lead. Many teams get a review before submitting a file for release.

## Workflow and revisions

| State | Meaning |
|---|---|
| WIP | Being designed. Check out/in freely. |
| InReview | Submitted for review. Read-only until an approver approves or rejects it. |
| Released | Approved. The revision letter (A, B, C…) is written into the file's `Revision` property, and a PDF of the drawing is saved in `_Released`. Read-only; use **Change request** to edit again. The next release gets the next letter. |
| Obsolete | No longer used. |

Only approvers can approve. The admin sets who they are. A release note is optional; the vault records who approved the release, when and which version, and the browser and history show it.

## Updates

When your admin publishes a new SwVault, you get a notification and a bold **Install update...** item on the tray icon. Click it: a window shows the download and what the update is doing. Close SOLIDWORKS when it asks. It installs in about a minute after one Windows administrator prompt. **Check for updates** on the tray icon checks right away.

## When something looks wrong

| You see | Do this |
|---|---|
| "checked out by Alex" | Ask Alex to check it in. If Alex is gone, an admin can release it. |
| Outdated | Get Latest. |
| Conflict | Someone checked in a newer version while you edited an old copy. Save your work as a copy, Undo Check Out, Get Latest, and redo the change on the newest version. |
| "checked out by you on another computer" | Check it in there, or choose to take over the check-out here. |
| Can't save a file | It's read-only because you haven't checked it out. Check it out. |
| Everything offline | Are you connected to the internet (or on campus / the CU VPN, if your team's server is on campus)? Is the tray icon running? |
| "...is empty (0 bytes)" | The last save didn't finish (SOLIDWORKS crashed, or the disk was full). Open the file, save it again, then check in. |
| "...is also in the vault as ..." | Another file has the same name. SOLIDWORKS finds parts by name, so an assembly can open the wrong one. Rename yours (e.g. add the part number) before adding it. |
| "...is an exact copy of ..." | You copied or moved a vault file in File Explorer. Assemblies still use the original; work on that one instead, and delete the copy. |
| "the full path is ... characters" | SOLIDWORKS can't open files with very long paths. Use shorter folder or file names. |
| "You opened ... from ..., which is not your vault folder" | You opened a copy (from Downloads, an email, a USB stick). Edits there never reach the team; open the vault's copy instead. |
| "files still checked out" | You've had files checked out for more than 3 days. Check them in, or Undo Check Out, so others can edit them. |
| "vault folder missing" | You moved or renamed `C:\SWVault\<Team>`. Move it back; SwVault and SOLIDWORKS look for the files there. |
| "Not enough free disk space" | Free up space on that drive (empty the Recycle Bin, delete old downloads), then try again. |

Run `swvault doctor` in a terminal for a quick health check of your PC's setup.
