using SwVault.Core.Index;
using SwVault.Core.Lfs;
using SwVault.Core.State;
using SwVault.Core.Util;
using SwVault.Core.Vault;
using SwVault.Protocol;

namespace SwVault.Core;

public enum StagedAction
{
    Write,
    Delete,
    SetReadOnly,
    SetWritable,
}

public sealed class StagedChange
{
    public required string VaultPath { get; init; }
    public required string LocalPath { get; init; }
    public StagedAction Action { get; init; }
    public string? TempFile { get; set; }
    public string? Oid { get; init; }
    public long Size { get; init; }
    public int Version { get; init; }

    /// <summary>Read-only attribute after a Write (true unless the file is checked out by this PC).</summary>
    public bool ReadOnly { get; init; } = true;

    /// <summary>Record Oid/Version as the local base (false for rollback, where the content is a pending change).</summary>
    public bool UpdateBase { get; init; } = true;
}

/// <summary>
/// Workspace changes prepared by an operation. Downloads land in a staging folder first, so the
/// add-in can release open SOLIDWORKS documents before <see cref="VaultSession.ApplyAsync"/> swaps them in.
/// </summary>
public sealed class StagedOperation
{
    public string Id { get; } = Guid.NewGuid().ToString("N");
    public JobKind Kind { get; init; }
    public List<StagedChange> Changes { get; } = new();
    public List<string> Warnings { get; } = new();

    /// <summary>Runs after apply with the vault paths that were actually applied (e.g. release locks).</summary>
    public List<Func<IReadOnlyCollection<string>, CancellationToken, Task>> AfterApply { get; } = new();
    public string? StagingDir { get; set; }

    public IReadOnlyList<string> Replacements =>
        Changes.Where(c => c.Action is StagedAction.Write or StagedAction.Delete && File.Exists(c.LocalPath)).Select(c => c.LocalPath).ToList();

    public IReadOnlyList<string> AffectedLocalPaths => Changes.Select(c => c.LocalPath).ToList();
}

public sealed record ApplyResult(IReadOnlyList<string> Applied, IReadOnlyList<string> Skipped, IReadOnlyList<string> Warnings);

public sealed partial class VaultSession
{
    internal StagedChange WriteChange(HeadFile head, bool readOnly) => new()
    {
        VaultPath = head.Path,
        LocalPath = LocalPathOf(head.Path),
        Action = StagedAction.Write,
        Oid = head.Oid,
        Size = head.Size,
        Version = head.Version,
        ReadOnly = readOnly,
    };

    /// <summary>Room for the downloads plus a margin, checked before starting instead of failing halfway.</summary>
    internal void EnsureFreeSpace(long bytes)
    {
        if (bytes <= 0) return;
        long free;
        string drive;
        try
        {
            var info = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(LocalRoot)) ?? LocalRoot);
            if (!info.IsReady) return;
            free = info.AvailableFreeSpace;
            drive = info.Name.TrimEnd('\\', '/');
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return; // can't tell; the download reports a full disk itself
        }
        var needed = FreeSpaceNeeded(bytes);
        if (free < needed)
            throw VaultException.BadRequest($"Not enough free disk space: this needs about {FileChecks.Bytes(needed)} on {drive} and {FileChecks.Bytes(free)} is free. Free up space (empty the Recycle Bin, remove old downloads), then try again.");
    }

    /// <summary>The download plus 10% and 1 GB, so Windows and SOLIDWORKS still have room afterwards.</summary>
    internal static long FreeSpaceNeeded(long bytes) => bytes + bytes / 10 + (1L << 30);

    internal async Task StageDownloadsAsync(StagedOperation op, IProgress<TransferProgress>? progress, CancellationToken ct)
    {
        var writes = op.Changes.Where(c => c.Action == StagedAction.Write && c.TempFile == null).ToList();
        if (writes.Count == 0) return;

        var localDir = Path.Combine(LocalRoot, PathRules.LocalDirName);
        Directory.CreateDirectory(localDir);
        EnsureFreeSpace(writes.Sum(w => w.Size));
        var dirInfo = new DirectoryInfo(localDir);
        if (!dirInfo.Attributes.HasFlag(FileAttributes.Hidden)) dirInfo.Attributes |= FileAttributes.Hidden;
        op.StagingDir ??= Path.Combine(localDir, "tmp", op.Id);
        Directory.CreateDirectory(op.StagingDir);

        var downloads = new List<LfsDownloadItem>();
        var n = 0;
        foreach (var change in writes)
        {
            change.TempFile = Path.Combine(op.StagingDir, (n++).ToString(System.Globalization.CultureInfo.InvariantCulture) + ".part");
            if (change.Oid!.StartsWith(HeadIndex.RawOidPrefix, StringComparison.Ordinal))
            {
                var content = await Mirror.ReadBlobAsync(change.Oid.Substring(HeadIndex.RawOidPrefix.Length), ct).ConfigureAwait(false)
                    ?? throw VaultException.NotFound($"Content for {change.VaultPath} is missing from the mirror.");
                await File.WriteAllBytesAsync(change.TempFile, content, ct).ConfigureAwait(false);
            }
            else
            {
                downloads.Add(new LfsDownloadItem(change.Oid, change.Size, change.TempFile));
            }
        }
        await Lfs.DownloadAsync(downloads, progress, ct).ConfigureAwait(false);
    }

    /// <summary>Moves staged content into the workspace. Files still open elsewhere are skipped, not failed.</summary>
    public async Task<ApplyResult> ApplyAsync(StagedOperation op, CancellationToken ct = default)
    {
        var applied = new List<string>();
        var skipped = new List<string>();
        var warnings = new List<string>();
        var inUse = new List<string>();
        foreach (var change in op.Changes)
        {
            try
            {
                ApplyOne(change);
                applied.Add(change.VaultPath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                skipped.Add(change.LocalPath);
                // Windows refuses to replace a file another program has open; say that, not "access denied".
                if (File.Exists(change.LocalPath) && (ex is UnauthorizedAccessException || (ex.HResult & 0xFFFF) is 32 or 33)) inUse.Add(change.VaultPath);
                else warnings.Add($"{change.VaultPath}: {ex.Message}");
            }
        }
        if (inUse.Count > 0)
            warnings.Add((inUse.Count == 1 ? "This file is" : $"These {inUse.Count} files are") + " open in SOLIDWORKS or another program, so "
                + (inUse.Count == 1 ? "it was" : "they were") + " left as " + (inUse.Count == 1 ? "it is" : "they are") + " (an older version):\n\n"
                + string.Join("\n", inUse.Take(15)) + (inUse.Count > 15 ? $"\n... and {inUse.Count - 15} more" : "")
                + "\n\nClose all documents in SOLIDWORKS (Window > Close All), then use Get Latest again.");

        foreach (var callback in op.AfterApply) await callback(applied, ct).ConfigureAwait(false);

        if (op.StagingDir != null)
        {
            try { Directory.Delete(op.StagingDir, recursive: true); } catch (IOException) { }
        }
        return new ApplyResult(applied, skipped, warnings);
    }

    private void ApplyOne(StagedChange change)
    {
        var local = change.LocalPath;
        switch (change.Action)
        {
            case StagedAction.Write:
            {
                Directory.CreateDirectory(Path.GetDirectoryName(local)!);
                if (File.Exists(local)) SetReadOnlyAttribute(local, false);
                File.Move(change.TempFile!, local, overwrite: true);
                SetReadOnlyAttribute(local, change.ReadOnly);
                var file = new FileInfo(local);
                var record = State.GetFile(change.VaultPath) ?? new FileRecord { Path = change.VaultPath };
                if (change.UpdateBase)
                {
                    record.BaseOid = change.Oid;
                    record.BaseSize = change.Size;
                    record.BaseVersion = change.Version;
                    record.BaseCommit = _head.Commit;
                    record.SnapSize = file.Length;
                    record.SnapMtime = file.LastWriteTimeUtc.Ticks;
                }
                record.HashOid = change.Oid;
                record.HashSize = file.Length;
                record.HashMtime = file.LastWriteTimeUtc.Ticks;
                State.UpsertFile(record);
                break;
            }
            case StagedAction.Delete:
                if (File.Exists(local))
                {
                    SetReadOnlyAttribute(local, false);
                    File.Delete(local);
                }
                State.DeleteFile(change.VaultPath);
                break;
            case StagedAction.SetReadOnly:
            case StagedAction.SetWritable:
                if (File.Exists(local)) SetReadOnlyAttribute(local, change.Action == StagedAction.SetReadOnly);
                break;
        }
    }

    internal static void SetReadOnlyAttribute(string path, bool readOnly)
    {
        var attributes = File.GetAttributes(path);
        var updated = readOnly ? attributes | FileAttributes.ReadOnly : attributes & ~FileAttributes.ReadOnly;
        if (updated != attributes) File.SetAttributes(path, updated);
    }

    /// <summary>Records the file's current content as the base for a freshly checked-in version.</summary>
    internal void RecordCheckedIn(string vaultPath, string oid, long size, int version, string commit)
    {
        var local = LocalPathOf(vaultPath);
        var record = State.GetFile(vaultPath) ?? new FileRecord { Path = vaultPath };
        record.Path = vaultPath;
        record.BaseOid = oid;
        record.BaseSize = size;
        record.BaseVersion = version;
        record.BaseCommit = commit;
        if (File.Exists(local))
        {
            var file = new FileInfo(local);
            record.SnapSize = file.Length;
            record.SnapMtime = file.LastWriteTimeUtc.Ticks;
            record.HashOid = oid;
            record.HashSize = file.Length;
            record.HashMtime = file.LastWriteTimeUtc.Ticks;
        }
        State.UpsertFile(record);
    }
}
