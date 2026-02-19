namespace PinballWizard.Domain.Abstractions;

public interface IChunkingStrategy
{
    string Name { get; }
    IReadOnlyList<TextChunk> Chunk(ExtractionResult extractionResult);
}

public sealed class TextChunk
{
    public required string Content { get; init; }
    public string? SectionPath { get; init; }
    public int? PageNumber { get; init; }
    public int TokenCount { get; init; }
}
