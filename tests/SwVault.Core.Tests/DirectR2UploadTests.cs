using System.Net;
using System.Security.Cryptography;
using System.Text;
using SwVault.Core.Auth;
using SwVault.Core.Lfs;

namespace SwVault.Core.Tests;

public class DirectR2UploadTests
{
    private sealed class MultipartHandler : HttpMessageHandler
    {
        public int Parts;
        public int AuthenticatedControls;
        public long ReceivedBytes;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (request.RequestUri.Host.EndsWith(".r2.cloudflarestorage.com", StringComparison.Ordinal))
            {
                Assert.Equal(HttpMethod.Put, request.Method);
                Assert.False(request.Headers.Contains("Authorization")); // no Gitea token at R2
                var bytes = await request.Content!.ReadAsByteArrayAsync(ct);
                Interlocked.Add(ref ReceivedBytes, bytes.Length);
                Interlocked.Increment(ref Parts);
                return Json(HttpStatusCode.OK, "{}");
            }
            Assert.StartsWith("/swvault-transfer/", path);
            Assert.True(request.Headers.Contains("Authorization"));
            Interlocked.Increment(ref AuthenticatedControls);
            if (path.EndsWith("/init", StringComparison.Ordinal))
                return Json(HttpStatusCode.OK, "{\"session_id\":\"" + new string('a', 48) + "\",\"part_size\":33554432,\"part_count\":1}");
            if (path.EndsWith("/part", StringComparison.Ordinal))
                return Json(HttpStatusCode.OK, "{\"url\":\"https://abcdef0123456789.r2.cloudflarestorage.com/bucket/stage?partNumber=1&uploadId=example\"}");
            if (path.EndsWith("/complete", StringComparison.Ordinal))
                return Json(HttpStatusCode.Accepted, "{\"status\":\"finalizing\"}");
            if (path.EndsWith("/status", StringComparison.Ordinal))
                return Json(HttpStatusCode.OK, "{\"status\":\"done\"}");
            throw new Exception("Unexpected HTTP request: " + request.RequestUri);
        }
        private static HttpResponseMessage Json(HttpStatusCode status, string value) => new(status)
        {
            Content = new StringContent(value, Encoding.UTF8, "application/json"),
        };
    }

    [Fact]
    public async Task LargeFile_IsSentToR2_NotThroughTunnel()
    {
        var path = Path.Combine(Path.GetTempPath(), "swvault-r2-test-" + Guid.NewGuid().ToString("N"));
        var bytes = new byte[8 * 1024 * 1024];
        RandomNumberGenerator.Fill(bytes);
        await File.WriteAllBytesAsync(path, bytes);
        try
        {
            var handler = new MultipartHandler();
            using var http = new HttpClient(handler);
            var baseUri = new Uri("https://vault.lynxmotorsports.racing/team/cad.git/info/lfs");
            var auth = new AuthContext(baseUri, new StaticCredentialProvider(new Credential("test", "secret-token")));
            var client = new DirectR2MultipartClient(http, baseUri, auth, http);
            await client.UploadAsync(new LfsUploadItem(Convert.ToHexStringLower(SHA256.HashData(bytes)), bytes.Length, path),
                new LfsClient.ProgressTracker(bytes.Length, 1, null), CancellationToken.None);
            Assert.Equal(1, handler.Parts);
            Assert.Equal(bytes.LongLength, handler.ReceivedBytes);
            Assert.True(handler.AuthenticatedControls >= 4);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
