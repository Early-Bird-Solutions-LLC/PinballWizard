using System.Reflection;
using System.Runtime.CompilerServices;
using PinballWizard.Application.Persistence;
using PinballWizard.Core.Scraping;
using PinballWizard.Infrastructure.Scraping.Stern;
using Xunit;

namespace PinballWizard.Infrastructure.Tests;

// Pins that every ISourceScraper.SourceId is a known IngestionSource id (an
// IngestionSourceIds constant). A scraper that declares an unknown/typo'd SourceId
// would write its run history to a partition the source-detail page never reads
// (orphan runs) — this makes that a build failure. Mirrors SourceAliasContractTests:
// SourceId is an expression-bodied literal, so GetUninitializedObject reads it without
// invoking the DI constructor.
public sealed class ScraperSourceIdContractTests
{
    private static HashSet<string> KnownIngestionSourceIds() =>
        typeof(IngestionSourceIds)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral && f.FieldType == typeof(string))
            .Select(f => (string)f.GetRawConstantValue()!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    [Fact]
    public void EveryScraperSourceIdIsAKnownIngestionSourceId()
    {
        var known = KnownIngestionSourceIds();

        var scraperTypes = typeof(ManualsScraper).Assembly
            .GetTypes()
            .Where(t => !t.IsAbstract && !t.IsInterface && typeof(ISourceScraper).IsAssignableFrom(t))
            .ToList();

        Assert.NotEmpty(scraperTypes);

        var unknown = new List<string>();
        foreach (var type in scraperTypes)
        {
            var instance = (ISourceScraper)RuntimeHelpers.GetUninitializedObject(type);
            var sourceId = instance.SourceId;
            if (!known.Contains(sourceId))
                unknown.Add($"{type.Name} → \"{sourceId}\"");
        }

        Assert.True(
            unknown.Count == 0,
            "Scraper(s) declare a SourceId not present in IngestionSourceIds — their run " +
            "history would write to an orphan partition: " + string.Join(", ", unknown));
    }
}
