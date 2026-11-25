using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SwVault.Core.Auth;

namespace SwVault.Core.Lfs;

/// <summary>
/// SWVAULT 1.4 opt-in multipart R2 upload transport. Only small control JSON passes
/// through Cloudflare Tunnel. Each file part is uploaded using a short-lived
/// R2 S3 UploadPart signature; no permanent R2 credential reaches a Windows PC.
/// Gitea receives the full object from the VM's verified cold copy before the
/// normal check-in Git commit is allowed to continue.
/// </summary>
internal sealed class DirectR2MultipartClient
{
    private const int MaxParallelParts = 4;
    private static readonly TimeSpan FinalizeTimeout = TimeSpan.FromHours(3);
    private readonly HttpClient _control;
    private static readonly HttpClient SharedDirect = new(new HttpClientHandler { AllowAutoRedirect = false })
    {
        Timeout = Timeout.InfiniteTimeSpan,
    };
    private readonly HttpClient _direct;
    private readonly AuthContext _auth;
    private readonly Uri _base;

    public DirectR2MultipartClient(HttpClient control, Uri lfsBase, AuthContext auth, HttpClient? partClient = null)
    {
        _control = control;
        _auth = auth;
        _base = new Uri(lfsBase.GetLeftPart(UriPartial.Authority) + "/swvault-transfer/");
        _direct = partClient ?? SharedDirect;
    }

    public async Task UploadAsync(LfsUploadItem item, LfsClient.ProgressTracker tracker, CancellationToken ct)
    {
        var fileInfo = new FileInfo(item.FilePath);
        if (fileInfo.Length != item.Size)
            throw VaultException.BadRequest("File changed while preparing the R2 multipart upload.");

        var init = await SendJsonAsync(HttpMethod.Post, "init", new { oid = item.Oid, size = item.Size }, ct).ConfigureAwait(false);
        var id = init.RootElement.GetProperty("session_id").GetString() ?? throw new IOException("Missing R2 session ID");
        var partSize = init.RootElement.GetProperty("part_size").GetInt32();
        var count = init.RootElement.GetProperty("part_count").GetInt32();
        if (partSize < 5 * 1024 * 1024 || partSize > 64 * 1024 * 1024 ||
            count != (item.Size + partSize - 1) / partSize)
            throw new IOException("R2 transfer service returned invalid part dimensions.");
        init.Dispose();

        await Parallel.ForEachAsync(Enumerable.Range(1, count),
            new ParallelOptions { MaxDegreeOfParallelism = MaxParallelParts, CancellationToken = ct },
            async (number, token) =>
            {
                var offset = (long)(number - 1) * partSize;
                var length = (int)Math.Min(partSize, item.Size - offset);
                for (var attempt = 1; ; attempt++)
                {
                    token.ThrowIfCancellationRequested();
                    try
                    {
                        using var signed = await SendJsonAsync(HttpMethod.Post, "part", new
                        {
                            session_id = id,
                            part_number = number,
                        }, token).ConfigureAwait(false);
                        var url = signed.RootElement.GetProperty("url").GetString();
                        if (!Uri.TryCreate(url, UriKind.Absolute, out var r2Url) ||
                            r2Url.Scheme != Uri.UriSchemeHttps ||
                            !r2Url.Host.EndsWith(".r2.cloudflarestorage.com", StringComparison.OrdinalIgnoreCase) ||
                            r2Url.UserInfo.Length != 0 || r2Url.Port != 443)
                            throw new IOException("Untrusted R2 multipart upload target.");
                        using var request = new HttpRequestMessage(HttpMethod.Put, r2Url)
                        {
                            Content = new FilePartContent(item.FilePath, offset, length),
                        };
                        using var response = await _direct.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
                        if (!response.IsSuccessStatusCode)
                            throw new HttpRequestException($"R2 rejected part {number} with HTTP {(int)response.StatusCode}.", null, response.StatusCode);
                        tracker.Add(length); // Count only successfully accepted parts, not retry bytes.
                        break;
                    }
                    catch (Exception ex) when (attempt < 5 && IsRetryable(ex) && !token.IsCancellationRequested)
                    {
                        await Task.Delay(TimeSpan.FromSeconds(attempt * 2), token).ConfigureAwait(false);
                    }
                }
            }).ConfigureAwait(false);

        // The server verifies SHA-256, stores a local cold copy, and registers
        // the complete object using Gitea's native LFS PUT endpoint. This runs
        // asynchronously server-side to survive Cloudflare's HTTP timeouts.
        using (await SendJsonAsync(HttpMethod.Post, "complete", new { session_id = id }, ct).ConfigureAwait(false)) { }
        var until = DateTimeOffset.UtcNow + FinalizeTimeout;
        while (DateTimeOffset.UtcNow < until)
        {
            ct.ThrowIfCancellationRequested();
            await Task.Delay(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);
            using var state = await SendJsonAsync(HttpMethod.Get, "status?session_id=" + Uri.EscapeDataString(id), null, ct).ConfigureAwait(false);
            var status = state.RootElement.GetProperty("status").GetString();
            if (status == "done") return;
            if (status == "failed")
            {
                var error = state.RootElement.TryGetProperty("error", out var e) ? e.GetString() : null;
                throw new IOException("R2 upload completed, but the server could not finalize the Git LFS object: " + error);
            }
        }
        throw new TimeoutException("Server-side R2 verification is still pending. Do not delete the local CAD file; ask the vault admin to check transfer status.");
    }

    private static bool IsRetryable(Exception ex) => ex switch
    {
        HttpRequestException e => e.StatusCode == null || (int)e.StatusCode >= 500 ||
                                  e.StatusCode is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests,
        IOException => true,
        _ => false,
    };

    private async Task<JsonDocument> SendJsonAsync(HttpMethod method, string path, object? body, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, new Uri(_base, path));
        var auth = await _auth.GetBasicHeaderAsync(ct).ConfigureAwait(false);
        if (auth == null) throw VaultException.Unauthorized("Sign in to SWVAULT before starting an R2 transfer.");
        request.Headers.TryAddWithoutValidation("Authorization", auth);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (body != null) request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        using var response = await _control.SendAsync(request, ct).ConfigureAwait(false);
        var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"SWVAULT R2 control request failed (HTTP {(int)response.StatusCode}): {json[..Math.Min(240, json.Length)]}",
                                           null, response.StatusCode);
        return JsonDocument.Parse(json);
    }

    private sealed class FilePartContent : HttpContent
    {
        private readonly string _path;
        private readonly long _offset;
        private readonly int _length;

        public FilePartContent(string path, long offset, int length)
        {
            _path = path;
            _offset = offset;
            _length = length;
            Headers.ContentLength = length;
        }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            SerializeToStreamAsync(stream, context, CancellationToken.None);

        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken ct)
        {
            await using var file = new FileStream(_path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete, 1024 * 1024, useAsync: true);
            file.Seek(_offset, SeekOrigin.Begin);
            var remaining = _length;
            var buf = new byte[1024 * 1024];
            while (remaining > 0)
            {
                var read = await file.ReadAsync(buf.AsMemory(0, Math.Min(buf.Length, remaining)), ct).ConfigureAwait(false);
                if (read == 0) throw new EndOfStreamException("CAD file was truncated during multipart check-in.");
                await stream.WriteAsync(buf.AsMemory(0, read), ct).ConfigureAwait(false);
                remaining -= read;
            }
        }

        protected override bool TryComputeLength(out long length)
        {
            length = _length;
            return true;
        }
    }
}
