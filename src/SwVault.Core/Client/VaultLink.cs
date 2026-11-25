using SwVault.Core.Util;

namespace SwVault.Core.Client;

/// <summary>
/// A <c>swvault://</c> link from the web file browser: an action on a vault file, run on this PC by
/// the agent (or by the SOLIDWORKS add-in when SOLIDWORKS is open), e.g.
/// <c>swvault://open?vault=https%3A%2F%2Fserver%2Fteam%2Fcad&amp;path=Chassis%2FFrame.SLDPRT</c>.
/// </summary>
public sealed record VaultLink(string Action, string? VaultUrl, string Path)
{
    public const string Scheme = "swvault";

    public const string Open = "open";
    public const string GetLatest = "getlatest";
    public const string CheckOut = "checkout";
    public const string CheckIn = "checkin";
    public const string History = "history";
    public const string ChangeState = "changestate";
    public const string RequestReview = "requestreview";

    /// <summary>
    /// What a link may ask for. Anything that loses work (undo check-out, rollback, delete) is
    /// deliberately not here: any web page can send a link.
    /// </summary>
    public static readonly IReadOnlyList<string> Actions = new[] { Open, GetLatest, CheckOut, CheckIn, History, ChangeState, RequestReview };

    /// <summary>Actions that change something on the server; the person confirms them first.</summary>
    public bool NeedsConfirmation => Action == CheckOut;

    /// <summary>The link, or null when it isn't a well-formed SwVault link for a vault file.</summary>
    public static VaultLink? TryParse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > 4096) return null;
        if (!Uri.TryCreate(text.Trim(), UriKind.Absolute, out var uri) || !string.Equals(uri.Scheme, Scheme, StringComparison.OrdinalIgnoreCase)) return null;
        // swvault://open?... puts the action in the host; swvault:open?... in the path.
        var action = (string.IsNullOrEmpty(uri.Host) ? uri.AbsolutePath : uri.Host).Trim('/').ToLowerInvariant();
        if (!Actions.Contains(action)) return null;

        string? vault = null, path = null;
        foreach (var part in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = part.IndexOf('=');
            if (eq <= 0) continue;
            var key = Uri.UnescapeDataString(part.Substring(0, eq).Replace('+', ' '));
            var value = Uri.UnescapeDataString(part.Substring(eq + 1).Replace('+', ' '));
            if (key == "path") path = value;
            else if (key == "vault") vault = value;
        }
        if (string.IsNullOrWhiteSpace(path)) return null;
        path = PathRules.Normalize(path);
        if (path.Length == 0 || PathRules.IsInternal(path) || path.Split('/').Any(s => s is "." or ".." || s.Contains(':'))) return null;
        if (vault != null && !(Uri.TryCreate(vault, UriKind.Absolute, out var v) && v.Scheme is "https" or "http")) vault = null;
        return new VaultLink(action, vault, path);
    }

    public override string ToString() =>
        $"{Scheme}://{Action}?" + (VaultUrl == null ? "" : "vault=" + Uri.EscapeDataString(VaultUrl) + "&") + "path=" + Uri.EscapeDataString(Path);

    /// <summary>Same vault server and repository, ignoring case, a trailing slash and ".git".</summary>
    public static bool SameVault(string? a, string? b)
    {
        static string Key(string? url)
        {
            var k = (url ?? "").Trim().TrimEnd('/').ToLowerInvariant();
            return k.EndsWith(".git", StringComparison.Ordinal) ? k[..^4] : k;
        }
        return Key(a).Length > 0 && Key(a) == Key(b);
    }
}
