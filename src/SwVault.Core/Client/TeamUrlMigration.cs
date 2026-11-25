using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using SwVault.Core.Auth;
using SwVault.Core.Util;

namespace SwVault.Core.Client;

/// <summary>
/// Opt-in identity-preserving migration: new installer team.json must name PreviousVaultUrl.
/// No data is deleted and no migrated URL is applied without authenticating against new Gitea.
/// </summary>
public static class TeamUrlMigration
{
    public static async Task<bool> TryApplyAsync(SwVaultProfile profile, TeamConfig team,
        Action<string>? log = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(team.PreviousVaultUrl)) return false;
        if (!Uri.TryCreate(team.PreviousVaultUrl, UriKind.Absolute, out var oldUri) ||
            !Uri.TryCreate(team.VaultUrl, UriKind.Absolute, out var newUri) ||
            oldUri.Scheme != Uri.UriSchemeHttps || newUri.Scheme != Uri.UriSchemeHttps ||
            oldUri.UserInfo.Length > 0 || newUri.UserInfo.Length > 0 ||
            oldUri.AbsolutePath.TrimEnd('/').Replace(".git", "", StringComparison.OrdinalIgnoreCase) !=
                newUri.AbsolutePath.TrimEnd('/').Replace(".git", "", StringComparison.OrdinalIgnoreCase))
        {
            log?.Invoke("Migration configuration is invalid or changes the repository path; no change made.");
            return false;
        }
        var registrations = profile.LoadRegistrations();
        var matches = registrations.Where(r => VaultLink.SameVault(r.RemoteUrl, team.PreviousVaultUrl)).ToList();
        if (matches.Count != 1 || registrations.Any(r => VaultLink.SameVault(r.RemoteUrl, team.VaultUrl))) return false;
        var credentials = new WindowsCredentialStore(profile.Name);
        var oldCredential = await credentials.GetAsync(oldUri, false, ct).ConfigureAwait(false);
        if (oldCredential == null)
        {
            log?.Invoke("Migration skipped: no saved credential for the previous vault. Sign in to the old vault first.");
            return false;
        }
        // Same Gitea database must recognize the existing user token. A 200 also proves
        // the target endpoint is online. Do not send tokens to a non-HTTPS destination.
        using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
        { Timeout = TimeSpan.FromSeconds(20) };
        // The destination must explicitly announce that the 1.2.9 repository
        // snapshot has been restored and that the administrator opened cutover.
        // Without this handshake a client might switch to an empty/new repository.
        using (var readiness = await http.GetAsync(new Uri(newUri.GetLeftPart(UriPartial.Authority) +
                "/swvault-transfer/migration-ready"), ct).ConfigureAwait(false))
        {
            if (!readiness.IsSuccessStatusCode) return false;
            using var document = JsonDocument.Parse(await readiness.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
            var root = document.RootElement;
            if (!root.TryGetProperty("ready", out var ready) || ready.ValueKind != JsonValueKind.True ||
                !root.TryGetProperty("source", out var source) ||
                !root.TryGetProperty("target", out var target) ||
                !VaultLink.SameVault(source.GetString(), team.PreviousVaultUrl) ||
                !VaultLink.SameVault(target.GetString(), team.VaultUrl))
            {
                log?.Invoke("The new vault is not armed for migration from this old URL.");
                return false;
            }
        }
        using var req = new HttpRequestMessage(HttpMethod.Get, new Uri(newUri.GetLeftPart(UriPartial.Authority) + "/api/v1/user"));
        req.Headers.Authorization = new AuthenticationHeaderValue("Basic",
            Convert.ToBase64String(Encoding.UTF8.GetBytes(oldCredential.UserName + ":" + oldCredential.Secret)));
        using var resp = await http.SendAsync(req, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            log?.Invoke("Migration skipped: new Gitea endpoint did not accept the existing credentials.");
            return false;
        }
        credentials.Save(newUri, oldCredential); // do not delete original credential
        var snapshot = profile.RegistryPath + ".before-cloudflare-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss") + ".bak";
        if (File.Exists(profile.RegistryPath)) File.Copy(profile.RegistryPath, snapshot, overwrite: false);
        matches[0].RemoteUrl = team.VaultUrl; // retain Id, LocalRoot, Name and vault data
        profile.SaveRegistrations(registrations);
        log?.Invoke("Migrated one vault registration to the new hostname; preserved workspace and vault identity. Backup: " + snapshot);
        return true;
    }
}
