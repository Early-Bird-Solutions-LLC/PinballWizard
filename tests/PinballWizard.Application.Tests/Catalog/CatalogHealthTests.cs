using PinballWizard.Application.Catalog;
using Xunit;

namespace PinballWizard.Application.Tests.Catalog;

public sealed class CatalogHealthTests
{
    private static MachineDocStats Stat(string id, int docs, bool manual, string? group = null) =>
        new(id, id, null, group, 2021, false, docs,
            manual ? new Dictionary<string, int> { ["Manual"] = 1 } : new(), manual);

    [Fact]
    public void Empty_When_NoDocs()
        => Assert.Contains(CatalogHealthFlag.Empty,
            CatalogHealth.Evaluate(Stat("m", 0, false), siblings: []));

    [Fact]
    public void NoManual_When_DocsButNoManual()
        => Assert.Contains(CatalogHealthFlag.NoManual,
            CatalogHealth.Evaluate(Stat("m", 3, false), siblings: []));

    [Fact]
    public void EditionGap_When_FewerDocsThanSibling()
    {
        var self = Stat("pro", 0, false, group: "G");
        var sibling = Stat("le", 5, true, group: "G");
        Assert.Contains(CatalogHealthFlag.EditionGap,
            CatalogHealth.Evaluate(self, siblings: [sibling]));
    }

    [Fact]
    public void Ok_When_HasDocsAndManualAndNoGap()
        => Assert.Equal(
            new[] { CatalogHealthFlag.Ok },
            CatalogHealth.Evaluate(Stat("m", 4, true), siblings: []));
}
