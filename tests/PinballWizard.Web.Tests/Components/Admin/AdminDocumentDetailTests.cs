using Bunit;
using Bunit.TestDoubles;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using MudBlazor.Services;
using NSubstitute;
using PinballWizard.Application.Documents;
using PinballWizard.Application.Persistence;
using PinballWizard.Web.Components.Pages.Admin;
using Xunit;

namespace PinballWizard.Web.Tests.Components.Admin;

// bUnit tests for AdminDocumentDetail.razor (/admin/documents/{id}).
//
// AdminDocumentDetail is a thin page wrapper: it provides the AppPageShell
// container and delegates all content to the shared DocumentDetail component.
// Tests here assert that the DocumentId parameter is forwarded correctly and
// the document content renders after load — not that AppPageShell exists.
public sealed class AdminDocumentDetailTests : AsyncBunitContext
{
    private readonly IRawDocumentRepository _repo = Substitute.For<IRawDocumentRepository>();
    private const string FakeDocId = "doc_abc123";

    public AdminDocumentDetailTests()
    {
        Services.AddMudServices();
        JSInterop.Mode = JSRuntimeMode.Loose;
        this.AddAuthorization().SetAuthorized("test@example.com");
        Services.AddSingleton(_repo);
        Services.AddSingleton<ILogger<PinballWizard.Web.Components.Shared.DocumentDetail>>(
            NullLogger<PinballWizard.Web.Components.Shared.DocumentDetail>.Instance);
        _ = Services.GetRequiredService<BunitNavigationManager>();
    }

    private static DocumentDetailRecord MakeDetail() =>
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
            LinkStatus: "linked",
            LinkFailureReason: null,
            ResolutionStrategy: "title match",
            LinkedMachineIds: ["G4do5-MkPnV"])
        { ManufacturerKey = "stern" };

    // MudBlazor 9 + bUnit: MudPopoverProvider sibling required.
    private IRenderedComponent<AdminDocumentDetail> RenderPage(string documentId)
    {
        var fragment = Render(builder =>
        {
            builder.OpenComponent<MudBlazor.MudPopoverProvider>(0);
            builder.CloseComponent();
            builder.OpenComponent<AdminDocumentDetail>(1);
            builder.AddAttribute(2, nameof(AdminDocumentDetail.DocumentId), documentId);
            builder.CloseComponent();
        });
        return fragment.FindComponent<AdminDocumentDetail>();
    }

    // The DocumentId is forwarded to DocumentDetail; after load the document's
    // title is visible. This exercises the pass-through without the AppPageShell
    // wrapper swallowing the parameter.
    [Fact]
    public async Task DocumentDetail_ReceivesDocumentId_RendersDocTitle()
    {
        _repo.GetDocumentDetailAsync(FakeDocId, Arg.Any<bool>(), Arg.Any<CancellationToken>())
             .Returns(Task.FromResult<DocumentDetailRecord?>(MakeDetail()));

        var cut = RenderPage(FakeDocId);
        await cut.InvokeAsync(() => Task.CompletedTask);

        var title = cut.Find("[data-testid='doc-detail-title']");
        Assert.Contains("Godzilla Pro Manual", title.TextContent, StringComparison.Ordinal);
    }

    // When the repo returns null, DocumentDetail shows the not-found state. The
    // AppPageShell wrapper must not suppress this error path.
    [Fact]
    public async Task DocumentDetail_NotFound_RendersNotFoundState()
    {
        _repo.GetDocumentDetailAsync(FakeDocId, Arg.Any<bool>(), Arg.Any<CancellationToken>())
             .Returns(Task.FromResult<DocumentDetailRecord?>(null));

        var cut = RenderPage(FakeDocId);
        await cut.InvokeAsync(() => Task.CompletedTask);

        cut.Find("[data-testid='doc-detail-not-found']");
    }
}
