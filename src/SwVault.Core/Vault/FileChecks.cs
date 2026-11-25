using SwVault.Core.Index;
using SwVault.Core.Util;

namespace SwVault.Core.Vault;

/// <summary>A problem with a file someone is about to check in. Blocking problems stop the check-in.</summary>
public sealed record FileProblem(string Path, bool Blocking, string Message);

/// <summary>
/// Mistakes people make when adding files, caught before they reach the vault: half-saved (empty)
/// files, paths SOLIDWORKS can't open, a second part with the same name (assemblies then load the
/// wrong one), copies of files already in the vault, and things that don't belong in a CAD vault.
/// </summary>
public static class FileChecks
{
    /// <summary>SOLIDWORKS and Windows Explorer fail on paths near 260 characters.</summary>
    public const int MaxLocalPath = 250;

    /// <summary>Above this, teammates whose vault folder is deeper than yours may hit the limit.</summary>
    public const int LongVaultPath = 180;

    /// <summary>The server's Git LFS limit (docker-compose.yml: LFS_MAX_FILE_SIZE).</summary>
    public const long MaxFileBytes = 4L * 1024 * 1024 * 1024;

    public const long LargeFileBytes = 200L * 1024 * 1024;

    private static readonly HashSet<string> SolidWorksTypes = new(StringComparer.OrdinalIgnoreCase) { ".sldprt", ".sldasm", ".slddrw" };

    private static readonly HashSet<string> UnusualTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".msi", ".dll", ".bat", ".cmd", ".ps1", ".lnk", ".url", ".zip", ".7z", ".rar", ".iso", ".tar", ".gz",
    };

    public static bool IsSolidWorksFile(string path) => SolidWorksTypes.Contains(Path.GetExtension(path));

    /// <summary>
    /// Checks one file of a check-in. <paramref name="isNew"/>: not in the vault yet. <paramref name="batch"/>:
    /// every vault path in the same check-in (for duplicate names among the new files themselves).
    /// </summary>
    public static IEnumerable<FileProblem> Check(string vaultPath, string localPath, long size, string? oid, bool isNew, HeadIndex head, IReadOnlyCollection<string> batch)
    {
        var name = PathRules.GetFileName(vaultPath);

        if (size == 0)
            yield return new(vaultPath, true, $"{name} is empty (0 bytes). That usually means a save didn't finish. Open it, save it again, then check it in.");
        if (size > MaxFileBytes)
            yield return new(vaultPath, true, $"{name} is {Bytes(size)}; the vault takes files up to {Bytes(MaxFileBytes)}. Keep it somewhere else and link to it in a note.");
        if (localPath.Length > MaxLocalPath)
            yield return new(vaultPath, isNew, $"{name}: the full path is {localPath.Length} characters, and SOLIDWORKS can't open files past about {MaxLocalPath}. Use shorter folder or file names.");

        if (!isNew) yield break;

        foreach (var segment in vaultPath.Split('/'))
        {
            if (segment.Length > 0 && segment != segment.TrimEnd(' ', '.'))
            {
                yield return new(vaultPath, true, $"\"{segment}\" ends with a space or a dot, which Windows can't handle reliably. Rename it.");
                break;
            }
        }

        if (localPath.Length <= MaxLocalPath && vaultPath.Length > LongVaultPath)
            yield return new(vaultPath, false, $"{name} has a long path inside the vault ({vaultPath.Length} characters). Teammates whose vault folder is deeper than yours may not be able to open it.");

        if (IsSolidWorksFile(vaultPath))
        {
            var sameName = head.Files.Values
                .Where(f => !string.Equals(f.Path, vaultPath, PathRules.Comparison) && string.Equals(PathRules.GetFileName(f.Path), name, PathRules.Comparison))
                .Select(f => f.Path)
                .Concat(batch.Where(p => !string.Equals(p, vaultPath, PathRules.Comparison) && string.Equals(PathRules.GetFileName(p), name, PathRules.Comparison)))
                .Distinct(PathRules.Comparer)
                .ToList();
            if (sameName.Count > 0)
                yield return new(vaultPath, false, $"{name} is also in the vault as {string.Join(", ", sameName.Take(3))}. SOLIDWORKS finds parts by file name, so assemblies can open the wrong one. Give it a unique name (e.g. with the part number).");
        }

        if (!string.IsNullOrEmpty(oid) && size > 0)
        {
            var copyOf = head.Files.Values.FirstOrDefault(f => f.Oid == oid && !string.Equals(f.Path, vaultPath, PathRules.Comparison));
            if (copyOf != null)
                yield return new(vaultPath, false, $"{name} is an exact copy of {copyOf.Path}, which is already in the vault. If you moved or copied it in File Explorer, assemblies still use the original; keep working on {PathRules.GetFileName(copyOf.Path)} instead.");
        }

        var extension = Path.GetExtension(vaultPath);
        if (UnusualTypes.Contains(extension))
            yield return new(vaultPath, false, $"{name} is a {extension} file. The vault is for CAD files and their documents; programs and archives make it slow for everyone.");
        else if (size > LargeFileBytes && !IsSolidWorksFile(vaultPath))
            yield return new(vaultPath, false, $"{name} is {Bytes(size)}. Everyone downloads every file in the vault; make sure the team needs it.");
    }

    /// <summary>
    /// A new file's path with each folder spelled the way the vault already spells it, so
    /// "suspension/Hub.SLDPRT" lands in the existing "Suspension" folder instead of a second one.
    /// </summary>
    public static string CanonicalFolders(string vaultPath, HeadIndex head)
    {
        var segments = PathRules.Normalize(vaultPath).Split('/');
        var folder = "";
        for (var i = 0; i < segments.Length - 1; i++)
        {
            var candidate = folder.Length == 0 ? segments[i] : folder + "/" + segments[i];
            folder = head.FolderSpelling(candidate) ?? candidate;
        }
        return folder.Length == 0 ? segments[^1] : folder + "/" + segments[^1];
    }

    internal static string Bytes(long bytes) =>
        bytes >= 1L << 30 ? $"{bytes / (double)(1L << 30):0.#} GB" : bytes >= 1L << 20 ? $"{bytes / (double)(1L << 20):0.#} MB" : $"{bytes / 1024.0:0.#} KB";
}

/// <summary>Where a vault folder may go. Wrong choices fail later in confusing ways, so they're refused up front.</summary>
public static class VaultFolderRules
{
    public const int MaxRootLength = 100;

    /// <summary>Why <paramref name="root"/> can't be a vault folder, or null when it's fine.</summary>
    /// <param name="otherRoots">The vault folders already on this PC.</param>
    public static string? Problem(string root, IEnumerable<string> otherRoots, bool checkDrive = true)
    {
        if (string.IsNullOrWhiteSpace(root)) return "Choose a folder for the vault, e.g. C:\\SWVault\\Team.";
        root = root.Trim().Trim('"');
        if (root.StartsWith(@"\\", StringComparison.Ordinal))
            return $"{root} is a network folder. SOLIDWORKS and SwVault need a folder on this PC's own drive, e.g. C:\\SWVault\\Team.";
        if (!(root.Length >= 3 && char.IsLetter(root[0]) && root[1] == ':' && root[2] is '\\' or '/'))
            return $"\"{root}\" isn't a full folder path. Use something like C:\\SWVault\\Team.";
        var full = WindowsFullPath(root);
        if (full.Length <= 2) return $"Don't use the whole {full}\\ drive; choose a folder on it, e.g. {full}\\SWVault\\Team.";
        if (full.Length > MaxRootLength)
            return $"That folder's path is {full.Length} characters long, which leaves too little room for file paths (SOLIDWORKS stops at about 250). Use a short one like C:\\SWVault\\Team.";
        if (PathRules.IsUnderSyncedFolder(full))
            return $"{full} is inside OneDrive, Dropbox or Google Drive. Cloud sync fights with SOLIDWORKS and SwVault over the files; use a folder like C:\\SWVault\\Team.";

        var lower = full.ToLowerInvariant() + "\\";
        var system = new[] { @"\windows\", @"\program files\", @"\program files (x86)\", @"\programdata\", @"\appdata\", @"\$recycle.bin\" };
        if (system.Any(s => lower.Contains(s, StringComparison.Ordinal)) || lower.EndsWith(@"\desktop\", StringComparison.Ordinal) || lower.EndsWith(@"\documents\", StringComparison.Ordinal) || lower.EndsWith(@"\downloads\", StringComparison.Ordinal))
            return $"{full} is a Windows or personal folder. Use a folder only for the vault, e.g. C:\\SWVault\\Team.";

        foreach (var other in otherRoots.Where(o => !string.IsNullOrWhiteSpace(o)))
        {
            var o = WindowsFullPath(other);
            if (string.Equals(o, full, StringComparison.OrdinalIgnoreCase)) continue; // reconnecting the same vault
            if (full.StartsWith(o + "\\", StringComparison.OrdinalIgnoreCase))
                return $"{full} is inside another vault's folder ({o}). Give each vault its own folder, e.g. C:\\SWVault\\<vault name>.";
            if (o.StartsWith(full + "\\", StringComparison.OrdinalIgnoreCase))
                return $"{full} contains another vault's folder ({o}). Give each vault its own folder, e.g. C:\\SWVault\\<vault name>.";
        }

        if (checkDrive)
        {
            try
            {
                var drive = new DriveInfo(full.Substring(0, 3));
                if (drive.DriveType == DriveType.Network)
                    return $"{drive.Name} is a network drive. Use a folder on this PC's own drive, e.g. C:\\SWVault\\Team.";
                if (drive.DriveType is DriveType.Removable or DriveType.CDRom)
                    return $"{drive.Name} is a removable drive. The vault folder must always be there when SOLIDWORKS opens files; use C:\\SWVault\\Team.";
                var format = drive.IsReady ? drive.DriveFormat : "";
                if (format.StartsWith("FAT", StringComparison.OrdinalIgnoreCase) || format.Equals("exFAT", StringComparison.OrdinalIgnoreCase))
                    return $"{drive.Name} is formatted {drive.DriveFormat}, which can't hold files over 4 GB or mark files read-only reliably. Use an NTFS drive (C: is).";
            }
            catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
            {
                // Not a drive Windows knows (e.g. a letter that isn't mapped): Directory.CreateDirectory reports it.
            }
        }
        return null;
    }

    /// <summary>"C:/SWVault//Team\\..\\Team\\" -> "C:\\SWVault\\Team", the same on every OS (tests run on Linux too).</summary>
    internal static string WindowsFullPath(string path)
    {
        var parts = new List<string>();
        foreach (var part in path.Trim().Replace('/', '\\').Split('\\', StringSplitOptions.RemoveEmptyEntries))
        {
            if (part == ".") continue;
            if (part == "..") { if (parts.Count > 1) parts.RemoveAt(parts.Count - 1); continue; }
            parts.Add(part);
        }
        return string.Join("\\", parts);
    }

    /// <summary>
    /// A folder that already holds other files would turn them all into "new" vault files. It's fine
    /// when it's empty, missing, or was already used by SwVault.
    /// </summary>
    public static string? NotEmptyProblem(string root)
    {
        if (!Directory.Exists(root)) return null;
        if (Directory.Exists(Path.Combine(root, PathRules.LocalDirName))) return null;
        var count = Directory.EnumerateFileSystemEntries(root).Take(1).Count();
        return count == 0 ? null
            : $"{root} already has other files in it, and SwVault would treat all of them as new vault files. Choose an empty or new folder, e.g. C:\\SWVault\\<vault name>.";
    }
}

/// <summary>Turns whatever someone pastes as the vault address into the repository URL SwVault uses.</summary>
public static class VaultUrls
{
    /// <summary>
    /// "swvault.x.ts.net/team/cad", "https://server/team/cad/src/branch/main" and
    /// "https://server/team/cad.git" all become "https://server/team/cad.git". Local paths (tests,
    /// file servers) pass through unchanged.
    /// </summary>
    public static string Normalize(string? input)
    {
        var text = (input ?? "").Trim().Trim('"', '\'', '<', '>').Trim();
        if (text.Length == 0) throw VaultException.BadRequest("Enter the vault address your admin gave you, like https://server/team/cad.");
        if (Path.IsPathRooted(text) || text.StartsWith("file:", StringComparison.OrdinalIgnoreCase)) return text;
        if (!text.Contains("://", StringComparison.Ordinal)) text = "https://" + text;
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http") || string.IsNullOrEmpty(uri.Host))
            throw VaultException.BadRequest($"\"{input}\" isn't a web address. Paste the vault address your admin gave you, like https://server/team/cad.");
        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length < 2)
            throw VaultException.BadRequest($"{uri.GetLeftPart(UriPartial.Authority)} is the server; the vault address also names the team and the vault, like {uri.GetLeftPart(UriPartial.Authority)}/team/cad.");
        var repo = segments[1].EndsWith(".git", StringComparison.OrdinalIgnoreCase) ? segments[1][..^4] : segments[1];
        return $"{uri.GetLeftPart(UriPartial.Authority)}/{segments[0]}/{repo}.git";
    }
}

/// <summary>Files someone checked out and forgot: they block teammates, so the agent reminds them once a day.</summary>
public static class CheckOutReminder
{
    public static readonly TimeSpan OldAfter = TimeSpan.FromDays(3);

    /// <summary>My check-outs older than <see cref="OldAfter"/>, oldest first.</summary>
    public static IReadOnlyList<Protocol.LockDto> Old(IEnumerable<Protocol.LockDto> locks, DateTimeOffset now) =>
        locks.Where(l => l.Mine && DateTimeOffset.TryParse(l.LockedAt, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out var at) && now - at > OldAfter)
            .OrderBy(l => l.LockedAt, StringComparer.Ordinal)
            .ToList();

    public static string Message(IReadOnlyList<Protocol.LockDto> old) =>
        (old.Count == 1 ? $"{PathRules.GetFileName(old[0].Path)} has been checked out to you for over {OldAfter.Days} days."
                        : $"{old.Count} files have been checked out to you for over {OldAfter.Days} days: {string.Join(", ", old.Take(4).Select(l => PathRules.GetFileName(l.Path)))}{(old.Count > 4 ? "..." : "")}.")
        + " Check them in (or Undo Check Out) so teammates can edit them.";
}
