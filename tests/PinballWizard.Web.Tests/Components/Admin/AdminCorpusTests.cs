using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using MudBlazor.Services;
using NSubstitute;
using PinballWizard.Application.Ai.Retrieval;
using PinballWizard.Web.Components.Pages.Admin;
using Xunit;

namespace PinballWizard.Web.Tests.Components.Admin;

// bUnit tests for AdminCorpus.razor (/admin/corpus). Static SSR + [StreamRendering]:
// OnInitializedAsync calls IRagCorpusStatsReader once. The reader is mocked here (the
// real AI Search wire path is validated live, not stubbed — DL-0002/0003). Tests assert
// the honest states: populated (3 sections), empty (distinct empty state), unreachable
// (visible alert, NOT empty), and null-freshness (backfill-pending while totals render).
public sealed class AdminCorpusTests : AsyncBunitContext
{
    private readonly IRagCorpusStatsReader _reader = Substitute.For<IRagCorpusStatsReader>();

    public AdminCorpusTests()
    {
        Services.AddMudServices();
        JSInterop.Mode = JSRuntimeMode.Loose;
        this.AddAuthorization().SetAuthorized("test-admin@example.com");
        Services.AddSingleton(_reader);
        Services.AddSingleton<ILogger<AdminCorpus>>(NullLogger<AdminCorpus>.Instance);
    }

    private static readonly DateTimeOffset DefaultFresh =
        new(2026, 6, 21, 0, 0, 0, TimeSpan.Zero);

    // fresh: omit (uses DefaultFresh) | pass an explicit value | pass nullFresh: true to get null freshness
    private static RagCorpusStats Stats(
        long total = 12438,
        DateTimeOffset? fresh = default,
        IReadOnlyList<DocTypeChunkCount>? byType = null,
        bool nullFresh = false) => new(
        total,
        byType ?? new List<DocTypeChunkCount>
        {
            new("Manual", 9102),
            new("ServiceBulletin", 2331),
            new("MetadataCard", 1005),
        },
        nullFresh ? null : (fresh ?? DefaultFresh));

    private IRenderedComponent<AdminCorpus> RenderCorpus()
    {
        var fragment = Render(builder =>
        {
            builder.OpenComponent<MudBlazor.MudPopoverProvider>(0);
            builder.CloseComponent();
            builder.OpenComponent<AdminCorpus>(1);
            builder.CloseComponent();
        });
        return fragment.FindComponent<AdminCorpus>();
    }

    [Fact]
    public async Task Populated_RendersAllThreeSections()
    {
        _reader.GetCorpusStatsAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult(Stats()));

        var cut = RenderCorpus();
        await cut.InvokeAsync(() => Task.CompletedTask);

        var total = cut.Find("[data-testid='corpus-total']");
        Assert.Contains("12438", total.TextContent.Replace(",", ""), StringComparison.Ordinal);
        var byType = cut.Find("[data-testid='corpus-by-type']");
        Assert.Contains("Manual", byType.TextContent, StringComparison.Ordinal);
        Assert.Contains("9102", byType.TextContent.Replace(",", ""), StringComparison.Ordinal);
        cut.Find("[data-testid='corpus-freshness']");
        Assert.Empty(cut.FindAll("[data-testid='corpus-load-failed']"));
        Assert.Empty(cut.FindAll("[data-testid='corpus-empty']"));
    }

    [Fact]
    public async Task EmptyIndex_RendersDistinctEmptyState()
    {
        _reader.GetCorpusStatsAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new RagCorpusStats(0, new List<DocTypeChunkCount>(), null)));

        var cut = RenderCorpus();
        await cut.InvokeAsync(() => Task.CompletedTask);

        cut.Find("[data-testid='corpus-empty']");
        Assert.Empty(cut.FindAll("[data-testid='corpus-load-failed']"));
    }

    [Fact]
    public async Task Unreachable_RendersVisibleAlert_NotEmptyState()
    {
        _reader.GetCorpusStatsAsync(Arg.Any<CancellationToken>())
            .Returns<Task<RagCorpusStats>>(_ => throw new InvalidOperationException("unavailable"));

        var cut = RenderCorpus();
        await cut.InvokeAsync(() => Task.CompletedTask);

        cut.Find("[data-testid='corpus-load-failed']");
        // Failure must not masquerade as a benign empty corpus.
        Assert.Empty(cut.FindAll("[data-testid='corpus-empty']"));
        Assert.Empty(cut.FindAll("[data-testid='corpus-total']"));
    }

    [Fact]
    public async Task NullFreshness_NonEmpty_ShowsBackfillPending()
    {
        _reader.GetCorpusStatsAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Stats(nullFresh: true)));

        var cut = RenderCorpus();
        await cut.InvokeAsync(() => Task.CompletedTask);

        var freshness = cut.Find("[data-testid='corpus-freshness']");
        Assert.Contains("backfill pending", freshness.TextContent, StringComparison.OrdinalIgnoreCase);
        // Totals still render.
        cut.Find("[data-testid='corpus-total']");
    }
}
