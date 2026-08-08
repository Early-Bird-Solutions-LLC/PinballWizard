using Microsoft.Extensions.Options;
using Microsoft.ML.Tokenizers;

namespace PinballWizard.Api.Pipeline;

public interface IContextAssembler
{
    ContextResult Assemble(List<ScoredChunk> chunks);
}

public sealed record ContextBlock
{
    public required int Index { get; init; }
    public required string Content { get; init; }
    public required string SourceName { get; init; }
    public int? PageNumber { get; init; }
    public string? SourceUrl { get; init; }
    public string? DocumentType { get; init; }
    public string? GameTitle { get; init; }
    public string? SectionPath { get; init; }
    public double Score { get; init; }
}

public sealed record ContextResult
{
    public required string FormattedContext { get; init; }
    public required List<ContextBlock> Blocks { get; init; }
    public int TotalTokens { get; init; }
}

public sealed class ContextAssembler(IOptions<ApiSettings> settings) : IContextAssembler
{
    private static readonly TiktokenTokenizer Tokenizer = TiktokenTokenizer.CreateForModel("gpt-4o");

    public ContextResult Assemble(List<ScoredChunk> chunks)
    {
        var budget = settings.Value.ContextTokenBudget;
        var deduplicated = Deduplicate(chunks);
        var ordered = deduplicated.OrderByDescending(c => c.Score).ToList();

        var blocks = new List<ContextBlock>();
        var totalTokens = 0;
        var index = 1;

        foreach (var scored in ordered)
        {
            var chunk = scored.Chunk;
            var sourceName = chunk.SourceName ?? chunk.SourceUrl ?? "Unknown";
            var header = chunk.PageNumber.HasValue
                ? $"[{index}] (source: {sourceName}, page: {chunk.PageNumber})"
                : $"[{index}] (source: {sourceName})";

            var blockText = $"{header}\n{chunk.Content}";
            var tokens = CountTokens(blockText);

            if (totalTokens + tokens > budget)
                break;

            blocks.Add(new ContextBlock
            {
                Index = index,
                Content = chunk.Content,
                SourceName = sourceName,
                PageNumber = chunk.PageNumber,
                SourceUrl = chunk.SourceUrl,
                DocumentType = chunk.DocumentType.ToString(),
                GameTitle = chunk.GameTitle,
                SectionPath = chunk.SectionPath,
                Score = scored.Score
            });

            totalTokens += tokens;
            index++;
        }

        var formattedContext = string.Join("\n\n", blocks.Select(b =>
        {
            var header = b.PageNumber.HasValue
                ? $"[{b.Index}] (source: {b.SourceName}, page: {b.PageNumber})"
                : $"[{b.Index}] (source: {b.SourceName})";
            return $"{header}\n{b.Content}";
        }));

        return new ContextResult
        {
            FormattedContext = formattedContext,
            Blocks = blocks,
            TotalTokens = totalTokens
        };
    }

    internal static List<ScoredChunk> Deduplicate(List<ScoredChunk> chunks)
    {
        var seen = new HashSet<string>();
        var result = new List<ScoredChunk>();

        foreach (var chunk in chunks.OrderByDescending(c => c.Score))
        {
            // Dedup key: same parent doc + adjacent sections
            var key = $"{chunk.Chunk.ParentDocId}|{chunk.Chunk.SectionPath}";
            if (seen.Add(key))
            {
                result.Add(chunk);
            }
        }

        return result;
    }

    internal static int CountTokens(string text)
    {
        return Tokenizer.CountTokens(text);
    }
}
