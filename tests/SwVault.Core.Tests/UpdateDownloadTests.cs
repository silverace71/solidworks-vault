using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using SwVault.Core;
using SwVault.Core.Client;

namespace SwVault.Core.Tests;

public class UpdateDownloadTests
{
    /// <summary>
    /// A stand-in for the team service's installer download: the first <paramref name="drops"/>
    /// responses stop half way through what was asked for and close the connection.
    /// </summary>
    private sealed class FlakyServer : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly byte[] _content;
        private readonly bool _supportsRange;
        private int _drops;

        public List<string> Ranges { get; } = new();

        public FlakyServer(byte[] content, int drops, bool supportsRange = true)
        {
            _content = content;
            _drops = drops;
            _supportsRange = supportsRange;
            _listener.Start();
            _ = Task.Run(ServeAsync);
        }

        public string VaultUrl => $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/team/Vault.git";

        private async Task ServeAsync()
        {
            while (true)
            {
                TcpClient client;
                try { client = await _listener.AcceptTcpClientAsync(); }
                catch (Exception ex) when (ex is ObjectDisposedException or SocketException) { return; }
                using (client)
                {
                    var stream = client.GetStream();
                    var reader = new StreamReader(stream, Encoding.ASCII, false, 4096, leaveOpen: true);
                    string? line;
                    long start = 0;
                    while (!string.IsNullOrEmpty(line = await reader.ReadLineAsync()))
                        if (_supportsRange && line.StartsWith("Range: bytes=", StringComparison.OrdinalIgnoreCase))
                            start = long.Parse(line["Range: bytes=".Length..].TrimEnd('-'));
                    lock (Ranges) Ranges.Add(start == 0 ? "whole" : "from " + start);
                    var length = _content.Length - start;
                    var header = (start > 0 ? "HTTP/1.1 206 Partial Content\r\nContent-Range: bytes " + start + "-" + (_content.Length - 1) + "/" + _content.Length : "HTTP/1.1 200 OK")
                                 + "\r\nContent-Type: application/zip\r\nContent-Length: " + length + "\r\nConnection: close\r\n\r\n";
                    await stream.WriteAsync(Encoding.ASCII.GetBytes(header));
                    var drop = _drops-- > 0;
                    await stream.WriteAsync(_content.AsMemory((int)start, (int)(drop ? length / 2 : length)));
                    await stream.FlushAsync();
                    if (drop)
                    {
                        await Task.Delay(200); // let what was sent arrive before the reset discards it
                        client.Client.Close(0);
                    }
                    else
                    {
                        client.Client.Shutdown(SocketShutdown.Send);
                        try { await stream.ReadAsync(new byte[1]); } catch (IOException) { }
                    }
                }
            }
        }

        public void Dispose() => _listener.Stop();
    }

    private static (byte[] Content, InstallerRelease Release) Installer(int size)
    {
        var content = RandomNumberGenerator.GetBytes(size);
        return (content, new InstallerRelease(new Version(9, 9, 9), Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant(), size, DateTimeOffset.UtcNow, false, null));
    }

    [Fact]
    public async Task DroppedConnection_ResumesFromWhatArrived()
    {
        var (content, release) = Installer(3_000_000);
        using var server = new FlakyServer(content, drops: 2);
        var target = Path.Combine(Path.GetTempPath(), "swvault-dl-" + Guid.NewGuid().ToString("N") + ".zip");
        try
        {
            await TeamInvites.DownloadInstallerAsync(server.VaultUrl, release, target);
            Assert.Equal(content, await File.ReadAllBytesAsync(target));
            Assert.False(File.Exists(target + ".part"));
            Assert.Equal(new[] { "whole", "from 1500000", "from 2250000" }, server.Ranges);
        }
        finally
        {
            File.Delete(target);
        }
    }

    [Fact]
    public async Task ServerWithoutRange_StartsOverAndStillFinishes()
    {
        var (content, release) = Installer(400_000);
        using var server = new FlakyServer(content, drops: 1, supportsRange: false);
        var target = Path.Combine(Path.GetTempPath(), "swvault-dl-" + Guid.NewGuid().ToString("N") + ".zip");
        try
        {
            await TeamInvites.DownloadInstallerAsync(server.VaultUrl, release, target);
            Assert.Equal(content, await File.ReadAllBytesAsync(target));
        }
        finally
        {
            File.Delete(target);
        }
    }

    [Fact]
    public async Task ConnectionThatKeepsDropping_SaysSoInsteadOfACrash()
    {
        var (content, release) = Installer(200_000);
        using var server = new FlakyServer(content, drops: 100, supportsRange: false);
        var target = Path.Combine(Path.GetTempPath(), "swvault-dl-" + Guid.NewGuid().ToString("N") + ".zip");
        var ex = await Assert.ThrowsAsync<VaultException>(() => TeamInvites.DownloadInstallerAsync(server.VaultUrl, release, target));
        Assert.Equal(SwVault.Protocol.ErrorCodes.Offline, ex.Code);
        Assert.Contains("interrupted", ex.Message);
        Assert.False(File.Exists(target));
        File.Delete(target + ".part");
    }
}
