using SwVault.Core.Git;

namespace SwVault.Core.Tests;

/// <summary>
/// Two check-ins arriving in the same instant: git reports the loser as "remote rejected", which
/// must be retried like any other lost race, not shown as "the server rejected the check-in".
/// </summary>
public class PushRaceTests
{
    [Theory]
    // git 2.x pushing to a repository whose main moved while the push was being received
    [InlineData("remote: error: cannot lock ref 'refs/heads/main': is at 583529338eea5aa5881040389f5de370913a70de but expected d1965a2b686f3870302adbdb0e8f0e5f47fb8207\n!\tabc:refs/heads/main\t[remote rejected] (incorrect old value provided)")]
    [InlineData("!\tabc:refs/heads/main\t[remote rejected] (failed to update ref)")]
    [InlineData("remote: error: Unable to create 'refs/heads/main.lock': File exists.\n!\tabc:refs/heads/main\t[remote rejected] (failed to lock)")]
    public void ALostRace_IsRetried(string output) => Assert.True(GitMirror.LostRefRace(output));

    [Theory]
    [InlineData("!\tabc:refs/heads/main\t[remote rejected] (pre-receive hook declined)")]
    [InlineData("!\tabc:refs/heads/main\t[remote rejected] (protected branch hook declined)")]
    public void ARealRejection_IsNot(string output) => Assert.False(GitMirror.LostRefRace(output));
}
