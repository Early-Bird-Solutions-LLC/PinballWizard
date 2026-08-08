using PinballWizard.Domain.Abstractions;

namespace PinballWizard.Processor.Chunking;

public sealed class WholeDocumentChunker : IChunkingStrategy
{
    private const int MaxTokens = 2048;

    public string Name => "WholeDocumentChunker";

    public IReadOnlyList<TextChunk> Chunk(ExtractionResult extractionResult)
    {
        var text = extractionResult.Text;
        if (string.IsNullOrWhiteSpace(text))
            return [];

        var sectionPath = extractionResult.Sections
            .Where(s => s.Heading is not null)
            .Select(s => s.Heading!)
            .Distinct()
            .Take(3);
        var path = string.Join(" > ", sectionPath);

        var tokenCount = TokenHelper.CountTokens(text);

        if (tokenCount <= MaxTokens)
        {
            return
            [
                new TextChunk
                {
                    Content = text,
                    SectionPath = string.IsNullOrEmpty(path) ? null : path,
                    PageNumber = extractionResult.Sections.FirstOrDefault()?.PageNumber,
                    TokenCount = tokenCount
                }
            ];
        }

        // Document exceeds max tokens — split at natural boundaries
        var chunks = new List<TextChunk>();
        var paragraphs = text.Split(["\n\n", "\r\n\r\n"], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var currentParagraphs = new List<string>();
        int currentTokens = 0;

        foreach (var paragraph in paragraphs)
        {
            var paragraphTokens = TokenHelper.CountTokens(paragraph);

            if (currentTokens + paragraphTokens > MaxTokens && currentParagraphs.Count > 0)
            {
                var content = string.Join("\n\n", currentParagraphs);
                chunks.Add(new TextChunk
                {
                    Content = content,
                    SectionPath = string.IsNullOrEmpty(path) ? null : path,
                    TokenCount = TokenHelper.CountTokens(content)
                });
                currentParagraphs.Clear();
                currentTokens = 0;
            }

            currentParagraphs.Add(paragraph);
            currentTokens += paragraphTokens;
        }

        if (currentParagraphs.Count > 0)
        {
            var content = string.Join("\n\n", currentParagraphs);
            chunks.Add(new TextChunk
            {
                Content = content,
                SectionPath = string.IsNullOrEmpty(path) ? null : path,
                TokenCount = TokenHelper.CountTokens(content)
            });
        }

        return chunks;
    }
}
