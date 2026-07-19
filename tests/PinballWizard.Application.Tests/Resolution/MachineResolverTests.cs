using PinballWizard.Application.Resolution;
using PinballWizard.Core.Domain;
using Xunit;

namespace PinballWizard.Application.Tests.Resolution;

public class MachineResolverTests
{
    // ManufacturerDisplayName is required by Machine but not exercised by the resolver —
    // using PartitionKey as a placeholder so the helper stays compact.
    private static Machine M(string id, string title, string group, string mfr, int year = 2020) =>
        new() { Id = id, Title = title, GroupId = group, PartitionKey = mfr, Year = year,
                ManufacturerDisplayName = mfr };

    private static readonly Machine Houdini   = M("GH-M1", "Houdini: Master of Mystery", "GH", "americanpinball", 2017);
    private static readonly Machine HotWheels = M("GW-M1", "Hot Wheels", "GW", "americanpinball");
    private static readonly Machine GTF       = M("GT-M1", "Galactic Tank Force", "GT", "americanpinball", 2023);
    private static readonly Machine SternPin  = M("GP-M1", "Pinball", "GP", "stern", 1977);
    private static readonly Machine GodzPro   = M("GZ-M1", "Godzilla", "GZ", "stern", 2021);
    private static readonly Machine GodzPrem  = M("GZ-M2", "Godzilla", "GZ", "stern", 2021);

    private static readonly MachineAliasEntry[] Aliases =
    [
        new("GTF", "GT", null, "americanpinball", "AP filename abbreviation", "jkeeley2073"),
    ];

    private static MachineResolver Build(params Machine[] machines)
    {
        var index = InMemoryMachineIndex.Build(machines, Aliases);
        return new MachineResolver(index, machines.ToDictionary(m => m.Id));
    }

    [Fact]
    public void Resolve_FranchiseTitle_BindsFilenameToSubtitledMachine()
    {
        // THE AP CASE: filename says "Houdini", catalog says "Houdini: Master of Mystery".
        // The FranchiseTitle variant "houdini" (stripped of the subtitle) must match via
        // containment even though it is a single-token variant — "houdini" is a specific
        // machine name, not a generic trailing qualifier.
        var r = Build(Houdini, HotWheels).Resolve(
            new ResolutionQuery("Houdini--Quick-Reference-Guide.pdf", EvidenceKind.Filename, "americanpinball"));

        var resolved = Assert.IsType<ResolutionResult.Resolved>(r);
        Assert.Equal("GH-M1", resolved.MachineId);
    }

    [Fact]
    public void Resolve_CuratedAlias_BindsAbbreviatedFilename()
    {
        var r = Build(GTF, Houdini).Resolve(
            new ResolutionQuery("GTF-Quick-Reference-Guide.pdf", EvidenceKind.Filename, "americanpinball"));

        var resolved = Assert.IsType<ResolutionResult.Resolved>(r);
        Assert.Equal("GT-M1", resolved.MachineId);
        Assert.Equal(VariantKind.CuratedAlias, resolved.Evidence.VariantKind);
    }

    [Fact]
    public void Resolve_GenericDocument_NoMatch()
    {
        // "Shaker.pdf" / "Assembly.pdf" are platform docs — they must NOT be attributed to a machine.
        var r = Build(Houdini, HotWheels, GTF).Resolve(
            new ResolutionQuery("Shaker.pdf", EvidenceKind.Filename, "americanpinball"));

        Assert.IsType<ResolutionResult.NoMatch>(r);
    }

    [Fact]
    public void Resolve_SingleTokenVariant_NotEligibleForContainmentEvidence()
    {
        // The 1977 Stern "Pinball" once matched 172 documents. A containment-kind query
        // mentioning the word "pinball" must NOT bind to it.
        // "pinball" is a trailing-qualifier word — this is what blocks it from containment,
        // not merely the fact that it is single-token.
        var r = Build(SternPin, GodzPro).Resolve(
            new ResolutionQuery("Stern-Pinball-Service-Bulletin.pdf", EvidenceKind.Filename, "stern"));

        Assert.IsType<ResolutionResult.NoMatch>(r);
    }

    [Fact]
    public void Resolve_SingleTokenVariant_IsEligibleForExactEvidence()
    {
        // ...but an exact provenance slug of "pinball" is strong evidence and MAY bind.
        var r = Build(SternPin, GodzPro).Resolve(
            new ResolutionQuery("pinball", EvidenceKind.ProvenanceSlug, "stern"));

        var resolved = Assert.IsType<ResolutionResult.Resolved>(r);
        Assert.Equal("GP-M1", resolved.MachineId);
        Assert.Equal(ResolutionStage.Exact, resolved.Evidence.Stage);
    }

    [Fact]
    public void Resolve_SameGroupSiblings_ResolvesAsFamily()
    {
        var r = Build(GodzPro, GodzPrem).Resolve(
            new ResolutionQuery("Godzilla-Manual.pdf", EvidenceKind.Filename, "stern"));

        var fam = Assert.IsType<ResolutionResult.ResolvedFamily>(r);
        Assert.Equal("GZ", fam.GroupId);
        Assert.Equal(2, fam.MachineIds.Count);
    }

    [Fact]
    public void Resolve_NonFamilyMultiMatch_IsAmbiguous_AndNeverGuesses()
    {
        var a = M("GA-M1", "Rampage", "GA", "americanpinball", 2019);
        var b = M("GB-M1", "Rampage", "GB", "americanpinball", 2024); // different group = different game
        var r = Build(a, b).Resolve(
            new ResolutionQuery("Rampage-Manual-10-19-2021.pdf", EvidenceKind.Filename, "americanpinball"));

        var amb = Assert.IsType<ResolutionResult.Ambiguous>(r);
        Assert.Equal(2, amb.Candidates.Count);
    }

    [Fact]
    public void Resolve_FuzzyEvidence_HardFiltersByManufacturer()
    {
        var sternHoudini = M("GX-M1", "Houdini", "GX", "stern");
        var r = Build(Houdini, sternHoudini).Resolve(
            new ResolutionQuery("Houdini--Quick-Reference-Guide.pdf", EvidenceKind.Filename, "americanpinball"));

        var resolved = Assert.IsType<ResolutionResult.Resolved>(r);
        Assert.Equal("GH-M1", resolved.MachineId); // AP, not Stern
    }

    [Fact]
    public void Resolve_LongestVariantWins()
    {
        var tank = M("GK-M1", "Tank", "GK", "americanpinball");
        var r = Build(GTF, tank).Resolve(
            new ResolutionQuery("Galactic-Tank-Force-Game-Manual.pdf", EvidenceKind.Filename, "americanpinball"));

        var resolved = Assert.IsType<ResolutionResult.Resolved>(r);
        Assert.Equal("GT-M1", resolved.MachineId);
    }

    // ── Edge-case contracts (added from local review) ──────────────────────
    //
    // These document the resolver's behavior at its boundaries for the six Wave-2
    // consumers that will call it. Each was reachable but unexercised.

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("---__--")]   // normalizes to zero tokens
    public void Resolve_QueryWithNoTokens_IsNoMatch(string text)
    {
        var r = Build(Houdini, GTF).Resolve(
            new ResolutionQuery(text, EvidenceKind.Filename, "americanpinball"));

        Assert.IsType<ResolutionResult.NoMatch>(r);
    }

    [Fact]
    public void Resolve_NullManufacturerHint_StillResolves()
    {
        // Scope() early-returns when the hint is null, so an unscoped query must
        // still bind rather than silently falling through to NoMatch.
        var r = Build(Houdini, HotWheels).Resolve(
            new ResolutionQuery("Houdini--Quick-Reference-Guide.pdf", EvidenceKind.Filename, null));

        var resolved = Assert.IsType<ResolutionResult.Resolved>(r);
        Assert.Equal("GH-M1", resolved.MachineId);
    }

    [Fact]
    public void Resolve_EmptyIndex_IsNoMatch()
    {
        var r = Build().Resolve(
            new ResolutionQuery("Houdini--Quick-Reference-Guide.pdf", EvidenceKind.Filename, "americanpinball"));

        Assert.IsType<ResolutionResult.NoMatch>(r);
    }

    [Fact]
    public void Resolve_MultiMatchWithNullGroupId_IsAmbiguous_NotResolved()
    {
        // Two same-titled machines with no group id cannot be proven a family, so
        // the resolver must decline rather than pick one. This is the never-guess
        // invariant at its least obvious boundary.
        var a = M("GN-M1", "Rampage", null!, "americanpinball");
        var b = M("GN-M2", "Rampage", null!, "americanpinball");

        var r = Build(a, b).Resolve(
            new ResolutionQuery("Rampage-Manual.pdf", EvidenceKind.Filename, "americanpinball"));

        Assert.IsType<ResolutionResult.Ambiguous>(r);
    }
}
