using System.Globalization;
using Microsoft.Win32;
using SwVault.Core;
using SwVault.Core.Client;
using SwVault.Protocol;

namespace SwVault.Agent;

/// <summary>Everything the agent runs, independent of the tray UI (so tests can host it headless).</summary>
internal sealed class AgentHost : IDisposable
{
    public SwVaultProfile Profile { get; }
    public VaultManager Vaults { get; }
    public FileLog Log { get; }
    public PipeServer Server { get; }
    public SyncLoop Sync { get; }
    public JobManager Jobs { get; }
    public ReviewWatcher ReviewWatcher { get; }
    public TeamWatcher TeamWatcher { get; }
    public UpdateWatcher Updates { get; }

    /// <summary>The team vault this install was packaged for (team.json), or null.</summary>
    public TeamConfig? Team { get; }

    /// <summary>True until this PC is connected to a vault while a team.json says which one to join.</summary>
    public bool NeedsTeamSignIn => Team != null && Vaults.Registrations.Count == 0;

    /// <summary>Raised for toast-worthy events; the tray shows them as Windows notifications.</summary>
    public event Action<string, string>? Toast;

    /// <summary>Raised after an idle, identity-preserving 1.2.9 -> 1.4 URL cutover.</summary>
    public event Action? MigrationApplied;

    /// <summary>A client (the add-in) asked for one of the agent's windows: "signIn" or "invite".</summary>
    public event Action<string>? WindowRequested;

    public AgentHost(SwVaultProfile profile)
    {
        Profile = profile;
        Log = new FileLog(profile.LogDir);
        Vaults = new VaultManager(profile);
        Jobs = new JobManager(Vaults, Broadcast, Log);
        try
        {
            Team = TeamConfig.Load();
        }
        catch (Exception ex)
        {
            Log.Warn("Could not read team.json: " + ex.Message);
        }
        var dispatcher = new RpcDispatcher(Vaults, Jobs, Log, () => Sync?.Poke(), Team, JoinTeamAsync, what => WindowRequested?.Invoke(what), SubsystemInfoAsync, ReviewsForFileAsync, HandleLinkAsync, ShareFolderAsync);
        Server = new PipeServer(profile.PipeName, dispatcher.DispatchAsync, Log);
        TeamWatcher = new TeamWatcher(this, RaiseToast);
        Sync = new SyncLoop(Vaults, Broadcast, RaiseToast, Log, TeamWatcher.SubsystemsIEngineer);
        ReviewWatcher = new ReviewWatcher(this, RaiseToast);
        Updates = new UpdateWatcher(this, RaiseToast);
    }

    public void Start()
    {
        Log.Info($"SwVault agent {typeof(AgentHost).Assembly.GetName().Version} starting (profile {Profile.Name}, pipe {Profile.PipeName})");
        RegisterLocation();
        RegisterLinkScheme();
        // A new team installer can advertise the previous repository hostname. Verify
        // the same account on the new server before changing any saved registration.
        var migratedAtStartup = false;
        if (Team != null && !string.IsNullOrWhiteSpace(Team.PreviousVaultUrl))
        {
            try { migratedAtStartup = TeamUrlMigration.TryApplyAsync(Profile, Team, Log.Warn).GetAwaiter().GetResult(); }
            catch (Exception ex) { Log.Warn("Team URL migration skipped: " + ex.Message); }
        }
        WarnAboutMissingVaultFolders();
        Server.Start();
        Sync.Start();
        ReviewWatcher.Start();
        TeamWatcher.Start();
        Updates.Start();
        _ = Task.Run(ResumePendingAsync);
        if (Team != null && !string.IsNullOrWhiteSpace(Team.PreviousVaultUrl))
            _ = Task.Run(PollOneTimeMigrationAsync);
        if (migratedAtStartup)
            MigrationApplied?.Invoke(); // reload any pre-opened sessions after the registry switch
    }

    /// <summary>
    /// The 1.2.9 updater is opt-in. A member may install 1.4 days before the
    /// server switch; retry the readiness handshake without changing working
    /// registrations until the destination is armed. Restart on the UI thread
    /// to dispose all cached Git/LFS sessions after a successful migration.
    /// </summary>
    private readonly CancellationTokenSource _migrationPolling = new();

    private async Task PollOneTimeMigrationAsync()
    {
        while (!_migrationPolling.IsCancellationRequested)
        {
            try { await Task.Delay(TimeSpan.FromMinutes(5), _migrationPolling.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
            if (Team == null || Jobs.HasActiveJobs) continue;
            try
            {
                if (await TeamUrlMigration.TryApplyAsync(Profile, Team, Log.Warn).ConfigureAwait(false))
                {
                    Log.Info("One-time 1.2.9 -> 1.4 migration ready; restarting agent to reload credentials and sessions.");
                    MigrationApplied?.Invoke();
                    return;
                }
                if (!Profile.LoadRegistrations().Any(r => VaultLink.SameVault(r.RemoteUrl, Team.PreviousVaultUrl)))
                    return; // Already migrated, or the old vault was removed.
            }
            catch (Exception ex)
            {
                Log.Warn("One-time migration will retry: " + ex.Message);
            }
        }
    }

    /// <summary>
    /// First sign-in on a team install: connect with user name and password, then download the
    /// vault in the background so the files are there when SOLIDWORKS opens.
    /// </summary>
    public async Task<VaultSession> JoinTeamAsync(string userName, string password)
    {
        var team = Team ?? throw VaultException.NotFound("This SwVault install has no team.json; connect with Vaults... instead.");
        var session = await TeamJoin.JoinAsync(Vaults, team, userName, password).ConfigureAwait(false);
        Log.Info($"Joined {team.Name} as {session.User?.Login}");
        Broadcast(Notifications.StatusChanged, new StatusChangedNotification { VaultId = session.VaultId, FullRefresh = true });
        Sync.Poke();
        if (team.DownloadAllOnJoin) _ = Task.Run(() => DownloadAllAsync(session));
        return session;
    }

    /// <summary>The vault this PC works with (the team's, or the only one connected), for the tray's account commands.</summary>
    public Core.Client.VaultRegistration? AccountVault()
    {
        var registrations = Vaults.Registrations;
        return (Team == null ? null : registrations.FirstOrDefault(r => VaultLink.SameVault(r.RemoteUrl, Team.VaultUrl))) ?? registrations.FirstOrDefault();
    }

    /// <summary>
    /// Signs this PC in to its vault as another account. The password is checked first, so a wrong
    /// one changes nothing; if the new account can't open the vault, the old sign-in is put back.
    /// Returns the account now signed in.
    /// </summary>
    public async Task<string> SwitchAccountAsync(string userName, string password)
    {
        var registration = AccountVault() ?? throw VaultException.NotFound("Connect to your team's vault first.");
        var server = new Uri(registration.RemoteUrl);
        var token = await TeamJoin.CreateTokenAsync(server, userName, password, CancellationToken.None).ConfigureAwait(false);
        var previous = await Profile.CredentialProvider.GetAsync(server, interactive: false, CancellationToken.None).ConfigureAwait(false);
        VaultSession session;
        try
        {
            session = await Vaults.AddAsync(registration.RemoteUrl, registration.LocalRoot, new Core.Auth.Credential(userName, token)).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is VaultException or HttpRequestException or IOException)
        {
            Log.Warn($"Switching to {userName} failed ({ex.Message}); staying signed in as {previous?.UserName ?? "before"}.");
            if (previous != null)
            {
                try { await Vaults.AddAsync(registration.RemoteUrl, registration.LocalRoot, previous).ConfigureAwait(false); }
                catch (Exception restore) when (restore is VaultException or HttpRequestException or IOException) { Log.Warn("Restoring the previous sign-in: " + restore.Message); }
                if (!string.IsNullOrEmpty(registration.Name)) Vaults.UpdateName(registration.Id, registration.Name);
            }
            throw ex as VaultException ?? new VaultException(ErrorCodes.Internal, $"{userName} couldn't open the vault: {ex.Message}");
        }
        if (!string.IsNullOrEmpty(registration.Name)) Vaults.UpdateName(session.VaultId, registration.Name);
        var login = session.User?.Login ?? userName;
        Log.Info($"Switched account: this PC is now signed in as {login}");
        Broadcast(Notifications.StatusChanged, new StatusChangedNotification { VaultId = session.VaultId, FullRefresh = true });
        Sync.Poke();
        _ = TeamWatcher.CheckNowAsync();
        _ = ReviewWatcher.CheckNowAsync();
        return login;
    }

    /// <summary>New member with an invite code: create the account, then sign in as above.</summary>
    public async Task<VaultSession> JoinWithInviteAsync(string code, string userName, string password, string? fullName,
        string? email, string? emailCode, TeamProfile profile)
    {
        var team = Team ?? throw VaultException.NotFound("This SwVault install has no team.json, so it doesn't know which vault the invite is for.");
        try
        {
            await TeamInvites.RedeemAsync(team.VaultUrl, code, userName, password, fullName, email, emailCode, profile).ConfigureAwait(false);
            Log.Info($"Redeemed invite {code} as {userName}");
        }
        catch (VaultException ex) when (ex.Code is ErrorCodes.Conflict or ErrorCodes.NotFound)
        {
            // "Name taken" or "code used up" can be this same person retrying after the account was
            // created but signing in failed (e.g. the connection dropped). If the password works, carry on.
            try
            {
                var session = await JoinTeamAsync(userName, password).ConfigureAwait(false);
                ClearPendingInvite();
                return session;
            }
            catch (VaultException retry) when (retry.Code == ErrorCodes.Unauthorized)
            {
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(ex); // the redeem error is the useful one
            }
        }
        ClearPendingInvite();
        return await JoinTeamAsync(userName, password).ConfigureAwait(false);
    }

    /// <summary>
    /// Invite code that came with this install: from the installer's folder name (the download link
    /// names the zip after the code; install.ps1 saves it) or baked into team.json.
    /// </summary>
    public string? PendingInviteCode()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\SwVault");
            var saved = TeamInvites.NormalizeCode(key?.GetValue("InviteCode") as string);
            if (saved != null) return saved;
        }
        catch (Exception ex)
        {
            Log.Warn("Could not read the saved invite code: " + ex.Message);
        }
        return TeamInvites.NormalizeCode(Team?.InviteCode);
    }

    private void ClearPendingInvite()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\SwVault", writable: true);
            key?.DeleteValue("InviteCode", throwOnMissingValue: false);
        }
        catch (Exception ex)
        {
            Log.Warn("Could not clear the saved invite code: " + ex.Message);
        }
    }

    /// <summary>The vault invites are for (the team's, else the first connected one), and its display name.</summary>
    public (string VaultUrl, string Name)? InviteTarget()
    {
        if (Team != null) return (Team.VaultUrl, Team.Name);
        var first = Vaults.Registrations.FirstOrDefault();
        return first == null ? null : (first.RemoteUrl, first.Name ?? first.Id);
    }

    /// <summary>This PC's saved sign-in for the vault server (the team service checks who it is).</summary>
    public async Task<Core.Auth.Credential> MyCredentialAsync(string vaultUrl)
    {
        var credential = await Profile.CredentialProvider.GetAsync(new Uri(vaultUrl), interactive: false, CancellationToken.None).ConfigureAwait(false);
        return credential ?? throw VaultException.Unauthorized("Sign in to the vault on this PC first.");
    }

    /// <summary>The team directory and this PC's user in it (for profiles and review requests).</summary>
    public async Task<(TeamDirectory Directory, string Me)> TeamDirectoryAsync()
    {
        var target = InviteTarget() ?? throw VaultException.NotFound("Connect to your team's vault first.");
        var me = await MyCredentialAsync(target.VaultUrl).ConfigureAwait(false);
        var directory = await TeamInvites.PeopleAsync(target.VaultUrl, me).ConfigureAwait(false);
        return (directory, me.UserName);
    }

    /// <summary>Saves my role; returns the subteam when becoming its lead waits for an admin.</summary>
    public async Task<string?> SaveMyProfileAsync(TeamProfile profile)
    {
        var target = InviteTarget() ?? throw VaultException.NotFound("Connect to your team's vault first.");
        var pending = await TeamInvites.SaveMyProfileAsync(target.VaultUrl, await MyCredentialAsync(target.VaultUrl).ConfigureAwait(false), profile).ConfigureAwait(false);
        _ = TeamWatcher.CheckNowAsync();
        return pending;
    }

    // ---------------------------------------------------------------- projects ("cars"), subsystems, approvals

    public async Task<(IReadOnlyList<Car> Cars, string Me)> CarsAsync()
    {
        var (vaultUrl, me) = await ReviewContextAsync().ConfigureAwait(false);
        return (await TeamInvites.CarsAsync(vaultUrl, me).ConfigureAwait(false), me.UserName);
    }

    public async Task AddCarAsync(string name, string? folder)
    {
        var (vaultUrl, me) = await ReviewContextAsync().ConfigureAwait(false);
        await TeamInvites.AddCarAsync(vaultUrl, me, name, folder).ConfigureAwait(false);
        Log.Info($"Added project {name}");
        _ = TeamWatcher.CheckNowAsync();
    }

    /// <summary>Adds a subsystem and creates its folder in the vault folder on this PC.</summary>
    public async Task AddSubsystemAsync(string carId, string name, string? folder, IReadOnlyList<string>? folders = null)
    {
        var (vaultUrl, me) = await ReviewContextAsync().ConfigureAwait(false);
        await TeamInvites.AddSubsystemAsync(vaultUrl, me, carId, name, folder, folders).ConfigureAwait(false);
        Log.Info($"Added subsystem {name}");
        // The team check creates the subsystem's folder and the folders inside it on this PC.
        await TeamWatcher.CheckNowAsync().ConfigureAwait(false);
    }

    /// <summary>Adds a folder inside a subsystem (e.g. "Fasteners"); it is created on every PC.</summary>
    public async Task AddSubsystemFolderAsync(string subsystemId, string name)
    {
        var (vaultUrl, me) = await ReviewContextAsync().ConfigureAwait(false);
        await TeamInvites.AddSubsystemFolderAsync(vaultUrl, me, subsystemId, name).ConfigureAwait(false);
        await TeamWatcher.CheckNowAsync().ConfigureAwait(false);
    }

    public async Task RemoveSubsystemFolderAsync(string subsystemId, string name)
    {
        var (vaultUrl, me) = await ReviewContextAsync().ConfigureAwait(false);
        await TeamInvites.RemoveSubsystemFolderAsync(vaultUrl, me, subsystemId, name).ConfigureAwait(false);
        _ = TeamWatcher.CheckNowAsync();
    }

    public async Task UpdateCarAsync(string carId, string name, string? folder)
    {
        var (vaultUrl, me) = await ReviewContextAsync().ConfigureAwait(false);
        await TeamInvites.UpdateCarAsync(vaultUrl, me, carId, name, folder).ConfigureAwait(false);
        _ = TeamWatcher.CheckNowAsync();
    }

    public async Task RemoveCarAsync(string carId)
    {
        var (vaultUrl, me) = await ReviewContextAsync().ConfigureAwait(false);
        await TeamInvites.RemoveCarAsync(vaultUrl, me, carId).ConfigureAwait(false);
        _ = TeamWatcher.CheckNowAsync();
    }

    /// <summary>Renames a subsystem or moves it to another folder, which is created in the vault folder on this PC.</summary>
    public async Task UpdateSubsystemAsync(string subsystemId, string name, string? folder)
    {
        var (vaultUrl, me) = await ReviewContextAsync().ConfigureAwait(false);
        await TeamInvites.UpdateSubsystemAsync(vaultUrl, me, subsystemId, name, folder).ConfigureAwait(false);
        var local = folder == null ? null : LocalPathOf(folder);
        if (local != null) Directory.CreateDirectory(local);
        _ = TeamWatcher.CheckNowAsync();
    }

    public async Task RemoveSubsystemAsync(string subsystemId)
    {
        var (vaultUrl, me) = await ReviewContextAsync().ConfigureAwait(false);
        await TeamInvites.RemoveSubsystemAsync(vaultUrl, me, subsystemId).ConfigureAwait(false);
        _ = TeamWatcher.CheckNowAsync();
    }

    /// <summary>Asks to be a responsible engineer; returns true when approved at once (admins).</summary>
    public async Task<bool> ClaimEngineerAsync(string subsystemId)
    {
        var (vaultUrl, me) = await ReviewContextAsync().ConfigureAwait(false);
        var approved = await TeamInvites.ClaimEngineerAsync(vaultUrl, me, subsystemId).ConfigureAwait(false);
        _ = TeamWatcher.CheckNowAsync();
        return approved;
    }

    public async Task RemoveEngineerAsync(string subsystemId, string login)
    {
        var (vaultUrl, me) = await ReviewContextAsync().ConfigureAwait(false);
        await TeamInvites.RemoveEngineerAsync(vaultUrl, me, subsystemId, login).ConfigureAwait(false);
        _ = TeamWatcher.CheckNowAsync();
    }

    public async Task<PendingApprovals> ApprovalsAsync()
    {
        var (vaultUrl, me) = await ReviewContextAsync().ConfigureAwait(false);
        return await TeamInvites.ApprovalsAsync(vaultUrl, me).ConfigureAwait(false);
    }

    public async Task DecideLeadAsync(string login, bool approve)
    {
        var (vaultUrl, me) = await ReviewContextAsync().ConfigureAwait(false);
        await TeamInvites.DecideLeadAsync(vaultUrl, me, login, approve).ConfigureAwait(false);
        Log.Info($"{(approve ? "Approved" : "Declined")} {login} as lead");
        _ = TeamWatcher.CheckNowAsync();
    }

    public async Task DecideEngineerAsync(string login, string subsystemId, bool approve)
    {
        var (vaultUrl, me) = await ReviewContextAsync().ConfigureAwait(false);
        await TeamInvites.DecideEngineerAsync(vaultUrl, me, login, subsystemId, approve).ConfigureAwait(false);
        Log.Info($"{(approve ? "Approved" : "Declined")} {login} as responsible engineer of {subsystemId}");
        _ = TeamWatcher.CheckNowAsync();
    }

    /// <summary>
    /// A new folder directly inside a subsystem is added to that subsystem's folders, so the team
    /// service creates it on everyone's PC. Folders anywhere else stay local until a file is checked
    /// in to them. Returns whether it was shared.
    /// </summary>
    private async Task<bool> ShareFolderAsync(VaultSession session, string vaultFolder)
    {
        try
        {
            var subsystem = TeamWatcher.SubsystemFor(vaultFolder + "/x");
            if (subsystem == null) return false;
            if (!string.Equals(Core.Util.PathRules.Normalize(subsystem.Folder), Core.Util.PathRules.GetFolder(vaultFolder), StringComparison.OrdinalIgnoreCase)) return false;
            await AddSubsystemFolderAsync(subsystem.Id, Core.Util.PathRules.GetFileName(vaultFolder)).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is VaultException or HttpRequestException or TaskCanceledException)
        {
            Log.Warn($"Could not share the folder {vaultFolder} with the team: {ex.Message}");
            return false;
        }
    }

    /// <summary>For the add-in's task pane: the subsystem a local file belongs to, from the last team check.</summary>
    public async Task<SubsystemInfo?> SubsystemInfoAsync(string localPath)
    {
        var session = await Vaults.FindByLocalPathAsync(localPath).ConfigureAwait(false);
        var vaultPath = session == null ? null : Core.Util.PathRules.ToVaultPath(session.LocalRoot, localPath);
        var subsystem = vaultPath == null ? null : TeamWatcher.SubsystemFor(vaultPath);
        if (subsystem == null) return null;
        return new SubsystemInfo
        {
            Car = subsystem.CarName,
            Name = subsystem.Name,
            Folder = subsystem.Folder,
            Engineers = subsystem.Engineers.Where(e => e.Approved).Select(e => e.Login).ToArray(),
            PendingEngineers = subsystem.Engineers.Where(e => !e.Approved).Select(e => e.Login).ToArray(),
        };
    }

    /// <summary>
    /// Responsible engineers to cc on my review request: only for general members' requests, only
    /// approved engineers of the file's subsystem, never me or the lead I'm asking.
    /// </summary>
    internal static (IReadOnlyList<string> Cc, string? Reason) ReviewCc(TeamDirectory directory, IReadOnlyList<Car> cars, string me, string lead, string vaultPath)
    {
        var myProfile = directory.People.FirstOrDefault(p => string.Equals(p.Login, me, StringComparison.OrdinalIgnoreCase))?.Profile;
        if (myProfile?.IsLead == true) return (Array.Empty<string>(), null);
        var subsystem = TeamInvites.SubsystemFor(cars, vaultPath);
        if (subsystem == null) return (Array.Empty<string>(), null);
        var cc = subsystem.ApprovedEngineers
            .Where(e => !string.Equals(e, me, StringComparison.OrdinalIgnoreCase) && !string.Equals(e, lead, StringComparison.OrdinalIgnoreCase))
            .ToList();
        return (cc, cc.Count == 0 ? null : $"responsible engineers of {subsystem.CarName} / {subsystem.Name}");
    }

    // ---------------------------------------------------------------- review requests

    private async Task<(string VaultUrl, Core.Auth.Credential Me)> ReviewContextAsync()
    {
        var target = InviteTarget() ?? throw VaultException.NotFound("Connect to your team's vault first.");
        return (target.VaultUrl, await MyCredentialAsync(target.VaultUrl).ConfigureAwait(false));
    }

    /// <summary>The vault path and checked-in version a review of this local file refers to.</summary>
    public async Task<(string VaultPath, int Version, bool ModifiedLocally)> ReviewSubjectAsync(string localPath)
    {
        var session = await Vaults.FindByLocalPathAsync(localPath).ConfigureAwait(false)
            ?? throw VaultException.BadRequest(Path.GetFileName(localPath) + " isn't in a vault folder.");
        var status = await session.GetStatusAsync(localPath).ConfigureAwait(false);
        if (status == null || status.ServerVersion == 0)
            throw VaultException.BadRequest(Path.GetFileName(localPath) + " isn't in the vault yet. Add it (Add to Vault) or check it in first, so the lead can open it.");
        var modified = status.LocalState is LocalState.Modified or LocalState.Conflict;
        return (status.Path, status.ServerVersion, modified);
    }

    public async Task<ReviewRequest> RequestReviewAsync(string localPath, ReviewKind kind, string lead, string? message)
    {
        var (vaultPath, version, _) = await ReviewSubjectAsync(localPath).ConfigureAwait(false);
        var (vaultUrl, me) = await ReviewContextAsync().ConfigureAwait(false);
        IReadOnlyList<string> cc = Array.Empty<string>();
        string? ccReason = null;
        try
        {
            var directory = await TeamInvites.PeopleAsync(vaultUrl, me).ConfigureAwait(false);
            var cars = await TeamInvites.CarsAsync(vaultUrl, me).ConfigureAwait(false);
            (cc, ccReason) = ReviewCc(directory, cars, me.UserName, lead, vaultPath);
        }
        catch (VaultException ex)
        {
            Log.Warn("Couldn't work out who to cc on the review: " + ex.Message); // the request itself still goes out
        }
        var review = await Reviews.RequestAsync(vaultUrl, me, kind, lead, vaultPath, version, message, cc, ccReason).ConfigureAwait(false);
        Log.Info($"Review #{review.Number} ({kind}) of {vaultPath} v{version} requested from {lead}" + (cc.Count > 0 ? ", cc " + string.Join(", ", cc) : ""));
        _ = EmailAboutReviewAsync(vaultUrl, me, review.Number);
        _ = ReviewWatcher.CheckNowAsync();
        return review;
    }

    /// <summary>Review requests on one local file, with their discussion, for the add-in's file browser.</summary>
    public async Task<ReviewDto[]> ReviewsForFileAsync(string localPath)
    {
        var session = await Vaults.FindByLocalPathAsync(localPath).ConfigureAwait(false)
            ?? throw VaultException.NotFound($"{localPath} is not inside a vault folder.");
        var status = await session.GetStatusAsync(localPath).ConfigureAwait(false);
        if (status == null || status.IsFolder) return Array.Empty<ReviewDto>();
        var (vaultUrl, me) = await ReviewContextAsync().ConfigureAwait(false);
        var reviews = await Reviews.ForFileAsync(vaultUrl, me, status.Path).ConfigureAwait(false);
        var result = new List<ReviewDto>();
        foreach (var r in reviews.Take(20))
        {
            var comments = await Reviews.CommentsAsync(vaultUrl, me, r.Number).ConfigureAwait(false);
            result.Add(new ReviewDto
            {
                Number = r.Number,
                Kind = r.KindText,
                Version = r.Version,
                Requester = r.Requester,
                Lead = r.Lead,
                Status = r.StatusText,
                Message = string.IsNullOrWhiteSpace(r.Message) ? null : r.Message,
                Created = r.Created.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture),
                Updated = r.Updated.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture),
                WebUrl = r.WebUrl,
                Cc = r.CcLogins.Count == 0 ? null : r.CcLogins.ToArray(),
                Comments = comments.Select(c => new ReviewCommentDto
                {
                    Author = c.Author,
                    At = c.At.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture),
                    Text = c.Text,
                }).ToArray(),
            });
        }
        return result.ToArray();
    }

    public async Task<IReadOnlyList<ReviewRequest>> ListReviewsAsync(bool forMe, bool includeClosed)
    {
        var (vaultUrl, me) = await ReviewContextAsync().ConfigureAwait(false);
        return await Reviews.ListAsync(vaultUrl, me, forMe, includeClosed).ConfigureAwait(false);
    }

    /// <summary>Reviews I'm cc'd on as a responsible engineer.</summary>
    public async Task<IReadOnlyList<ReviewRequest>> ListCcReviewsAsync(bool includeClosed)
    {
        var (vaultUrl, me) = await ReviewContextAsync().ConfigureAwait(false);
        return await Reviews.ListCcAsync(vaultUrl, me, includeClosed).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<ReviewComment>> ReviewCommentsAsync(ReviewRequest review)
    {
        var (vaultUrl, me) = await ReviewContextAsync().ConfigureAwait(false);
        return await Reviews.CommentsAsync(vaultUrl, me, review.Number).ConfigureAwait(false);
    }

    public async Task RespondToReviewAsync(ReviewRequest review, ReviewDecision decision, string? comment)
    {
        var (vaultUrl, me) = await ReviewContextAsync().ConfigureAwait(false);
        await Reviews.RespondAsync(vaultUrl, me, review, decision, comment).ConfigureAwait(false);
        Log.Info($"Review #{review.Number}: {decision}");
        if (decision is ReviewDecision.Approve or ReviewDecision.RequestChanges) _ = EmailAboutReviewAsync(vaultUrl, me, review.Number);
        _ = ReviewWatcher.CheckNowAsync();
    }

    /// <summary>Best effort: the review itself is already saved; email is a courtesy.</summary>
    private async Task EmailAboutReviewAsync(string vaultUrl, Core.Auth.Credential me, int number)
    {
        try
        {
            await TeamInvites.NotifyReviewAsync(vaultUrl, me, number).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Warn($"Couldn't email about review #{number}: {ex.Message}");
        }
    }

    /// <summary>Local path of a vault file in the vault this PC is connected to (for "Open file").</summary>
    public string? LocalPathOf(string vaultPath)
    {
        var target = InviteTarget();
        var registration = Vaults.Registrations.FirstOrDefault(r => target != null && string.Equals(r.RemoteUrl, target.Value.VaultUrl, StringComparison.OrdinalIgnoreCase))
            ?? Vaults.Registrations.FirstOrDefault();
        return registration == null ? null : Path.Combine(registration.LocalRoot, vaultPath.Replace('/', Path.DirectorySeparatorChar));
    }

    private async Task DownloadAllAsync(VaultSession session)
    {
        RaiseToast("SwVault", $"Signed in. Downloading the {session.Config.Name} files to {session.LocalRoot}...");
        var job = await Jobs.StartAsync(new JobRequest { Kind = JobKind.GetLatest, VaultId = session.VaultId, AutoApply = true }).ConfigureAwait(false);
        if (job.State == JobState.Completed)
            RaiseToast("SwVault", $"All {session.Config.Name} files are in {session.LocalRoot}. Open them from SOLIDWORKS.");
        else
            RaiseToast("SwVault", "Downloading the vault didn't finish: " + (job.Error ?? job.State.ToString()) + " Use Get Latest in SOLIDWORKS to retry.");
    }

    private void Broadcast(string method, object payload) => Server.Broadcast(method, payload);

    /// <summary>Folders were created in the vault folder outside a job: clients refresh their folder views.</summary>
    public void NotifyFoldersChanged() => Broadcast(Notifications.StatusChanged, new StatusChangedNotification { FullRefresh = true });

    private void RaiseToast(string title, string message)
    {
        Toast?.Invoke(title, message);
        Broadcast(Notifications.Toast, new ToastNotification { Title = title, Message = message });
    }

    /// <summary>Lets the add-in find and start the agent (HKCU\Software\SwVault\AgentPath).</summary>
    private void RegisterLocation()
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(@"Software\SwVault");
            key.SetValue(Profile.Name == "default" ? "AgentPath" : "AgentPath." + Profile.Name, Environment.ProcessPath ?? "");
        }
        catch (Exception ex)
        {
            Log.Warn("Could not record the agent location: " + ex.Message);
        }
    }

    /// <summary>
    /// A vault folder that was there before but isn't now was usually moved or renamed in Explorer.
    /// SwVault would quietly download everything again into a new, empty folder, while any work not
    /// checked in sits wherever the folder went, so say so before that happens.
    /// </summary>
    private void WarnAboutMissingVaultFolders()
    {
        foreach (var registration in Vaults.Registrations)
        {
            var usedBefore = File.Exists(Path.Combine(Profile.VaultDataDir(registration.Id), "state.db"));
            if (!usedBefore || Directory.Exists(registration.LocalRoot)) continue;
            Log.Warn($"Vault folder {registration.LocalRoot} of {registration.Name} is missing.");
            RaiseToast("SwVault: vault folder missing",
                $"{registration.LocalRoot} isn't there anymore. If you moved or renamed it, move it back (SwVault and SOLIDWORKS look for files there). " +
                "Otherwise SwVault downloads the vault into it again; files you hadn't checked in are only in the old copy.");
        }
    }

    /// <summary>
    /// Registers swvault:// for this user (on every start, so it follows the installed location), so
    /// the web file browser's "Open in SOLIDWORKS" and other buttons reach this agent.
    /// </summary>
    private void RegisterLinkScheme()
    {
        if (Profile.Name != "default") return; // one handler per user: the normal install
        try
        {
            var exe = Environment.ProcessPath ?? "";
            using var key = Registry.CurrentUser.CreateSubKey(@"Software\Classes\" + VaultLink.Scheme);
            key.SetValue(null, "URL:SwVault");
            key.SetValue("URL Protocol", "");
            using (var icon = key.CreateSubKey("DefaultIcon")) icon.SetValue(null, "\"" + exe + "\",0");
            using var command = key.CreateSubKey(@"shell\open\command");
            command.SetValue(null, "\"" + exe + "\" --link \"%1\"");
        }
        catch (Exception ex)
        {
            Log.Warn("Could not register swvault:// links: " + ex.Message);
        }
    }

    /// <summary>
    /// A swvault:// link from the web file browser. With SOLIDWORKS open, the add-in runs the same
    /// command as its task pane; otherwise the agent does what it can on its own (open the file in
    /// SOLIDWORKS, get latest, check out, request a review).
    /// </summary>
    public async Task HandleLinkAsync(string uri)
    {
        // The website's "Test the link to this PC": proves the browser can reach this agent.
        if (uri.TrimEnd('/').StartsWith(VaultLink.Scheme + "://ping", StringComparison.OrdinalIgnoreCase) || uri.StartsWith(VaultLink.Scheme + ":ping", StringComparison.OrdinalIgnoreCase))
        {
            Log.Info("Link: test from the website");
            var solidworks = Server.SendTo(AddInClientName, Notifications.StatusChanged, new StatusChangedNotification { FullRefresh = false });
            RaiseToast("SwVault", "The website can reach SwVault on this PC (version " + (typeof(AgentHost).Assembly.GetName().Version?.ToString(3) ?? "?") + "). "
                + (solidworks ? "SOLIDWORKS is open and connected." : "SOLIDWORKS isn't open; files will open in it when you click Open in SOLIDWORKS."));
            return;
        }
        var link = VaultLink.TryParse(uri);
        if (link == null)
        {
            Log.Warn("Ignored a link that isn't a SwVault file link: " + uri);
            RaiseToast("SwVault", "That link isn't a SwVault file link.");
            return;
        }
        Log.Info("Link: " + link);
        try
        {
            var session = await SessionForLinkAsync(link).ConfigureAwait(false);
            var local = session.LocalPathOf(link.Path);
            if (session.Head.Get(link.Path) == null && !File.Exists(local))
                throw VaultException.NotFound($"{link.Path} isn't in the {session.Config.Name} vault (was it renamed or deleted?).");

            var action = new VaultActionNotification { Action = link.Action, LocalPath = local, VaultId = session.VaultId };
            if (Server.SendTo(AddInClientName, Notifications.VaultAction, action)) return;

            var name = Path.GetFileName(local);
            switch (link.Action)
            {
                case VaultLink.Open:
                    await RunLinkJobAsync(session, JobKind.GetLatest, link.Path, "Getting " + name).ConfigureAwait(false);
                    if (!File.Exists(local)) throw VaultException.NotFound(name + " couldn't be downloaded.");
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(local) { UseShellExecute = true });
                    break;
                case VaultLink.GetLatest:
                    await RunLinkJobAsync(session, JobKind.GetLatest, link.Path, "Getting " + name).ConfigureAwait(false);
                    RaiseToast("SwVault", name + " and the files it uses are up to date.");
                    break;
                case VaultLink.CheckOut:
                    if (!Confirm($"Check out {name}?\n\nA web page asked SwVault to lock it for you to edit. Only continue if you clicked Check Out in the SwVault file browser.")) return;
                    await RunLinkJobAsync(session, JobKind.CheckOut, link.Path, "Checking out " + name).ConfigureAwait(false);
                    RaiseToast("SwVault", name + " is checked out to you.");
                    break;
                case VaultLink.RequestReview:
                    WindowRequested?.Invoke("requestReview|" + local);
                    break;
                default:
                    RaiseToast("SwVault", "Open SOLIDWORKS first: " + link.Action switch
                    {
                        VaultLink.CheckIn => "checking in",
                        VaultLink.History => "the version history",
                        VaultLink.ChangeState => "changing the workflow state",
                        _ => link.Action,
                    } + " runs in SOLIDWORKS. Then click the button again.");
                    break;
            }
        }
        catch (VaultException ex)
        {
            Log.Warn($"Link {link.Action} {link.Path}: {ex.Message}");
            RaiseToast("SwVault", ex.Message);
        }
        catch (Exception ex)
        {
            Log.Error($"Link {link.Action} {link.Path} failed", ex);
            RaiseToast("SwVault", "That didn't work: " + ex.Message);
        }
    }

    /// <summary>Client name the SOLIDWORKS add-in introduces itself with (AgentConnection).</summary>
    internal const string AddInClientName = "SOLIDWORKS add-in";

    private async Task<VaultSession> SessionForLinkAsync(VaultLink link)
    {
        var registrations = Vaults.Registrations;
        var match = link.VaultUrl == null ? null : registrations.FirstOrDefault(r => VaultLink.SameVault(r.RemoteUrl, link.VaultUrl));
        if (match == null && registrations.Count == 1 && (link.VaultUrl == null || Team != null && VaultLink.SameVault(Team.VaultUrl, link.VaultUrl)
                || SameVaultPath(registrations[0].RemoteUrl, link.VaultUrl)))
            match = registrations[0];
        if (match == null)
            throw VaultException.NotFound(registrations.Count == 0
                ? "Sign in to SwVault first (click the tray icon), then click the link again."
                : "That file is in a vault this PC isn't connected to.");
        return await Vaults.GetAsync(match.Id).ConfigureAwait(false);
    }

    /// <summary>
    /// The same owner/repository on a different host: older servers put their internal address
    /// (http://127.0.0.1:3000/team/Vault) in links. Only used when this PC has a single vault.
    /// </summary>
    private static bool SameVaultPath(string a, string? b)
    {
        static string? PathOf(string? url) =>
            Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.AbsolutePath.Trim('/').ToLowerInvariant() is var p && p.EndsWith(".git", StringComparison.Ordinal) ? p[..^4] : uri.AbsolutePath.Trim('/').ToLowerInvariant() : null;
        var pa = PathOf(a);
        return !string.IsNullOrEmpty(pa) && pa == PathOf(b);
    }

    private async Task RunLinkJobAsync(VaultSession session, JobKind kind, string vaultPath, string what)
    {
        var job = await Jobs.StartAsync(new JobRequest { Kind = kind, VaultId = session.VaultId, Paths = new[] { session.LocalPathOf(vaultPath) }, WithReferences = true, AutoApply = true }).ConfigureAwait(false);
        if (job.State != JobState.Completed) throw new VaultException(job.ErrorCode != 0 ? job.ErrorCode : ErrorCodes.Internal, what + " failed: " + (job.Error ?? job.State.ToString()));
        if (job.Skipped is { Length: > 0 })
            RaiseToast("SwVault", "Some files were in use and weren't replaced: " + string.Join(", ", job.Skipped.Select(Path.GetFileName)));
    }

    /// <summary>A yes/no question in front of everything (links arrive while the browser has focus).</summary>
    private static bool Confirm(string message)
    {
        var answer = false;
        var thread = new Thread(() =>
        {
            using var owner = new Form { TopMost = true, ShowInTaskbar = false, StartPosition = FormStartPosition.CenterScreen, Size = new System.Drawing.Size(1, 1), FormBorderStyle = FormBorderStyle.None };
            owner.Show();
            answer = MessageBox.Show(owner, message, "SwVault", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes;
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        return answer;
    }

    private async Task ResumePendingAsync()
    {
        foreach (var registration in Vaults.Registrations)
        {
            try
            {
                var session = await Vaults.GetAsync(registration.Id).ConfigureAwait(false);
                await session.ResumePendingAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log.Warn($"Could not finish pending work for {registration.Id}: {ex.Message}");
            }
        }
    }

    public void Dispose()
    {
        _migrationPolling.Cancel();
        Log.Info("SwVault agent stopping");
        ReviewWatcher.Dispose();
        TeamWatcher.Dispose();
        Updates.Dispose();
        Sync.Dispose();
        Server.Dispose();
        Vaults.Dispose();
    }
}
