using System.Runtime.CompilerServices;
using PinballWizard.Application;
using PinballWizard.Core.Scraping;
using PinballWizard.Infrastructure.Scraping.Stern;
using Xunit;

namespace PinballWizard.Infrastructure.Tests;

/// <summary>
/// Pins the contract between <see cref="ISourceScraper"/> implementations
/// and <see cref="ScraperOrchestrator.KnownSourceCanonicalNames"/>.
/// A scraper whose <c>Name</c> is missing from the alias map silently
/// becomes unreachable from the CLI <c>--source &lt;alias&gt;</c> flag —
/// the run completes with no scrapers selected and no error. This test
/// catches the typo that would cause that.
/// </summary>
/// <remarks>
/// Uses <see cref="RuntimeHelpers.GetUninitializedObject"/> to read
/// each scraper's <c>Name</c> property without invoking its
/// dependency-injected constructor. All six current scrapers expose
/// <c>Name</c> as an expression-bodied literal, so the getter does
/// not depend on instance state.
/// </remarks>
public sealed class SourceAliasContractTests
{
    [Fact]
    public void EveryRegisteredScraperNameIsRecognisedByTheCliFilter()
    {
        var infrastructureAssembly = typeof(ManualsScraper).Assembly;

        var scraperTypes = infrastructureAssembly
            .GetTypes()
            .Where(t => !t.IsAbstract
                     && !t.IsInterface
                     && typeof(ISourceScraper).IsAssignableFrom(t))
            .ToList();

        Assert.NotEmpty(scraperTypes);

        var unknown = new List<string>();
        foreach (var type in scraperTypes)
        {
            var instance = (ISourceScraper)RuntimeHelpers.GetUninitializedObject(type);
            var name = instance.Name;
            if (!ScraperOrchestrator.KnownSourceCanonicalNames.Contains(name))
            {
                unknown.Add($"{type.Name} → \"{name}\"");
            }
        }

        Assert.True(
            unknown.Count == 0,
            $"Scraper(s) declare a Name not present in ScraperOrchestrator.KnownSourceCanonicalNames; --source filter would silently miss them: {string.Join(", ", unknown)}");
    }

    [Fact]
    public void KnownSourceCanonicalNamesIsNonEmpty()
    {
        Assert.NotEmpty(ScraperOrchestrator.KnownSourceCanonicalNames);
    }
}
