using Microsoft.Extensions.DependencyInjection;
using PinballWizard.Web.Engineering;
using Xunit;

namespace PinballWizard.Web.Tests.Engineering;

public sealed class EngineeringDocsProviderTests
{
    private static IEngineeringDocsProvider Provider()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IEngineeringDocsProvider, EngineeringDocsProvider>();
        return services.BuildServiceProvider().GetRequiredService<IEngineeringDocsProvider>();
    }

    [Fact]
    public void Docs_AreLoadedFromEmbeddedManifestSet()
    {
        var p = Provider();
        Assert.Contains(p.Docs, d => d.Slug == "vision");
        Assert.NotNull(p.BySlug("glossary"));
        Assert.Null(p.BySlug("does-not-exist"));
    }

    [Fact]
    public void Adrs_ArePopulatedAndParsedForNumberTitleStatus()
    {
        var p = Provider();
        Assert.NotEmpty(p.Adrs);
        var first = p.Adrs[0];
        Assert.True(first.Number > 0);
        Assert.False(string.IsNullOrWhiteSpace(first.Title));
    }

    [Fact]
    public void SourceCommit_IsExposedFromAssemblyMetadata()
    {
        var p = Provider();
        Assert.False(string.IsNullOrWhiteSpace(p.SourceCommit));
    }
}
