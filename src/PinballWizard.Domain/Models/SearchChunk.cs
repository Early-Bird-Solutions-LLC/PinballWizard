namespace PinballWizard.Domain.Models;

/// <summary>
/// The document model for Azure AI Search. Maps 1:1 to the search index schema.
/// Used by Processor (write) and Api (read).
/// </summary>
public sealed class SearchChunk
{
    public required string ChunkId { get; init; }
    public required string Content { get; set; }
    public required string ParentDocId { get; init; }
    public string? GameSlug { get; set; }
    public string? GameTitle { get; set; }
    public string? Manufacturer { get; set; }
    public DocumentType DocumentType { get; set; }
    public SourceType SourceType { get; set; }
    public string? SourceUrl { get; set; }
    public string? SourceName { get; set; }
    public string? SectionPath { get; set; }
    public int? PageNumber { get; set; }
    public List<ContentCategory> ContentCategories { get; set; } = [];
    public DateTimeOffset LastUpdated { get; set; }
}
