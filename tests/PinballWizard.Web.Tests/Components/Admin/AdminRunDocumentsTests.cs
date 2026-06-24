using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using MudBlazor.Services;
using NSubstitute;
using PinballWizard.Application.Persistence;
using PinballWizard.Core.Domain;
using PinballWizard.Core.Models;
using PinballWizard.Web.Components.Pages.Admin;
using Xunit;

namespace PinballWizard.Web.Tests.Components.Admin;

// bUnit tests for AdminRunDocuments.razor — non-routable child component embedded in
// AdminSourceDetail. Branches on SourceId: OPDB → IMachineRepository.StreamByRunIdAsync;
// any other source → IRawDocumentRepository.StreamByRunIdAsync.
// States: loading, loaded list, empty ("re-confirmed existing"), failure (section alert).
public sealed class AdminRunDocumentsTests : AsyncBunitContext
{
    public AdminRunDocumentsTests()
    {
        Services.AddMudServices();
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddSingleton<ILogger<AdminRunDocuments>>(NullLogger<AdminRunDocuments>.Instance);
    }

    // ── async-enumerable helpers ─────────────────────────────────────────────

    private static async IAsyncEnumerable<RawDocumentRecord> Docs(
        params RawDocumentRecord[] records)
    {
        await Task.CompletedTask;
        foreach (var r in records) yield return r;
    }

    private static async IAsyncEnumerable<Machine> Machines(params Machine[] machines)
    {
        await Task.CompletedTask;
        foreach (var m in machines) yield return m;
    }

    private static RawDocumentRecord DocRec(string title) => new()
    {
        DocumentId = "doc_" + title.ToLowerInvariant().Replace(" ", "_"),
        DocumentUrl = $"https://sternpinball.com/{title}.pdf",
        DocumentType = DocumentType.Manual,
        Source = new SourceInfo
        {
            DiscoveryUrl = "https://sternpinball.com/support/",
            DiscoveryContext = "Manuals Page",
            FileUrl = $"https://sternpinball.com/files/{title}.pdf",
            ScrapedAt = DateTime.UtcNow,
        },
        Timeline = new TimelineInfo
        {
            FirstDiscoveredAt = DateTime.UtcNow,
            LastCheckedAt = DateTime.UtcNow,
        },
    };

    private static Machine Mch(string title) => new()
    {
        Id = "opdb_" + title.ToLowerInvariant().Replace(" ", "_").Replace("'", ""),
        PartitionKey = "opdb",
        ManufacturerDisplayName = "Stern Pinball",
        Title = title,
        Year = 2021,
        Designers = [],
        Themes = [],
        Editions = [],
        ManufacturerSlugs = new Dictionary<string, string>(),
        FirstSeenAt = DateTimeOffset.UtcNow,
        LastSeenAt = DateTimeOffset.UtcNow,
    };

    // ── render helper ────────────────────────────────────────────────────────

    private IRenderedComponent<AdminRunDocuments> RenderRunDocs(string sourceId, string runId)
    {
        var fragment = Render(builder =>
        {
            builder.OpenComponent<MudBlazor.MudPopoverProvider>(0);
            builder.CloseComponent();
            builder.OpenComponent<AdminRunDocuments>(1);
            builder.AddAttribute(2, nameof(AdminRunDocuments.SourceId), sourceId);
            builder.AddAttribute(3, nameof(AdminRunDocuments.RunId), runId);
            builder.CloseComponent();
        });
        return fragment.FindComponent<AdminRunDocuments>();
    }

    // ── tests ────────────────────────────────────────────────────────────────

    [Fact]
    public void ManufacturerSource_ListsDocuments()
    {
        var raw = Substitute.For<IRawDocumentRepository>();
        raw.StreamByRunIdAsync("stern_x", Arg.Any<CancellationToken>())
            .Returns(Docs(DocRec("Jaws Manual")));
        Services.AddSingleton(raw);
        Services.AddSingleton(Substitute.For<IMachineRepository>());

        var cut = RenderRunDocs("stern", "stern_x");
        cut.WaitForAssertion(() => Assert.Contains("Jaws Manual", cut.Markup));
    }

    [Fact]
    public void OpdbSource_ListsMachines()
    {
        var machines = Substitute.For<IMachineRepository>();
        machines.StreamByRunIdAsync("opdb_x", Arg.Any<CancellationToken>())
            .Returns(Machines(Mch("Elvira's House of Horrors")));
        Services.AddSingleton(machines);
        Services.AddSingleton(Substitute.For<IRawDocumentRepository>());

        var cut = RenderRunDocs("opdb", "opdb_x");
        cut.WaitForAssertion(() => Assert.Contains("Elvira's House of Horrors", cut.Markup));
    }

    [Fact]
    public void EmptyRun_ShowsReconfirmedMessage()
    {
        var raw = Substitute.For<IRawDocumentRepository>();
        raw.StreamByRunIdAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Docs());
        Services.AddSingleton(raw);
        Services.AddSingleton(Substitute.For<IMachineRepository>());

        var cut = RenderRunDocs("stern", "stern_x");
        cut.WaitForAssertion(() => Assert.Contains("re-confirmed existing", cut.Markup));
    }

    [Fact]
    public void LoadFailure_ShowsSectionScopedAlert()
    {
        var raw = Substitute.For<IRawDocumentRepository>();
        raw.StreamByRunIdAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ThrowingDocs());
        Services.AddSingleton(raw);
        Services.AddSingleton(Substitute.For<IMachineRepository>());

        var cut = RenderRunDocs("stern", "stern_x");
        cut.WaitForAssertion(() => cut.Find("[data-testid='run-docs-failed']"));
    }

    private static async IAsyncEnumerable<RawDocumentRecord> ThrowingDocs()
    {
        await Task.CompletedTask;
        throw new InvalidOperationException("simulated Cosmos failure");
#pragma warning disable CS0162
        yield break;
#pragma warning restore CS0162
    }
}
