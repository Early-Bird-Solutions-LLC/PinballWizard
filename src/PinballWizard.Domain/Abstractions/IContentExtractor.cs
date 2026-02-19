namespace PinballWizard.Domain.Abstractions;

/// <summary>
/// Interface for content extractors. Lives in Domain so Processor can implement
/// and tests can mock.
/// </summary>
public interface IContentExtractor
{
    string Name { get; }
    bool CanExtract(string mimeType, string fileExtension);
    Task<ExtractionResult> ExtractAsync(Stream content, string filename, CancellationToken ct = default);
}

public sealed class ExtractionResult
{
    public required string Text { get; init; }
    public List<TextSection> Sections { get; init; } = [];
    public int? PageCount { get; init; }
    public Dictionary<string, string> Metadata { get; init; } = [];
}

public sealed class TextSection
{
    public required string Content { get; init; }
    public string? Heading { get; init; }
    public int Level { get; init; }
    public int? PageNumber { get; init; }
}
