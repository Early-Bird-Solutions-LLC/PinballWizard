using System.Text;
using System.Text.Json;
using PinballWizard.Infrastructure.Persistence.Cosmos;
using Xunit;

namespace PinballWizard.Infrastructure.Tests.Persistence.Cosmos;

/// <summary>
/// The machine-detail read (<see cref="CosmosMachineDocumentReadRepository"/>) surfaces the
/// scraped-side fields of each document. It only needs six fields, so it reads through the
/// narrow <see cref="ScrapedDocumentReadProjection"/> rather than the full write-model
/// <see cref="ScrapedDocumentRecord"/>.
///
/// Same incident class as <see cref="ScrapedDocumentTypeProjectionTests"/>: documents written
/// before <c>edition_scope</c> became <c>required</c> (#318) lack it, and deserializing them into
/// the full write model throws <see cref="JsonException"/> — which would 500 the
/// <c>/admin/machines/{id}</c> detail page. The read projection tolerates the historical shape.
/// </summary>
public sealed class ScrapedDocumentReadProjectionTests
{
    // A scraped_documents document as written BEFORE #318 — no edition_scope.
    private const string PreEditionScopeDocumentJson =
        """
        {
          "id": "doc_pre318_GRBN-A",
          "machine_id": "GRBN-A",
          "document_id": "doc_pre318",
          "document_url": "https://sternpinball.com/godzilla-manual.pdf",
          "machine_title": "Godzilla (Pro)",
          "manufacturer": "stern",
          "document_type": "Manual",
          "content_hash": "abc123",
          "edition": "Pro",
          "last_downloaded_at": "2026-01-15T08:30:00+00:00"
        }
        """;

    private static SystemTextJsonCosmosSerializer Serializer() =>
        new(CosmosClientConfiguration.BuildJsonOptions());

    private static MemoryStream StreamOf(string json) =>
        new(Encoding.UTF8.GetBytes(json));

    [Fact]
    public void Projection_DeserializesDocumentMissingEditionScope_WithoutThrowing()
    {
        var projection = Serializer().FromStream<ScrapedDocumentReadProjection>(
            StreamOf(PreEditionScopeDocumentJson));

        Assert.Equal("doc_pre318", projection.DocumentId);
        Assert.Equal("Manual", projection.DocumentType);
        Assert.Equal("https://sternpinball.com/godzilla-manual.pdf", projection.DocumentUrl);
        Assert.Equal("Pro", projection.Edition);
        // Missing edition_scope degrades to empty rather than throwing.
        Assert.Equal(string.Empty, projection.EditionScope);
        Assert.Equal(
            new DateTimeOffset(2026, 1, 15, 8, 30, 0, TimeSpan.Zero),
            projection.LastDownloadedAt);
    }

    [Fact]
    public void Projection_DeserializesDocumentWithEditionScope_RoundTripsTheValue()
    {
        const string json =
            """
            {
              "id": "doc_post318_GRBN-A",
              "machine_id": "GRBN-A",
              "document_id": "doc_post318",
              "document_url": "https://sternpinball.com/x.pdf",
              "document_type": "Manual",
              "edition_scope": "single-edition"
            }
            """;

        var projection = Serializer().FromStream<ScrapedDocumentReadProjection>(StreamOf(json));

        Assert.Equal("single-edition", projection.EditionScope);
    }
}
