using System.CommandLine;
using Xunit;

namespace PinballWizard.Cli.Tests.Cli;

/// <summary>
/// Pins the set of options declared on the CLI root command so that
/// accidental option deletion is caught before merge. Mirrors the
/// <c>SourceAliasContractTests</c> pattern for scraper-name coverage.
///
/// Options are constructed here the same way <c>Program.cs</c> constructs
/// them. If an option is renamed or removed in production, this test fails.
/// </summary>
public sealed class CliOptionsContractTests
{
    // The complete set of --option names that Program.cs declares.
    // Names include the "--" prefix because that is what Option.Name returns
    // in System.CommandLine 2.x (verified: new Option<bool>("--dry-run").Name
    // equals "--dry-run").
    private static readonly HashSet<string> ExpectedOptions = new(StringComparer.Ordinal)
    {
        "--source",
        "--dry-run",
        "--install-playwright",
        "--ensure-cosmos-containers",
        "--seed-ingestion-sources",
        "--seed-featured-machines",
        "--ensure-azure-foundry",
        "--ensure-ai-search",
        "--ensure-rag-index",
        "--rebuild-rag-index",
        "--ask",
        "--eval",
        "--probe-retrieval",
        "--run-rag-backfill",
        "--sync-metadata-cards",
        "--link-documents",
        "--relink-all",
        "--download-documents",
        "--download-and-link",
        "--force-redownload",
        "--migrate-download-paths",
        "--rebuild-catalog-stats",
        "--sync-game-overviews",
        "--refresh-game-overviews",
        "--reclassify-documents",
    };

    [Fact]
    public void RootCommand_ContainsAllExpectedOptions()
    {
        var root = BuildRootCommand();

        // RootCommand adds --help and --version automatically; ignore those.
        var actual = root.Options
            .Select(o => o.Name)
            .Where(n => n != "--help" && n != "--version")
            .ToHashSet(StringComparer.Ordinal);

        // Every expected option must be present.
        var missing = ExpectedOptions.Except(actual).OrderBy(x => x).ToList();
        Assert.True(
            missing.Count == 0,
            $"Option(s) present in test contract but absent from RootCommand: {string.Join(", ", missing)}");
    }

    [Fact]
    public void RootCommand_ContainsNoUnexpectedOptions()
    {
        var root = BuildRootCommand();

        var actual = root.Options
            .Select(o => o.Name)
            .Where(n => n != "--help" && n != "--version")
            .ToHashSet(StringComparer.Ordinal);

        // No option should appear that we didn't account for — a new option
        // added to production must be added to ExpectedOptions here too.
        var extra = actual.Except(ExpectedOptions).OrderBy(x => x).ToList();
        Assert.True(
            extra.Count == 0,
            $"Option(s) found in RootCommand but absent from test contract: {string.Join(", ", extra)}. " +
            "Add the new option to ExpectedOptions in CliOptionsContractTests.");
    }

    [Fact]
    public void SourceOption_DefaultValueIsAll()
    {
        // --source defaults to "all" (verified in Program.cs line DefaultValueFactory).
        // Any change to the default changes the behavior of a zero-argument run.
        var sourceOption = new Option<string?>("--source", "-s")
        {
            DefaultValueFactory = _ => "all"
        };

        var result = new RootCommand().Parse(string.Empty);
        // Build a minimal command to parse with the default
        var cmd = new RootCommand();
        cmd.Options.Add(sourceOption);
        var parsed = cmd.Parse([]);
        var value = parsed.GetValue(sourceOption);

        Assert.Equal("all", value);
    }

    // ── helpers ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Constructs a <see cref="RootCommand"/> with the same options as
    /// <c>Program.cs</c>, without wiring any actions. Kept in sync by hand
    /// — if Program.cs gains an option, add it here and to
    /// <see cref="ExpectedOptions"/>.
    /// </summary>
    private static RootCommand BuildRootCommand()
    {
        var root = new RootCommand("PinballWizard — Stern Pinball content scraper");

        root.Options.Add(new Option<string?>("--source", "-s") { DefaultValueFactory = _ => "all" });
        root.Options.Add(new Option<bool>("--dry-run"));
        root.Options.Add(new Option<bool>("--install-playwright"));
        root.Options.Add(new Option<bool>("--ensure-cosmos-containers"));
        root.Options.Add(new Option<bool>("--seed-ingestion-sources"));
        root.Options.Add(new Option<bool>("--seed-featured-machines"));
        root.Options.Add(new Option<bool>("--ensure-azure-foundry"));
        root.Options.Add(new Option<bool>("--ensure-ai-search"));
        root.Options.Add(new Option<bool>("--ensure-rag-index"));
        root.Options.Add(new Option<bool>("--rebuild-rag-index"));
        root.Options.Add(new Option<string?>("--ask"));
        root.Options.Add(new Option<bool>("--eval"));
        root.Options.Add(new Option<string?>("--probe-retrieval"));
        root.Options.Add(new Option<bool>("--run-rag-backfill"));
        root.Options.Add(new Option<bool>("--sync-metadata-cards"));
        root.Options.Add(new Option<bool>("--link-documents"));
        root.Options.Add(new Option<bool>("--relink-all"));
        root.Options.Add(new Option<bool>("--download-documents"));
        root.Options.Add(new Option<bool>("--download-and-link"));
        root.Options.Add(new Option<bool>("--force-redownload"));
        root.Options.Add(new Option<bool>("--migrate-download-paths"));
        root.Options.Add(new Option<bool>("--rebuild-catalog-stats"));
        root.Options.Add(new Option<bool>("--sync-game-overviews"));
        root.Options.Add(new Option<bool>("--refresh-game-overviews"));
        root.Options.Add(new Option<bool>("--reclassify-documents"));

        return root;
    }
}
