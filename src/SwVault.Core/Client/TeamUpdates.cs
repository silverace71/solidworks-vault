using System.Security.Cryptography;
using System.Text.Json;
using SwVault.Core.Hosts;
using SwVault.Protocol;

namespace SwVault.Core.Client;

/// <summary>The installer an admin published on the team server (scripts/package.ps1 -Publish).</summary>
public sealed record InstallerRelease(Version Version, string Sha256, long Size, DateTimeOffset Published, bool Required, string? Notes)
{
    /// <summary>True when this release is newer than <paramref name="current"/> (compared as major.minor.build).</summary>
    public bool IsNewerThan(Version current) => Version > Normalize(current);

    internal static Version Normalize(Version v) => new(v.Major, v.Minor, Math.Max(v.Build, 0));
}

public static partial class TeamInvites
{
    /// <summary>The published installer, or null when there is none (or the server predates updates).</summary>
    public static async Task<InstallerRelease?> LatestInstallerAsync(string vaultUrl, CancellationToken ct = default)
    {
        try
        {
            using var doc = await SendAsync(HttpMethod.Get, vaultUrl, "installer/latest", null, null, ct).ConfigureAwait(false);
            return ParseRelease(doc.RootElement);
        }
        catch (VaultException ex) when (ex.Code == ErrorCodes.NotFound)
        {
            return null;
        }
    }

    internal static InstallerRelease? ParseRelease(JsonElement e)
    {
        if (!Version.TryParse(HostAdapters.GetString(e, "version"), out var version)) return null;
        var sha = HostAdapters.GetString(e, "sha256");
        if (string.IsNullOrEmpty(sha)) return null;
        return new InstallerRelease(
            InstallerRelease.Normalize(version),
            sha.ToLowerInvariant(),
            e.TryGetProperty("size", out var size) && size.TryGetInt64(out var s) ? s : 0,
            DateTimeOffset.TryParse(HostAdapters.GetString(e, "published"), out var published) ? published : DateTimeOffset.MinValue,
            e.TryGetProperty("required", out var required) && required.ValueKind == JsonValueKind.True,
            HostAdapters.GetString(e, "notes") is { Length: > 0 } notes ? notes : null);
    }

    /// <summary>
    /// Downloads the published installer zip to <paramref name="target"/> and checks it against the
    /// release's SHA-256; a mismatch (a newer upload mid-download, or a damaged file) deletes it and throws.
    /// </summary>
    public static async Task DownloadInstallerAsync(string vaultUrl, InstallerRelease release, string target,
        IProgress<double>? progress = null, CancellationToken ct = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var partial = target + ".part";
        if (File.Exists(partial)) File.Delete(partial); // a leftover may be from an older release
        using (var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan })
        {
            // The installer is large and comes through the server's public address, so a dropped
            // connection is ordinary: carry on from what has arrived instead of starting over.
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    await DownloadPartAsync(http, vaultUrl, release, partial, progress, ct).ConfigureAwait(false);
                    break;
                }
                catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException && !ct.IsCancellationRequested)
                {
                    if (attempt >= DownloadAttempts)
                        throw VaultException.Offline($"The update download kept getting interrupted ({ex.Message}). Check the connection and try again.", ex);
                    await Task.Delay(TimeSpan.FromSeconds(2 * attempt), ct).ConfigureAwait(false);
                }
            }
        }

        string actual;
        await using (var file = File.OpenRead(partial))
            actual = Convert.ToHexString(await SHA256.HashDataAsync(file, ct).ConfigureAwait(false)).ToLowerInvariant();
        if (actual != release.Sha256)
        {
            File.Delete(partial);
            throw new VaultException(ErrorCodes.Internal, "The downloaded update didn't match what the server published (it may have just been replaced). Try again.");
        }
        File.Move(partial, target, overwrite: true);
    }

    internal const int DownloadAttempts = 5;

    /// <summary>One attempt: appends to <paramref name="partial"/> from where it ends (the server may answer with the whole file instead).</summary>
    private static async Task DownloadPartAsync(HttpClient http, string vaultUrl, InstallerRelease release, string partial, IProgress<double>? progress, CancellationToken ct)
    {
        var have = File.Exists(partial) ? new FileInfo(partial).Length : 0;
        // The zip, not the setup wizard the same address offers people in a browser.
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(ServiceUrl(vaultUrl), "download?format=zip"));
        if (have > 0) request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(have, null);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (response.StatusCode == System.Net.HttpStatusCode.RequestedRangeNotSatisfiable) return; // everything had arrived
        if (!response.IsSuccessStatusCode)
            throw new VaultException(ErrorCodes.Internal, $"Couldn't download the update (HTTP {(int)response.StatusCode}).");
        var resumed = response.StatusCode == System.Net.HttpStatusCode.PartialContent;
        if (!resumed) have = 0;
        var total = have + (response.Content.Headers.ContentLength ?? Math.Max(0, release.Size - have));
        await using var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using var file = new FileStream(partial, resumed ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None);
        var buffer = new byte[1 << 20];
        var done = have;
        while (true)
        {
            // No data for a minute means the connection is dead even if nobody said so.
            using var stall = CancellationTokenSource.CreateLinkedTokenSource(ct);
            stall.CancelAfter(TimeSpan.FromSeconds(60));
            int read;
            try
            {
                read = await source.ReadAsync(buffer, stall.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new IOException("the server stopped sending");
            }
            if (read == 0) break;
            await file.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
            done += read;
            if (total > 0) progress?.Report(Math.Min(1.0, (double)done / total));
        }
        if (total > 0 && done < total) throw new IOException("the connection closed early");
    }
}
