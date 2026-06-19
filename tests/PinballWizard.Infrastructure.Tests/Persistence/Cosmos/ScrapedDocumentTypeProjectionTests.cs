using System.Text;
using System.Text.Json;
using PinballWizard.Infrastructure.Persistence.Cosmos;
using Xunit;

namespace PinballWizard.Infrastructure.Tests.Persistence.Cosmos;

/// <summary>
/// The catalog-stats doc-type count (rebuild backstop + change-feed projection)
/// only needs <c>document_type</c> and <c>machine_title</c>, so it reads through
/// the narrow <see cref="ScrapedDocumentTypeProjection"/> rather than the full
/// <see cref="ScrapedDocumentRecord"/>.
///
/// Why this matters (live incident, 2026-06-19): <c>scraped_documents</c> written
/// before <c>edition_scope</c> became a <c>required</c> field (#318) lack it.
/// Deserializing those documents into the full write-model record throws
/// <see cref="JsonException"/> (missing required property), which crashed the
/// catalog-stats <c>BackgroundService</c> (StopHost) and made
/// <c>--rebuild-catalog-stats</c> fail. The projection must tolerate any historical
/// schema shape, because counting doc-types must never depend on a document
/// satisfying the current write-model invariants.
/// </summary>
public sealed class ScrapedDocumentTypeProjectionTests
{
    // A scraped_documents document as written BEFORE #318 — note the absence of
    // edition_scope (and other fields the count does not need).
    private const string PreEditionScopeDocumentJson =
        """
        {
          "id": "doc_pre318",
          "machine_id": "GRBN-A",
          "document_id": "doc_pre318",
          "document_url": "https://sternpinball.com/godzilla-manual.pdf",
          "machine_title": "Godzilla (Pro)",
          "manufacturer": "stern",
          "document_type": "Manual",
          "content_hash": "abc123"
        }
        """;

    private static SystemTextJsonCosmosSerializer Serializer() =>
        new(CosmosClientConfiguration.BuildJsonOptions());

    private static MemoryStream StreamOf(string json) =>
        new(Encoding.UTF8.GetBytes(json));

    [Fact]
    public void Projection_DeserializesDocumentMissingEditionScope_WithoutThrowing()
    {
        var projection = Serializer().FromStream<ScrapedDocumentTypeProjection>(
            StreamOf(PreEditionScopeDocumentJson));

        Assert.Equal("Manual", projection.DocumentType);
        Assert.Equal("Godzilla (Pro)", projection.MachineTitle);
    }

    [Fact]
    public void FullRecord_OnSameDocument_Throws_WhichIsWhyTheProjectionExists()
    {
        // Characterizes the live incident: the full write-model record enforces
        // edition_scope as required, so it cannot read pre-#318 documents. This is
        // the exact failure the projection above routes around for the read path.
        Assert.Throws<JsonException>(() =>
            Serializer().FromStream<ScrapedDocumentRecord>(StreamOf(PreEditionScopeDocumentJson)));
    }
}
