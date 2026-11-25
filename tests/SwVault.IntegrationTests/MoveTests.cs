using SwVault.Core;
using SwVault.Protocol;
using static SwVault.IntegrationTests.TestVault;

namespace SwVault.IntegrationTests;

public class MoveTests
{
    [Fact]
    public async Task Move_KeepsVersionAndHistory_AndOthersGetItAtTheNewPlace()
    {
        await using var vault = await TestVault.CreateAsync();
        var alice = await vault.OpenAsync("alice");
        var bob = await vault.OpenAsync("bob");

        Write(alice, "Bolt.SLDPRT", "bolt v1");
        await alice.CheckInPathsAsync("First bolt", "Bolt.SLDPRT");
        await alice.CheckOutAndApplyAsync("Bolt.SLDPRT");
        Write(alice, "Bolt.SLDPRT", "bolt v2");
        await alice.CheckInPathsAsync("Longer bolt", "Bolt.SLDPRT");

        var (commit, moved, warnings) = await alice.MoveAsync(new[] { "Bolt.SLDPRT" }, "CB8/Steering Wheel/Fasteners", null);
        Assert.NotNull(commit);
        Assert.Equal(new[] { "CB8/Steering Wheel/Fasteners/Bolt.SLDPRT" }, moved);
        Assert.Empty(warnings);
        Assert.Empty(vault.Server.CurrentLocks);

        // Alice: the local file moved along, read-only and up to date; nothing is left at the old path.
        Assert.False(File.Exists(alice.LocalPathOf("Bolt.SLDPRT")));
        Assert.Equal("bolt v2", Read(alice, "CB8/Steering Wheel/Fasteners/Bolt.SLDPRT"));
        Assert.True(IsReadOnly(alice, "CB8/Steering Wheel/Fasteners/Bolt.SLDPRT"));
        var status = await StatusAsync(alice, "CB8/Steering Wheel/Fasteners/Bolt.SLDPRT");
        Assert.Equal(LocalState.UpToDate, status.LocalState);
        Assert.Equal(2, status.ServerVersion);

        // The history carries on across the move, without an extra version for the move itself.
        var history = await alice.GetHistoryAsync("CB8/Steering Wheel/Fasteners/Bolt.SLDPRT");
        Assert.Equal(new[] { 2, 1 }, history.Select(h => h.Version));
        Assert.Equal(new[] { "Longer bolt", "First bolt" }, history.Select(h => h.Comment));

        // Bob gets it at the new place.
        await bob.SyncAsync();
        await bob.GetLatestAsync(false, "CB8");
        Assert.Equal("bolt v2", Read(bob, "CB8/Steering Wheel/Fasteners/Bolt.SLDPRT"));

        // A later check-in at the new place continues the numbering and keeps the old versions.
        await bob.CheckOutAndApplyAsync("CB8/Steering Wheel/Fasteners/Bolt.SLDPRT");
        Write(bob, "CB8/Steering Wheel/Fasteners/Bolt.SLDPRT", "bolt v3");
        await bob.CheckInPathsAsync("Shorter again", "CB8/Steering Wheel/Fasteners/Bolt.SLDPRT");
        history = await bob.GetHistoryAsync("CB8/Steering Wheel/Fasteners/Bolt.SLDPRT");
        Assert.Equal(new[] { 3, 2, 1 }, history.Select(h => h.Version));
    }

    [Fact]
    public async Task Move_IsRefused_WhenCheckedOutOrTheNameIsTaken()
    {
        await using var vault = await TestVault.CreateAsync();
        var alice = await vault.OpenAsync("alice");
        var bob = await vault.OpenAsync("bob");
        Write(alice, "Nut.SLDPRT", "nut");
        Write(alice, "Fasteners/Nut.SLDPRT", "another nut");
        Write(alice, "Washer.SLDPRT", "washer");
        await alice.CheckInPathsAsync("Parts", "Nut.SLDPRT", "Fasteners/Nut.SLDPRT", "Washer.SLDPRT");

        var taken = await Assert.ThrowsAsync<VaultException>(() => alice.MoveAsync(new[] { "Nut.SLDPRT" }, "Fasteners", null));
        Assert.Equal(ErrorCodes.Conflict, taken.Code);

        await bob.SyncAsync();
        await bob.CheckOutAndApplyAsync("Washer.SLDPRT");
        var locked = await Assert.ThrowsAsync<VaultException>(() => alice.MoveAsync(new[] { "Washer.SLDPRT" }, "Fasteners", null));
        Assert.Equal(ErrorCodes.Conflict, locked.Code);
        Assert.Contains("bob", locked.Message);

        // Nothing moved.
        await alice.SyncAsync();
        Assert.Equal(LocalState.UpToDate, (await StatusAsync(alice, "Nut.SLDPRT")).LocalState);
        Assert.True(File.Exists(alice.LocalPathOf("Washer.SLDPRT")));
    }

    [Fact]
    public async Task Move_ToANameUsedBefore_DoesNotInheritTheOldFilesHistory()
    {
        await using var vault = await TestVault.CreateAsync();
        var alice = await vault.OpenAsync("alice");
        Write(alice, "Fasteners/Pin.SLDPRT", "old pin");
        await alice.CheckInPathsAsync("An older pin", "Fasteners/Pin.SLDPRT");
        await alice.DeleteAsync(new[] { "Fasteners/Pin.SLDPRT" }, "Gone", false);

        Write(alice, "Pin.SLDPRT", "new pin");
        await alice.CheckInPathsAsync("A new pin", "Pin.SLDPRT");
        await alice.MoveAsync(new[] { "Pin.SLDPRT" }, "Fasteners", null);

        var history = await alice.GetHistoryAsync("Fasteners/Pin.SLDPRT");
        Assert.Equal(new[] { "A new pin" }, history.Select(h => h.Comment));
    }

    [Fact]
    public async Task LeftoverCopies_OfFilesMovedElsewhere_AreRemovedOnSync_UnlessChanged()
    {
        await using var vault = await TestVault.CreateAsync();
        var alice = await vault.OpenAsync("alice");
        var bob = await vault.OpenAsync("bob");
        Write(alice, "Bolt.SLDPRT", "bolt");
        Write(alice, "Nut.SLDPRT", "nut");
        Write(alice, "Stays.SLDPRT", "stays");
        await alice.CheckInPathsAsync("Hardware", "Bolt.SLDPRT", "Nut.SLDPRT", "Stays.SLDPRT");
        await bob.SyncAsync();
        await bob.GetLatestAsync(false, "");

        // Bob edits his copy of the nut without checking it out (so the vault doesn't know about it).
        File.SetAttributes(bob.LocalPathOf("Nut.SLDPRT"), FileAttributes.Normal);
        Write(bob, "Nut.SLDPRT", "bob's nut");
        Write(bob, "Mine.SLDPRT", "never added");

        await alice.MoveAsync(new[] { "Bolt.SLDPRT", "Nut.SLDPRT" }, "CB8/Fasteners", null);

        await bob.SyncAsync();
        var (removed, kept) = await bob.RemoveLeftoverCopiesAsync();
        Assert.Equal(new[] { "Bolt.SLDPRT" }, removed);
        Assert.Equal(new[] { "Nut.SLDPRT" }, kept);
        Assert.False(File.Exists(bob.LocalPathOf("Bolt.SLDPRT")));
        Assert.Equal("bob's nut", Read(bob, "Nut.SLDPRT"));   // changed here: kept
        Assert.Equal("stays", Read(bob, "Stays.SLDPRT"));      // still in the vault
        Assert.Equal("never added", Read(bob, "Mine.SLDPRT")); // never was in the vault

        // Nothing more to do the second time, and moving a leftover says what it is.
        (removed, _) = await bob.RemoveLeftoverCopiesAsync();
        Assert.Empty(removed);
        var ex = await Assert.ThrowsAsync<VaultException>(() => bob.MoveAsync(new[] { "Nut.SLDPRT" }, "CB8/Fasteners", null));
        Assert.Contains("leftover", ex.Message);
    }
}
