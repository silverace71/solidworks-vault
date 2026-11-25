using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using SwVault.AddIn.Infrastructure;
using SwVault.AddIn.Sw;
using SwVault.AddIn.Ui;
using SwVault.Protocol;

namespace SwVault.AddIn
{
    /// <summary>
    /// The PDM workflows as SOLIDWORKS users see them. Each one asks the agent to do the vault work
    /// and takes care of the documents open in this session (save before check-in, release before
    /// files are replaced, reload afterwards, keep the read-only state in sync).
    /// </summary>
    internal sealed class VaultCommands
    {
        private readonly SwDocs _docs;
        private readonly AgentConnection _agent;
        private readonly HashSet<string> _promptedDocs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _confirmedWarnings = new HashSet<string>(StringComparer.Ordinal);
        private ProgressWindow _progress;
        private string _progressTag;
        // One job at a time: a second one would open a second progress window and steal the first one's updates.
        private readonly System.Threading.SemaphoreSlim _jobGate = new System.Threading.SemaphoreSlim(1, 1);
        private const int ModifiedSettleMs = 400;
        private readonly Dictionary<string, IModelDoc2> _pendingModified = new Dictionary<string, IModelDoc2>(StringComparer.OrdinalIgnoreCase);
        private bool _modifiedBatchRunning;

        public VaultCommands(SwDocs docs, AgentConnection agent)
        {
            _docs = docs;
            _agent = agent;
            _agent.JobUpdated += job =>
            {
                if (_progress != null && job.Tag == _progressTag) _progress.Update(job);
            };
        }

        /// <summary>Raised after an operation so the task pane can refresh.</summary>
        public event Action<IEnumerable<string>> Changed;

        /// <summary>The folder selected in the task pane (a local path), where Add to Vault starts.</summary>
        public Func<string> DefaultFolder { get; set; }

        // ------------------------------------------------------------ job plumbing

        /// <summary>
        /// Runs a job end to end: shows progress, releases open documents the job will replace,
        /// applies, reloads, and syncs read-only flags. Throws AgentException on failure.
        /// </summary>
        private async Task<JobInfo> RunJobAsync(JobRequest request, string title)
        {
            await _jobGate.WaitAsync();
            try
            {
                await UiThread.OnUi();
                return await RunJobCoreAsync(request, title);
            }
            finally
            {
                _jobGate.Release();
            }
        }

        private async Task<JobInfo> RunJobCoreAsync(JobRequest request, string title)
        {
            request.Tag = Guid.NewGuid().ToString("N");
            _progressTag = request.Tag;
            // Downloads can be stopped: nothing has been changed on this PC until they finish.
            var canCancel = request.Kind == JobKind.GetLatest || request.Kind == JobKind.GetVersion;
            var progress = _progress = ProgressWindow.ShowFor(title, canCancel ? CancelJob : (Action<string>)null);
            JobInfo job;
            var released = new List<ReleasedDoc>();
            try
            {
                job = await _agent.StartJobAsync(request);
                if (job.State == JobState.ReadyToApply && progress.CancelRequested)
                {
                    // Cancel was clicked just as the download finished: drop it instead of applying it.
                    try { await _agent.CancelJobAsync(job.JobId); } catch (AgentException) { }
                    throw new OperationCanceledException();
                }
                if (job.State == JobState.ReadyToApply)
                {
                    progress.DisableCancel();
                    var blocked = new List<string>();
                    released = _docs.Release(job.Replacements ?? new string[0], blocked);
                    job = await _agent.ApplyJobAsync(job.JobId, blocked.ToArray());
                    if (blocked.Count > 0)
                        UiThread.ShowInfo("These files are open with unsaved changes, so they were not replaced:\n\n" + string.Join("\n", blocked.Select(Path.GetFileName)));
                }
            }
            finally
            {
                if (released.Count > 0) _docs.Restore(released);
                _progress?.Close();
                _progress = null;
                _progressTag = null;
            }

            if (job.State == JobState.Cancelled || progress.CancelRequested && job.State == JobState.Failed)
                throw new OperationCanceledException();
            if (job.State == JobState.Failed)
                throw new AgentException(job.ErrorCode == 0 ? ErrorCodes.Internal : job.ErrorCode, job.Error ?? "The operation did not complete.");

            _docs.SyncReadOnlyState((job.Affected ?? new string[0]).Concat(request.Paths ?? new string[0]));
            // Warnings the person already confirmed before the check-in aren't repeated after it.
            var news = (job.Warnings ?? new string[0]).Where(w => !_confirmedWarnings.Contains(w)).ToList();
            _confirmedWarnings.Clear();
            if (news.Count > 0)
                UiThread.ShowInfo(string.Join("\n\n", news));
            Changed?.Invoke(job.Affected ?? request.Paths ?? new string[0]);
            return job;
        }

        private async void CancelJob(string jobId)
        {
            try
            {
                await _agent.CancelJobAsync(jobId);
            }
            catch (AgentException ex)
            {
                Log.Info("Cancel: " + ex.Message); // it had just finished
            }
        }

        private static List<string> InVault(IEnumerable<FileStatusDto> statuses) => statuses.Select(s => s.LocalPath).ToList();

        // ------------------------------------------------------------ check out

        public async Task CheckOutAsync(IReadOnlyList<string> paths)
        {
            if (paths.Count == 0) return;
            var targets = paths.ToList();
            if (targets.Count == 1 && SwDocs.HasReferences(targets[0]))
            {
                var tree = await _agent.ReferencesAsync(targets[0]);
                if (tree?.Children != null && tree.Children.Length > 0)
                {
                    using (var dialog = new CheckOutDialog(tree))
                    {
                        if (dialog.ShowDialog(UiThread.Owner) != DialogResult.OK) return;
                        targets = dialog.SelectedPaths;
                    }
                    if (targets.Count == 0) return;
                }
            }

            var dirtyOutdated = await DirtyAndOutdatedAsync(targets);
            if (dirtyOutdated.Count > 0 && !UiThread.Confirm(
                    "A newer version exists for:\n\n" + string.Join("\n", dirtyOutdated.Select(Path.GetFileName)) +
                    "\n\nChecking out gets the newer version, and your unsaved changes to these files will be lost. Continue?"))
                return;

            // Already yours, from the website or another PC: carry on with it here.
            var held = (await _agent.GetStatusAsync(targets.ToArray())).Where(s => s.LockState == LockState.MineElsewhere).Select(s => s.LocalPath).ToList();
            await UiThread.OnUi();
            if (held.Count > 0 && !UiThread.Confirm(
                    "You already have these checked out, from the website or another computer:\n\n" + string.Join("\n", held.Take(10).Select(Path.GetFileName)) +
                    "\n\nEdit them on this computer? (If you changed them on another computer without checking in, those changes stay there.)"))
                return;

            await RunJobAsync(new JobRequest { Kind = JobKind.CheckOut, Paths = targets.ToArray(), Force = held.Count > 0 }, "Checking out");
        }

        private async Task<List<string>> DirtyAndOutdatedAsync(IEnumerable<string> paths)
        {
            var dirty = _docs.DirtyAmong(paths);
            if (dirty.Count == 0) return dirty;
            var statuses = await _agent.GetStatusAsync(dirty.ToArray());
            return statuses.Where(s => s.LocalState == LocalState.Outdated || s.LocalState == LocalState.Conflict).Select(s => s.LocalPath).ToList();
        }

        // ------------------------------------------------------------ check in

        /// <summary>Checks in the given files plus anything in their reference trees that you changed or added.</summary>
        public async Task CheckInAsync(IReadOnlyList<string> paths)
        {
            if (paths.Count == 0) return;
            // Parts inserted from outside the vault since the last check-in: copy them in first.
            var vaults = await _agent.GetVaultsAsync();
            foreach (var path in paths.Where(p => SwDocs.HasReferences(p) && _docs.FindOpen(p) != null))
            {
                var tree = WithReferences(path);
                var plan = await PlanOutsideReferencesAsync(path, tree, vaults);
                await UiThread.OnUi();
                if (plan == null) return;
                if (BringReferencesIntoVault(path, plan, vaults, null) == 0) continue;
                _docs.SaveDirty(_docs.DirtyWritable(tree.Where(c => VaultOf(vaults, c) != null)));
            }

            var candidates = new List<string>();
            foreach (var path in paths)
                foreach (var file in WithReferences(path))
                    if (!candidates.Contains(file, StringComparer.OrdinalIgnoreCase)) candidates.Add(file);

            var dirty = _docs.DirtyWritable(candidates);
            if (dirty.Count > 0)
            {
                if (!UiThread.Confirm("Save these files before checking in?\n\n" + string.Join("\n", dirty.Select(Path.GetFileName)))) return;
                var failed = _docs.SaveDirty(dirty);
                if (failed.Count > 0)
                {
                    UiThread.ShowError("These files could not be saved (are they checked out?):\n\n" + string.Join("\n", failed.Select(Path.GetFileName)));
                    return;
                }
            }

            var statuses = await _agent.GetStatusAsync(candidates.ToArray());
            var requested = new HashSet<string>(paths, StringComparer.OrdinalIgnoreCase);
            var outside = candidates.Where(c => !statuses.Any(s => string.Equals(s.LocalPath, c, StringComparison.OrdinalIgnoreCase))).ToList();
            var rows = statuses
                .Where(s => requested.Contains(s.LocalPath) || s.LocalState == LocalState.LocalOnly || s.LockState == LockState.MineHere)
                .Select(s => (Status: s, Checked: s.LockState == LockState.MineHere || s.LocalState == LocalState.LocalOnly))
                .ToList();
            if (rows.Count == 0)
            {
                UiThread.ShowInfo("Nothing to check in: these files are not checked out by you and have no new files.");
                return;
            }

            string comment;
            bool keep;
            List<string> selected;
            using (var dialog = new CheckInDialog(rows))
            {
                if (dialog.ShowDialog(UiThread.Owner) != DialogResult.OK) return;
                comment = dialog.Comment;
                keep = dialog.KeepCheckedOut;
                selected = dialog.SelectedPaths;
            }
            if (outside.Any(p => SwDocs.IsSolidWorksFile(p)) && !UiThread.Confirm(
                    "These referenced files are still outside the vault folder (SwVault copies them in only while the assembly is open, and a copy can fail; see the log), so teammates won't be able to open them:\n\n" +
                    string.Join("\n", outside.Take(10)) + "\n\nCheck in anyway?"))
                return;
            if (!await PreflightOkAsync(selected, "Check in")) return;

            await RunJobAsync(new JobRequest
            {
                Kind = JobKind.CheckIn,
                Paths = selected.ToArray(),
                Comment = comment,
                KeepCheckedOut = keep,
                Files = selected.Select(FileInfoFor).ToArray(),
            }, "Checking in");
        }

        // ------------------------------------------------------------ add to vault

        /// <summary>
        /// Add to Vault: asks where in the vault the document goes (project, subsystem, folder),
        /// saves it there, and checks it in together with any new files it references.
        /// </summary>
        public async Task AddToVaultAsync(IModelDoc2 doc)
        {
            if (doc == null) return;
            var vaults = await _agent.GetVaultsAsync();
            if (vaults.Length == 0)
            {
                UiThread.ShowInfo("Connect this PC to a vault first.");
                await SetupVaultAsync();
                vaults = await _agent.GetVaultsAsync();
                if (vaults.Length == 0) return;
            }

            var path = SwDocs.PathOf(doc);
            var originalFolder = string.IsNullOrEmpty(path) ? null : Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(path) && VaultOf(vaults, path) != null)
            {
                // Already checked in (or ignored): nothing to place; the messages below say so.
                var existing = (await _agent.GetStatusAsync(path)).FirstOrDefault();
                await UiThread.OnUi();
                if (existing != null && existing.LocalState == LocalState.LocalOnly)
                {
                    var target = await ChooseVaultLocationAsync(doc, vaults, path);
                    if (target == null) return;
                    if (!string.Equals(target, path, StringComparison.OrdinalIgnoreCase))
                    {
                        if (!_docs.SaveAs(doc, target))
                            throw new AgentException(ErrorCodes.Internal, "Could not save " + Path.GetFileName(target) + " in the vault folder.");
                        path = target;
                    }
                }
            }
            else
            {
                var target = await ChooseVaultLocationAsync(doc, vaults, path);
                if (target == null) return;
                if (!_docs.SaveAs(doc, target))
                    throw new AgentException(ErrorCodes.Internal, "Could not save " + Path.GetFileName(target) + " in the vault folder.");
                path = target;
            }

            var candidates = WithReferences(path);
            var plan = await PlanOutsideReferencesAsync(path, candidates, vaults);
            await UiThread.OnUi();
            if (plan == null) return;
            var brought = BringReferencesIntoVault(path, plan, vaults, originalFolder);
            if (brought > 0)
            {
                _docs.SaveDirty(_docs.DirtyWritable(candidates.Where(c => VaultOf(vaults, c) != null)));
                candidates = WithReferences(path);
            }

            var failed = _docs.SaveDirty(_docs.DirtyWritable(candidates));
            if (failed.Count > 0)
            {
                UiThread.ShowError("These files could not be saved:\n\n" + string.Join("\n", failed.Select(Path.GetFileName)));
                return;
            }

            var statuses = await _agent.GetStatusAsync(candidates.ToArray());
            var own = statuses.FirstOrDefault(s => string.Equals(s.LocalPath, path, StringComparison.OrdinalIgnoreCase));
            if (own == null) throw new AgentException(ErrorCodes.BadRequest, Path.GetFileName(path) + " is not in a vault folder.");
            if (own.LocalState == LocalState.Ignored)
            {
                UiThread.ShowInfo(Path.GetFileName(path) + " matches the vault's ignore list, so it can't be added.");
                return;
            }
            if (own.LocalState != LocalState.LocalOnly)
            {
                UiThread.ShowInfo(Path.GetFileName(path) + " is already in the vault." +
                                  (own.LockState == LockState.MineHere ? " Use Check In to upload your changes." : ""));
                return;
            }

            var toAdd = statuses.Where(s => s.LocalState == LocalState.LocalOnly).Select(s => s.LocalPath).ToList();
            var outside = candidates.Where(c => !statuses.Any(s => string.Equals(s.LocalPath, c, StringComparison.OrdinalIgnoreCase))).ToList();
            if (outside.Any(p => SwDocs.IsSolidWorksFile(p)) && !UiThread.Confirm(
                    "These referenced files are still outside the vault folder (SwVault copies them in only while the assembly is open, and a copy can fail; see the log), so teammates won't be able to open them:\n\n" +
                    string.Join("\n", outside.Take(10)) + "\n\nAdd to the vault anyway?"))
                return;

            if (!await PreflightOkAsync(toAdd, "Add to Vault")) return;

            var name = Path.GetFileName(path);
            string note;
            var caption = toAdd.Count == 1
                ? "Adding " + name + " to the vault as version 1."
                : "Adding " + name + " and " + (toAdd.Count - 1) + " new referenced file(s) to the vault as version 1.";
            using (var dialog = new VersionNoteDialog("Add to Vault", caption, "Added " + name))
            {
                if (dialog.ShowDialog(UiThread.Owner) != DialogResult.OK) return;
                note = dialog.Note;
            }
            await RunJobAsync(new JobRequest
            {
                Kind = JobKind.CheckIn,
                Paths = toAdd.ToArray(),
                Comment = note,
                Files = toAdd.Select(FileInfoFor).ToArray(),
            }, "Adding to vault");

            ((IFrame)_docs.App.Frame()).SetStatusBarText("SwVault: added " + name + (toAdd.Count > 1 ? " and " + (toAdd.Count - 1) + " referenced file(s)" : "") + " to the vault. Check it out to edit it again.");
        }

        /// <summary>
        /// The file plus everything it references, as stored on disk. Virtual components are left
        /// out: they are saved inside their assembly.
        /// </summary>
        private List<string> WithReferences(string path)
        {
            var list = new List<string> { path };
            foreach (var reference in _docs.References(path, traverse: true))
                if (!SwDocs.IsEmbedded(reference) && !list.Contains(reference, StringComparer.OrdinalIgnoreCase)) list.Add(reference);
            return list;
        }

        /// <summary>The files a document uses directly, looking through its virtual components to the files those use.</summary>
        private List<string> DirectReferences(string path)
        {
            var result = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { path };
            var pending = new Queue<string>();
            pending.Enqueue(path);
            while (pending.Count > 0)
                foreach (var reference in _docs.References(pending.Dequeue(), traverse: false))
                {
                    if (!seen.Add(reference)) continue;
                    if (SwDocs.IsEmbedded(reference)) pending.Enqueue(reference);
                    else result.Add(reference);
                }
            return result;
        }

        /// <summary>What to do with the files a document uses from outside the vault folder.</summary>
        private sealed class OutsidePlan
        {
            /// <summary>Saved into the vault next to the document.</summary>
            public List<string> Copy { get; } = new List<string>();

            /// <summary>Outside file -> the vault's own file of that name, used in its place.</summary>
            public Dictionary<string, string> Use { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            /// <summary>Which file refers to which entry of <see cref="Use"/>.</summary>
            public List<KeyValuePair<string, string>> Holders { get; } = new List<KeyValuePair<string, string>>();
        }

        private static bool IsOutside(VaultInfo[] vaults, string file) =>
            SwDocs.IsSolidWorksFile(file) && VaultOf(vaults, file) == null && File.Exists(file);

        /// <summary>
        /// Sorts the outside files <paramref name="path"/> uses into ones to copy into the vault and
        /// ones the vault already has under the same name (after asking whether to use the vault's).
        /// Null when the person cancels.
        /// </summary>
        private async Task<OutsidePlan> PlanOutsideReferencesAsync(string path, IReadOnlyList<string> candidates, VaultInfo[] vaults)
        {
            var plan = new OutsidePlan();
            var outside = candidates.Skip(1).Where(c => IsOutside(vaults, c)).ToList();
            var vault = VaultOf(vaults, path);
            if (vault == null)
            {
                plan.Copy.AddRange(outside);
                return plan;
            }

            // Each file's own list of what it uses can name outside files the open document's list
            // doesn't (a sub-assembly that stores another copy's location): those count too.
            var direct = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            Func<string, List<string>> uses = file =>
            {
                List<string> list;
                if (!direct.TryGetValue(file, out list)) direct[file] = list = DirectReferences(file);
                return list;
            };
            // What a vault file that isn't checked out uses is that file's business: it can't be
            // pointed anywhere else, so a copy of its outside parts would be used by nothing.
            Func<string, bool> isFixed = file => !string.Equals(file, path, StringComparison.OrdinalIgnoreCase)
                && VaultOf(vaults, file) != null && File.Exists(file) && File.GetAttributes(file).HasFlag(FileAttributes.ReadOnly);
            var ours = new HashSet<string>(StringComparer.OrdinalIgnoreCase);   // reached through files that can be saved
            var theirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase); // reached through fixed files
            var pending = new Queue<KeyValuePair<string, bool>>();
            pending.Enqueue(new KeyValuePair<string, bool>(path, false));
            ours.Add(path);
            while (pending.Count > 0)
            {
                var item = pending.Dequeue();
                // An outside file under a fixed one stays theirs; a checked-out vault file below is ours again.
                var below = isFixed(item.Key) || (item.Value && VaultOf(vaults, item.Key) == null);
                foreach (var reference in uses(item.Key))
                    if ((below ? theirs : ours).Add(reference) && !(below && ours.Contains(reference)))
                        pending.Enqueue(new KeyValuePair<string, bool>(reference, below));
            }
            foreach (var left in outside.Concat(theirs).Distinct(StringComparer.OrdinalIgnoreCase).Where(f => IsOutside(vaults, f) && theirs.Contains(f) && !ours.Contains(f)))
                Log.Info("Outside file " + left + ": only used by vault files that aren't checked out, so it is left where it is");
            outside.RemoveAll(f => theirs.Contains(f) && !ours.Contains(f));
            var loaded = outside.ToList(); // what the open document itself uses
            outside.AddRange(ours.Where(f => IsOutside(vaults, f) && !loaded.Contains(f, StringComparer.OrdinalIgnoreCase)));
            if (outside.Count == 0) return plan;

            // Files of the same name that are checked in (not just lying in the vault folder).
            var root = vault.LocalRoot.TrimEnd('\\');
            var names = new HashSet<string>(outside.Select(Path.GetFileName), StringComparer.OrdinalIgnoreCase);
            var sameName = Directory.EnumerateFiles(root, "*.sld*", SearchOption.AllDirectories)
                .Where(f => names.Contains(Path.GetFileName(f)) && f.IndexOf("\\.", root.Length, StringComparison.Ordinal) < 0)
                .ToList();
            var inVault = new List<string>();
            if (sameName.Count > 0)
            {
                var statuses = await _agent.GetStatusAsync(sameName.ToArray());
                await UiThread.OnUi();
                inVault.AddRange(statuses
                    .Where(s => s.LocalState != LocalState.LocalOnly && s.LocalState != LocalState.Ignored && s.LocalState != LocalState.DeletedOnServer && File.Exists(s.LocalPath))
                    .Select(s => s.LocalPath));
            }

            // With several of that name, the one closest to where the document is going.
            var folder = Path.GetDirectoryName(path);
            var match = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var file in outside)
            {
                var best = inVault.Where(v => string.Equals(Path.GetFileName(v), Path.GetFileName(file), StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(v => SharedFolders(v, folder))
                    .ThenBy(v => v, StringComparer.OrdinalIgnoreCase)
                    .FirstOrDefault();
                if (best != null) match[file] = best;
            }
            if (match.Count == 0)
            {
                plan.Copy.AddRange(loaded);
                return plan;
            }

            // The vault's file comes with its own references, so what an outside copy uses doesn't count.
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { path };
            Action<string> visit = null;
            visit = file =>
            {
                var fixedHolder = isFixed(file);
                foreach (var reference in uses(file))
                {
                    string existing;
                    if (match.TryGetValue(reference, out existing))
                    {
                        if (fixedHolder) continue;
                        plan.Use[reference] = existing;
                        plan.Holders.Add(new KeyValuePair<string, string>(file, reference));
                        continue;
                    }
                    if (IsOutside(vaults, reference) && !ours.Contains(reference)) continue; // left where it is
                    if (!seen.Add(reference)) continue;
                    if (IsOutside(vaults, reference)) plan.Copy.Add(reference);
                    visit(reference);
                }
            };
            visit(path);
            foreach (var use in plan.Use) Log.Info("Outside file " + use.Key + ": the vault has it as " + use.Value);
            foreach (var copy in plan.Copy) Log.Info("Outside file " + copy + ": no checked-in file of that name on this PC, so it is copied in");
            if (plan.Use.Count == 0) return plan;

            var name = Path.GetFileName(path);
            var answer = MessageBox.Show(UiThread.Owner,
                "The vault already has " + (plan.Use.Count == 1 ? "a file" : plan.Use.Count + " files") + " that " + name + " uses from outside the vault folder:\n\n" +
                string.Join("\n", plan.Use.Take(12).Select(u => Path.GetFileName(u.Key) + "   (vault: " + VaultFolderOf(root, u.Value) + ")")) +
                (plan.Use.Count > 12 ? "\n... and " + (plan.Use.Count - 12) + " more" : "") +
                "\n\nYes: use the vault's files. Nothing is copied, and " + name + " is closed and reopened to point at them. Changes that are only in your outside copies are not included." +
                "\nNo: copy the outside files into the vault next to " + name + " as new files.",
                "SwVault", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
            if (answer == DialogResult.Cancel) return null;
            if (answer == DialogResult.No)
            {
                Log.Info("Asked to copy the outside files in instead of using the vault's.");
                plan = new OutsidePlan();
                plan.Copy.AddRange(loaded);
            }
            return plan;
        }

        /// <summary>A file's folder as the vault names it, e.g. "E1\Tractive Battery".</summary>
        private static string VaultFolderOf(string root, string file)
        {
            var folder = Path.GetDirectoryName(file);
            return folder.Length > root.Length ? folder.Substring(root.Length + 1) : "top of the vault";
        }

        /// <summary>How many leading folders two paths have in common.</summary>
        private static int SharedFolders(string file, string folder)
        {
            var a = Path.GetDirectoryName(file).Split('\\');
            var b = folder.Split('\\');
            var n = 0;
            while (n < a.Length && n < b.Length && string.Equals(a[n], b[n], StringComparison.OrdinalIgnoreCase)) n++;
            return n;
        }

        /// <summary>
        /// Saves the SOLIDWORKS files a document uses from outside the vault into the vault, next to the
        /// document (keeping their subfolders when they were under <paramref name="originalFolder"/>).
        /// It's a Save As in this session, so the open assemblies and drawings then point at the copies;
        /// the caller saves them. Files the vault already has are not copied: the document is pointed
        /// at the vault's. Returns how many references were brought in either way.
        /// </summary>
        private int BringReferencesIntoVault(string path, OutsidePlan plan, VaultInfo[] vaults, string originalFolder)
        {
            if (plan.Copy.Count == 0 && plan.Holders.Count == 0) return 0;

            var folder = Path.GetDirectoryName(path);
            // Parts first, then sub-assemblies deepest first, so each one is saved already pointing at the copies it uses.
            var ordered = plan.Copy.Select((p, i) => new { Path = p, Index = i })
                .OrderBy(x => SwDocs.TypeFromExtension(x.Path) == (int)swDocumentTypes_e.swDocPART ? 0 : 1)
                .ThenByDescending(x => x.Index)
                .Select(x => x.Path)
                .ToList();
            var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var copies = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var frame = (IFrame)_docs.App.Frame();
            foreach (var source in ordered)
            {
                var target = FreeName(TargetFor(source, folder, originalFolder), taken);
                taken.Add(target);
                frame.SetStatusBarText("SwVault: copying " + Path.GetFileName(source) + " into the vault...");
                var doc = _docs.FindOpen(source);
                var openedHere = doc == null;
                if (openedHere) doc = _docs.Open(source, silent: true);
                if (doc == null || !_docs.SaveAs(doc, target))
                {
                    Log.Warn("Couldn't bring " + source + " into the vault as " + target);
                    continue;
                }
                Log.Info("Brought " + source + " into the vault as " + target);
                copies[source] = target;
                if (openedHere && !ReferenceEquals(doc, _docs.ActiveDoc)) _docs.Close(doc);
            }
            if (copies.Count > 0) frame.SetStatusBarText("SwVault: copied " + copies.Count + " referenced file(s) into the vault.");

            // A holder that was itself outside is now its copy; one that couldn't be copied is skipped.
            var swaps = new List<Tuple<string, string, string>>();
            foreach (var holder in plan.Holders)
            {
                string file;
                if (!copies.TryGetValue(holder.Key, out file)) file = VaultOf(vaults, holder.Key) != null ? holder.Key : null;
                if (file != null) swaps.Add(Tuple.Create(file, holder.Value, plan.Use[holder.Value]));
            }
            return copies.Count + UseVaultFiles(path, swaps, vaults);
        }

        /// <summary>
        /// Points files (holder, outside file, vault file) at the vault's files. SOLIDWORKS can't swap
        /// a loaded document for another of the same name, so <paramref name="path"/> is saved, closed,
        /// changed on disk and reopened. Returns how many references were changed.
        /// </summary>
        private int UseVaultFiles(string path, List<Tuple<string, string, string>> swaps, VaultInfo[] vaults)
        {
            var readOnly = swaps.Where(s => File.Exists(s.Item1) && File.GetAttributes(s.Item1).HasFlag(FileAttributes.ReadOnly)).ToList();
            foreach (var s in readOnly) Log.Warn("Can't point " + s.Item1 + " at " + s.Item3 + ": it is read-only (not checked out).");
            swaps = swaps.Except(readOnly).ToList();
            var top = _docs.FindOpen(path);
            if (swaps.Count == 0 || top == null) return 0;

            // Closing discards unsaved work: save what the vault folder holds. (Read-only files can't
            // be saved anyway, and the outside copies are the ones being given up.)
            _docs.SaveDirty(_docs.DirtyWritable(WithReferences(path).Where(c => VaultOf(vaults, c) != null)));
            var unsaved = _docs.DirtyAmong(swaps.Select(s => s.Item1).Concat(new[] { path }).Distinct(StringComparer.OrdinalIgnoreCase));
            if (unsaved.Count > 0)
                throw new AgentException(ErrorCodes.BadRequest,
                    "These files could not be saved. Save them, then try again: SwVault has to close and reopen " + Path.GetFileName(path) + " to point it at the files the vault already has:\n\n" +
                    string.Join("\n", unsaved.Take(10).Select(Path.GetFileName)));

            var frame = (IFrame)_docs.App.Frame();
            frame.SetStatusBarText("SwVault: pointing " + Path.GetFileName(path) + " at the files the vault already has...");
            _docs.Close(top);
            var count = 0;
            var stillOpen = swaps.SelectMany(s => new[] { s.Item1, s.Item2 }).Where(f => _docs.FindOpen(f) != null)
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (stillOpen.Count == 0)
                foreach (var swap in swaps)
                {
                    // The path as the holder stores it; normally the outside file itself.
                    var stored = _docs.StoredReferences(swap.Item1)
                        .Where(r => string.Equals(Path.GetFileName(r), Path.GetFileName(swap.Item2), StringComparison.OrdinalIgnoreCase)
                                    && !string.Equals(r, swap.Item3, StringComparison.OrdinalIgnoreCase))
                        .DefaultIfEmpty(swap.Item2).Distinct(StringComparer.OrdinalIgnoreCase);
                    foreach (var old in stored)
                        if (_docs.App.ReplaceReferencedDocument(swap.Item1, old, swap.Item3))
                        {
                            Log.Info("Pointed " + swap.Item1 + " at " + swap.Item3 + " (was " + old + ")");
                            count++;
                        }
                        else Log.Warn("ReplaceReferencedDocument failed: " + swap.Item1 + " : " + old + " -> " + swap.Item3);
                }

            if (_docs.Open(path, silent: true) == null)
                throw new AgentException(ErrorCodes.Internal, Path.GetFileName(path) + " was saved in the vault folder but could not be reopened. Open it and try again.");
            if (stillOpen.Count > 0)
                throw new AgentException(ErrorCodes.BadRequest,
                    "Close these documents in SOLIDWORKS, then try again. While they are open, " + Path.GetFileName(path) + " can't be pointed at the files the vault already has:\n\n" +
                    string.Join("\n", stillOpen.Take(10).Select(Path.GetFileName)));
            frame.SetStatusBarText("SwVault: " + Path.GetFileName(path) + " now uses " + count + " file(s) the vault already has.");
            return count;
        }

        private static string TargetFor(string source, string folder, string originalFolder)
        {
            if (!string.IsNullOrEmpty(originalFolder) &&
                source.StartsWith(originalFolder.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase))
                return Path.Combine(folder, source.Substring(originalFolder.TrimEnd('\\').Length + 1));
            return Path.Combine(folder, Path.GetFileName(source));
        }

        /// <summary>The target, or "Name (2).SLDPRT" etc. when a different file already has that name.</summary>
        private static string FreeName(string target, ICollection<string> taken)
        {
            var candidate = target;
            for (var n = 2; File.Exists(candidate) || taken.Contains(candidate); n++)
                candidate = Path.Combine(Path.GetDirectoryName(target), Path.GetFileNameWithoutExtension(target) + " (" + n + ")" + Path.GetExtension(target));
            return candidate;
        }

        private static VaultInfo VaultOf(IEnumerable<VaultInfo> vaults, string path) =>
            vaults.FirstOrDefault(v => !string.IsNullOrEmpty(v.LocalRoot)
                && path.StartsWith(v.LocalRoot.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase));

        /// <summary>Asks where in a vault folder to save a document that isn't in one yet.</summary>
        private async Task<string> ChooseVaultLocationAsync(IModelDoc2 doc, VaultInfo[] vaults, string currentPath)
        {
            var extension = SwDocs.ExtensionFor(doc);
            var fileName = string.IsNullOrEmpty(currentPath) ? Path.GetFileNameWithoutExtension(doc.GetTitle() ?? "") + extension : Path.GetFileName(currentPath);
            // Start where the file already is (when it's in the vault folder), else in the folder
            // picked in the task pane (e.g. a subsystem's), else at the top of the vault.
            var folder = !string.IsNullOrEmpty(currentPath) && VaultOf(vaults, currentPath) != null ? Path.GetDirectoryName(currentPath) : DefaultFolder?.Invoke();
            var vault = string.IsNullOrEmpty(folder) ? null : VaultOf(vaults, folder.TrimEnd('\\') + "\\x");
            if (vault == null)
            {
                vault = vaults[0];
                folder = vault.LocalRoot;
            }
            var root = vault.LocalRoot.TrimEnd('\\');
            var selected = folder.TrimEnd('\\').Length > root.Length ? folder.TrimEnd('\\').Substring(root.Length + 1).Replace('\\', '/') : "";

            var folders = await AllFoldersAsync(vault);

            while (true)
            {
                string target;
                using (var dialog = new AddToVaultDialog(vault.Name, root, folders, selected, fileName, extension))
                {
                    if (dialog.ShowDialog(UiThread.Owner) != DialogResult.OK) return null;
                    target = dialog.TargetPath;
                    selected = dialog.Folder;
                    if (selected.Length > 0 && !folders.Contains(selected, StringComparer.OrdinalIgnoreCase)) folders.Add(selected);
                }
                fileName = Path.GetFileName(target);
                Directory.CreateDirectory(Path.GetDirectoryName(target));

                if (string.Equals(target, currentPath, StringComparison.OrdinalIgnoreCase)) return target; // stays where it is
                if (target.Length > MaxPath)
                {
                    UiThread.ShowError("That path is " + target.Length + " characters long, and SOLIDWORKS can't open files past about " + MaxPath + ". Choose a shorter file name or a folder nearer the top of the vault.");
                    continue;
                }
                var sameName = await SameNameElsewhereAsync(vault, target);
                if (sameName != null && !UiThread.Confirm("There's already a " + fileName + " in the vault, at " + sameName + ".\n\n" +
                        "SOLIDWORKS finds parts by file name, so assemblies can end up opening the wrong one. A unique name (e.g. with the part number) avoids that.\n\n" +
                        "Use the name " + fileName + " anyway?"))
                    continue;
                if (File.Exists(target))
                {
                    var existing = (await _agent.GetStatusAsync(target)).FirstOrDefault();
                    if (existing != null && existing.LocalState != LocalState.LocalOnly)
                    {
                        UiThread.ShowError(fileName + " is already in the vault. Choose a different name.");
                        continue;
                    }
                    if (!UiThread.Confirm(fileName + " already exists in that folder. Replace it?")) continue;
                }
                return target;
            }
        }

        /// <summary>Every folder in the vault, top down (projects, subsystems and the folders inside them).</summary>
        private async Task<List<string>> AllFoldersAsync(VaultInfo vault)
        {
            var folders = new List<string>();
            var pending = new Queue<string>();
            pending.Enqueue("");
            while (pending.Count > 0 && folders.Count < 5000)
            {
                foreach (var entry in (await _agent.ListFolderAsync(vault.Id, pending.Dequeue())).Where(e => e.IsFolder))
                {
                    folders.Add(entry.Path);
                    pending.Enqueue(entry.Path);
                }
            }
            await UiThread.OnUi();
            return folders;
        }

        // ------------------------------------------------------------ move

        /// <summary>
        /// Moves checked-in files to another vault folder (their history goes along), then points the
        /// assemblies and drawings that use them at the new place: those are checked out, repointed
        /// while closed, and checked back in.
        /// </summary>
        public async Task MoveAsync(IReadOnlyList<string> paths)
        {
            if (paths == null || paths.Count == 0) return;
            var vaults = await _agent.GetVaultsAsync();
            var vault = VaultOf(vaults, paths[0]);
            if (vault == null) throw new AgentException(ErrorCodes.BadRequest, Path.GetFileName(paths[0]) + " is not in a vault folder.");
            var root = vault.LocalRoot.TrimEnd('\\');

            var statuses = await _agent.GetStatusAsync(paths.ToArray());
            await UiThread.OnUi();
            var leftovers = statuses.Where(s => s.LocalState == LocalState.DeletedOnServer).Select(s => Path.GetFileName(s.LocalPath)).ToList();
            if (leftovers.Count > 0)
            {
                UiThread.ShowInfo("These were moved or deleted in the vault by someone else; what you selected are leftover copies on this PC, so there is nothing to move:\n\n"
                                  + string.Join("\n", leftovers.Take(10)) + (leftovers.Count > 10 ? "\n... and " + (leftovers.Count - 10) + " more" : "")
                                  + "\n\nRefresh removes the leftovers you haven't changed. Use Get Latest on the folder they moved to for the current files.");
                return;
            }
            var notInVault = statuses.Where(s => s.LocalState == LocalState.LocalOnly).Select(s => Path.GetFileName(s.LocalPath)).ToList();
            if (notInVault.Count > 0)
            {
                UiThread.ShowInfo("These files aren't in the vault yet, so there is nothing to move there. Use Add to Vault and pick the folder:\n\n" + string.Join("\n", notInVault.Take(10)));
                return;
            }
            var checkedOut = statuses.Where(s => s.LockState != LockState.None).ToList();
            if (checkedOut.Count > 0)
            {
                UiThread.ShowInfo("Checked-out files can't be moved. Check these in (or undo the check-out) first:\n\n" +
                                  string.Join("\n", checkedOut.Take(10).Select(s => Path.GetFileName(s.LocalPath) + (s.LockState == LockState.Other ? "  (checked out by " + s.LockOwner + ")" : ""))));
                return;
            }

            // Where to.
            var currentFolder = Path.GetDirectoryName(paths[0]).TrimEnd('\\');
            var selected = currentFolder.Length > root.Length ? currentFolder.Substring(root.Length + 1).Replace('\\', '/') : "";
            var folders = await AllFoldersAsync(vault);
            string targetFolder, targetVaultFolder;
            using (var dialog = new AddToVaultDialog(vault.Name, root, folders, selected, null, null, "Move",
                       "Choose the folder to move " + (paths.Count == 1 ? Path.GetFileName(paths[0]) : paths.Count + " files") + " to. Their history goes along."))
            {
                if (dialog.ShowDialog(UiThread.Owner) != DialogResult.OK) return;
                targetFolder = dialog.LocalFolderPath;
                targetVaultFolder = dialog.Folder;
            }
            var moving = paths.Where(p => !string.Equals(Path.GetDirectoryName(p).TrimEnd('\\'), targetFolder.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)).ToList();
            if (moving.Count == 0)
            {
                UiThread.ShowInfo("The files are already in that folder.");
                return;
            }

            // Assemblies and drawings that use the files and stay where they are store the old location.
            var movingSet = new HashSet<string>(moving, StringComparer.OrdinalIgnoreCase);
            var parents = new List<string>();
            foreach (var path in moving)
                foreach (var parent in await _agent.WhereUsedAsync(path))
                {
                    var local = Path.IsPathRooted(parent) ? parent : Path.Combine(root, parent.Replace('/', '\\'));
                    if (!movingSet.Contains(local) && !parents.Contains(local, StringComparer.OrdinalIgnoreCase)) parents.Add(local);
                }
            await UiThread.OnUi();

            var open = moving.Concat(parents).Where(p => _docs.FindOpen(p) != null).ToList();
            if (open.Count > 0)
            {
                UiThread.ShowInfo("Close these files in SOLIDWORKS first, then move again:\n\n" + string.Join("\n", open.Take(10).Select(Path.GetFileName)));
                return;
            }
            if (parents.Count > 0 && !UiThread.Confirm(
                    parents.Count + " file(s) use what you're moving:\n\n" + string.Join("\n", parents.Take(10).Select(Path.GetFileName)) + (parents.Count > 10 ? "\n..." : "") +
                    "\n\nSwVault will check them out, point them at the new location and check them back in. Continue?"))
                return;

            if (parents.Count > 0)
            {
                // Everything has to be on this PC, and the files that use the moved ones in your hands.
                await GetLatestAsync(moving.Concat(parents).ToList(), withReferences: false);
                await RunJobAsync(new JobRequest { Kind = JobKind.CheckOut, Paths = parents.ToArray() }, "Checking out the files that use them");
            }

            await RunJobAsync(new JobRequest
            {
                Kind = JobKind.Move,
                Paths = moving.ToArray(),
                TargetFolder = targetFolder,
                Comment = "Move " + (moving.Count == 1 ? Path.GetFileName(moving[0]) : moving.Count + " files") + " to " + (targetVaultFolder.Length == 0 ? "the top of the vault" : targetVaultFolder),
            }, "Moving");

            if (parents.Count > 0)
            {
                var failed = new List<string>();
                foreach (var parent in parents)
                    foreach (var path in moving)
                    {
                        var moved = Path.Combine(targetFolder, Path.GetFileName(path));
                        if (!_docs.References(parent, traverse: false).Contains(path, StringComparer.OrdinalIgnoreCase)) continue;
                        if (!_docs.App.ReplaceReferencedDocument(parent, path, moved))
                        {
                            Log.Warn("ReplaceReferencedDocument failed: " + parent + " : " + path + " -> " + moved);
                            if (!failed.Contains(Path.GetFileName(parent))) failed.Add(Path.GetFileName(parent));
                        }
                    }
                await RunJobAsync(new JobRequest
                {
                    Kind = JobKind.CheckIn,
                    Paths = parents.ToArray(),
                    Comment = "Point at " + (moving.Count == 1 ? Path.GetFileName(moving[0]) : moving.Count + " files") + " in " + (targetVaultFolder.Length == 0 ? "the top of the vault" : targetVaultFolder),
                    Files = parents.Select(FileInfoFor).ToArray(),
                    Force = true, // the sidecars already point at the new place
                }, "Updating the files that use them");
                if (failed.Count > 0)
                    UiThread.ShowInfo("The files moved, but these couldn't be pointed at the new location. Open them and, if SOLIDWORKS asks, browse to the moved files; then check them in:\n\n" + string.Join("\n", failed));
            }

            ((IFrame)_docs.App.Frame()).SetStatusBarText("SwVault: moved " + moving.Count + " file(s) to " + (targetVaultFolder.Length == 0 ? "the top of the vault" : targetVaultFolder) + ".");
        }

        /// <summary>SOLIDWORKS and Windows Explorer fail on paths near 260 characters (Core: FileChecks.MaxLocalPath).</summary>
        private const int MaxPath = 250;

        /// <summary>Where else the vault has a file with this name (another folder), or null.</summary>
        private async Task<string> SameNameElsewhereAsync(VaultInfo vault, string target)
        {
            if (!SwDocs.IsSolidWorksFile(target)) return null;
            try
            {
                var name = Path.GetFileName(target);
                var found = await _agent.SearchAsync(vault.Id, name, StatusFilter.All);
                return found.Where(s => string.Equals(Path.GetFileName(s.Path), name, StringComparison.OrdinalIgnoreCase)
                                        && !string.Equals(s.LocalPath, target, StringComparison.OrdinalIgnoreCase))
                    .Select(s => s.Path).FirstOrDefault();
            }
            catch (AgentException)
            {
                return null; // the check-in preflight looks again
            }
        }

        /// <summary>
        /// Shows what the vault found wrong with files about to be checked in. False when something
        /// blocks it or the person backs out of a warning.
        /// </summary>
        private async Task<bool> PreflightOkAsync(IReadOnlyCollection<string> paths, string action)
        {
            if (paths.Count == 0) return true;
            FileProblemDto[] problems;
            try
            {
                problems = await _agent.PreflightAsync(paths.ToArray());
            }
            catch (AgentException ex) when (ex.Message.Contains("Unknown method"))
            {
                return true; // an older agent; it still refuses what it must at check-in
            }
            var blocking = problems.Where(p => p.Blocking).ToList();
            if (blocking.Count > 0)
            {
                UiThread.ShowError(action + " can't go ahead:\n\n" + string.Join("\n\n", blocking.Take(8).Select(p => "- " + p.Message)) +
                                   (blocking.Count > 8 ? "\n\n...and " + (blocking.Count - 8) + " more." : ""));
                return false;
            }
            var warnings = problems.Where(p => !p.Blocking).ToList();
            if (warnings.Count == 0) return true;
            _confirmedWarnings.Clear();
            foreach (var w in warnings) _confirmedWarnings.Add(w.Message);
            return UiThread.Confirm("Please check before you continue:\n\n" + string.Join("\n\n", warnings.Take(8).Select(p => "- " + p.Message)) +
                                    (warnings.Count > 8 ? "\n\n...and " + (warnings.Count - 8) + " more." : "") + "\n\n" + action + " anyway?");
        }

        /// <summary>What the vault stores about a file: references, custom properties, SOLIDWORKS version.</summary>
        private CheckInFileInfo FileInfoFor(string path)
        {
            var info = new CheckInFileInfo { LocalPath = path };
            if (!SwDocs.IsSolidWorksFile(path)) return info;
            info.References = DirectReferences(path).ToArray();
            info.SwVersion = _docs.Version();
            info.Thumbnail = Thumbnails.CaptureForCheckIn(path, _docs.SavePreviewBitmap);
            var doc = _docs.FindOpen(path);
            if (doc != null)
            {
                info.Properties = _docs.Properties(doc);
                info.Configurations = _docs.Configurations(doc);
            }
            return info;
        }

        /// <summary>
        /// Update Preview: saves the thumbnail of files that are already in the vault (ones added
        /// before previews worked, or imported without them). No new version is made.
        /// </summary>
        public async Task SavePreviewsAsync(IReadOnlyList<string> paths)
        {
            var files = paths.Where(p => SwDocs.IsSolidWorksFile(p) && File.Exists(p)).ToList();
            if (files.Count == 0)
            {
                UiThread.ShowInfo("Previews are saved for SOLIDWORKS parts, assemblies and drawings that are on this PC. Use Get Latest first.");
                return;
            }
            var infos = files.Select(p => new CheckInFileInfo { LocalPath = p, Thumbnail = Thumbnails.CaptureForCheckIn(p, _docs.SavePreviewBitmap) }).ToArray();
            await RunJobAsync(new JobRequest { Kind = JobKind.SavePreviews, Paths = files.ToArray(), Files = infos }, "Saving previews");
        }

        // ------------------------------------------------------------ get latest / undo

        public Task GetLatestAsync(IReadOnlyList<string> paths, bool withReferences) =>
            paths.Count == 0 ? Task.CompletedTask
                : RunJobAsync(new JobRequest { Kind = JobKind.GetLatest, Paths = paths.ToArray(), WithReferences = withReferences }, "Getting latest");

        public async Task UndoCheckOutAsync(IReadOnlyList<string> paths)
        {
            if (paths.Count == 0) return;
            var statuses = await _agent.GetStatusAsync(paths.ToArray());
            var changed = statuses.Where(s => s.LocalState == LocalState.Modified || s.LocalState == LocalState.Conflict).ToList();
            var dirty = _docs.DirtyAmong(paths);
            if ((changed.Count > 0 || dirty.Count > 0) && !UiThread.Confirm(
                    "Undo check-out discards your changes to:\n\n" + string.Join("\n", changed.Select(s => s.LocalPath).Concat(dirty).Distinct(StringComparer.OrdinalIgnoreCase).Select(Path.GetFileName)) +
                    "\n\nContinue?"))
                return;

            // Unsaved edits must go too: reload from disk first so the documents can be released.
            foreach (var path in dirty)
                _docs.FindOpen(path)?.ReloadOrReplace(false, path, true);

            await RunJobAsync(new JobRequest { Kind = JobKind.UndoCheckOut, Paths = paths.ToArray(), Force = true }, "Undoing check-out");
        }

        // ------------------------------------------------------------ history

        public async Task HistoryAsync(string path)
        {
            var versions = await _agent.HistoryAsync(path);
            if (versions.Length == 0)
            {
                UiThread.ShowInfo(Path.GetFileName(path) + " has no history in the vault yet.");
                return;
            }
            var status = (await _agent.GetStatusAsync(path)).FirstOrDefault();
            HistoryDialog.HistoryAction action;
            VersionInfoDto version;
            bool asBuilt;
            using (var dialog = new HistoryDialog(path, versions, status, SwDocs.HasReferences(path), SwDocs.IsSolidWorksFile(path),
                       v => _agent.SavedThumbnailAsync(path, v.Commit)))
            {
                if (dialog.ShowDialog(UiThread.Owner) != DialogResult.OK || dialog.SelectedVersion == null) return;
                action = dialog.Action;
                version = dialog.SelectedVersion;
                asBuilt = dialog.AsBuilt;
            }

            if (action == HistoryDialog.HistoryAction.GetVersion || action == HistoryDialog.HistoryAction.Open)
                await GetVersionAsync(path, version.Version, asBuilt, open: action == HistoryDialog.HistoryAction.Open);
            else if (action == HistoryDialog.HistoryAction.Rollback)
                await RollbackAsync(path, version.Version);
        }

        /// <summary>Puts an older version in the vault folder (read-only), optionally opening it in SOLIDWORKS.</summary>
        public async Task GetVersionAsync(string path, int version, bool asBuilt, bool open)
        {
            await RunJobAsync(new JobRequest { Kind = JobKind.GetVersion, Paths = new[] { path }, Version = version, AsBuilt = asBuilt }, "Getting version " + version);
            if (open && File.Exists(path))
            {
                _docs.Open(path, silent: false);
                ((IFrame)_docs.App.Frame()).SetStatusBarText("SwVault: " + Path.GetFileName(path) + " is at version " + version + " (read-only). Use Get Latest to return to the newest version.");
            }
            else
            {
                UiThread.ShowInfo(Path.GetFileName(path) + " is now at version " + version + " (read-only). Use Get Latest to return to the newest version.");
            }
        }

        public async Task RollbackAsync(string path, int version)
        {
            await RunJobAsync(new JobRequest { Kind = JobKind.Rollback, Paths = new[] { path }, Version = version }, "Rolling back");
            UiThread.ShowInfo("Version " + version + " is in place. Check the file in to make it the newest version.");
        }

        public async Task WhereUsedAsync(string path)
        {
            var parents = await _agent.WhereUsedAsync(path);
            if (parents.Length == 0)
            {
                UiThread.ShowInfo("No files in the vault reference " + Path.GetFileName(path) + ".");
                return;
            }
            using (var dialog = new FileListDialog("Where Used - " + Path.GetFileName(path), "Files that reference " + Path.GetFileName(path) + ":", parents))
            {
                if (dialog.ShowDialog(UiThread.Owner) == DialogResult.OK && dialog.SelectedFile != null)
                    await OpenAsync(dialog.SelectedFile);
            }
        }

        // ------------------------------------------------------------ workflow

        public async Task ChangeStateAsync(string path)
        {
            var status = (await _agent.GetStatusAsync(path)).FirstOrDefault();
            if (status == null || status.ServerVersion == 0)
            {
                UiThread.ShowInfo("Check the file in before changing its workflow state.");
                return;
            }
            var options = await _agent.TransitionsAsync(path);
            TransitionOptionDto option;
            string comment;
            using (var dialog = new TransitionDialog(path, status.State, status.Revision, options))
            {
                if (dialog.ShowDialog(UiThread.Owner) != DialogResult.OK || dialog.Selected == null) return;
                option = dialog.Selected;
                comment = dialog.Comment;
            }

            if (option.BumpRevision && SwDocs.IsSolidWorksFile(path))
                await ReleaseWithRevisionAsync(path, option, comment);
            else
                await RunJobAsync(new JobRequest { Kind = JobKind.Transition, Paths = new[] { path }, TransitionName = option.Name, Comment = comment }, option.Name);
        }

        /// <summary>
        /// Release: check out for the transition, stamp Revision/ReleasedBy/ReleaseDate into the file,
        /// export PDF/STEP, and check everything in with the new state in one commit.
        /// </summary>
        private async Task ReleaseWithRevisionAsync(string path, TransitionOptionDto option, string comment)
        {
            await RunJobAsync(new JobRequest { Kind = JobKind.CheckOut, Paths = new[] { path }, TransitionName = option.Name }, "Preparing release");

            var vaults = await _agent.GetVaultsAsync();
            var user = vaults.FirstOrDefault(v => path.StartsWith(v.LocalRoot, StringComparison.OrdinalIgnoreCase))?.UserDisplayName ?? System.Environment.UserName;
            var doc = _docs.FindOpen(path);
            var openedHere = doc == null;
            if (doc == null) doc = _docs.Open(path, silent: true);
            if (doc == null) throw new AgentException(ErrorCodes.Internal, "Could not open " + Path.GetFileName(path) + " to stamp the revision.");

            var exports = new List<string>();
            try
            {
                _docs.SetProperty(doc, "Revision", option.NextRevision);
                _docs.SetProperty(doc, "ReleasedBy", user);
                _docs.SetProperty(doc, "ReleaseDate", DateTime.Now.ToString("yyyy-MM-dd"));
                doc.ForceRebuild3(false);
                if (!_docs.Save(doc)) throw new AgentException(ErrorCodes.Internal, "Could not save " + Path.GetFileName(path) + ".");

                var type = doc.GetType();
                var name = Path.GetFileNameWithoutExtension(path) + "_Rev" + option.NextRevision;
                foreach (var kind in option.Exports ?? new string[0])
                {
                    string extension = null;
                    if (kind == "pdf" && type == (int)swDocumentTypes_e.swDocDRAWING) extension = ".pdf";
                    else if (kind == "dxf" && type == (int)swDocumentTypes_e.swDocDRAWING) extension = ".dxf";
                    else if (kind == "step" && type != (int)swDocumentTypes_e.swDocDRAWING) extension = ".step";
                    if (extension == null) continue;
                    var target = Path.Combine(option.ExportFolder ?? Path.GetDirectoryName(path), name + extension);
                    if (_docs.Export(doc, target)) exports.Add(target);
                }
            }
            finally
            {
                if (openedHere) _docs.Close(doc);
            }

            var files = new List<CheckInFileInfo> { FileInfoFor(path) };
            await RunJobAsync(new JobRequest
            {
                Kind = JobKind.CheckIn,
                Paths = new[] { path }.Concat(exports).ToArray(),
                TransitionName = option.Name,
                Comment = comment, // blank: the vault notes "Released revision X"
                Files = files.ToArray(),
            }, "Releasing revision " + option.NextRevision);
        }

        // ------------------------------------------------------------ open & prompts

        /// <summary>Opens a vault file, first offering to get the latest versions of anything it references.</summary>
        public async Task OpenAsync(string path)
        {
            var status = (await _agent.GetStatusAsync(path)).FirstOrDefault();
            var needsFiles = status == null || status.LocalState == LocalState.NotLocal || status.LocalState == LocalState.MissingLocally;
            if (SwDocs.HasReferences(path) || needsFiles)
            {
                var tree = await _agent.ReferencesAsync(path);
                var stale = Flatten(tree).Where(n => n.Status != null && (n.Status.LocalState == LocalState.NotLocal
                    || n.Status.LocalState == LocalState.MissingLocally || n.Status.LocalState == LocalState.Outdated)).ToList();
                var missing = stale.Any(n => n.Status.LocalState != LocalState.Outdated);
                if (stale.Count > 0 && (missing || UiThread.Confirm(stale.Count + " file(s) have newer versions in the vault. Get the latest before opening?")))
                    await GetLatestAsync(new[] { path }, withReferences: true);
            }
            if (!File.Exists(path)) throw new AgentException(ErrorCodes.NotFound, Path.GetFileName(path) + " is not available locally.");
            await UiThread.OnUi();
            if (!CadFormats.OpensInSolidWorks(path))
            {
                // A PDF, a spreadsheet...: whatever Windows opens it with.
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
                return;
            }
            if (_docs.Open(path, silent: false) == null && !SwDocs.IsSolidWorksFile(path))
                throw new AgentException(ErrorCodes.BadRequest, "SOLIDWORKS couldn't open " + Path.GetFileName(path) + " (" + CadFormats.Of(path).Name
                    + "). Some formats need SOLIDWORKS Premium or 3D Interconnect; File > Open in SOLIDWORKS shows the reason.");
        }

        /// <summary>
        /// Add Files: picks files anywhere on this PC (STEP, IGES, DXF, vendor models, PDFs...),
        /// copies them into a vault folder and checks them in as version 1. For SOLIDWORKS parts
        /// and assemblies use Add to Vault or Import Folder instead: those fix up references.
        /// </summary>
        public async Task AddFilesAsync()
        {
            var vaults = await _agent.GetVaultsAsync();
            await UiThread.OnUi();
            if (vaults.Length == 0)
            {
                UiThread.ShowInfo("Connect this PC to a vault first.");
                return;
            }
            string[] picked;
            using (var dialog = new OpenFileDialog { Title = "Add files to the vault", Multiselect = true, Filter = CadFormats.DialogFilter(), CheckFileExists = true })
            {
                if (dialog.ShowDialog(UiThread.Owner) != DialogResult.OK || dialog.FileNames.Length == 0) return;
                picked = dialog.FileNames;
            }
            var withReferences = picked.Where(SwDocs.HasReferences).Select(Path.GetFileName).ToList();
            if (withReferences.Count > 0 && !UiThread.Confirm(
                    "Assemblies and drawings point at other files, and Add Files only copies what you picked:\n\n" + string.Join("\n", withReferences.Take(10))
                    + "\n\nOpen them and use Add to Vault (or use Import Folder) to bring their parts along. Add just these files anyway?"))
                return;

            // Start in the folder picked in the task pane, like Add to Vault.
            var start = DefaultFolder?.Invoke();
            var vault = string.IsNullOrEmpty(start) ? null : VaultOf(vaults, start.TrimEnd('\\') + "\\x");
            if (vault == null)
            {
                vault = vaults[0];
                start = vault.LocalRoot;
            }
            var root = vault.LocalRoot.TrimEnd('\\');
            var selected = start.TrimEnd('\\').Length > root.Length ? start.TrimEnd('\\').Substring(root.Length + 1).Replace('\\', '/') : "";
            var folders = await AllFoldersAsync(vault);
            await UiThread.OnUi();
            string folder;
            using (var dialog = new AddToVaultDialog(vault.Name, root, folders, selected, null, null, "Add Files",
                       "Choose where in the vault " + (picked.Length == 1 ? Path.GetFileName(picked[0]) + " goes." : "these " + picked.Length + " files go.")))
            {
                dialog.OkText = "Add here";
                if (dialog.ShowDialog(UiThread.Owner) != DialogResult.OK) return;
                folder = dialog.LocalFolderPath;
            }
            Directory.CreateDirectory(folder);

            var added = new List<string>();
            var skipped = new List<string>();
            foreach (var source in picked)
            {
                var target = Path.Combine(folder, Path.GetFileName(source));
                if (!string.Equals(Path.GetFullPath(source), Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase))
                {
                    if (File.Exists(target))
                    {
                        var existing = (await _agent.GetStatusAsync(target)).FirstOrDefault();
                        await UiThread.OnUi();
                        if (existing != null && existing.LocalState != LocalState.LocalOnly)
                        {
                            skipped.Add(Path.GetFileName(target) + " (already in the vault there; check it out and replace it to make a new version)");
                            continue;
                        }
                        if (!UiThread.Confirm(Path.GetFileName(target) + " already exists in that folder. Replace it?"))
                        {
                            skipped.Add(Path.GetFileName(target));
                            continue;
                        }
                    }
                    File.Copy(source, target, overwrite: true);
                    File.SetAttributes(target, File.GetAttributes(target) & ~FileAttributes.ReadOnly);
                }
                added.Add(target);
            }

            var statuses = await _agent.GetStatusAsync(added.ToArray());
            await UiThread.OnUi();
            var toAdd = added.Where(a => statuses.Any(s => string.Equals(s.LocalPath, a, StringComparison.OrdinalIgnoreCase) && s.LocalState == LocalState.LocalOnly)).ToList();
            skipped.AddRange(added.Except(toAdd).Select(a => Path.GetFileName(a) + " (already in the vault, or a kind of file the vault ignores)"));
            if (toAdd.Count > 0)
            {
                string note;
                var caption = toAdd.Count == 1 ? "Adding " + Path.GetFileName(toAdd[0]) + " to the vault as version 1." : "Adding " + toAdd.Count + " files to the vault as version 1.";
                using (var dialog = new VersionNoteDialog("Add Files", caption, toAdd.Count == 1 ? "Added " + Path.GetFileName(toAdd[0]) : "Added " + toAdd.Count + " files"))
                {
                    if (dialog.ShowDialog(UiThread.Owner) != DialogResult.OK) return;
                    note = dialog.Note;
                }
                await RunJobAsync(new JobRequest
                {
                    Kind = JobKind.CheckIn,
                    Paths = toAdd.ToArray(),
                    Comment = note,
                    Files = toAdd.Select(FileInfoFor).ToArray(),
                    Force = true, // picked files may be assemblies whose parts weren't picked; the question above covered it
                }, "Adding to vault");
                await UiThread.OnUi();
            }
            if (skipped.Count > 0) UiThread.ShowInfo("Not added:\n\n" + string.Join("\n", skipped));
        }

        private static IEnumerable<ReferenceNodeDto> Flatten(ReferenceNodeDto node)
        {
            if (node == null) yield break;
            yield return node;
            foreach (var child in node.Children ?? new ReferenceNodeDto[0])
                foreach (var n in Flatten(child)) yield return n;
        }

        /// <summary>
        /// First edit of a read-only vault document: offer to check it out. Tools that change many
        /// files at once (Altium CoDesigner pushing a board, a rename that updates references) raise
        /// one event per document, so edits are gathered for a moment and offered as one check-out.
        /// </summary>
        public async Task OnDocumentModifiedAsync(IModelDoc2 doc)
        {
            var path = SwDocs.PathOf(doc);
            if (string.IsNullOrEmpty(path) || _promptedDocs.Contains(path) || !doc.IsOpenedReadOnly()) return;
            _pendingModified[path] = doc;
            if (_modifiedBatchRunning) return; // the batch already under way picks it up
            _modifiedBatchRunning = true;
            try
            {
                while (_pendingModified.Count > 0)
                {
                    // Wait until the events stop coming before asking.
                    int seen;
                    do
                    {
                        seen = _pendingModified.Count;
                        await Task.Delay(ModifiedSettleMs);
                        await UiThread.OnUi();
                    } while (_pendingModified.Count != seen);

                    var batch = _pendingModified.ToList();
                    _pendingModified.Clear();
                    await OfferCheckOutAsync(batch);
                    await UiThread.OnUi();
                }
            }
            finally
            {
                _modifiedBatchRunning = false;
            }
        }

        private async Task OfferCheckOutAsync(List<KeyValuePair<string, IModelDoc2>> edited)
        {
            edited = edited.Where(e => !_promptedDocs.Contains(e.Key)).ToList();
            if (edited.Count == 0) return;
            var statuses = await _agent.GetStatusAsync(edited.Select(e => e.Key).ToArray());
            await UiThread.OnUi();
            FileStatusDto StatusOf(string p) => statuses.FirstOrDefault(s => string.Equals(s.LocalPath, p, StringComparison.OrdinalIgnoreCase));

            var offer = new List<KeyValuePair<string, IModelDoc2>>();
            var lockedByOthers = new List<string>();
            var readOnlyState = new List<string>();
            var alreadyMine = new List<string>();
            foreach (var e in edited)
            {
                var status = StatusOf(e.Key);
                if (status == null || status.LocalState == LocalState.LocalOnly) continue;
                if (status.LockState == LockState.MineHere)
                {
                    // Checked out outside SOLIDWORKS (tray icon, command line) while the document was open.
                    alreadyMine.Add(e.Key);
                    continue;
                }
                _promptedDocs.Add(e.Key);
                if (status.LockState == LockState.Other)
                    lockedByOthers.Add(Path.GetFileName(e.Key) + " (" + status.LockOwner + ")");
                else if (status.State == "Released" || status.State == "InReview" || status.State == "Obsolete")
                    readOnlyState.Add(Path.GetFileName(e.Key) + " (" + status.State + ")");
                else
                    offer.Add(e);
            }
            if (alreadyMine.Count > 0) _docs.SyncReadOnlyState(alreadyMine);

            var notes = new List<string>();
            if (lockedByOthers.Count > 0)
                notes.Add("Checked out by someone else, so you can look and experiment but won't be able to save:\n" + ListForMessage(lockedByOthers));
            if (readOnlyState.Count > 0)
                notes.Add("Released or in review; use Change State > Change request before editing:\n" + ListForMessage(readOnlyState));

            if (offer.Count == 0)
            {
                if (notes.Count > 0) UiThread.ShowInfo(string.Join("\n\n", notes));
                return;
            }

            var outdated = offer.Where(e => StatusOf(e.Key).LocalState == LocalState.Outdated).ToList();
            string question;
            if (offer.Count == 1)
            {
                var name = Path.GetFileName(offer[0].Key);
                question = outdated.Count > 0
                    ? name + " is not checked out, and a newer version exists. Check out now? (The newer version replaces the file and your edits so far are discarded.)"
                    : name + " is not checked out, so you won't be able to save it. Check it out now?";
            }
            else
            {
                question = offer.Count + " files were changed but are not checked out, so you won't be able to save them:\n\n" +
                           ListForMessage(offer.Select(e => Path.GetFileName(e.Key)).ToList()) +
                           (outdated.Count > 0 ? "\n\nA newer version exists for " + outdated.Count + " of them; checking out replaces those files and your edits so far to them are discarded." : "") +
                           "\n\nCheck out all " + offer.Count + " now?";
            }
            if (notes.Count > 0) question = string.Join("\n\n", notes) + "\n\n" + question;
            if (!UiThread.Confirm(question)) return;

            foreach (var e in outdated) e.Value.ReloadOrReplace(true, e.Key, true);
            await RunJobAsync(new JobRequest { Kind = JobKind.CheckOut, Paths = offer.Select(e => e.Key).ToArray() }, "Checking out");
        }

        private static string ListForMessage(IReadOnlyList<string> names, int max = 15) =>
            string.Join("\n", names.Take(max)) + (names.Count > max ? "\n...and " + (names.Count - max) + " more" : "");

        /// <summary>
        /// Someone opened a SOLIDWORKS file from outside the vault (Downloads, Desktop, an email, a USB
        /// stick) while the vault has a file of the same name: their edits would never reach the team.
        /// Offers to open the vault's copy instead. Asked once per file per session.
        /// </summary>
        public async Task CheckOpenedOutsideVaultAsync(string path)
        {
            if (!SwDocs.IsSolidWorksFile(path) || _promptedDocs.Contains("outside|" + path)) return;
            var vaults = await _agent.GetVaultsAsync();
            if (vaults.Length == 0 || VaultOf(vaults, path) != null) return;
            var name = Path.GetFileName(path);
            FileStatusDto match = null;
            foreach (var vault in vaults)
            {
                var found = await _agent.SearchAsync(vault.Id, name, StatusFilter.All);
                match = found.FirstOrDefault(s => string.Equals(Path.GetFileName(s.Path), name, StringComparison.OrdinalIgnoreCase));
                if (match != null) break;
            }
            if (match == null) return; // a new file: the task pane offers Add to Vault
            _promptedDocs.Add("outside|" + path);
            var doc = _docs.FindOpen(path);
            if (!UiThread.Confirm("You opened " + name + " from " + Path.GetDirectoryName(path) + ", which is not your vault folder.\n\n" +
                                  "Changes saved there won't reach the vault or your team. The vault has " + name + " at " + match.Path + ".\n\n" +
                                  "Close this copy and open the vault's " + name + " instead?"))
                return;
            if (doc != null && doc.GetSaveFlag())
            {
                UiThread.ShowInfo("This copy has unsaved changes, so it stays open. Save what you need, then open " + match.LocalPath + ".");
                return;
            }
            if (doc != null) _docs.Close(doc);
            await OpenAsync(match.LocalPath);
        }

        public void OnDocumentClosed(string path)
        {
            if (!string.IsNullOrEmpty(path)) _promptedDocs.Remove(path);
        }

        // ------------------------------------------------------------ setup

        public async Task SetupVaultAsync()
        {
            var existing = await _agent.GetVaultsAsync();
            var team = existing.Length == 0 ? await _agent.GetTeamAsync() : null;
            if (team != null)
            {
                // Team install: the agent's sign-in window (account or invite code) handles it.
                await _agent.ShowAgentWindowAsync("signIn");
                return;
            }
            using (var dialog = new VaultSetupDialog(existing))
            {
                // A typo shouldn't mean typing everything again: show the problem and reopen the dialog as it was.
                while (true)
                {
                    if (dialog.ShowDialog(UiThread.Owner) != DialogResult.OK) return;
                    var progress = ProgressWindow.ShowFor("Connecting");
                    try
                    {
                        var vault = await _agent.AddVaultAsync(dialog.Request);
                        progress.Close();
                        UiThread.ShowInfo("Connected to " + vault.Name + " as " + vault.UserLogin + ".\nVault folder: " + vault.LocalRoot);
                        break;
                    }
                    catch (AgentException ex)
                    {
                        progress.Close();
                        dialog.Problem = ex.Message;
                    }
                }
            }
            Changed?.Invoke(new string[0]);
        }

        /// <summary>Vault admins: the agent's Invite People window (makes a link + code to send).</summary>
        public Task InvitePeopleAsync() => _agent.ShowAgentWindowAsync("invite");
    }
}
