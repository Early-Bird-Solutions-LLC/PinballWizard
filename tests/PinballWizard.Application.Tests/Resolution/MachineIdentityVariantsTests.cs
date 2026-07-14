using PinballWizard.Application.Resolution;
using PinballWizard.Core.Domain;
using Xunit;

namespace PinballWizard.Application.Tests.Resolution;

public class MachineIdentityVariantsTests
{
    // NOTE (build fix): ManufacturerDisplayName is a required property on Machine that the
    // brief's test code did not set. Adding it to prevent a compile error; the value is not
    // exercised by MachineIdentityVariants (which uses PartitionKey, not display name).
    private static Machine Ap(string id, string title, string group) => new()
    {
        Id = id, Title = title, GroupId = group, PartitionKey = "americanpinball", Year = 2017,
        ManufacturerDisplayName = "American Pinball",
    };

    [Fact]
    public void For_ProducesFranchiseTitle_StrippingSubtitle()
    {
        // The whole AP gap: filenames say "Houdini", the catalog says "Houdini: Master of Mystery".
        var vs = MachineIdentityVariants.For(Ap("GH-M1", "Houdini: Master of Mystery", "GH"), []);

        Assert.Contains(vs, v => v.Kind == VariantKind.FullTitle && v.Key == "houdini master of mystery");
        Assert.Contains(vs, v => v.Kind == VariantKind.FranchiseTitle && v.Key == "houdini");
    }

    [Fact]
    public void For_StripsTrailingQualifiers()
    {
        // Generalizes PR #750 (which fixed this only in the reconciler).
        var vs = MachineIdentityVariants.For(Ap("GM-M1", "Medieval Madness Merlin Edition Pinball", "GM"), []);
        Assert.Contains(vs, v => v.Kind == VariantKind.FranchiseTitle && v.Key == "medieval madness");
    }

    [Fact]
    public void For_IncludesScraperSlugs_AsOneEvidenceSourceAmongSeveral()
    {
        var m = Ap("GH-M1", "Houdini: Master of Mystery", "GH");
        m.ManufacturerSlugs["americanpinball"] = "houdini";

        var vs = MachineIdentityVariants.For(m, []);
        Assert.Contains(vs, v => v.Kind == VariantKind.ScraperSlug && v.Key == "houdini");
    }

    [Fact]
    public void For_IncludesCuratedAliases_ScopedToManufacturerAndGroup()
    {
        var m = Ap("GTFx-M1", "Galactic Tank Force", "GTFx");
        var aliases = new List<MachineAliasEntry>
        {
            new("GTF", "GTFx", null, "americanpinball", "AP filename abbreviation", "jkeeley2073"),
            new("GTF", "OTHER", null, "stern", "must NOT apply — different manufacturer", "x"),
        };

        var vs = MachineIdentityVariants.For(m, aliases);
        Assert.Single(vs, v => v.Kind == VariantKind.CuratedAlias && v.Key == "gtf");
    }

    [Fact]
    public void For_IncludesEditionAndManufacturerForms()
    {
        // NOTE (build fix): ManufacturerDisplayName added; required by Machine but not used by For().
        var m = new Machine
        {
            Id = "GZ-M1", Title = "Godzilla", GroupId = "GZ", PartitionKey = "stern", Year = 2021,
            ManufacturerDisplayName = "Stern Pinball",
            EditionTokens = ["pro"],
        };

        var vs = MachineIdentityVariants.For(m, []);
        Assert.Contains(vs, v => v.Kind == VariantKind.TitleWithEdition && v.Key == "godzilla pro");
        Assert.Contains(vs, v => v.Kind == VariantKind.ManufacturerPrefixed && v.Key == "stern godzilla");
    }

    [Fact]
    public void For_NeverEmitsAnEmptyVariant()
    {
        var vs = MachineIdentityVariants.For(Ap("G-M1", "Pinball", "G"), []);
        Assert.All(vs, v => Assert.NotEmpty(v.Tokens));
    }
}
