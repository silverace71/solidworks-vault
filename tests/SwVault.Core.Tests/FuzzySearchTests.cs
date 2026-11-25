using SwVault.Core.Search;

namespace SwVault.Core.Tests;

public class FuzzyMatcherTests
{
    [Theory]
    [InlineData("brack", "FrontBracket.SLDPRT")]
    [InlineData("BRACKET", "front_bracket.sldprt")]
    [InlineData("fbr", "Front_Bracket_Rev.SLDPRT")]          // initials
    [InlineData("frbrk", "FrontBracket.SLDPRT")]              // abbreviation
    [InlineData("braket", "FrontBracket.SLDPRT")]             // missing letter
    [InlineData("bracekt", "FrontBracket.SLDPRT")]            // swapped letters
    [InlineData("uprigth", "Upright_Front_Left.SLDPRT")]
    [InlineData("6061", "Aluminum 6061-T6")]
    public void Matches(string term, string text) => Assert.NotNull(FuzzyMatcher.Match(term, text));

    [Theory]
    [InlineData("xyz", "FrontBracket.SLDPRT")]
    [InlineData("bkt", "Manifold_Intake_Tube.SLDPRT")]        // letters exist only scattered
    [InlineData("brkt", "")]
    [InlineData("hub", "Hb.SLDPRT")]
    public void Rejects(string term, string text) => Assert.Null(FuzzyMatcher.Match(term, text));

    [Fact]
    public void Ranks_SubstringAboveSubsequenceAboveTypo()
    {
        var substring = FuzzyMatcher.Match("bracket", "FrontBracket.SLDPRT")!.Score;
        var subsequence = FuzzyMatcher.Match("frbrk", "FrontBracket.SLDPRT")!.Score;
        var typo = FuzzyMatcher.Match("brakcet", "FrontBracket.SLDPRT")!.Score;
        Assert.True(substring > subsequence, $"{substring} vs {subsequence}");
        Assert.True(substring > typo, $"{substring} vs {typo}");
    }

    [Fact]
    public void Ranks_WordStartAboveMidWord()
    {
        var wordStart = FuzzyMatcher.Match("arm", "Arm_Upper.SLDPRT")!.Score;
        var midWord = FuzzyMatcher.Match("arm", "Swarmbot.SLDPRT")!.Score;
        Assert.True(wordStart > midWord);
    }

    [Fact]
    public void Positions_PointAtMatchedCharacters()
    {
        Assert.Equal(new[] { 5, 6, 7, 8, 9 }, FuzzyMatcher.Match("brack", "FrontBracket")!.Positions);
        var initials = FuzzyMatcher.Match("fb", "Front_Bracket")!.Positions;
        Assert.Equal(new[] { 0, 6 }, initials);
    }

    [Fact]
    public void Words_SplitsOnSeparatorsCaseAndDigits()
    {
        var text = "FrontBracket_rev2.SLDPRT";
        var words = FuzzyMatcher.Words(text).Select(w => text.Substring(w.Start, w.Length)).ToArray();
        Assert.Equal(new[] { "Front", "Bracket", "rev", "2", "SLDPRT" }, words);
    }
}

public class FuzzyQueryTests
{
    private static List<SearchField> Fields(string name, params (string Label, string Value)[] more)
    {
        var fields = new List<SearchField> { new(FuzzyQuery.NameLabel, name, 1.0), new("Folder", "Suspension/Front", 0.6) };
        fields.AddRange(more.Select(m => new SearchField(m.Label, m.Value, 0.85)));
        return fields;
    }

    [Fact]
    public void EveryTermMustMatchSomeField()
    {
        var fields = Fields("Upright.SLDPRT", ("Material", "7075-T6"));
        Assert.NotNull(FuzzyQuery.Parse("upright 7075").Score(fields));
        Assert.NotNull(FuzzyQuery.Parse("7075 susp").Score(fields));
        Assert.Null(FuzzyQuery.Parse("upright titanium").Score(fields));
    }

    [Fact]
    public void LabelledTerm_OnlySearchesThatField()
    {
        var fields = Fields("Steel_Tab.SLDPRT", ("Material", "6061 Aluminum"));
        Assert.NotNull(FuzzyQuery.Parse("material:alum").Score(fields));
        Assert.Null(FuzzyQuery.Parse("material:steel").Score(fields));
        var hit = FuzzyQuery.Parse("mat:6061").Score(fields)!;
        Assert.Equal("Material", hit.MatchLabel);
        Assert.Equal("6061 Aluminum", hit.MatchValue);
    }

    [Fact]
    public void NameHits_OutrankPropertyHits_AndReportPositions()
    {
        var byName = FuzzyQuery.Parse("hub").Score(Fields("Hub.SLDPRT"))!;
        var byProperty = FuzzyQuery.Parse("hub").Score(Fields("Part7.SLDPRT", ("Description", "Rear hub")))!;
        Assert.True(byName.Score > byProperty.Score);
        Assert.Equal(new[] { 0, 1, 2 }, byName.NamePositions);
        Assert.Null(byName.MatchLabel);
        Assert.Equal("Description", byProperty.MatchLabel);
    }

    [Fact]
    public void QuotedTermsKeepSpaces()
    {
        var fields = Fields("Part.SLDPRT", ("Description", "front left upright"));
        Assert.NotNull(FuzzyQuery.Parse("\"left upright\"").Score(fields));
        Assert.Null(FuzzyQuery.Parse("\"upright left\"").Score(fields));
    }

    [Fact]
    public void EmptyQuery_MatchesEverything() => Assert.NotNull(FuzzyQuery.Parse("  ").Score(Fields("Any.SLDPRT")));
}

public class VersionNoteTests
{
    [Fact]
    public void NewVersion_ShowsItsFullNote()
    {
        var meta = new SwVault.Core.Vault.FileMeta { Version = 4, Comment = "Thinned the web\r\nsaves 40 g" };
        Assert.Equal("Thinned the web\nsaves 40 g", VaultSession.VersionNote(true, meta, "Thinned the web saves 40 g"));
    }

    [Fact]
    public void NewVersion_WithoutNote_IsNamedAfterItsNumber() =>
        Assert.Equal("Version 7", VaultSession.VersionNote(true, new SwVault.Core.Vault.FileMeta { Version = 7 }, "Check in 3 file(s)"));

    [Fact]
    public void WorkflowChange_ShowsItsOwnSubject() =>
        Assert.Equal("Approve: Tube.SLDPRT", VaultSession.VersionNote(false, new SwVault.Core.Vault.FileMeta { Version = 2, Comment = "old note" }, "Approve: Tube.SLDPRT"));

    [Theory]
    [InlineData(null, 3, "Version 3")]
    [InlineData("  ", 1, "Version 1")]
    [InlineData(" New fillets \r\n", 2, "New fillets")]
    public void DefaultNote(string? note, int version, string expected) => Assert.Equal(expected, VaultSession.DefaultNote(note, version));

    [Fact]
    public void Thumbnails_AcceptOnlySmallImages()
    {
        Assert.NotNull(VaultSession.DecodeThumbnail(Convert.ToBase64String(new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 1, 2 })));
        Assert.Null(VaultSession.DecodeThumbnail(Convert.ToBase64String(new byte[] { 1, 2, 3, 4, 5 })));
        Assert.Null(VaultSession.DecodeThumbnail("not base64!"));
        Assert.Null(VaultSession.DecodeThumbnail(Convert.ToBase64String(new byte[VaultSession.MaxThumbnailBytes + 10])));
        Assert.Equal("Car/Hub.SLDPRT", SwVault.Core.Util.PathRules.FileForThumbPath(SwVault.Core.Util.PathRules.ThumbPathFor("Car/Hub.SLDPRT")));
    }
}

public class ReleaseApprovalTests
{
    [Fact]
    public void Release_RecordsWhoApprovedIt()
    {
        var config = SwVault.Core.Vault.VaultConfig.CreateDefault("T", @"C:\SWVault\T", "lead", null);
        var approve = config.Workflow.Transitions.Single(t => t.Name == "Approve");
        var meta = new SwVault.Core.Vault.FileMeta { Version = 5, State = "InReview" };
        SwVault.Core.Vault.WorkflowEngine.Apply(meta, approve, "A", "bob", "2026-10-04T10:00:00Z", "  ");
        Assert.Equal(("Released", "A", "bob", 5, "Approve"), (meta.State, meta.Revision, meta.ApprovedBy, meta.ApprovedVersion ?? 0, meta.ApprovedWith));
        Assert.Null(meta.ApprovalNote); // release notes are optional

        var reject = config.Workflow.Transitions.Single(t => t.Name == "Reject");
        var other = new SwVault.Core.Vault.FileMeta { Version = 2, State = "InReview" };
        SwVault.Core.Vault.WorkflowEngine.Apply(other, reject, null, "bob", "2026-10-04T10:00:00Z", "needs fillets");
        Assert.Null(other.ApprovedBy);
    }
}

public class VaultLinkTests
{
    [Fact]
    public void RoundTrips()
    {
        var link = new SwVault.Core.Client.VaultLink("open", "https://vault.example.ts.net/team/cad", "Chassis/Front Frame #2.SLDPRT");
        Assert.Equal(link, SwVault.Core.Client.VaultLink.TryParse(link.ToString()));
    }

    [Theory]
    [InlineData("swvault://checkout?path=Brakes%2FCaliper.SLDPRT", "checkout", "Brakes/Caliper.SLDPRT")]
    [InlineData("SWVAULT://Open/?path=Brakes\\Caliper.SLDPRT", "open", "Brakes/Caliper.SLDPRT")]
    [InlineData("swvault:history?path=Hub.SLDPRT", "history", "Hub.SLDPRT")]
    public void Parses(string text, string action, string path)
    {
        var link = SwVault.Core.Client.VaultLink.TryParse(text)!;
        Assert.Equal((action, path), (link.Action, link.Path));
    }

    [Theory]
    [InlineData("swvault://undocheckout?path=Hub.SLDPRT")]   // not allowed from a link
    [InlineData("swvault://open?path=..%2F..%2FWindows%2Fwin.ini")]
    [InlineData("swvault://open?path=.swvault%2Fvault.json")]
    [InlineData("swvault://open?path=C:%5CWindows%5Cnotepad.exe")]
    [InlineData("swvault://open")]
    [InlineData("https://open?path=Hub.SLDPRT")]
    public void Rejects(string text) => Assert.Null(SwVault.Core.Client.VaultLink.TryParse(text));

    [Fact]
    public void ComparesVaultUrls()
    {
        Assert.True(SwVault.Core.Client.VaultLink.SameVault("https://Vault.example/team/cad.git", "https://vault.example/team/cad/"));
        Assert.False(SwVault.Core.Client.VaultLink.SameVault("https://vault.example/team/cad", "https://vault.example/team/cam"));
        Assert.False(SwVault.Core.Client.VaultLink.SameVault(null, null));
    }
}
