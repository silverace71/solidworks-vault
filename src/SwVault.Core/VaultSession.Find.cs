using SwVault.Core.Index;
using SwVault.Core.Search;
using SwVault.Core.Util;
using SwVault.Core.Vault;
using SwVault.Protocol;

namespace SwVault.Core;

public sealed partial class VaultSession
{
    /// <summary>
    /// Fuzzy search for the file browser: scores every candidate from the head index (cheap), then
    /// computes local status only for the best ones. An empty query lists assemblies first, the top level at the top.
    /// </summary>
    public async Task<IReadOnlyList<FindResultDto>> FindAsync(string? query, StatusFilter filter, IReadOnlyCollection<string>? extensions, int max, CancellationToken ct = default)
    {
        if (max <= 0) max = 200;
        var parsed = FuzzyQuery.Parse(query);
        var wanted = extensions is { Count: > 0 } ? new HashSet<string>(extensions.Select(e => e.StartsWith('.') ? e : "." + e), StringComparer.OrdinalIgnoreCase) : null;

        var scored = new List<(string Path, HeadFile? Head, SearchHit Hit)>();
        foreach (var path in Candidates(filter))
        {
            if (wanted != null && !wanted.Contains(Path.GetExtension(path))) continue;
            var head = _head.Get(path);
            var hit = parsed.Score(SearchFields(path, head));
            if (hit != null) scored.Add((path, head, hit));
        }

        // Without a query: assemblies first with the top level at the top, then parts, drawings and the rest.
        static int TypeRank(string path) => Path.GetExtension(path).ToLowerInvariant() switch { ".sldasm" => 0, ".sldprt" => 1, ".slddrw" => 2, _ => 3 };
        IEnumerable<(string Path, HeadFile? Head, SearchHit Hit)> ordered = parsed.IsEmpty
            ? scored.OrderBy(s => TypeRank(s.Path))
                .ThenBy(s => TypeRank(s.Path) == 0 && s.Head != null ? _head.LevelOf(s.Head.Path) : 0)
                .ThenBy(s => PathRules.GetFileName(s.Path), StringComparer.OrdinalIgnoreCase)
                .ThenBy(s => s.Path, PathRules.Comparer)
            : scored.OrderByDescending(s => s.Hit.Score).ThenBy(s => s.Path.Length).ThenBy(s => s.Path, PathRules.Comparer);

        var result = new List<FindResultDto>();
        foreach (var (path, head, hit) in ordered)
        {
            if (result.Count >= max) break;
            var status = await FilteredStatusAsync(path, head, filter, ct).ConfigureAwait(false);
            if (status == null) continue;
            result.Add(new FindResultDto
            {
                Status = status,
                Score = hit.Score,
                NameMatches = hit.NamePositions.Length == 0 ? null : hit.NamePositions,
                MatchField = hit.MatchLabel,
                MatchText = hit.MatchValue,
            });
        }
        return result;
    }

    /// <summary>What a file can be found by. Weights rank a name match above a folder or property match.</summary>
    internal List<SearchField> SearchFields(string path, HeadFile? head)
    {
        var fields = new List<SearchField>
        {
            new(FuzzyQuery.NameLabel, PathRules.GetFileName(path), 1.0),
            new("Folder", PathRules.GetFolder(path), 0.6),
            new("Type", Path.GetExtension(path).TrimStart('.'), 0.5),
        };
        var meta = head?.Meta;
        if (meta != null)
        {
            foreach (var (key, value) in meta.Properties ?? new SortedDictionary<string, string>())
                if (!string.IsNullOrWhiteSpace(value)) fields.Add(new SearchField(key, value, 0.85));
            foreach (var configuration in meta.Configurations ?? new List<string>())
                fields.Add(new SearchField("Configuration", configuration, 0.6));
            if (!string.IsNullOrEmpty(meta.Comment)) fields.Add(new SearchField("Comment", meta.Comment, 0.5));
            if (!string.IsNullOrEmpty(meta.CheckedInBy)) fields.Add(new SearchField("By", meta.CheckedInBy, 0.5));
            if (!string.IsNullOrEmpty(meta.Revision)) fields.Add(new SearchField("Revision", meta.Revision, 0.5));
        }
        if (head != null) fields.Add(new SearchField("State", WorkflowEngine.CurrentState(meta, Config), 0.5));
        if (_locks.TryGetValue(path, out var lockRecord)) fields.Add(new SearchField("Checked out by", lockRecord.Owner, 0.5));
        return fields;
    }

    /// <summary>Everything the vault records about one file, for the browser's details panel.</summary>
    public async Task<FileDetailsDto?> GetDetailsAsync(string pathOrLocal, CancellationToken ct = default)
    {
        var status = await GetStatusAsync(pathOrLocal, ct).ConfigureAwait(false);
        if (status == null || status.IsFolder) return null;
        var head = _head.Get(status.Path);
        var meta = head?.Meta;
        return new FileDetailsDto
        {
            Status = status,
            Oid = head?.Oid,
            Configurations = meta?.Configurations?.ToArray(),
            SwVersion = meta?.SwVersion,
            Revisions = meta?.RevisionHistory?.Select(r => new RevisionDto { Rev = r.Rev, Version = r.Version, By = r.By, At = r.At }).ToArray(),
            References = meta?.References?.Select(r => new ReferenceDto { Path = r.Path, Version = r.Version }).ToArray(),
            ExternalReferences = meta?.ExternalReferences?.ToArray(),
            WhereUsed = WhereUsed(status.Path).Select(LocalPathOf).ToArray(),
            DerivedFrom = meta?.DerivedFrom,
            ApprovedBy = meta?.ApprovedBy,
            ApprovedAt = meta?.ApprovedAt,
            ApprovedVersion = meta?.ApprovedVersion ?? 0,
            ApprovedWith = meta?.ApprovedWith,
            ApprovalNote = meta?.ApprovalNote,
            HasThumbnail = _head.Thumbnails.Contains(status.Path),
        };
    }

    /// <summary>The preview saved at check-in, at head or at a commit from the file's history; null when there is none.</summary>
    public async Task<byte[]?> GetThumbnailAsync(string pathOrLocal, string? commit, CancellationToken ct = default)
    {
        var vaultPath = ResolveVaultPath(pathOrLocal);
        if (vaultPath == null) return null;
        var canonical = _head.Get(vaultPath)?.Path ?? PathRules.Normalize(vaultPath);
        if (string.IsNullOrEmpty(commit))
        {
            if (_head.Commit == null || !_head.Thumbnails.Contains(canonical)) return null;
            commit = _head.Commit;
        }
        else if (!System.Text.RegularExpressions.Regex.IsMatch(commit, "^[0-9a-fA-F]{7,64}$"))
        {
            throw VaultException.BadRequest("Not a commit id: " + commit);
        }
        return await Mirror.ReadBlobAsync($"{commit}:{PathRules.ThumbPathFor(canonical)}", ct).ConfigureAwait(false);
    }
}
