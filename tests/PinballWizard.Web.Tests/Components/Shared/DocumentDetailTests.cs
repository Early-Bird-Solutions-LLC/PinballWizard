using Bunit;
using Bunit.TestDoubles;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using MudBlazor.Services;
using NSubstitute;
using PinballWizard.Application.Documents;
using PinballWizard.Application.Persistence;
using Xunit;

namespace PinballWizard.Web.Tests.Components.SharedComponents;

// bUnit tests for DocumentDetail.razor (shared component) — the provenance
// card, open-document button, not-found state, admin panel visibility, and
// error handling.
//
// DocumentDetail loads via OnParametersSetAsync → data is present after a
// single InvokeAsync flush.
//
// The component is rendered directly (not through its page wrapper) so that
// IsAdmin can be set independently of the page route.
public sealed class DocumentDetailTests : AsyncBunitContext
{
    private readonly IRawDocumentRepository _repo = Substitute.For<IRawDocumentRepository>();
    private const string FakeDocId = "doc_abc123";

    public DocumentDetailTests()
    {
        Services.AddMudServices();
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddSingleton(_repo);
        Services.AddSingleton<ILogger<PinballWizard.Web.Components.Shared.DocumentDetail>>(
            NullLogger<PinballWizard.Web.Components.Shared.DocumentDetail>.Instance);
        this.AddAuthorization().SetAuthorized("test@example.com");
        _ = Services.GetRequiredService<BunitNavigationManager>();
    }

    private static DocumentDetailRecord MakeDetail(string? linkStatus = null, string? mfrKey = "stern") =>
        new(FakeDocId, "Godzilla Pro Manual", "Manual", "pdf",
            PageCount: 150, SizeBytes: 5_200_000,
            FileUrl: "https://sternpinball.com/docs/godzilla-pro-manual.pdf",
            DiscoveryUrl: "https://sternpinball.com/game/godzilla/",
            DiscoveryContext: "Game Page → Specs & Manual tab",
            SourceTab: "Specs & Manual",
            SourceType: "GamePage",
            GameTitle: "Godzilla",
            GameSlug: "godzilla",
            Edition: "Pro",
            EditionScope: "single-edition",
            Manufacturer: "Stern",
            FirstDiscoveredAt: DateTimeOffset.UtcNow,
            LastDownloadedAt: DateTimeOffset.UtcNow,
            LinkStatus: linkStatus,
            LinkFailureReason: linkStatus is "failed" ? "No match found" : null,
            ResolutionStrategy: linkStatus is "linked" ? "title match" : null,
            LinkedMachineIds: linkStatus is "linked" ? ["G4do5-MkPnV"] : null)
        { ManufacturerKey = mfrKey };

    private IRenderedComponent<PinballWizard.Web.Components.Shared.DocumentDetail> RenderDetail(
        string documentId, bool isAdmin = false)
    {
        var fragment = Render(builder =>
        {
            builder.OpenComponent<MudBlazor.MudPopoverProvider>(0);
            builder.CloseComponent();
            builder.OpenComponent<PinballWizard.Web.Components.Shared.DocumentDetail>(1);
            builder.AddAttribute(2, nameof(PinballWizard.Web.Components.Shared.DocumentDetail.DocumentId), documentId);
            builder.AddAttribute(3, nameof(PinballWizard.Web.Components.Shared.DocumentDetail.IsAdmin), isAdmin);
            builder.CloseComponent();
        });
        return fragment.FindComponent<PinballWizard.Web.Components.Shared.DocumentDetail>();
    }

    [Fact]
    public async Task RendersProvenanceCard()
    {
        _repo.GetDocumentDetailAsync(FakeDocId, false, Arg.Any<CancellationToken>())
             .Returns(MakeDetail());

        var cut = RenderDetail(FakeDocId);
        await cut.InvokeAsync(() => Task.CompletedTask);

        Assert.Contains("Godzilla Pro Manual", cut.Markup);
        Assert.Contains("Game Page → Specs &amp; Manual tab", cut.Markup);
        Assert.Contains("Stern", cut.Markup);
    }

    [Fact]
    public async Task Manufacturer_LinksToManufacturerDetailPage()
    {
        _repo.GetDocumentDetailAsync(FakeDocId, false, Arg.Any<CancellationToken>())
             .Returns(MakeDetail());

        var cut = RenderDetail(FakeDocId);
        await cut.InvokeAsync(() => Task.CompletedTask);

        var link = cut.Find("[data-testid='doc-detail-manufacturer'] a[href='/manufacturers/stern']");
        Assert.Contains("Stern", link.TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Manufacturer_NullKey_DegradesToTextWithNoLink()
    {
        _repo.GetDocumentDetailAsync(FakeDocId, false, Arg.Any<CancellationToken>())
             .Returns(MakeDetail(mfrKey: null));

        var cut = RenderDetail(FakeDocId);
        await cut.InvokeAsync(() => Task.CompletedTask);

        var mfr = cut.Find("[data-testid='doc-detail-manufacturer']");
        Assert.Empty(mfr.QuerySelectorAll("a[href='/manufacturers/stern']"));
        Assert.Contains("Stern", mfr.TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OpenDocumentButton_HasCorrectHref()
    {
        _repo.GetDocumentDetailAsync(FakeDocId, false, Arg.Any<CancellationToken>())
             .Returns(MakeDetail());

        var cut = RenderDetail(FakeDocId);
        await cut.InvokeAsync(() => Task.CompletedTask);

        var btn = cut.Find("[data-testid='doc-detail-open-btn']");
        Assert.Equal("https://sternpinball.com/docs/godzilla-pro-manual.pdf",
            btn.GetAttribute("href"));
    }

    [Fact]
    public async Task NotFound_ShowsErrorAndBackLink()
    {
        _repo.GetDocumentDetailAsync(FakeDocId, false, Arg.Any<CancellationToken>())
             .Returns((DocumentDetailRecord?)null);

        var cut = RenderDetail(FakeDocId);
        await cut.InvokeAsync(() => Task.CompletedTask);

        cut.Find("[data-testid='doc-detail-not-found']");
        cut.Find("[data-testid='doc-detail-back-link']");
    }

    [Fact]
    public async Task AdminPanel_HiddenOnPublicComponent()
    {
        _repo.GetDocumentDetailAsync(FakeDocId, false, Arg.Any<CancellationToken>())
             .Returns(MakeDetail("linked"));

        var cut = RenderDetail(FakeDocId, isAdmin: false);
        await cut.InvokeAsync(() => Task.CompletedTask);

        Assert.DoesNotContain("doc-detail-admin-panel", cut.Markup);
        Assert.DoesNotContain("doc-detail-doc-id", cut.Markup);
    }

    [Fact]
    public async Task AdminPanel_VisibleWhenIsAdminTrue()
    {
        _repo.GetDocumentDetailAsync(FakeDocId, true, Arg.Any<CancellationToken>())
             .Returns(MakeDetail("linked"));

        var cut = RenderDetail(FakeDocId, isAdmin: true);
        await cut.InvokeAsync(() => Task.CompletedTask);

        cut.Find("[data-testid='doc-detail-admin-panel']");
        cut.Find("[data-testid='doc-detail-doc-id']");
    }

    [Fact]
    public async Task RepositoryError_ShowsErrorAlert()
    {
        _repo.GetDocumentDetailAsync(FakeDocId, false, Arg.Any<CancellationToken>())
             .Returns<Task<DocumentDetailRecord?>>(_ => throw new InvalidOperationException("Cosmos down"));

        var cut = RenderDetail(FakeDocId);
        await cut.InvokeAsync(() => Task.CompletedTask);

        cut.Find("[data-testid='doc-detail-load-error']");
    }
}
