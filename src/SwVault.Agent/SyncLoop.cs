using System.Globalization;
using SwVault.Core;
using SwVault.Core.Client;
using SwVault.Core.Util;
using SwVault.Core.Vault;
using SwVault.Protocol;

namespace SwVault.Agent;

/// <summary>
/// Polls every vault: cheap ls-remote each tick (fetch only when the server moved) and the lock
/// list every other tick. Changes go to connected add-ins; relevant ones also become toasts.
/// </summary>
internal sealed class SyncLoop : IDisposable
{
    private readonly VaultManager _vaults;
    private readonly Action<string, object> _broadcast;
    private readonly Action<string, string> _toast;
    private readonly FileLog _log;
    private readonly CancellationTokenSource _cts = new();
    private readonly TimeSpan _interval;
    private readonly SemaphoreSlim _wake = new(0);
    private readonly HashSet<string> _warnedLeftovers = new();
    private Task? _loop;
    private readonly Dictionary<string, DateTimeOffset> _reminded = new();

    private readonly Func<VaultSession, IReadOnlyList<Core.Client.Subsystem>> _engineerOf;

    /// <param name="engineerOf">Subsystems of a vault where I'm a responsible engineer (their changes get their own toast).</param>
    public SyncLoop(VaultManager vaults, Action<string, object> broadcast, Action<string, string> toast, FileLog log,
        Func<VaultSession, IReadOnlyList<Core.Client.Subsystem>>? engineerOf = null)
    {
        _engineerOf = engineerOf ?? (_ => Array.Empty<Core.Client.Subsystem>());
        _vaults = vaults;
        _broadcast = broadcast;
        _toast = toast;
        _log = log;
        var seconds = int.TryParse(Environment.GetEnvironmentVariable("SWVAULT_SYNC_SECONDS"), NumberStyles.None, CultureInfo.InvariantCulture, out var s) ? s : 30;
        _interval = TimeSpan.FromSeconds(Math.Max(5, seconds));
    }

    public void Start() => _loop = Task.Run(RunAsync);

    /// <summary>Sync right away (e.g. "Sync now" in the tray menu).</summary>
    public void Poke() => _wake.Release();

    private async Task RunAsync()
    {
        var tick = 0;
        while (!_cts.IsCancellationRequested)
        {
            IReadOnlyList<VaultSession> sessions;
            try
            {
                sessions = await _vaults.GetAllAsync(_cts.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.Warn("Could not open vaults: " + ex.Message);
                sessions = Array.Empty<VaultSession>();
            }

            foreach (var session in sessions)
            {
                try
                {
                    await SyncOneAsync(session, refreshLocks: tick % 2 == 0).ConfigureAwait(false);
                    RemindOldCheckOuts(session);
                }
                catch (VaultException ex)
                {
                    if (ex.Code is not (ErrorCodes.Offline or ErrorCodes.Unauthorized)) _log.Warn($"Sync of {session.VaultId} failed: {ex.Message}");
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _log.Error($"Sync of {session.VaultId} crashed", ex);
                }
            }
            tick++;

            try
            {
                await _wake.WaitAsync(_interval, _cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>Once a day per vault: "these files have been checked out to you for days".</summary>
    private void RemindOldCheckOuts(VaultSession session)
    {
        var now = DateTimeOffset.UtcNow;
        if (_reminded.TryGetValue(session.VaultId, out var last) && now - last < TimeSpan.FromDays(1)) return;
        var old = CheckOutReminder.Old(session.GetLocks(), now);
        _reminded[session.VaultId] = now; // also when there's nothing to say, so this runs daily, not every sync
        if (old.Count > 0) _toast("SwVault: files still checked out", CheckOutReminder.Message(old));
    }

    private async Task SyncOneAsync(VaultSession session, bool refreshLocks)
    {
        var wasOnline = session.Online;
        SyncResult result;
        try
        {
            result = await session.SyncAsync(fetch: true, refreshLocks: refreshLocks, _cts.Token).ConfigureAwait(false);
        }
        finally
        {
            if (wasOnline != session.Online)
                _broadcast(Notifications.StatusChanged, new StatusChangedNotification { VaultId = session.VaultId, FullRefresh = true });
        }

        // Copies of files that someone moved or deleted on another PC don't linger here.
        var leftovers = new List<string>();
        try
        {
            var (removed, keptModified) = await session.RemoveLeftoverCopiesAsync(_cts.Token).ConfigureAwait(false);
            leftovers.AddRange(removed);
            if (removed.Count > 0)
                _log.Info($"Removed {removed.Count} leftover cop{(removed.Count == 1 ? "y" : "ies")} of files moved or deleted in {session.VaultId}: {string.Join(", ", removed.Take(5))}{(removed.Count > 5 ? "..." : "")}");
            if (keptModified.Count > 0 && _warnedLeftovers.Add(session.VaultId + "|" + string.Join("|", keptModified)))
                _toast("Files moved or deleted in " + session.Config.Name,
                    Summarize(keptModified.Select(PathRules.GetFileName).ToList()) + "\nYour copies have changes that aren't in the vault, so they were kept. Add them again or delete them.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or VaultException)
        {
            _log.Warn("Removing leftover copies: " + ex.Message);
        }

        var changed = result.ChangedPaths.Concat(result.LockChangedPaths).Concat(leftovers).Distinct(PathRules.Comparer).ToList();
        if (changed.Count == 0) return;
        _broadcast(Notifications.StatusChanged, new StatusChangedNotification { VaultId = session.VaultId, Paths = changed.Select(session.LocalPathOf).ToArray() });
        if (result.ChangedPaths.Count > 0) NotifyRelevantChanges(session, result);
    }

    /// <summary>Toasts only for what matters to this user: files they have locally got newer, and reviews waiting on them.</summary>
    private void NotifyRelevantChanges(VaultSession session, SyncResult result)
    {
        var me = session.User?.Login;
        var updated = new List<string>();
        var toReview = new List<string>();
        foreach (var path in result.ChangedPaths)
        {
            var head = session.Head.Get(path);
            if (head?.Meta == null) continue;
            if (string.Equals(head.Meta.CheckedInBy, me, StringComparison.OrdinalIgnoreCase) && head.Meta.State != "InReview") continue;
            if (string.Equals(head.Meta.State, "InReview", StringComparison.OrdinalIgnoreCase) && session.Config.UserHasRole(me, "approver"))
                toReview.Add(PathRules.GetFileName(path));
            else if (File.Exists(session.LocalPathOf(path)))
                updated.Add($"{PathRules.GetFileName(path)} v{head.Version} by {head.Meta.CheckedInBy}");
        }
        if (updated.Count > 0)
            _toast("New versions in " + session.Config.Name, Summarize(updated) + "\nUse Get Latest to update your copies.");
        if (toReview.Count > 0)
            _toast("Ready for your review", Summarize(toReview));
        NotifyEngineer(session, result, me);
    }

    /// <summary>Responsible engineers hear about everything others add, check in or release in their subsystems.</summary>
    private void NotifyEngineer(VaultSession session, SyncResult result, string? me)
    {
        var mine = _engineerOf(session);
        if (mine.Count == 0) return;
        foreach (var group in result.ChangedPaths
                     .Select(path => (Path: path, Head: session.Head.Get(path), Subsystem: mine.Where(s => s.Contains(path)).OrderByDescending(s => s.Folder.Length).FirstOrDefault()))
                     .Where(x => x.Head?.Meta != null && x.Subsystem != null)
                     .GroupBy(x => x.Subsystem!.Id))
        {
            var lines = new List<string>();
            foreach (var (path, head, _) in group)
            {
                var meta = head!.Meta!;
                var released = string.Equals(meta.State, "Released", StringComparison.OrdinalIgnoreCase);
                var actor = released ? meta.RevisionHistory?.LastOrDefault()?.By ?? meta.CheckedInBy : meta.CheckedInBy;
                if (string.Equals(actor, me, StringComparison.OrdinalIgnoreCase)) continue;
                var name = PathRules.GetFileName(path);
                lines.Add(released ? $"{actor} released {name} rev {meta.Revision}"
                    : head.Version == 1 ? $"{actor} added {name}"
                    : $"{actor} checked in {name} v{head.Version}");
            }
            if (lines.Count == 0) continue;
            var subsystem = group.First().Subsystem!;
            _toast($"{subsystem.CarName} / {subsystem.Name} (you're RE)", Summarize(lines));
        }
    }

    private static string Summarize(List<string> items) =>
        items.Count <= 3 ? string.Join("\n", items) : string.Join("\n", items.Take(3)) + $"\n...and {items.Count - 3} more";

    public void Dispose()
    {
        _cts.Cancel();
        try { _loop?.Wait(2000); } catch (AggregateException) { }
        _cts.Dispose();
    }
}
