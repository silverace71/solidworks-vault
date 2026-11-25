using System.Text;
using SwVault.Core.Git;
using SwVault.Core.Index;
using SwVault.Core.State;
using SwVault.Core.Util;
using SwVault.Core.Vault;
using SwVault.Protocol;

namespace SwVault.Core;

public sealed partial class VaultSession
{
    /// <summary>Makes every file LFS-stored and lockable for plain git-lfs users too; SwVault's own files stay text.</summary>
    public const string GitAttributes =
        "# Managed by SwVault. All vault files are stored in Git LFS and are lockable (check-out = LFS lock).\n" +
        "* filter=lfs diff=lfs merge=lfs -text lockable\n" +
        ".gitattributes -filter -diff -merge text -lockable\n" +
        ".swvault/** -filter -diff -merge text -lockable\n" +
        ".swvault/thumbs/** -filter -diff -merge -text -lockable\n";

    internal sealed record VersionEntry(CommitInfo Commit, FileMeta Meta);

    // ---------------------------------------------------------------- history

    public async Task<IReadOnlyList<VersionInfoDto>> GetHistoryAsync(string path, CancellationToken ct = default)
    {
        var vaultPath = ResolveVaultPath(path) ?? throw VaultException.BadRequest($"{path} is outside the vault folder.");
        await TrySyncAsync(refreshLocks: false, ct).ConfigureAwait(false);
        var entries = await LoadHistoryAsync(vaultPath, ct).ConfigureAwait(false);
        return entries.Select((e, i) =>
        {
            var older = i + 1 < entries.Count ? entries[i + 1].Meta : null;
            var isNewVersion = older == null || older.Version != e.Meta.Version || older.Oid != e.Meta.Oid;
            var released = e.Meta.ApprovedAt != null && e.Meta.ApprovedAt != older?.ApprovedAt;
            return new VersionInfoDto
            {
                Version = e.Meta.Version,
                Commit = e.Commit.Sha,
                Oid = e.Meta.Oid,
                Size = e.Meta.Size,
                By = isNewVersion && e.Meta.CheckedInBy != null ? e.Meta.CheckedInBy : e.Commit.AuthorName.Length > 0 ? e.Commit.AuthorName : e.Meta.CheckedInBy,
                At = e.Commit.Date.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ", System.Globalization.CultureInfo.InvariantCulture),
                Comment = VersionNote(isNewVersion, e.Meta, e.Commit.Subject),
                State = e.Meta.State,
                Revision = e.Meta.Revision,
                Properties = e.Meta.Properties == null ? null : new Dictionary<string, string>(e.Meta.Properties),
                References = e.Meta.References?.Select(r => new ReferenceDto { Path = r.Path, Version = r.Version }).ToArray(),
                Configurations = e.Meta.Configurations?.ToArray(),
                ApprovedBy = released ? e.Meta.ApprovedBy : null,
                ApprovalNote = released ? e.Meta.ApprovalNote : null,
                IsNewVersion = isNewVersion,
            };
        }).ToList();
    }

    /// <summary>
    /// What the history shows for a commit. A new version shows the note saved with it (in full; git's
    /// subject is only the first paragraph), or "Version N" when it was checked in without one. A
    /// workflow change keeps the older note in the sidecar, so it shows its own commit subject.
    /// </summary>
    internal static string VersionNote(bool isNewVersion, FileMeta meta, string subject) =>
        isNewVersion ? (string.IsNullOrWhiteSpace(meta.Comment) ? DefaultNote(null, meta.Version) : meta.Comment.Replace("\r\n", "\n").Trim()) : subject;

    internal async Task<IReadOnlyList<VersionEntry>> LoadHistoryAsync(string vaultPath, CancellationToken ct)
    {
        var canonical = _head.Get(vaultPath)?.Path ?? PathRules.Normalize(vaultPath);
        var list = new List<VersionEntry>();

        // Newest first along this path; where the file was moved here, carry on at the path it came from.
        var path = canonical;
        string? before = null; // at an earlier path: only what happened before the file left it
        for (var hop = 0; hop < 20; hop++)
        {
            var metaPath = PathRules.MetaPathFor(path);
            var log = await Mirror.LogPathAsync(metaPath, 1000, ct).ConfigureAwait(false);
            var start = 0;
            if (before != null)
            {
                start = log.Count;
                for (var i = 0; i < log.Count; i++)
                    if (log[i].Sha == before) { start = i + 1; break; }
            }

            string? movedFrom = null;
            string? arrived = null; // the commit that moved the file to this path
            var first = true;
            for (var i = start; i < log.Count; i++)
            {
                var bytes = await Mirror.ReadBlobAsync($"{log[i].Sha}:{metaPath}", ct).ConfigureAwait(false);
                var meta = bytes == null ? null : HeadIndex.TryParseMeta(Encoding.UTF8.GetString(bytes));
                if (meta == null || meta.Oid.Length == 0)
                {
                    if (arrived != null) break; // an earlier file that had this name
                    continue;                   // the commit that deleted the file
                }
                if (arrived != null && !string.Equals(meta.MovedFrom, movedFrom, PathRules.Comparison)) break; // the same
                var entry = new VersionEntry(log[i], meta);
                // The move itself made no new version: show the check-in it carried over instead.
                if (first && hop > 0 && list.Count > 0 && list[^1].Meta.Version == meta.Version && list[^1].Meta.Oid == meta.Oid) list[^1] = entry;
                else list.Add(entry);
                first = false;
                if (meta.MovedFrom != null)
                {
                    movedFrom = meta.MovedFrom;
                    arrived = log[i].Sha;
                }
            }
            if (movedFrom == null || arrived == null || string.Equals(movedFrom, path, PathRules.Comparison)) break;
            path = movedFrom;
            before = arrived;
        }
        if (list.Count > 0) return list;

        // Added outside SwVault (no sidecar): derive versions from the pointer's own history.
        var commits = await Mirror.LogPathAsync(canonical, 1000, ct).ConfigureAwait(false);
        for (var i = 0; i < commits.Count; i++)
        {
            var bytes = await Mirror.ReadBlobAsync($"{commits[i].Sha}:{canonical}", ct).ConfigureAwait(false);
            if (bytes == null || !LfsPointer.TryParse(bytes, out var pointer)) continue;
            list.Add(new VersionEntry(commits[i], new FileMeta
            {
                Version = commits.Count - i,
                Oid = pointer.Oid,
                Size = pointer.Size,
                CheckedInBy = commits[i].AuthorName,
                CheckedInAt = commits[i].Date.ToString("O"),
            }));
        }
        return list;
    }

    internal async Task<VersionEntry> FindVersionAsync(string vaultPath, int version, CancellationToken ct)
    {
        var history = await LoadHistoryAsync(vaultPath, ct).ConfigureAwait(false);
        return history.FirstOrDefault(h => h.Meta.Version == version)
            ?? throw VaultException.NotFound($"Version {version} of {vaultPath} was not found.");
    }

    internal async Task<long> SizeOfVersionAsync(string vaultPath, int version, string oid, CancellationToken ct)
    {
        var head = _head.Get(vaultPath);
        if (head?.Oid == oid) return head.Size;
        var history = await LoadHistoryAsync(vaultPath, ct).ConfigureAwait(false);
        return history.FirstOrDefault(h => h.Meta.Oid == oid)?.Meta.Size ?? -1;
    }

    // ---------------------------------------------------------------- references

    public async Task<ReferenceNodeDto?> GetReferenceTreeAsync(string path, bool recursive, CancellationToken ct = default)
    {
        var vaultPath = ResolveVaultPath(path);
        if (vaultPath == null) return null;
        var visited = new HashSet<string>(PathRules.Comparer);

        async Task<ReferenceNodeDto> BuildAsync(string p, int version, int depth)
        {
            var head = _head.Get(p);
            var canonical = head?.Path ?? p;
            var (state, record, _) = await ComputeLocalStateAsync(canonical, head, ct).ConfigureAwait(false);
            var node = new ReferenceNodeDto
            {
                Path = canonical,
                LocalPath = LocalPathOf(canonical),
                Version = version,
                Status = BuildStatus(canonical, head, state, record),
            };
            if (!visited.Add(canonical) || depth > 40) return node;
            var refs = head?.Meta?.References;
            if (refs != null && (recursive || depth == 0))
            {
                var children = new List<ReferenceNodeDto>();
                foreach (var r in refs) children.Add(await BuildAsync(r.Path, r.Version, depth + 1).ConfigureAwait(false));
                node.Children = children.ToArray();
            }
            return node;
        }

        return await BuildAsync(vaultPath, _head.Get(vaultPath)?.Version ?? 0, 0).ConfigureAwait(false);
    }

    public IReadOnlyList<string> WhereUsed(string path)
    {
        var vaultPath = ResolveVaultPath(path);
        return vaultPath != null && _head.WhereUsed.TryGetValue(vaultPath, out var parents) ? parents : Array.Empty<string>();
    }

    // ---------------------------------------------------------------- workflow

    public async Task<IReadOnlyList<TransitionOptionDto>> GetTransitionsAsync(string path, CancellationToken ct = default)
    {
        var user = await RequireUserAsync(ct).ConfigureAwait(false);
        var vaultPath = ResolveVaultPath(path);
        var head = vaultPath == null ? null : _head.Get(vaultPath);
        if (head == null) return Array.Empty<TransitionOptionDto>();
        return WorkflowEngine.Options(Config, head, user.Login, _head).Select(c => new TransitionOptionDto
        {
            Name = c.Transition.Name,
            To = c.Transition.To,
            BumpRevision = c.Transition.BumpRevision,
            NextRevision = c.NextRevision,
            Exports = c.Transition.Exports.ToArray(),
            ExportFolder = LocalPathOf(PathRules.Normalize(Config.ReleaseExportFolder.Replace("{folder}", PathRules.GetFolder(head.Path), StringComparison.OrdinalIgnoreCase))),
            Allowed = c.Allowed,
            Reason = c.Reason,
            Warnings = c.Warnings.Count > 0 ? c.Warnings.ToArray() : null,
        }).ToList();
    }

    /// <summary>
    /// Metadata-only state change (submit, reject, change request, or a release that doesn't need to
    /// write into the file). Releases that stamp the revision into the file go through CheckInAsync
    /// with a TransitionName instead.
    /// </summary>
    public async Task<(string? Commit, IReadOnlyList<string> Warnings)> TransitionAsync(
        IReadOnlyList<string> paths, string transitionName, string? comment, CancellationToken ct = default)
    {
        await _opGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var user = await RequireUserAsync(ct).ConfigureAwait(false);
            await SyncAsync(ct: ct).ConfigureAwait(false);
            var warnings = new List<string>();
            var items = new List<(HeadFile Head, TransitionCheck Check, bool HeldBefore)>();
            foreach (var raw in paths.Distinct(PathRules.Comparer))
            {
                var vaultPath = ResolveVaultPath(raw);
                var head = vaultPath == null ? null : _head.Get(vaultPath);
                if (head == null) throw VaultException.NotFound($"{raw} is not in the vault.");
                var record = State.GetFile(head.Path);
                var lockState = LockStateOf(head.Path, record, out var lockRecord);
                if (lockState == LockState.Other) throw VaultException.Conflict($"{head.Path} is checked out by {lockRecord!.Owner}.");
                if (lockState == LockState.MineElsewhere) throw VaultException.Conflict($"{head.Path} is checked out by you on another computer.");
                if (lockState == LockState.MineHere)
                {
                    var (state, _, _) = await ComputeLocalStateAsync(head.Path, head, ct).ConfigureAwait(false);
                    if (state != LocalState.UpToDate) throw VaultException.Conflict($"{head.Path} has changes that aren't checked in yet. Check it in first.");
                }
                var check = WorkflowEngine.Evaluate(Config, head, transitionName, user.Login, _head);
                if (!check.Allowed) throw VaultException.Forbidden($"{head.Path}: {check.Reason}");
                warnings.AddRange(check.Warnings);
                items.Add((head, check, lockState == LockState.MineHere));
            }

            var tempLocks = new List<string>();
            try
            {
                foreach (var item in items.Where(i => !i.HeldBefore))
                {
                    var created = await Locks.CreateAsync(item.Head.Path, ct).ConfigureAwait(false);
                    if (!created.Success) throw VaultException.Conflict($"{item.Head.Path} was just checked out by {created.Existing?.OwnerName}.");
                    SetLock(new LockRecord(created.Created!.Id, item.Head.Path, user.Login, created.Created.LockedAt, true));
                    tempLocks.Add(item.Head.Path);
                }

                var message = string.IsNullOrWhiteSpace(comment)
                    ? $"{transitionName}: {string.Join(", ", items.Select(i => PathRules.GetFileName(i.Head.Path)).Take(5))}{(items.Count > 5 ? "..." : "")}"
                    : comment!.Trim();
                message += $"\n\nWorkflow: {transitionName}\n";
                var now = NowIso();
                var commit = await CommitWithRetryAsync(head =>
                {
                    var plan = new CommitPlan();
                    foreach (var item in items)
                    {
                        var current = head.Get(item.Head.Path);
                        if (current == null || current.Oid != item.Head.Oid)
                            throw VaultException.Conflict($"{item.Head.Path} changed on the server; refresh and try again.");
                        var meta = current.Meta?.Clone() ?? new FileMeta { Version = current.Version, Oid = current.Oid, Size = current.Size };
                        WorkflowEngine.Apply(meta, item.Check.Transition, item.Check.NextRevision, user.Login, now, comment);
                        plan.Writes.Add((PathRules.MetaPathFor(current.Path), Encoding.UTF8.GetBytes(Json.Serialize(meta) + "\n")));
                    }
                    return plan;
                }, message, user, ct).ConfigureAwait(false);

                // A file moving into a read-only state (e.g. InReview) can't stay checked out.
                if (Config.IsReadOnlyState(items.FirstOrDefault().Check?.Transition.To))
                    foreach (var item in items.Where(i => i.HeldBefore)) await ReleaseAsync(item.Head.Path, ct).ConfigureAwait(false);
                return (commit, warnings);
            }
            finally
            {
                foreach (var path in tempLocks)
                {
                    try { await ReleaseAsync(path, CancellationToken.None).ConfigureAwait(false); }
                    catch (VaultException ex) { warnings.Add($"Could not release the temporary lock on {path}: {ex.Message}"); }
                }
            }
        }
        finally
        {
            _opGate.Release();
        }
    }

    // ---------------------------------------------------------------- delete & admin

    public async Task<(string? Commit, IReadOnlyList<string> Warnings)> DeleteAsync(
        IReadOnlyList<string> paths, string? comment, bool force, CancellationToken ct = default)
    {
        await _opGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var user = await RequireUserAsync(ct).ConfigureAwait(false);
            await SyncAsync(ct: ct).ConfigureAwait(false);
            var warnings = new List<string>();
            var targets = ExpandTargets(paths, withReferences: false, warnings);
            if (targets.Count == 0) throw VaultException.NotFound("Nothing to delete.");
            if (force && !Config.IsAdmin(user.Login)) throw VaultException.Forbidden("Only vault admins can delete files that other files still use.");

            var targetSet = new HashSet<string>(targets.Select(t => t.Path), PathRules.Comparer);
            foreach (var target in targets)
            {
                var parents = WhereUsed(target.Path).Where(p => !targetSet.Contains(p)).ToList();
                if (parents.Count > 0 && !force)
                    throw VaultException.Conflict($"{target.Path} is used by {string.Join(", ", parents.Take(5))}{(parents.Count > 5 ? "..." : "")}. Remove those references first.");
                if (LockStateOf(target.Path, State.GetFile(target.Path), out var l) == LockState.Other)
                    throw VaultException.Conflict($"{target.Path} is checked out by {l!.Owner}.");
            }

            foreach (var target in targets.Where(t => LockOf(t.Path) == null))
            {
                var created = await Locks.CreateAsync(target.Path, ct).ConfigureAwait(false);
                if (!created.Success) throw VaultException.Conflict($"{target.Path} was just checked out by {created.Existing?.OwnerName}.");
                SetLock(new LockRecord(created.Created!.Id, target.Path, user.Login, created.Created.LockedAt, true));
            }

            var message = (string.IsNullOrWhiteSpace(comment) ? $"Delete {targets.Count} file(s)" : comment!.Trim())
                + "\n\nDeleted:\n" + string.Join("\n", targets.Take(50).Select(t => "  " + t.Path)) + "\n";
            var commit = await CommitWithRetryAsync(head =>
            {
                var plan = new CommitPlan();
                foreach (var target in targets)
                {
                    var current = head.Get(target.Path);
                    if (current == null) continue;
                    plan.Deletes.Add(current.Path);
                    if (current.Meta != null) plan.Deletes.Add(PathRules.MetaPathFor(current.Path));
                    if (head.Thumbnails.Contains(current.Path)) plan.Deletes.Add(PathRules.ThumbPathFor(current.Path));
                }
                return plan;
            }, message, user, ct).ConfigureAwait(false);

            foreach (var target in targets)
            {
                var lockRecord = LockOf(target.Path);
                if (lockRecord is { Mine: true })
                {
                    await Locks.UnlockAsync(lockRecord.Id, force: false, ct).ConfigureAwait(false);
                    ClearLock(target.Path);
                }
                var local = LocalPathOf(target.Path);
                var record = State.GetFile(target.Path);
                if (File.Exists(local))
                {
                    var current = await CurrentOidAsync(target.Path, new FileInfo(local), record, ct).ConfigureAwait(false);
                    if (current == target.Oid)
                    {
                        SetReadOnlyAttribute(local, false);
                        File.Delete(local);
                    }
                    else
                    {
                        warnings.Add($"{target.Path} had local changes, so your local copy was kept.");
                    }
                }
                State.DeleteFile(target.Path);
            }
            return (commit, warnings);
        }
        finally
        {
            _opGate.Release();
        }
    }

    // ---------------------------------------------------------------- move

    /// <summary>
    /// Moves files to another vault folder in one commit. Each file keeps its content, version,
    /// state and history (its sidecar remembers where it came from), and the references other
    /// files' sidecars hold are pointed at the new place. Local copies move along.
    /// SOLIDWORKS files that use a moved file still store its old location; the add-in repoints them.
    /// </summary>
    /// <returns>The commit, and the new vault paths.</returns>
    public async Task<(string? Commit, IReadOnlyList<string> Moved, IReadOnlyList<string> Warnings)> MoveAsync(
        IReadOnlyList<string> paths, string targetFolder, string? comment, CancellationToken ct = default)
    {
        await _opGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var user = await RequireUserAsync(ct).ConfigureAwait(false);
            await SyncAsync(ct: ct).ConfigureAwait(false);
            var warnings = new List<string>();
            var folder = ResolveVaultPath(targetFolder) ?? throw VaultException.BadRequest($"{targetFolder} is outside the vault folder.");
            if (IsIgnored(PathRules.Combine(folder, "x"))) throw VaultException.BadRequest("Files can't be moved there.");
            if (_head.Get(folder) != null) throw VaultException.BadRequest($"{folder} is a file, not a folder.");

            var moves = new List<(HeadFile File, string To, bool MoveLocal)>();
            foreach (var file in ExpandTargets(paths, withReferences: false, warnings))
            {
                var name = PathRules.GetFileName(file.Path);
                var to = PathRules.Combine(folder, name);
                if (string.Equals(to, file.Path, PathRules.Comparison)) continue; // already there
                if (_head.Get(to) != null || moves.Any(m => string.Equals(m.To, to, PathRules.Comparison)))
                    throw VaultException.Conflict($"{(folder.Length == 0 ? "The top of the vault" : folder)} already has a file called {name}.");
                switch (LockStateOf(file.Path, State.GetFile(file.Path), out var l))
                {
                    case LockState.Other:
                        throw VaultException.Conflict($"{file.Path} is checked out by {l!.Owner}.");
                    case LockState.MineHere:
                    case LockState.MineElsewhere:
                        throw VaultException.Conflict($"{file.Path} is checked out by you. Check it in (or undo the check-out) before moving it.");
                }

                // The local copy moves along only when it is exactly the version in the vault.
                var local = LocalPathOf(file.Path);
                var moveLocal = false;
                if (File.Exists(local))
                {
                    var current = await CurrentOidAsync(file.Path, new FileInfo(local), State.GetFile(file.Path), ct).ConfigureAwait(false);
                    moveLocal = current == file.Oid;
                    if (!moveLocal) warnings.Add($"Your local copy of {file.Path} isn't the latest version, so it was left where it is. Get Latest brings the moved file.");
                }
                if (File.Exists(LocalPathOf(to)))
                    throw VaultException.Conflict($"A file called {name} that isn't in the vault is already in that folder on this PC. Move or rename it first.");
                moves.Add((file, to, moveLocal));
            }
            if (moves.Count == 0)
            {
                // Say which it is: already there, or not vault files at all (e.g. leftover copies of
                // files someone else moved or deleted, which the next refresh removes).
                var selected = paths.Select(ResolveVaultPath).Where(p => p != null).ToList();
                var notInVault = selected.Where(p => _head.Get(p!) == null && !_head.Files.Keys.Any(f => f.StartsWith(p + "/", PathRules.Comparison))).ToList();
                if (notInVault.Count > 0)
                    throw VaultException.NotFound(notInVault.Count == 1
                        ? $"{PathRules.GetFileName(notInVault[0]!)} isn't in the vault at that place any more: someone moved or deleted it. The copy on this PC is a leftover; Refresh removes it if you haven't changed it."
                        : $"These {notInVault.Count} files aren't in the vault at that place any more: someone moved or deleted them. The copies on this PC are leftovers; Refresh removes the ones you haven't changed.");
                throw VaultException.NotFound("Nothing to move: the files are already in that folder.");
            }

            var pointers = new Dictionary<string, byte[]>(PathRules.Comparer);
            foreach (var (file, _, _) in moves)
                pointers[file.Path] = await Mirror.ReadBlobAsync($"{_head.Commit}:{file.Path}", ct).ConfigureAwait(false)
                    ?? throw VaultException.NotFound($"{file.Path} is not in the vault any more.");

            // Hold the files while they move, so nobody checks one out half-way.
            var held = new List<string>();
            string? commit;
            try
            {
                foreach (var (file, _, _) in moves)
                {
                    var created = await Locks.CreateAsync(file.Path, ct).ConfigureAwait(false);
                    if (!created.Success) throw VaultException.Conflict($"{file.Path} was just checked out by {created.Existing?.OwnerName}.");
                    SetLock(new LockRecord(created.Created!.Id, file.Path, user.Login, created.Created.LockedAt, true));
                    held.Add(file.Path);
                }

                var map = moves.ToDictionary(m => m.File.Path, m => m.To, PathRules.Comparer);
                var message = (string.IsNullOrWhiteSpace(comment) ? $"Move {moves.Count} file(s) to {(folder.Length == 0 ? "the top of the vault" : folder)}" : comment!.Trim())
                    + "\n\nMoved:\n" + string.Join("\n", moves.Take(50).Select(m => $"  {m.File.Path} -> {m.To}")) + "\n";
                commit = await CommitWithRetryAsync(head =>
                {
                    var plan = new CommitPlan();
                    foreach (var (from, to) in map)
                    {
                        var current = head.Get(from);
                        if (current == null) continue;
                        var meta = current.Meta?.Clone() ?? new FileMeta { Version = current.Version, Oid = current.Oid, Size = current.Size };
                        meta.MovedFrom = current.Path;
                        Repoint(meta, map);
                        plan.Writes.Add((to, pointers[from]));
                        plan.Writes.Add((PathRules.MetaPathFor(to), Encoding.UTF8.GetBytes(Json.Serialize(meta) + "\n")));
                        plan.Deletes.Add(current.Path);
                        if (current.Meta != null) plan.Deletes.Add(PathRules.MetaPathFor(current.Path));
                    }
                    // Files that stay where they are but use a moved one.
                    foreach (var file in head.Files.Values)
                    {
                        if (map.ContainsKey(file.Path) || file.Meta == null) continue;
                        var meta = file.Meta.Clone();
                        if (Repoint(meta, map)) plan.Writes.Add((PathRules.MetaPathFor(file.Path), Encoding.UTF8.GetBytes(Json.Serialize(meta) + "\n")));
                    }
                    return plan;
                }, message, user, ct).ConfigureAwait(false);
            }
            finally
            {
                foreach (var path in held)
                {
                    try
                    {
                        if (LockOf(path) is { Mine: true } lockRecord) await Locks.UnlockAsync(lockRecord.Id, force: false, CancellationToken.None).ConfigureAwait(false);
                    }
                    catch (VaultException)
                    {
                        // The next sync shows it; "Undo Check Out" releases it.
                    }
                    ClearLock(path);
                }
            }

            foreach (var (file, to, moveLocal) in moves)
            {
                var record = State.GetFile(file.Path);
                State.DeleteFile(file.Path);
                if (!moveLocal) continue;
                var local = LocalPathOf(file.Path);
                var target = LocalPathOf(to);
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    SetReadOnlyAttribute(local, false);
                    File.Move(local, target);
                    SetReadOnlyAttribute(target, true);
                    if (record != null)
                    {
                        record.Path = to;
                        record.LockedHere = false;
                        State.UpsertFile(record);
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    warnings.Add($"{file.Path} moved in the vault, but your local copy couldn't be moved ({ex.Message}). Close the file, delete the old copy and Get Latest.");
                }
            }
            return (commit, moves.Select(m => m.To).ToList(), warnings);
        }
        finally
        {
            _opGate.Release();
        }
    }

    /// <summary>Points a sidecar's references at the moved files' new paths. True when anything changed.</summary>
    private static bool Repoint(FileMeta meta, IReadOnlyDictionary<string, string> map)
    {
        var changed = false;
        foreach (var reference in meta.References ?? new List<ReferenceEntry>())
        {
            if (!map.TryGetValue(reference.Path, out var to)) continue;
            reference.Path = to;
            changed = true;
        }
        if (meta.DerivedFrom != null && map.TryGetValue(meta.DerivedFrom, out var source))
        {
            meta.DerivedFrom = source;
            changed = true;
        }
        return changed;
    }

    /// <summary>Admin: release someone else's check-out (e.g. they left for the semester with files checked out).</summary>
    public async Task ForceUnlockAsync(string path, CancellationToken ct = default)
    {
        var user = await RequireUserAsync(ct).ConfigureAwait(false);
        if (!Config.IsAdmin(user.Login)) throw VaultException.Forbidden("Only vault admins can release other people's check-outs.");
        await SyncAsync(fetch: false, refreshLocks: true, ct).ConfigureAwait(false);
        var vaultPath = ResolveVaultPath(path) ?? throw VaultException.BadRequest($"{path} is outside the vault folder.");
        var lockRecord = LockOf(vaultPath) ?? throw VaultException.NotFound($"{vaultPath} is not checked out.");
        await Locks.UnlockAsync(lockRecord.Id, force: !lockRecord.Mine, ct).ConfigureAwait(false);
        ClearLock(vaultPath);
    }

    /// <summary>First commit of a new vault: .gitattributes and .swvault/vault.json.</summary>
    public async Task<string?> InitializeAsync(VaultConfig config, CancellationToken ct = default)
    {
        await _opGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var user = await RequireUserAsync(ct).ConfigureAwait(false);
            await SyncAsync(fetch: true, refreshLocks: false, ct).ConfigureAwait(false);
            if (_head.HasConfig) throw VaultException.Conflict("This vault is already initialized.");
            return await CommitWithRetryAsync(head =>
            {
                if (head.HasConfig) throw VaultException.Conflict("This vault is already initialized.");
                var plan = new CommitPlan();
                plan.Writes.Add((PathRules.GitAttributesPath, Encoding.UTF8.GetBytes(GitAttributes)));
                plan.Writes.Add((PathRules.ConfigPath, Encoding.UTF8.GetBytes(Json.Serialize(config) + "\n")));
                return plan;
            }, $"Initialize vault '{config.Name}'", user, ct).ConfigureAwait(false);
        }
        finally
        {
            _opGate.Release();
        }
    }

    public async Task<string?> UpdateConfigAsync(VaultConfig config, string? comment, CancellationToken ct = default)
    {
        await _opGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var user = await RequireUserAsync(ct).ConfigureAwait(false);
            await SyncAsync(fetch: true, refreshLocks: false, ct).ConfigureAwait(false);
            if (_head.HasConfig && !Config.IsAdmin(user.Login)) throw VaultException.Forbidden("Only vault admins can change vault settings.");
            return await CommitWithRetryAsync(_ =>
            {
                var plan = new CommitPlan();
                plan.Writes.Add((PathRules.ConfigPath, Encoding.UTF8.GetBytes(Json.Serialize(config) + "\n")));
                return plan;
            }, string.IsNullOrWhiteSpace(comment) ? "Update vault settings" : comment!, user, ct).ConfigureAwait(false);
        }
        finally
        {
            _opGate.Release();
        }
    }
}
