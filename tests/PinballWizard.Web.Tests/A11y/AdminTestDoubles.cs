using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NSubstitute;
using PinballWizard.Application.Ai;
using PinballWizard.Application.Catalog;
using PinballWizard.Application.Linking;
using PinballWizard.Application.Persistence;
using PinballWizard.Core.Configuration;
using PinballWizard.Core.Domain;
using PinballWizard.Core.Models;
using PinballWizard.Web.Security;

namespace PinballWizard.Web.Tests.A11y;

// Shared in-memory test doubles for the /admin/* pages, used by both the SSR
// axe host (Half A) and the real-circuit host (Half B). Synthetic seed data —
// a single manufacturer ("stern") with a two-edition Godzilla family where the
// LE has zero docs, so health chips, the edition-gap callout, triage rows,
// an override, and a settings row all render. NSubstitute matches the existing
// repo-stub pattern (AdminMachinesTests). No real PII, no Cosmos, no Foundry.
internal static class AdminTestDoubles
{
    public const string Manufacturer = "stern";
    public const string ProId = "mch_godzilla_pro";
    public const string LeId = "mch_godzilla_le";
    public const string GroupId = "godzilla";

    private static readonly DateTimeOffset AsOf =
        new(2026, 6, 1, 12, 0, 0, TimeSpan.Zero);

    public static IServiceCollection AddAdminTestDoubles(this IServiceCollection services)
    {
        services.AddSingleton(CatalogStats());
        services.AddSingleton(Machines());
        services.AddSingleton(MachineDocs());
        services.AddSingleton(RawDocs());
        services.AddSingleton(Linker());
        services.AddSingleton(Overrides());
        services.AddSingleton(IngestionSources());
        services.AddSingleton(Settings());
        services.AddSingleton(Prompts());
        services.AddSingleton(CorpusStats());

        // Concrete singleton — parameterless, loads embedded prompt .md resources.
        services.AddSingleton<EmbeddedResourceAgentPromptProvider>();

        // AdminSettings injects IOptions<AiFoundryOptions>; defaults are usable.
        services.AddSingleton<IOptions<AiFoundryOptions>>(Options.Create(new AiFoundryOptions()));

        // AdminDocumentTriage injects AdminActionGuard (Task 3: public-read with gated actions).
        services.AddScoped<AdminActionGuard>();

        return services;
    }

    // ── ICatalogStatsReadRepository ──────────────────────────────────────────
    private static readonly MachineDocStats ProStat = new(
        MachineId: ProId, Title: "Godzilla", EditionLabel: "Pro", GroupId: GroupId,
        Year: 2021, IsOpdbOnly: false, DocCount: 2,
        DocTypeCounts: new Dictionary<string, int> { ["Manual"] = 1 }, HasManual: true);

    private static readonly MachineDocStats LeStat = new(
        MachineId: LeId, Title: "Godzilla", EditionLabel: "LE", GroupId: GroupId,
        Year: 2021, IsOpdbOnly: false, DocCount: 0,
        DocTypeCounts: new Dictionary<string, int>(), HasManual: false);

    private static readonly ManufacturerCatalogStats SternStats =
        new(Manufacturer, AsOf, [ProStat, LeStat]);

    private static ICatalogStatsReadRepository CatalogStats()
    {
        var repo = Substitute.For<ICatalogStatsReadRepository>();
        repo.StreamAllManufacturersAsync(Arg.Any<CancellationToken>())
            .Returns(_ => Stream(SternStats));
        repo.GetByManufacturerAsync(Manufacturer, Arg.Any<CancellationToken>())
            .Returns(SternStats);
        return repo;
    }

    // ── IMachineRepository ───────────────────────────────────────────────────
    private static Machine MachineRecord(string id, string edition) => new()
    {
        Id = id,
        PartitionKey = Manufacturer,
        ManufacturerDisplayName = "Stern Pinball",
        Title = "Godzilla",
        GroupId = GroupId,
        Year = 2021,
        EditionLabel = edition,
        EditionTokens = [edition],
        Designers = [],
        Themes = [],
        Editions = [],
        ManufacturerSlugs = new Dictionary<string, string>(),
        OpdbSourceUrl = "https://opdb.org/machines/" + id,
        FirstSeenAt = AsOf,
        LastSeenAt = AsOf,
    };

    private static IMachineRepository Machines()
    {
        var repo = Substitute.For<IMachineRepository>();
        var pro = MachineRecord(ProId, "Pro");
        var le = MachineRecord(LeId, "LE");
        repo.GetByOpdbIdAsync(ProId, Manufacturer, Arg.Any<CancellationToken>()).Returns(pro);
        repo.GetByOpdbIdAsync(LeId, Manufacturer, Arg.Any<CancellationToken>()).Returns(le);
        repo.GetSiblingsByGroupIdAsync(GroupId, Arg.Any<CancellationToken>())
            .Returns(_ => Stream(pro, le));
        return repo;
    }

    // ── IMachineDocumentReadRepository ───────────────────────────────────────
    private static IMachineDocumentReadRepository MachineDocs()
    {
        var repo = Substitute.For<IMachineDocumentReadRepository>();
        var links = new[]
        {
            new MachineDocumentLink(
                DocumentId: "doc_man_1", DocumentType: "Manual",
                DocumentUrl: "https://sternpinball.com/godzilla-manual.pdf",
                LinkText: "Godzilla Manual", Edition: "Pro", EditionScope: "SingleEdition",
                LinkStatus: "Linked", ResolutionStrategy: "title_match",
                LastDownloadedUtc: AsOf, SizeBytes: 2_400_000, PageCount: 48),
            new MachineDocumentLink(
                DocumentId: "doc_rules_1", DocumentType: "Other",
                DocumentUrl: "https://sternpinball.com/godzilla-rules.pdf",
                LinkText: "Rules", Edition: null, EditionScope: "FranchiseWide",
                LinkStatus: "Linked", ResolutionStrategy: "title_match",
                LastDownloadedUtc: AsOf, SizeBytes: 800_000, PageCount: 12),
        };
        repo.StreamByMachineIdAsync(ProId, Arg.Any<CancellationToken>())
            .Returns(_ => Stream(links));
        repo.StreamByMachineIdAsync(LeId, Arg.Any<CancellationToken>())
            .Returns(_ => Stream<MachineDocumentLink>());
        return repo;
    }

    // ── IRawDocumentRepository ───────────────────────────────────────────────
    private static RawDocumentRecord TriageDoc() => new()
    {
        DocumentId = "doc_triage_1",
        DocumentUrl = "https://sternpinball.com/unknown.pdf",
        DocumentType = DocumentType.Manual,
        Source = new SourceInfo
        {
            DiscoveryUrl = "https://sternpinball.com/support/",
            DiscoveryContext = "Support page",
            FileUrl = "https://sternpinball.com/unknown.pdf",
            LinkText = "Mystery doc",
            ScrapedAt = AsOf.UtcDateTime,
        },
        Timeline = new TimelineInfo
        {
            FirstDiscoveredAt = AsOf.UtcDateTime,
            LastCheckedAt = AsOf.UtcDateTime,
        },
        LinkStatus = LinkStatus.Failed,
        LinkFailureReason = "No matching machine",
    };

    private static IRawDocumentRepository RawDocs()
    {
        var repo = Substitute.For<IRawDocumentRepository>();
        repo.StreamByStatusAsync(Arg.Any<IReadOnlyCollection<LinkStatus>>(), Arg.Any<CancellationToken>())
            .Returns(_ => Stream(TriageDoc()));
        repo.GetAsync("doc_triage_1", Arg.Any<CancellationToken>()).Returns(TriageDoc());
        repo.UpdateLinkStatusAsync(
            Arg.Any<string>(), Arg.Any<LinkStatus>(), Arg.Any<string?>(),
            Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        return repo;
    }

    // ── IDocumentLinker (Relink returns Linked so the row resolves) ──────────
    private static IDocumentLinker Linker()
    {
        var linker = Substitute.For<IDocumentLinker>();
        linker.InitializeAsync(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        linker.LinkAsync(Arg.Any<RawDocumentRecord>(), Arg.Any<CancellationToken>())
            .Returns(new LinkingResult(
                DocumentId: "doc_triage_1",
                FinalStatus: LinkStatus.Linked,
                ResolutionStrategy: "admin_relink",
                LinkedMachineIds: [ProId]));
        return linker;
    }

    // ── ILinkOverrideRepository ──────────────────────────────────────────────
    private static ILinkOverrideRepository Overrides()
    {
        var repo = Substitute.For<ILinkOverrideRepository>();
        var seed = new LinkOverrideRecord
        {
            SourcePattern = "https://sternpinball.com/x|Manual",
            MachineIds = [ProId],
            CreatedBy = "admin (local-dev)",
            CreatedAt = AsOf,
            Notes = "seed override",
        };
        repo.LoadAllAsync(Arg.Any<CancellationToken>())
            .Returns(new Dictionary<string, LinkOverrideRecord> { [seed.SourcePattern] = seed });
        repo.UpsertAsync(Arg.Any<LinkOverrideRecord>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        repo.DeleteAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        return repo;
    }

    // ── IIngestionSourceRepository ───────────────────────────────────────────
    private static IIngestionSourceRepository IngestionSources()
    {
        var repo = Substitute.For<IIngestionSourceRepository>();
        var stern = new IngestionSource
        {
            Id = "stern",
            DisplayName = "Stern Pinball",
            ScraperImplKey = "stern",
            BaseUrl = "https://sternpinball.com",
            Enabled = true,
            Cadence = "weekly",
            LastRunAt = AsOf,
            LastSuccessAt = AsOf,
            TotalDocumentsDiscovered = 12,
            TotalRunFailures = 0,
        };
        var opdb = new IngestionSource
        {
            Id = "opdb",
            DisplayName = "OPDB",
            ScraperImplKey = "opdb",
            BaseUrl = "https://opdb.org",
            Enabled = false,
            Cadence = "manual",
            TotalDocumentsDiscovered = 0,
            TotalRunFailures = 0,
        };
        repo.StreamAllAsync(Arg.Any<CancellationToken>())
            .Returns(_ => Stream(stern, opdb));
        repo.StreamEnabledAsync(Arg.Any<CancellationToken>())
            .Returns(_ => Stream(stern));
        repo.GetByIdAsync("stern", "config", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IngestionSource?>(stern));
        repo.GetByIdAsync("opdb", "config", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IngestionSource?>(opdb));
        repo.SetEnabledAsync(Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(true));
        return repo;
    }

    // ── IAdminSettingsRepository ─────────────────────────────────────────────
    private static IAdminSettingsRepository Settings()
    {
        var repo = Substitute.For<IAdminSettingsRepository>();
        var rows = new List<AdminSettingRecord>
        {
            new("ai.confidence_threshold", "0.70", AsOf, "admin (local-dev)"),
        };
        repo.GetAllAsync(Arg.Any<CancellationToken>())
            .Returns((IReadOnlyList<AdminSettingRecord>)rows);
        repo.SetAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        repo.DeleteAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        return repo;
    }

    // ── IAgentPromptOverrideRepository (no overrides → embedded default) ─────
    private static IAgentPromptOverrideRepository Prompts()
    {
        var repo = Substitute.For<IAgentPromptOverrideRepository>();
        repo.GetActiveAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((AgentPromptOverride?)null);
        repo.GetVersionsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((IReadOnlyList<AgentPromptOverride>)[]);
        return repo;
    }

    // ── IRagCorpusStatsReader ────────────────────────────────────────────────
    private static PinballWizard.Application.Ai.Retrieval.IRagCorpusStatsReader CorpusStats()
    {
        var reader = Substitute.For<PinballWizard.Application.Ai.Retrieval.IRagCorpusStatsReader>();
        reader.GetCorpusStatsAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new PinballWizard.Application.Ai.Retrieval.RagCorpusStats(
                1234,
                new List<PinballWizard.Application.Ai.Retrieval.DocTypeChunkCount>
                {
                    new("Manual", 900),
                    new("ServiceBulletin", 334),
                },
                AsOf)));
        return reader;
    }

    // ── async-enumerable helper ──────────────────────────────────────────────
    private static async IAsyncEnumerable<T> Stream<T>(params T[] items)
    {
        await Task.CompletedTask;
        foreach (var i in items) yield return i;
    }
}
