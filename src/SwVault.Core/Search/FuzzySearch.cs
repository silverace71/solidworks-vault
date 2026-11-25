namespace SwVault.Core.Search;

/// <summary>One searchable piece of a file: its name, folder, a custom property, the last comment, ...</summary>
public sealed record SearchField(string Label, string Value, double Weight);

/// <summary>A file that matched every term of a query.</summary>
public sealed class SearchHit
{
    public double Score { get; init; }

    /// <summary>Character positions in the file name that matched, for highlighting.</summary>
    public int[] NamePositions { get; init; } = Array.Empty<int>();

    /// <summary>The best match outside the name ("Material", "6061-T6"), or null when the name explains the hit.</summary>
    public string? MatchLabel { get; init; }
    public string? MatchValue { get; init; }
}

/// <summary>
/// Multi-term fuzzy search over a file's fields. Every term must match some field (in any order).
/// <c>label:term</c> restricts a term to fields whose label starts with <c>label</c>
/// ("material:6061", "by:alice", "state:released", "type:sldasm").
/// </summary>
public sealed class FuzzyQuery
{
    public const string NameLabel = "Name";

    private readonly List<(string? Label, string Text)> _terms;

    private FuzzyQuery(List<(string? Label, string Text)> terms) => _terms = terms;

    public bool IsEmpty => _terms.Count == 0;

    public static FuzzyQuery Parse(string? query)
    {
        var terms = new List<(string? Label, string Text)>();
        foreach (var raw in Tokenize(query ?? ""))
        {
            var colon = raw.IndexOf(':');
            if (colon > 0 && colon < raw.Length - 1) terms.Add((raw.Substring(0, colon), raw.Substring(colon + 1)));
            else terms.Add((null, raw.Trim(':')));
        }
        return new FuzzyQuery(terms.Where(t => t.Text.Length > 0).ToList());
    }

    /// <summary>Whitespace-separated terms; "double quotes" keep spaces in one term.</summary>
    private static IEnumerable<string> Tokenize(string query)
    {
        var current = new System.Text.StringBuilder();
        var quoted = false;
        foreach (var c in query)
        {
            if (c == '"') quoted = !quoted;
            else if (char.IsWhiteSpace(c) && !quoted)
            {
                if (current.Length > 0) yield return current.ToString();
                current.Clear();
            }
            else current.Append(c);
        }
        if (current.Length > 0) yield return current.ToString();
    }

    /// <summary>Scores a file; null when some term matches none of its fields.</summary>
    public SearchHit? Score(IReadOnlyList<SearchField> fields)
    {
        if (IsEmpty) return new SearchHit();
        double total = 0;
        var namePositions = new SortedSet<int>();
        (double Score, SearchField Field)? bestOther = null;
        var allInName = true;

        foreach (var (label, text) in _terms)
        {
            (double Score, SearchField Field, FuzzyMatch Match)? best = null;
            foreach (var field in fields)
            {
                if (label != null && !field.Label.StartsWith(label, StringComparison.OrdinalIgnoreCase)) continue;
                var match = FuzzyMatcher.Match(text, field.Value);
                if (match == null) continue;
                var weighted = match.Score * field.Weight;
                if (best == null || weighted > best.Value.Score) best = (weighted, field, match);
            }
            if (best == null) return null;

            total += best.Value.Score;
            if (best.Value.Field.Label == NameLabel)
            {
                foreach (var p in best.Value.Match.Positions) namePositions.Add(p);
            }
            else
            {
                allInName = false;
                if (bestOther == null || best.Value.Score > bestOther.Value.Score) bestOther = (best.Value.Score, best.Value.Field);
            }
        }

        if (allInName) total += 25; // everything in the file name beats a scattered hit across fields
        return new SearchHit
        {
            Score = total,
            NamePositions = namePositions.ToArray(),
            MatchLabel = bestOther?.Field.Label,
            MatchValue = bestOther?.Field.Value,
        };
    }
}
