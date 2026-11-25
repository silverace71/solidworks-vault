namespace SwVault.Core.Vault;

/// <summary>
/// Sidecar stored at <c>.swvault/meta/&lt;path&gt;.json</c> and committed together with the file.
/// Its git history is the file's version history.
/// </summary>
public sealed class FileMeta
{
    public int Schema { get; set; } = 1;

    /// <summary>Content version: increments only when the file content changes.</summary>
    public int Version { get; set; }

    /// <summary>LFS oid (sha256 hex) of this version's content.</summary>
    public string Oid { get; set; } = "";
    public long Size { get; set; }

    public string? CheckedInBy { get; set; }
    public string? CheckedInAt { get; set; }
    public string? Comment { get; set; }

    public string? State { get; set; }
    public string? Revision { get; set; }
    public List<RevisionEntry>? RevisionHistory { get; set; }

    /// <summary>Who approved the latest release (the person who ran the releasing transition), when, and which version.</summary>
    public string? ApprovedBy { get; set; }
    public string? ApprovedAt { get; set; }
    public int? ApprovedVersion { get; set; }
    /// <summary>The transition that released it ("Approve"), and its optional note.</summary>
    public string? ApprovedWith { get; set; }
    public string? ApprovalNote { get; set; }

    /// <summary>Direct references with the exact child versions the author had ("as built").</summary>
    public List<ReferenceEntry>? References { get; set; }
    public List<string>? ExternalReferences { get; set; }

    public SortedDictionary<string, string>? Properties { get; set; }
    public List<string>? Configurations { get; set; }
    public string? SwVersion { get; set; }

    /// <summary>For generated exports (PDF/STEP): the vault path of the source model or drawing.</summary>
    public string? DerivedFrom { get; set; }

    /// <summary>The vault path this file was moved here from; its history carries on there.</summary>
    public string? MovedFrom { get; set; }

    public FileMeta Clone()
    {
        return new FileMeta
        {
            Schema = Schema,
            Version = Version,
            Oid = Oid,
            Size = Size,
            CheckedInBy = CheckedInBy,
            CheckedInAt = CheckedInAt,
            Comment = Comment,
            State = State,
            Revision = Revision,
            RevisionHistory = RevisionHistory?.Select(r => new RevisionEntry { Rev = r.Rev, Version = r.Version, By = r.By, At = r.At }).ToList(),
            ApprovedBy = ApprovedBy,
            ApprovedAt = ApprovedAt,
            ApprovedVersion = ApprovedVersion,
            ApprovedWith = ApprovedWith,
            ApprovalNote = ApprovalNote,
            References = References?.Select(r => new ReferenceEntry { Path = r.Path, Version = r.Version, Oid = r.Oid }).ToList(),
            ExternalReferences = ExternalReferences?.ToList(),
            Properties = Properties == null ? null : new SortedDictionary<string, string>(Properties, StringComparer.Ordinal),
            Configurations = Configurations?.ToList(),
            SwVersion = SwVersion,
            DerivedFrom = DerivedFrom,
            MovedFrom = MovedFrom,
        };
    }
}

public sealed class RevisionEntry
{
    public string Rev { get; set; } = "";
    public int Version { get; set; }
    public string? By { get; set; }
    public string? At { get; set; }
}

public sealed class ReferenceEntry
{
    public string Path { get; set; } = "";
    public int Version { get; set; }
    public string? Oid { get; set; }
}
