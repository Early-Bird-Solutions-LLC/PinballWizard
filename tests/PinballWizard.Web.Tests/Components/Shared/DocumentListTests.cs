using System.Runtime.CompilerServices;
using Bunit;
using Bunit.TestDoubles;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using MudBlazor;
using MudBlazor.Services;
using NSubstitute;
using PinballWizard.Application.Documents;
using PinballWizard.Application.Persistence;
using PinballWizard.Web.Components.Pages;
using Xunit;

namespace PinballWizard.Web.Tests.Components.SharedComponents;

// bUnit tests for DocumentList.razor (shared component) exercised via the
// public Documents page and the AdminDocuments page.
//
// DocumentList loads via OnParametersSetAsync → all data is present after a
// single InvokeAsync flush. Tests use RenderWithPopover so the MudPopoverProvider
// sibling is present for MudDataGrid.
//
// ILogger<DocumentList> is registered as NullLogger — the component logs errors
// but the logger is not part of the behavioral assertions.
public sealed class DocumentListTests : AsyncBunitContext
{
    private readonly IRawDocumentRepository _repo = Substitute.For<IRawDocumentRepository>();

    public DocumentListTests()
    {
        Services.AddMudServices();
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddSingleton(_repo);
        Services.AddSingleton<ILogger<PinballWizard.Web.Components.Shared.DocumentList>>(
            NullLogger<PinballWizard.Web.Components.Shared.DocumentList>.Instance);
        this.AddAuthorization().SetAuthorized("test@example.com");
        _ = Services.GetRequiredService<BunitNavigationManager>();
    }

    private static async IAsyncEnumerable<DocumentListItem> FakeStream(
        IEnumerable<DocumentListItem> items,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        foreach (var item in items)
            yield return item;
    }

    private static DocumentListItem MakeItem(string id = "doc_abc", string game = "Godzilla",
        string mfr = "Stern", string? mfrKey = "stern") =>
        new(id, $"{game} Manual", "Manual", game, "Pro", mfr,
            "pdf", 150, 5_200_000, DateTimeOffset.UtcNow,
            null, null, null)
        { ManufacturerKey = mfrKey };

    [Fact]
    public async Task ShowsDocumentsFromRepository()
    {
        var item = MakeItem();
        _repo.StreamDocumentsAsync(null, null, null, false, Arg.Any<CancellationToken>())
             .Returns(_ => FakeStream([item]));

        var nav = Services.GetRequiredService<BunitNavigationManager>();
        nav.NavigateTo("/documents");

        var cut = RenderWithPopover<Documents>();
        await cut.InvokeAsync(() => Task.CompletedTask);

        Assert.Contains("Godzilla Manual", cut.Markup);
    }

    [Fact]
    public async Task ManufacturerCell_LinksToManufacturerDetailPage()
    {
        _repo.StreamDocumentsAsync(null, null, null, false, Arg.Any<CancellationToken>())
             .Returns(_ => FakeStream([MakeItem()]));

        var nav = Services.GetRequiredService<BunitNavigationManager>();
        nav.NavigateTo("/documents");

        var cut = RenderWithPopover<Documents>();
        await cut.InvokeAsync(() => Task.CompletedTask);

        var link = cut.Find("a[href='/manufacturers/stern']");
        Assert.Contains("Stern", link.TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ManufacturerCell_NullKey_DegradesToTextWithNoLink()
    {
        _repo.StreamDocumentsAsync(null, null, null, false, Arg.Any<CancellationToken>())
             .Returns(_ => FakeStream([MakeItem(mfrKey: null)]));

        var nav = Services.GetRequiredService<BunitNavigationManager>();
        nav.NavigateTo("/documents");

        var cut = RenderWithPopover<Documents>();
        await cut.InvokeAsync(() => Task.CompletedTask);

        Assert.Empty(cut.FindAll("a[href='/manufacturers/stern']"));
        Assert.Contains("Stern", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EmptyCorpus_ShowsEmptyState()
    {
        _repo.StreamDocumentsAsync(null, null, null, false, Arg.Any<CancellationToken>())
             .Returns(_ => FakeStream([]));

        var cut = RenderWithPopover<Documents>();
        await cut.InvokeAsync(() => Task.CompletedTask);

        cut.Find("[data-testid='doc-list-empty-corpus']");
    }

    [Fact]
    public async Task WithFilters_NoResults_ShowsFilteredEmptyState()
    {
        _repo.StreamDocumentsAsync(Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(), false, Arg.Any<CancellationToken>())
             .Returns(_ => FakeStream([]));

        var nav = Services.GetRequiredService<BunitNavigationManager>();
        nav.NavigateTo("/documents?game=Godzilla");

        var cut = RenderWithPopover<Documents>();
        await cut.InvokeAsync(() => Task.CompletedTask);

        cut.Find("[data-testid='doc-list-empty-filtered']");
    }

    [Fact]
    public async Task GameQueryParam_InitializesGameFilter()
    {
        _repo.StreamDocumentsAsync(Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(), false, Arg.Any<CancellationToken>())
             .Returns(_ => FakeStream([]));

        // Render DocumentList directly with Game param — tests the behavioral
        // contract that the filter control is populated from the Game parameter.
        var fragment = Render(builder =>
        {
            builder.OpenComponent<MudBlazor.MudPopoverProvider>(0);
            builder.CloseComponent();
            builder.OpenComponent<PinballWizard.Web.Components.Shared.DocumentList>(1);
            builder.AddAttribute(2, nameof(PinballWizard.Web.Components.Shared.DocumentList.Game), "Godzilla");
            builder.AddAttribute(3, nameof(PinballWizard.Web.Components.Shared.DocumentList.IsAdmin), false);
            builder.CloseComponent();
        });
        var cut = fragment.FindComponent<PinballWizard.Web.Components.Shared.DocumentList>();
        await cut.InvokeAsync(() => Task.CompletedTask);

        // Verify the repository was called with the game filter forwarded — not just displayed.
        _repo.Received(1).StreamDocumentsAsync("Godzilla", Arg.Any<string?>(), Arg.Any<string?>(), false, Arg.Any<CancellationToken>());

        // MudTextField splats UserAttributes to the inner <input> element,
        // so data-testid lands on the input directly — not on a wrapper div.
        var input = cut.Find("input[data-testid='doc-list-game-filter']");
        Assert.Equal("Godzilla", input.GetAttribute("value"));
    }

    [Fact]
    public async Task AdminColumns_HiddenOnPublicPage()
    {
        _repo.StreamDocumentsAsync(null, null, null, false, Arg.Any<CancellationToken>())
             .Returns(_ => FakeStream([MakeItem()]));

        var cut = RenderWithPopover<Documents>();
        await cut.InvokeAsync(() => Task.CompletedTask);

        Assert.DoesNotContain("Link Status", cut.Markup);
        Assert.DoesNotContain("Failure Reason", cut.Markup);
    }

    [Fact]
    public async Task AdminPage_ShowsAdminColumns()
    {
        var item = MakeItem() with { LinkStatus = "linked" };
        _repo.StreamDocumentsAsync(null, null, null, true, Arg.Any<CancellationToken>())
             .Returns(_ => FakeStream([item]));

        var cut = RenderWithPopover<PinballWizard.Web.Components.Pages.Admin.AdminDocuments>();
        await cut.InvokeAsync(() => Task.CompletedTask);

        Assert.Contains("Link Status", cut.Markup);
    }

    [Fact]
    public async Task RepositoryError_ShowsErrorAlert()
    {
        _repo.StreamDocumentsAsync(null, null, null, false, Arg.Any<CancellationToken>())
             .Returns<IAsyncEnumerable<DocumentListItem>>(_ =>
                 throw new InvalidOperationException("Cosmos down"));

        var cut = RenderWithPopover<Documents>();
        await cut.InvokeAsync(() => Task.CompletedTask);

        cut.Find("[data-testid='doc-list-load-error']");
    }

    [Fact]
    public async Task TypeQueryParam_PassesTypeFilterToRepository()
    {
        // Arrange: two items — one Manual, one ServiceBulletin.
        // With ?type=Manual the repository is called with type="Manual".
        var manualItem = MakeItem("doc_man", "Godzilla", "Stern");
        _repo.StreamDocumentsAsync(Arg.Any<string?>(), Arg.Any<string?>(), "Manual", false, Arg.Any<CancellationToken>())
             .Returns(_ => FakeStream([manualItem]));

        var nav = Services.GetRequiredService<BunitNavigationManager>();
        nav.NavigateTo("/documents?type=Manual");

        var cut = RenderWithPopover<Documents>();
        await cut.InvokeAsync(() => Task.CompletedTask);

        // Repository received the type arg forwarded from the query param.
        _repo.Received(1).StreamDocumentsAsync(Arg.Any<string?>(), Arg.Any<string?>(), "Manual", false, Arg.Any<CancellationToken>());

        // The matching document is rendered.
        Assert.Contains("Godzilla Manual", cut.Markup);
    }

    [Fact]
    public async Task ManufacturerQueryParam_PassesManufacturerFilterToRepository()
    {
        // Regression coverage for the manufacturer filter (the control that surfaced the
        // null-manufacturer live-data bug). The ?manufacturer=… query param must forward
        // to the repository unchanged so the exact-match Cosmos filter can run.
        var apItem = MakeItem("doc_ap", "Legends of Valhalla", "American Pinball");
        _repo.StreamDocumentsAsync(Arg.Any<string?>(), "American Pinball", Arg.Any<string?>(), false, Arg.Any<CancellationToken>())
             .Returns(_ => FakeStream([apItem]));

        var nav = Services.GetRequiredService<BunitNavigationManager>();
        nav.NavigateTo("/documents?manufacturer=American Pinball");

        var cut = RenderWithPopover<Documents>();
        await cut.InvokeAsync(() => Task.CompletedTask);

        // Repository received the manufacturer arg forwarded from the query param.
        _repo.Received(1).StreamDocumentsAsync(Arg.Any<string?>(), "American Pinball", Arg.Any<string?>(), false, Arg.Any<CancellationToken>());

        // The matching document is rendered.
        Assert.Contains("Legends of Valhalla Manual", cut.Markup);
    }

    [Fact]
    public async Task TypeFilterChipStrip_RendersUserFacingDocumentTypes()
    {
        _repo.StreamDocumentsAsync(null, null, null, false, Arg.Any<CancellationToken>())
             .Returns(_ => FakeStream([]));

        var cut = RenderWithPopover<Documents>();
        await cut.InvokeAsync(() => Task.CompletedTask);

        // Strip renders user-facing types and excludes internal artefacts.
        var chipSetMarkup = cut.Find("[data-testid='doc-list-type-filter']").InnerHtml;
        Assert.Contains("Manual", chipSetMarkup);
        Assert.Contains("Rulesheet", chipSetMarkup);
        Assert.DoesNotContain("MetadataCard", chipSetMarkup);
    }

    [Fact]
    public async Task TypeFilter_WithNoResults_ShowsFilteredEmptyState()
    {
        _repo.StreamDocumentsAsync(Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(), false, Arg.Any<CancellationToken>())
             .Returns(_ => FakeStream([]));

        var nav = Services.GetRequiredService<BunitNavigationManager>();
        nav.NavigateTo("/documents?type=Schematic");

        var cut = RenderWithPopover<Documents>();
        await cut.InvokeAsync(() => Task.CompletedTask);

        cut.Find("[data-testid='doc-list-empty-filtered']");
    }
}
