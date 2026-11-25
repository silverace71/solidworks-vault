using SwVault.Core.Index;
using SwVault.Core.Vault;

namespace SwVault.Core.Tests;

public class FileChecksTests
{
    private static readonly HeadIndex Head = HeadIndex.FromFiles(new[]
    {
        new HeadFile { Path = "Suspension/Hub.SLDPRT", BlobSha = "1", Oid = "aaa", Size = 10 },
        new HeadFile { Path = "Suspension/Front/Upright.SLDPRT", BlobSha = "2", Oid = "bbb", Size = 10 },
    }, VaultConfig.CreateDefault("T", @"C:\SWVault\T", "lead", null));

    private static List<FileProblem> Check(string path, long size = 1000, string oid = "new", bool isNew = true, string? local = null, params string[] batch) =>
        FileChecks.Check(path, local ?? @"C:\SWVault\T\" + path.Replace('/', '\\'), size, oid, isNew, Head, batch.Length == 0 ? new[] { path } : batch).ToList();

    [Fact]
    public void CleanNewFile_HasNoProblems() => Assert.Empty(Check("Brakes/Caliper.SLDPRT"));

    [Fact]
    public void EmptyFile_IsBlocked_EvenForNewVersions()
    {
        Assert.Contains(Check("Brakes/Caliper.SLDPRT", size: 0), p => p.Blocking && p.Message.Contains("empty"));
        Assert.Contains(Check("Suspension/Hub.SLDPRT", size: 0, isNew: false), p => p.Blocking);
    }

    [Fact]
    public void HugeFile_IsBlocked() => Assert.Contains(Check("Sim/run.dat", size: FileChecks.MaxFileBytes + 1), p => p.Blocking && p.Message.Contains("4 GB"));

    [Fact]
    public void LongLocalPath_BlocksNewFiles_WarnsForExisting()
    {
        var local = @"C:\SWVault\T\" + new string('x', 260) + ".SLDPRT";
        Assert.Contains(Check("Long.SLDPRT", local: local), p => p.Blocking);
        Assert.Contains(Check("Suspension/Hub.SLDPRT", isNew: false, oid: "aaa", local: local), p => !p.Blocking);
    }

    [Fact]
    public void TrailingSpaceOrDot_IsBlocked() => Assert.Contains(Check("Brakes /Caliper.SLDPRT"), p => p.Blocking && p.Message.Contains("space or a dot"));

    [Fact]
    public void SameNameElsewhere_Warns()
    {
        var problems = Check("Rear/Hub.SLDPRT");
        Assert.Contains(problems, p => !p.Blocking && p.Message.Contains("Suspension/Hub.SLDPRT"));
        Assert.Contains(Check("A/Bolt.SLDPRT", batch: new[] { "A/Bolt.SLDPRT", "B/Bolt.SLDPRT" }), p => p.Message.Contains("B/Bolt.SLDPRT"));
        Assert.Empty(Check("Rear/Hub.pdf")); // only SOLIDWORKS documents resolve references by name
    }

    [Fact]
    public void CopyOfVaultFile_Warns() =>
        Assert.Contains(Check("Old/Upright_copy.SLDPRT", oid: "bbb"), p => !p.Blocking && p.Message.Contains("exact copy of Suspension/Front/Upright.SLDPRT"));

    [Fact]
    public void ProgramsAndBigNonCadFiles_Warn()
    {
        Assert.Contains(Check("Tools/setup.exe"), p => !p.Blocking && p.Message.Contains(".exe"));
        Assert.Contains(Check("Video/test.mp4", size: FileChecks.LargeFileBytes + 1), p => !p.Blocking);
        Assert.Empty(Check("Big/Chassis.SLDASM", size: FileChecks.LargeFileBytes + 1));
    }

    [Theory]
    [InlineData("suspension/front/New.SLDPRT", "Suspension/Front/New.SLDPRT")]
    [InlineData("SUSPENSION/Rear/New.SLDPRT", "Suspension/Rear/New.SLDPRT")]
    [InlineData("Brakes/New.SLDPRT", "Brakes/New.SLDPRT")]
    [InlineData("New.SLDPRT", "New.SLDPRT")]
    public void NewFiles_UseTheVaultsFolderSpelling(string typed, string expected) => Assert.Equal(expected, FileChecks.CanonicalFolders(typed, Head));
}

public class VaultFolderRulesTests
{
    [Theory]
    [InlineData(@"C:\SWVault\Team")]
    [InlineData(@"D:\CAD\FSAE")]
    [InlineData(@"C:/SWVault/Team/")]
    [InlineData(@"C:\Users\alex\Documents\SWVault")]
    public void GoodFolders(string root) => Assert.Null(VaultFolderRules.Problem(root, Array.Empty<string>(), checkDrive: false));

    [Theory]
    [InlineData(@"\\server\share\cad", "network")]
    [InlineData(@"C:\", "whole")]
    [InlineData(@"SWVault", "full folder path")]
    [InlineData(@"C:\Users\alex\OneDrive\CAD", "OneDrive")]
    [InlineData(@"C:\Program Files\Vault", "Windows or personal")]
    [InlineData(@"C:\Users\alex\Desktop", "Windows or personal")]
    [InlineData(@"C:\Users\alex\AppData\Local\Temp\x", "Windows or personal")]
    [InlineData("", "Choose a folder")]
    public void BadFolders(string root, string expected) =>
        Assert.Contains(expected, VaultFolderRules.Problem(root, Array.Empty<string>(), checkDrive: false));

    [Fact]
    public void LongFolder_IsRefused() =>
        Assert.Contains("characters long", VaultFolderRules.Problem(@"C:\" + new string('a', 120), Array.Empty<string>(), checkDrive: false));

    [Fact]
    public void VaultsMustNotNest()
    {
        var others = new[] { @"C:\SWVault\Team" };
        Assert.Contains("inside another vault", VaultFolderRules.Problem(@"C:\SWVault\Team\Sub", others, checkDrive: false));
        Assert.Contains("contains another vault", VaultFolderRules.Problem(@"C:\SWVault", others, checkDrive: false));
        Assert.Null(VaultFolderRules.Problem(@"C:\SWVault\Team", others, checkDrive: false)); // reconnecting
        Assert.Null(VaultFolderRules.Problem(@"C:\SWVault\Team2", others, checkDrive: false));
    }

    [Fact]
    public void NonEmptyFolder_IsRefused_UnlessSwVaultUsedIt()
    {
        var root = Path.Combine(Path.GetTempPath(), "swvault-rules-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            Assert.Null(VaultFolderRules.NotEmptyProblem(root)); // missing
            Directory.CreateDirectory(root);
            Assert.Null(VaultFolderRules.NotEmptyProblem(root)); // empty
            File.WriteAllText(Path.Combine(root, "holiday.jpg"), "x");
            Assert.Contains("already has other files", VaultFolderRules.NotEmptyProblem(root));
            Directory.CreateDirectory(Path.Combine(root, ".swvault-local"));
            Assert.Null(VaultFolderRules.NotEmptyProblem(root)); // an earlier SwVault install
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}

public class VaultUrlTests
{
    [Theory]
    [InlineData("https://swvault.tail46edb9.ts.net/team/cad.git", "https://swvault.tail46edb9.ts.net/team/cad.git")]
    [InlineData("swvault.tail46edb9.ts.net/team/cad", "https://swvault.tail46edb9.ts.net/team/cad.git")]
    [InlineData("  \"https://server/team/cad/\" ", "https://server/team/cad.git")]
    [InlineData("https://server/team/cad/src/branch/main/Suspension", "https://server/team/cad.git")]
    [InlineData("http://localhost:3000/team/cad", "http://localhost:3000/team/cad.git")]
    public void Normalizes(string input, string expected) => Assert.Equal(expected, VaultUrls.Normalize(input));

    [Theory]
    [InlineData("", "Enter the vault address")]
    [InlineData("https://server", "names the team and the vault")]
    [InlineData("https://server/team", "names the team and the vault")]
    [InlineData("ftp://server/team/cad", "isn't a web address")]
    public void ExplainsBadAddresses(string input, string expected) =>
        Assert.Contains(expected, Assert.Throws<VaultException>(() => VaultUrls.Normalize(input)).Message);

    [Fact]
    public void LocalPathsPassThrough() => Assert.Equal("/srv/git/cad.git", VaultUrls.Normalize("/srv/git/cad.git"));
}

public class FreeSpaceTests
{
    [Fact]
    public void LeavesRoom() => Assert.Equal(10L * 1024 * 1024 * 1024 + 1024L * 1024 * 1024 + (1L << 30), VaultSession.FreeSpaceNeeded(10L * 1024 * 1024 * 1024));
}

public class UserNameTests
{
    [Theory]
    [InlineData("alexs")]
    [InlineData("alex.smith")]
    [InlineData("a")]
    [InlineData("Alex_Smith-2")]
    public void Accepts(string name) => Assert.Null(SwVault.Core.Client.TeamInvites.UserNameProblem(name));

    [Theory]
    [InlineData("alex@colorado.edu", "email address")]
    [InlineData("Alex Smith", "spaces. Try alexsmith")]
    [InlineData("alex!", "letters, digits")]
    [InlineData(".alex", "letters, digits")]
    [InlineData("al..ex", "letters, digits")]
    [InlineData("", "Choose a user name")]
    public void Explains(string name, string expected) => Assert.Contains(expected, SwVault.Core.Client.TeamInvites.UserNameProblem(name));
}

public class CheckOutReminderTests
{
    [Fact]
    public void RemindsAboutMyOldCheckOutsOnly()
    {
        var now = DateTimeOffset.Parse("2026-10-10T12:00:00Z");
        var locks = new[]
        {
            new SwVault.Protocol.LockDto { Path = "A/Hub.SLDPRT", Mine = true, LockedAt = "2026-10-01T09:00:00Z" },
            new SwVault.Protocol.LockDto { Path = "A/New.SLDPRT", Mine = true, LockedAt = "2026-10-09T09:00:00Z" },
            new SwVault.Protocol.LockDto { Path = "A/Theirs.SLDPRT", Mine = false, LockedAt = "2026-09-01T09:00:00Z" },
            new SwVault.Protocol.LockDto { Path = "A/Odd.SLDPRT", Mine = true, LockedAt = null },
        };
        var old = CheckOutReminder.Old(locks, now);
        Assert.Equal("A/Hub.SLDPRT", Assert.Single(old).Path);
        Assert.StartsWith("Hub.SLDPRT has been checked out to you for over 3 days.", CheckOutReminder.Message(old));
    }
}
