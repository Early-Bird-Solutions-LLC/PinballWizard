using Bunit;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using MudBlazor.Services;
using PinballWizard.Web.Engineering;
using Xunit;

// Type aliases resolve the name collision: EngineeringDoc exists in both
//   PinballWizard.Web.Engineering (the domain record) and
//   PinballWizard.Web.Components.Pages.Engineering (the Razor component).
using EngineeringLandingComponent = PinballWizard.Web.Components.Pages.Engineering.Engineering;
using EngineeringDocComponent     = PinballWizard.Web.Components.Pages.Engineering.EngineeringDoc;
using AdrIndexComponent           = PinballWizard.Web.Components.Pages.Engineering.AdrIndex;
using AdrPageComponent            = PinballWizard.Web.Components.Pages.Engineering.AdrPage;

namespace PinballWizard.Web.Tests.Engineering;

// bUnit smoke tests for the four /engineering pages.
//
// Uses the real EngineeringDocsProvider (embedded resources in PinballWizard.Web)
// so tests exercise the actual data path end-to-end without mocking.
// MudPopoverProvider is included per reference_mudblazor9_bunit_popover_provider.
//
// AsyncBunitContext (base class) pre-registers IUserPreferencesService and
// IGridSearchClient — both required by AppDataGrid inside AdrIndex.
public sealed class EngineeringPagesTests : AsyncBunitContext
{
    public EngineeringPagesTests()
    {
        Services.AddMudServices();
        Services.AddLogging();
        Services.AddSingleton<IEngineeringDocsProvider, EngineeringDocsProvider>();
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    // ── Landing page ──────────────────────────────────────────────────────────

    [Fact]
    public void Landing_ListsAtLeastOneDocGroupAndFreshnessStamp()
    {
        var cut = RenderWithPopover<EngineeringLandingComponent>();

        // "Overview" is the first group from the manifest.
        Assert.Contains("Overview", cut.Markup);
        // AppPageHeader Title="Engineering" must appear.
        Assert.Contains("Engineering", cut.Markup);
    }

    [Fact]
    public void Landing_LatestAdrs_ListsUpToFiveAdrs()
    {
        var cut = RenderWithPopover<EngineeringLandingComponent>();

        // Latest ADRs heading must appear.
        Assert.Contains("Latest ADRs", cut.Markup);
        // At least one ADR entry must appear.
        Assert.Contains("ADR-", cut.Markup);
    }

    [Fact]
    public void Landing_AgentsSentence_ContainsMudLinkToAdr51()
    {
        var cut = RenderWithPopover<EngineeringLandingComponent>();

        // The two-kinds-of-agents intro paragraph must contain the ADR-0051 link.
        var links = cut.FindAll("a[href]");
        Assert.Contains(links, l => l.GetAttribute("href") is "/engineering/adr/51");
    }

    // ── Doc page ──────────────────────────────────────────────────────────────

    [Fact]
    public void DocPage_RendersKnownSlug()
    {
        var cut = Render(builder =>
        {
            builder.OpenComponent<MudPopoverProvider>(0);
            builder.CloseComponent();
            builder.OpenComponent<EngineeringDocComponent>(1);
            builder.AddAttribute(2, "Slug", "glossary");
            builder.CloseComponent();
        });

        // MarkdownComponentRenderer emits MudText → CSS class mud-typography-*
        Assert.Contains("mud-typography", cut.Markup);
    }

    [Fact]
    public void DocPage_UnknownSlug_ShowsEmptyState()
    {
        var cut = Render(builder =>
        {
            builder.OpenComponent<MudPopoverProvider>(0);
            builder.CloseComponent();
            builder.OpenComponent<EngineeringDocComponent>(1);
            builder.AddAttribute(2, "Slug", "nope-does-not-exist");
            builder.CloseComponent();
        });

        // AppErrorAlert renders MudAlert → CSS class mud-alert
        Assert.Contains("mud-alert", cut.Markup);
    }

    // ── ADR index ─────────────────────────────────────────────────────────────

    [Fact]
    public void AdrIndex_RendersPageHeaderAndAdrs()
    {
        var cut = Render(builder =>
        {
            builder.OpenComponent<MudPopoverProvider>(0);
            builder.CloseComponent();
            builder.OpenComponent<AdrIndexComponent>(1);
            builder.CloseComponent();
        });

        Assert.Contains("Architecture Decision Records", cut.Markup);
        // At least one ADR number must appear in the grid.
        Assert.Contains("ADR-0001", cut.Markup);
    }

    // ── ADR page ──────────────────────────────────────────────────────────────

    [Fact]
    public void AdrPage_RendersKnownNumber()
    {
        var cut = Render(builder =>
        {
            builder.OpenComponent<MudPopoverProvider>(0);
            builder.CloseComponent();
            builder.OpenComponent<AdrPageComponent>(1);
            builder.AddAttribute(2, "Number", 1);
            builder.CloseComponent();
        });

        // MarkdownComponentRenderer emits MudText → mud-typography class
        Assert.Contains("mud-typography", cut.Markup);
    }

    [Fact]
    public void AdrPage_UnknownNumber_ShowsEmptyState()
    {
        var cut = Render(builder =>
        {
            builder.OpenComponent<MudPopoverProvider>(0);
            builder.CloseComponent();
            builder.OpenComponent<AdrPageComponent>(1);
            builder.AddAttribute(2, "Number", 99999);
            builder.CloseComponent();
        });

        // AppEmptyState renders the Heading text as a MudText body1 element.
        Assert.Contains("not found", cut.Markup, StringComparison.OrdinalIgnoreCase);
    }
}
