namespace SwVault.Protocol
{
    public static class ProtocolInfo
    {
        /// <summary>Bump when the wire contract changes incompatibly.</summary>
        public const int Version = 1;

        /// <summary>Per-user pipe name; the agent restricts its ACL to the same user.</summary>
        public static string PipeName(string userSid) => "SwVault." + userSid;

        /// <summary>COM class id of the SOLIDWORKS add-in (registry: HKLM\SOFTWARE\SolidWorks\Addins\{guid}).</summary>
        public const string AddInGuid = "7F3C2A51-9B4E-4C2D-A6E1-5D8B0F2C9E47";
    }

    /// <summary>Request method names (client -> agent).</summary>
    public static class Methods
    {
        public const string Hello = "hello";
        public const string VaultsList = "vaults.list";
        public const string VaultAdd = "vault.add";
        public const string TeamGet = "team.get";
        /// <summary>The subsystem (and its responsible engineers) a local file belongs to, or null.</summary>
        public const string SubsystemFor = "team.subsystemFor";
        /// <summary>Opens one of the agent's windows ("signIn", "invite") so the add-in doesn't duplicate them.</summary>
        public const string UiShow = "ui.show";
        public const string VaultSync = "vault.sync";
        public const string StatusGet = "status.get";
        public const string StatusFolder = "status.folder";
        /// <summary>Makes a folder in the vault folder; one directly inside a subsystem is shared with the whole team.</summary>
        public const string FolderCreate = "folder.create";
        public const string Search = "search";
        /// <summary>Fuzzy search over names, folders, properties and comments, best match first (file browser).</summary>
        public const string Find = "files.find";
        /// <summary>Everything the vault knows about one file (configurations, revisions, references, where used).</summary>
        public const string Details = "file.details";
        /// <summary>What would go wrong checking these files in (PathsRequest -> FileProblemDto[]); nothing changes.</summary>
        public const string CheckInPreflight = "checkin.preflight";
        public const string History = "file.history";
        /// <summary>The preview saved with a version at check-in (ThumbnailRequest -> ThumbnailDto).</summary>
        public const string Thumbnail = "file.thumbnail";
        /// <summary>Review requests on one file, with their comments (PathsRequest -> ReviewDto[]).</summary>
        public const string ReviewsForFile = "reviews.forFile";
        public const string References = "file.references";
        public const string WhereUsed = "file.whereUsed";
        public const string Transitions = "file.transitions";
        public const string Locks = "locks.list";
        /// <summary>A swvault:// link from the web file browser (LinkRequest); the agent runs or forwards it.</summary>
        public const string LinkHandle = "link.handle";
        public const string JobStart = "job.start";
        public const string JobApply = "job.apply";
        public const string JobCancel = "job.cancel";
        public const string JobGet = "job.get";
    }

    /// <summary>Notification names (agent -> clients).</summary>
    public static class Notifications
    {
        public const string StatusChanged = "status.changed";
        public const string JobUpdated = "job.updated";
        public const string Toast = "toast";
        /// <summary>To the SOLIDWORKS add-in: run a command on a file (a swvault:// link was opened).</summary>
        public const string VaultAction = "vault.action";
    }
}
