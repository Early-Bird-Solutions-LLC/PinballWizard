using PinballWizard.Application.Linking;
using PinballWizard.Core.Domain;
using Xunit;

namespace PinballWizard.Application.Tests.Linking;

/// <summary>
/// Tests for <see cref="EditionResolver"/> — resolving a per-edition document
/// to the edition-correct base machine in a same-franchise candidate set.
/// </summary>
public sealed class EditionResolverTests
{
    // ── Filename token extraction ────────────────────────────────────────

    [Theory]
    [InlineData("Godzilla_Pro_web.pdf", "pro")]
    [InlineData("GODZILLA-PRO-New-Address-compressed.pdf", "pro")]
    [InlineData("Godzilla_LE_Pre_web.pdf", "le")]
    [InlineData("GODZILLA-PREM-New-Address-compressed.pdf", "premium")]
    [InlineData("Godzilla_70th_web.pdf", "70th")]
    public void ExtractEditionToken_FromFilename(string filename, string expected)
    {
        Assert.Equal(expected, EditionResolver.ExtractEditionToken(filename));
    }

    [Theory]
    [InlineData("Godzilla-Pinball-Feature-Matrix-3kjhasdf.pdf")]
    [InlineData("Godzilla-Rulesheet.pdf")]
    public void ExtractEditionToken_GroupLevelDoc_ReturnsNull(string filename)
    {
        Assert.Null(EditionResolver.ExtractEditionToken(filename));
    }

    [Theory]
    [InlineData("Godzilla-Pinball-Feature-Matrix-3kjhasdf.pdf", null, true)]
    [InlineData("Godzilla-Rulesheet.pdf", null, true)]
    [InlineData("Godzilla_Pro_web.pdf", null, false)]                       // no marker, null link_text is harmless
    [InlineData("Godzilla_random.pdf", "Godzilla Rulesheet", true)]         // marker only in link_text
    [InlineData("Godzilla_random.pdf", "Godzilla Rulesheet (all editions)", true)]
    [InlineData("Godzilla_Pro_web.pdf", "Godzilla Pro Manual", false)]      // no group marker in either place
    public void IsGroupLevelDoc(string filename, string? linkText, bool expected)
    {
        Assert.Equal(expected, EditionResolver.IsGroupLevelDoc(filename, linkText));
    }

    // ── Resolve candidate set → edition-correct base ─────────────────────

    private static Machine Base(string id, params string[] editionTokens) => new()
    {
        Id = id, PartitionKey = "stern", ManufacturerDisplayName = "Stern Pinball",
        Title = "Godzilla", GroupId = "GweeP", Year = 2021,
        EditionTokens = [.. editionTokens],
    };

    private static readonly Machine Pro = Base("GweeP-MW95j", "pro");
    private static readonly Machine PremLe = Base("GweeP-Ml9pZ", "premium", "le", "70th");

    [Fact]
    public void Resolve_ProToken_PicksProBase()
    {
        var result = EditionResolver.Resolve("Godzilla_Pro_web.pdf", page1Text: null, [Pro, PremLe]);

        Assert.False(result.IsGroupFanOut);
        Assert.False(result.IsUnresolved);
        Assert.Single(result.Machines);
        Assert.Equal("GweeP-MW95j", result.Machines[0].Id);
    }

    [Fact]
    public void Resolve_LeToken_PicksPremiumLeBase()
    {
        var result = EditionResolver.Resolve("Godzilla_LE_Pre_web.pdf", page1Text: null, [Pro, PremLe]);

        Assert.Single(result.Machines);
        Assert.Equal("GweeP-Ml9pZ", result.Machines[0].Id);
    }

    [Fact]
    public void Resolve_GroupLevelDoc_FansOutToAllBases()
    {
        var result = EditionResolver.Resolve("Godzilla-Rulesheet.pdf", page1Text: null, [Pro, PremLe]);

        Assert.True(result.IsGroupFanOut);
        Assert.Equal(2, result.Machines.Count);
    }

    [Fact]
    public void Resolve_Page1OverridesMisleadingFilename()
    {
        // Filename says LE, but the PDF page 1 self-identifies as PRO — page 1 wins.
        var result = EditionResolver.Resolve(
            "Godzilla_LE_mislabeled.pdf",
            page1Text: "GODZILLA PRO MANUAL 500-55T5-01 TABLE OF CONTENTS",
            [Pro, PremLe]);

        Assert.Single(result.Machines);
        Assert.Equal("GweeP-MW95j", result.Machines[0].Id);
    }

    [Fact]
    public void Resolve_NoEditionSignal_ReturnsUnresolved()
    {
        var result = EditionResolver.Resolve("Godzilla_mystery.pdf", page1Text: null, [Pro, PremLe]);

        Assert.True(result.IsUnresolved);
        Assert.Empty(result.Machines);
    }

    [Fact]
    public void Resolve_LePreCombined_MapsToPremiumLeBaseOnly()
    {
        var r = EditionResolver.Resolve("Godzilla_LE_Pre_web.pdf", page1Text: null, [Pro, PremLe]);
        Assert.Single(r.Machines);
        Assert.Equal("GweeP-Ml9pZ", r.Machines[0].Id);   // _le_ token ∈ PremLe tokens only
    }

    [Fact]
    public void Resolve_Rulesheet_FansOutToAll()
    {
        var r = EditionResolver.Resolve("Godzilla-Rulesheet.pdf", page1Text: null, [Pro, PremLe]);
        Assert.True(r.IsGroupFanOut);
        Assert.Equal(2, r.Machines.Count);
    }

    [Fact]
    public void Resolve_SingleCandidate_ReturnsItDirectly()
    {
        var result = EditionResolver.Resolve("anything.pdf", page1Text: null, [Pro]);

        Assert.False(result.IsUnresolved);
        Assert.Single(result.Machines);
        Assert.Equal("GweeP-MW95j", result.Machines[0].Id);
    }

    // ── EditionResolution.Scope (resolution outcome → structural scope) ───

    [Fact]
    public void Scope_SingleEdition_WhenResolvedToOneBase()
    {
        var result = EditionResolver.Resolve("Godzilla_Pro_web.pdf", page1Text: null, [Pro, PremLe]);

        Assert.Equal(EditionScope.SingleEdition, result.Scope);
    }

    [Fact]
    public void Scope_FranchiseWide_WhenGroupLevelFanOut()
    {
        var result = EditionResolver.Resolve("Godzilla-Rulesheet.pdf", page1Text: null, [Pro, PremLe]);

        Assert.Equal(EditionScope.FranchiseWide, result.Scope);
    }

    [Fact]
    public void Scope_EditionSubset_WhenResolvedToAStrictSubset()
    {
        var result = EditionResolution.ForSubset([Pro, PremLe]);

        Assert.Equal(EditionScope.EditionSubset, result.Scope);
    }

    [Fact]
    public void Scope_SingleEdition_WhenSingleCandidate()
    {
        var result = EditionResolver.Resolve("anything.pdf", page1Text: null, [Pro]);

        Assert.Equal(EditionScope.SingleEdition, result.Scope);
    }
}
