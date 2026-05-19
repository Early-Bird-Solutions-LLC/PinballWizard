using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using PinballWizard.Application.Persistence;
using PinballWizard.Application.Sync;
using PinballWizard.Core.Domain;
using PinballWizard.Core.Models;
using Xunit;

namespace PinballWizard.Scraper.Tests.Application.Sync;

// Exercises ScrapedDocumentSeeder in isolation using temp-file fixtures.
// The four behaviors under test:
//   1. Documents classified as Manual or ServiceBulletin with a matching
//      machine are upserted and counted in Upserted.
//   2. Documents with no game title are skipped (counted in Skipped).
//   3. Documents whose game title has no match in the machines container
//      are skipped (counted in Skipped).
//   4. Documents with a document type other than Manual / ServiceBulletin
//      are skipped entirely (not counted in Skipped — just not processed).
public sealed class ScrapedDocumentSeederTests : IDisposable
{
    private readonly List<string> _tempFiles = [];

    public void Dispose()
    {
        foreach (var path in _tempFiles)
        {
            try { File.Delete(path); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task SeedAsync_ManualWithMatchingMachine_UpsertsAndCountsUpserted()
    {
        var machine = BuildMachine("GweeP-MW95j", "Godzilla", "Stern Pinball");
        var machineRepo = BuildMachineRepo(machine);
        var docRepo = Substitute.For<IScrapedDocumentRepository>();

        var catalogPath = WriteCatalog(
            Doc("doc_abc123", "Manual", "Godzilla", "https://example.com/godzilla-manual.pdf"));

        var seeder = new ScrapedDocumentSeeder(docRepo, machineRepo, NullLogger<ScrapedDocumentSeeder>.Instance);
        var result = await seeder.SeedAsync(catalogPath, CancellationToken.None);

        Assert.Equal(1, result.Upserted);
        Assert.Equal(0, result.Skipped);
        await docRepo.Received(1).UpsertAsync(
            Arg.Is<DocumentRecord>(r => r.DocumentId == "doc_abc123"),
            "GweeP-MW95j",
            "Godzilla",
            "Stern Pinball",
            CancellationToken.None);
    }

    [Fact]
    public async Task SeedAsync_ServiceBulletinWithMatchingMachine_UpsertsAndCountsUpserted()
    {
        var machine = BuildMachine("GpeoL-MyNPq", "Foo Fighters", "Stern Pinball");
        var machineRepo = BuildMachineRepo(machine);
        var docRepo = Substitute.For<IScrapedDocumentRepository>();

        var catalogPath = WriteCatalog(
            Doc("doc_def456", "ServiceBulletin", "Foo Fighters", "https://example.com/ff-sb.pdf"));

        var seeder = new ScrapedDocumentSeeder(docRepo, machineRepo, NullLogger<ScrapedDocumentSeeder>.Instance);
        var result = await seeder.SeedAsync(catalogPath, CancellationToken.None);

        Assert.Equal(1, result.Upserted);
        Assert.Equal(0, result.Skipped);
    }

    [Fact]
    public async Task SeedAsync_DocWithNoGameTitle_SkipsAndCountsSkipped()
    {
        var machineRepo = Substitute.For<IMachineRepository>();
        var docRepo = Substitute.For<IScrapedDocumentRepository>();

        var catalogPath = WriteCatalog(
            DocNoTitle("doc_notitle", "Manual"));

        var seeder = new ScrapedDocumentSeeder(docRepo, machineRepo, NullLogger<ScrapedDocumentSeeder>.Instance);
        var result = await seeder.SeedAsync(catalogPath, CancellationToken.None);

        Assert.Equal(0, result.Upserted);
        Assert.Equal(1, result.Skipped);
        await docRepo.DidNotReceive().UpsertAsync(
            Arg.Any<DocumentRecord>(),
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SeedAsync_DocWithNoMatchingMachine_SkipsAndCountsSkipped()
    {
        var machineRepo = BuildEmptyMachineRepo();
        var docRepo = Substitute.For<IScrapedDocumentRepository>();

        var catalogPath = WriteCatalog(
            Doc("doc_nomatch", "Manual", "Unknown Machine", "https://example.com/unknown.pdf"));

        var seeder = new ScrapedDocumentSeeder(docRepo, machineRepo, NullLogger<ScrapedDocumentSeeder>.Instance);
        var result = await seeder.SeedAsync(catalogPath, CancellationToken.None);

        Assert.Equal(0, result.Upserted);
        Assert.Equal(1, result.Skipped);
    }

    [Fact]
    public async Task SeedAsync_NonManualDocType_IsNotCountedInSkipped()
    {
        // Flyers and Other docs are silently excluded (not added to Skipped)
        // because they're not part of the RAG corpus — Skipped is reserved
        // for Manual/ServiceBulletin docs that had resolution failures.
        var machineRepo = Substitute.For<IMachineRepository>();
        var docRepo = Substitute.For<IScrapedDocumentRepository>();

        var catalogPath = WriteCatalog(
            Doc("doc_flyer1", "Flyer", "Godzilla", "https://example.com/flyer.pdf"),
            Doc("doc_other1", "Other", "Godzilla", "https://example.com/other.pdf"));

        var seeder = new ScrapedDocumentSeeder(docRepo, machineRepo, NullLogger<ScrapedDocumentSeeder>.Instance);
        var result = await seeder.SeedAsync(catalogPath, CancellationToken.None);

        Assert.Equal(0, result.Upserted);
        Assert.Equal(0, result.Skipped);
    }

    [Fact]
    public async Task SeedAsync_MixedCatalog_CountsCorrectly()
    {
        var machine = BuildMachine("GweeP-MW95j", "Godzilla", "Stern Pinball");
        var machineRepo = BuildMachineRepo(machine);
        var docRepo = Substitute.For<IScrapedDocumentRepository>();

        var catalogPath = WriteCatalog(
            Doc("doc_1", "Manual", "Godzilla", "https://example.com/gz-manual.pdf"),
            Doc("doc_2", "ServiceBulletin", "Godzilla", "https://example.com/gz-sb.pdf"),
            Doc("doc_3", "Manual", "Unknown Machine", "https://example.com/unk.pdf"),   // no machine match
            DocNoTitle("doc_4", "Manual"),                                               // no title
            Doc("doc_5", "Flyer", "Godzilla", "https://example.com/flyer.pdf"));        // excluded type

        var seeder = new ScrapedDocumentSeeder(docRepo, machineRepo, NullLogger<ScrapedDocumentSeeder>.Instance);
        var result = await seeder.SeedAsync(catalogPath, CancellationToken.None);

        Assert.Equal(2, result.Upserted);
        Assert.Equal(2, result.Skipped);
    }

    [Fact]
    public async Task SeedAsync_CatalogFileNotFound_ThrowsFileNotFoundException()
    {
        var machineRepo = Substitute.For<IMachineRepository>();
        var docRepo = Substitute.For<IScrapedDocumentRepository>();
        var seeder = new ScrapedDocumentSeeder(docRepo, machineRepo, NullLogger<ScrapedDocumentSeeder>.Instance);

        await Assert.ThrowsAsync<FileNotFoundException>(
            () => seeder.SeedAsync("/does/not/exist/catalog.json", CancellationToken.None));
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static Machine BuildMachine(string id, string title, string manufacturer) => new()
    {
        Id = id,
        PartitionKey = manufacturer.ToLowerInvariant().Split(' ')[0],
        Title = title,
        ManufacturerDisplayName = manufacturer,
        Year = 2023,
        OpdbSourceUrl = $"https://opdb.org/machines/{id}",
    };

    private static IMachineRepository BuildMachineRepo(Machine machine)
    {
        var repo = Substitute.For<IMachineRepository>();
        repo.QueryByTitleAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var title = callInfo.ArgAt<string>(0);
                return string.Equals(title, machine.Title, StringComparison.OrdinalIgnoreCase)
                    ? AsyncEnum(machine)
                    : AsyncEnum<Machine>();
            });
        return repo;
    }

    private static IMachineRepository BuildEmptyMachineRepo()
    {
        var repo = Substitute.For<IMachineRepository>();
        repo.QueryByTitleAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(_ => AsyncEnum<Machine>());
        return repo;
    }

    private static async IAsyncEnumerable<T> AsyncEnum<T>(params T[] items)
    {
        foreach (var item in items)
        {
            yield return item;
        }
        await Task.CompletedTask;
    }

    private static readonly JsonSerializerOptions CatalogWriteOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    private string WriteCatalog(params object[] docs)
    {
        var path = Path.GetTempFileName();
        _tempFiles.Add(path);
        var catalog = new { documents = docs };
        File.WriteAllText(path, JsonSerializer.Serialize(catalog, CatalogWriteOptions));
        return path;
    }

    private static object Doc(string documentId, string docType, string gameTitle, string fileUrl) => new
    {
        document_id = documentId,
        source = new
        {
            discovery_url = "https://example.com",
            discovery_context = "test",
            file_url = fileUrl,
            source_type = "ManualsPage",
            scraped_at = DateTime.UtcNow,
        },
        classification = new { document_type = docType, file_format = "pdf" },
        game = new { title = gameTitle, slug = gameTitle.ToLowerInvariant().Replace(' ', '-'), game_page_url = "https://example.com" },
        timeline = new { first_discovered_at = DateTime.UtcNow },
    };

    private static object DocNoTitle(string documentId, string docType) => new
    {
        document_id = documentId,
        source = new
        {
            discovery_url = "https://example.com",
            discovery_context = "test",
            file_url = "https://example.com/file.pdf",
            source_type = "ManualsPage",
            scraped_at = DateTime.UtcNow,
        },
        classification = new { document_type = docType, file_format = "pdf" },
        game = (object?)null,
        timeline = new { first_discovered_at = DateTime.UtcNow },
    };
}
