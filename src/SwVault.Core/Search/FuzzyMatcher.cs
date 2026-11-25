namespace SwVault.Core.Search;

/// <summary>How well one query term matched one piece of text.</summary>
public sealed record FuzzyMatch(double Score, int[] Positions);

/// <summary>
/// Scores a query term against a piece of text. Three kinds of match, best first:
/// a substring ("brack" in "FrontBracket"), a subsequence that favours word starts
/// ("fbr" in "Front_Bracket_Rev"), and a word with a typo ("braket" for "bracket").
/// Case-insensitive. Returns null when the term doesn't match.
/// </summary>
public static class FuzzyMatcher
{
    private const double SubstringBase = 100;
    private const double TypoBase = 45;

    public static FuzzyMatch? Match(string term, string? text)
    {
        if (string.IsNullOrEmpty(term) || string.IsNullOrEmpty(text)) return null;
        var t = term.ToLowerInvariant();
        var s = text.ToLowerInvariant();

        var best = Substring(t, s, text);
        if (best != null) return best;
        best = Subsequence(t, s, text);
        var typo = Typo(t, s, text);
        if (typo != null && (best == null || typo.Score > best.Score)) best = typo;
        return best;
    }

    /// <summary>Start of a word: the first character, after a separator, a lower-to-upper case change, or a letter/digit change.</summary>
    internal static bool IsWordStart(string original, int i)
    {
        if (i == 0) return true;
        char prev = original[i - 1], c = original[i];
        if (!char.IsLetterOrDigit(prev)) return char.IsLetterOrDigit(c);
        if (char.IsLower(prev) && char.IsUpper(c)) return true;
        return char.IsDigit(prev) != char.IsDigit(c);
    }

    private static FuzzyMatch? Substring(string t, string s, string original)
    {
        var bestIndex = -1;
        var bestScore = double.MinValue;
        for (var i = s.IndexOf(t, StringComparison.Ordinal); i >= 0; i = s.IndexOf(t, i + 1, StringComparison.Ordinal))
        {
            var score = SubstringBase + 2.0 * t.Length;
            if (IsWordStart(original, i)) score += 30;
            if (i == 0) score += 15;
            var end = i + t.Length;
            if (end == s.Length || IsWordStart(original, end) || !char.IsLetterOrDigit(original[end])) score += 10; // whole word
            if (score > bestScore)
            {
                bestScore = score;
                bestIndex = i;
            }
        }
        if (bestIndex < 0) return null;
        bestScore -= Math.Min(20, s.Length / 8.0); // shorter texts are more specific
        return new FuzzyMatch(bestScore, Enumerable.Range(bestIndex, t.Length).ToArray());
    }

    /// <summary>
    /// Every character of the term in order. Dynamic programming picks the alignment that lands on
    /// word starts and keeps runs together; weak scattered matches are rejected.
    /// </summary>
    private static FuzzyMatch? Subsequence(string t, string s, string original)
    {
        int n = t.Length, m = s.Length;
        if (n < 2 || n > m || m > 512) return null;

        // score[i, j]: best score with t[..i] matched and t[i] matched at s[j]. run[i, j]: consecutive length.
        var score = new double[n, m];
        var run = new int[n, m];
        var from = new int[n, m];
        const double None = double.MinValue;
        for (var i = 0; i < n; i++)
        for (var j = 0; j < m; j++)
            score[i, j] = None;

        for (var j = 0; j < m; j++)
        {
            if (s[j] != t[0]) continue;
            score[0, j] = 10 + (IsWordStart(original, j) ? 20 : 0) + (j == 0 ? 10 : 0) - Math.Min(10, j * 0.5);
            run[0, j] = 1;
            from[0, j] = -1;
        }
        for (var i = 1; i < n; i++)
        {
            // Best predecessor k <= j - 2 (a gap), ranked by score[i-1, k] + 0.5k so the gap penalty is accounted for.
            var gapKey = None;
            var gapIndex = -1;
            for (var j = i; j < m; j++)
            {
                if (j >= 2 && score[i - 1, j - 2] != None && score[i - 1, j - 2] + 0.5 * (j - 2) > gapKey)
                {
                    gapKey = score[i - 1, j - 2] + 0.5 * (j - 2);
                    gapIndex = j - 2;
                }
                if (s[j] != t[i]) continue;

                var wordStart = IsWordStart(original, j);
                var best = None;
                if (score[i - 1, j - 1] != None)
                {
                    best = score[i - 1, j - 1] + 10 + 8 * Math.Min(run[i - 1, j - 1], 4) + (wordStart ? 10 : 0);
                    from[i, j] = j - 1;
                    run[i, j] = run[i - 1, j - 1] + 1;
                }
                if (gapIndex >= 0)
                {
                    var gap = j - gapIndex - 1;
                    var viaGap = score[i - 1, gapIndex] + 10 + (wordStart ? 20 : -6) - Math.Min(12, gap * 0.5);
                    if (viaGap > best)
                    {
                        best = viaGap;
                        from[i, j] = gapIndex;
                        run[i, j] = 1;
                    }
                }
                score[i, j] = best;
            }
        }

        var end = -1;
        var total = None;
        for (var j = n - 1; j < m; j++)
            if (score[n - 1, j] != None && score[n - 1, j] > total)
            {
                total = score[n - 1, j];
                end = j;
            }
        if (end < 0) return null;

        var positions = new int[n];
        for (int i = n - 1, j = end; i >= 0; j = from[i, j], i--) positions[i] = j;

        // Reject scattered matches: most characters should start words or continue a run.
        var anchored = positions.Count(p => IsWordStart(original, p)) + positions.Zip(positions.Skip(1), (a, b) => b == a + 1 ? 1 : 0).Sum();
        if (anchored < (n + 1) / 2 + (n >= 4 ? 1 : 0)) return null;

        total = Math.Min(total, SubstringBase - 5) - Math.Min(10, m / 16.0);
        return new FuzzyMatch(total, positions);
    }

    /// <summary>A word (or a word's start) within a small edit distance of the term.</summary>
    private static FuzzyMatch? Typo(string t, string s, string original)
    {
        if (t.Length < 4) return null;
        var allowed = t.Length >= 8 ? 2 : 1;
        FuzzyMatch? best = null;
        foreach (var (start, length) in Words(original))
        {
            if (Math.Abs(length - t.Length) > allowed && length < t.Length) continue;
            // Compare against the whole word and against a prefix of the term's length (+/- allowed).
            for (var len = Math.Max(1, t.Length - allowed); len <= Math.Min(length, t.Length + allowed); len++)
            {
                if (len != length && len < t.Length - allowed) continue;
                var distance = Distance(t, s.Substring(start, len), allowed);
                if (distance > allowed) continue;
                var score = TypoBase + 2.0 * t.Length - 12 * distance - (len == length ? 0 : 5);
                if (best == null || score > best.Score) best = new FuzzyMatch(score, Enumerable.Range(start, len).ToArray());
            }
        }
        return best;
    }

    /// <summary>Word spans: runs of letters or digits, also split at camelCase and letter/digit changes.</summary>
    internal static IEnumerable<(int Start, int Length)> Words(string text)
    {
        var start = -1;
        for (var i = 0; i <= text.Length; i++)
        {
            var isChar = i < text.Length && char.IsLetterOrDigit(text[i]);
            if (start >= 0 && (!isChar || IsWordStart(text, i)))
            {
                yield return (start, i - start);
                start = -1;
            }
            if (isChar && start < 0) start = i;
        }
    }

    /// <summary>Optimal string alignment distance (adjacent swaps count as one edit), stopping early past <paramref name="max"/>.</summary>
    internal static int Distance(string a, string b, int max)
    {
        if (Math.Abs(a.Length - b.Length) > max) return max + 1;
        var d = new int[a.Length + 1, b.Length + 1];
        for (var i = 0; i <= a.Length; i++) d[i, 0] = i;
        for (var j = 0; j <= b.Length; j++) d[0, j] = j;
        for (var i = 1; i <= a.Length; i++)
        {
            var rowMin = int.MaxValue;
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                var v = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + cost);
                if (i > 1 && j > 1 && a[i - 1] == b[j - 2] && a[i - 2] == b[j - 1]) v = Math.Min(v, d[i - 2, j - 2] + 1);
                d[i, j] = v;
                rowMin = Math.Min(rowMin, v);
            }
            if (rowMin > max) return max + 1;
        }
        return d[a.Length, b.Length];
    }
}
