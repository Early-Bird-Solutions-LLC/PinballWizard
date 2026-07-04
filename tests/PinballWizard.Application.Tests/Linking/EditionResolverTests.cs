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

    // ── Group marker carried only by link_text (design §87) ──────────────
    // ~35 game-page matrices/rulesheets carry no edition token in the
    // filename/URL — they are identifiable only by their anchor text.

    [Fact]
    public void Resolve_GroupMarkerInLinkTextOnly_FansOutToAll()
    {
        // Filename has NO marker/token; the group signal is only in the anchor text.
        var r = EditionResolver.Resolve(
            "godzilla_doc_3kjh.pdf", page1Text: null, [Pro, PremLe], linkText: "Godzilla Rulesheet");

        Assert.True(r.IsGroupFanOut);
        Assert.Equal(2, r.Machines.Count);
    }

    [Theory]
    [InlineData("Godzilla Feature Matrix")]   // spaced form — real anchor text
    [InlineData("Godzilla Rule Sheet")]       // spaced form — real anchor text
    public void Resolve_SpacedGroupMarkerInLinkText_FansOutToAll(string linkText)
    {
        var r = EditionResolver.Resolve(
            "godzilla_doc_3kjh.pdf", page1Text: null, [Pro, PremLe], linkText: linkText);

        Assert.True(r.IsGroupFanOut);
        Assert.Equal(2, r.Machines.Count);
    }

    [Theory]
    [InlineData("godzilla_doc.pdf", "Godzilla Feature Matrix", true)]   // spaced anchor text
    [InlineData("godzilla_doc.pdf", "Godzilla Rule Sheet", true)]       // spaced anchor text
    public void IsGroupLevelDoc_SpacedMarkerInLinkText(string filename, string linkText, bool expected)
    {
        Assert.Equal(expected, EditionResolver.IsGroupLevelDoc(filename, linkText));
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

    // ── Cross-year token collision (issue #677) ──────────────────────────
    // AC/DC's 2012 "Pro" base (EditionTokens=["pro"]) and its 2017 "Pro Vault
    // Edition" reissue (EditionTokens=["pro","vault"]) both answer to the bare
    // "pro" token once DocumentLinker.IsEditionFamily stops requiring a shared
    // year. A document whose only signal is "pro" must resolve to the base
    // that ISN'T carrying an unsignaled extra qualifier.

    private static readonly Machine AcdcPro2012 = Base2("G43W4-MKNW0", 2012, "pro");
    private static readonly Machine AcdcProVault2017 = Base2("G43W4-MKNX0", 2017, "pro", "vault");
    private static readonly Machine AcdcPremium2012 = Base2("G43W4-MXrPx", 2012, "premium");
    private static readonly Machine AcdcPremiumVault2017 = Base2("G43W4-MdEjy", 2017, "premium", "vault");

    private static Machine Base2(string id, int year, params string[] editionTokens) => new()
    {
        Id = id, PartitionKey = "stern", ManufacturerDisplayName = "Stern Pinball",
        Title = "AC/DC", GroupId = "G43W4", Year = year,
        EditionTokens = [.. editionTokens],
    };

    [Fact]
    public void Resolve_BareToken_PrefersBaseOverUnsignaledVaultReissue()
    {
        // "ACDC_Pro_web.pdf" has no "vault" marker — must resolve to the 2012
        // Pro base, not nondeterministically to the 2017 Pro Vault Edition.
        var result = EditionResolver.Resolve(
            "ACDC_Pro_web.pdf", page1Text: null, [AcdcPro2012, AcdcProVault2017]);

        Assert.False(result.IsUnresolved);
        Assert.Single(result.Machines);
        Assert.Equal("G43W4-MKNW0", result.Machines[0].Id);
    }

    [Fact]
    public void Resolve_BareToken_AcrossFullFiveEditionAcdcFamily_StillPicksBase()
    {
        var acdcLe2012 = Base2("G43W4-MrRpw", 2012, "le");
        var result = EditionResolver.Resolve(
            "ACDC_Pro_web.pdf", page1Text: null,
            [AcdcPro2012, AcdcPremium2012, acdcLe2012, AcdcPremiumVault2017, AcdcProVault2017]);

        Assert.False(result.IsUnresolved);
        Assert.Single(result.Machines);
        Assert.Equal("G43W4-MKNW0", result.Machines[0].Id);
    }

    [Fact]
    public void Resolve_VaultToken_PicksVaultReissueOverBase()
    {
        // A doc that DOES signal "vault" (e.g. filename carries "_vault_")
        // must resolve to the 2017 reissue, not the 2012 base.
        var result = EditionResolver.Resolve(
            "ACDC_Pro_Vault_web.pdf", page1Text: null, [AcdcPro2012, AcdcProVault2017]);

        Assert.False(result.IsUnresolved);
        Assert.Single(result.Machines);
        Assert.Equal("G43W4-MKNX0", result.Machines[0].Id);
    }

    [Fact]
    public void Resolve_TokenTiesAcrossEquallySpecificCandidates_StaysUnresolved()
    {
        // Neither candidate is more specific than the other for this token —
        // never guess between two equally-plausible bases.
        var vaultA = Base2("G43W4-AAAA", 2017, "pro", "vault");
        var vaultB = Base2("G43W4-BBBB", 2019, "pro", "vault");

        var result = EditionResolver.Resolve("ACDC_Pro_web.pdf", page1Text: null, [vaultA, vaultB]);

        Assert.True(result.IsUnresolved);
        Assert.Empty(result.Machines);
    }
}
