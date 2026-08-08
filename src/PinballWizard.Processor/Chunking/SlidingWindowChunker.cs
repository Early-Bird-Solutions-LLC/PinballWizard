using Microsoft.Extensions.Options;
using PinballWizard.Domain.Abstractions;

namespace PinballWizard.Processor.Chunking;

public sealed class SlidingWindowChunker : IChunkingStrategy
{
    private readonly int _maxTokens;
    private readonly int _overlap;

    public SlidingWindowChunker(IOptions<ProcessorSettings> settings)
    {
        _maxTokens = settings.Value.ChunkTokenSize;
        _overlap = settings.Value.ChunkOverlap;
    }

    public string Name => "SlidingWindowChunker";

    public IReadOnlyList<TextChunk> Chunk(ExtractionResult extractionResult)
    {
        var text = extractionResult.Text;
        if (string.IsNullOrWhiteSpace(text))
            return [];

        // Split into sentences for cleaner boundaries
        var sentences = SplitIntoSentences(text);
        var chunks = new List<TextChunk>();

        var currentChunk = new List<string>();
        int currentTokens = 0;

        foreach (var sentence in sentences)
        {
            var sentenceTokens = TokenHelper.CountTokens(sentence);

            // If a single sentence exceeds max tokens, split it further
            if (sentenceTokens > _maxTokens)
            {
                // Flush current chunk first
                if (currentChunk.Count > 0)
                {
                    chunks.Add(CreateChunk(currentChunk, extractionResult));
                    currentChunk = GetOverlapSentences(currentChunk, _overlap);
                    currentTokens = currentChunk.Sum(s => TokenHelper.CountTokens(s));
                }

                // Add the large sentence as its own chunk
                var largeChunkText = sentence[..Math.Min(sentence.Length, _maxTokens * 4)];
                chunks.Add(new TextChunk
                {
                    Content = largeChunkText,
                    TokenCount = TokenHelper.CountTokens(largeChunkText),
                    SectionPath = GetSectionPath(extractionResult)
                });
                continue;
            }

            if (currentTokens + sentenceTokens > _maxTokens && currentChunk.Count > 0)
            {
                chunks.Add(CreateChunk(currentChunk, extractionResult));
                currentChunk = GetOverlapSentences(currentChunk, _overlap);
                currentTokens = currentChunk.Sum(s => TokenHelper.CountTokens(s));
            }

            currentChunk.Add(sentence);
            currentTokens += sentenceTokens;
        }

        if (currentChunk.Count > 0)
        {
            chunks.Add(CreateChunk(currentChunk, extractionResult));
        }

        return chunks;
    }

    private static TextChunk CreateChunk(List<string> sentences, ExtractionResult extractionResult)
    {
        var content = string.Join(" ", sentences);
        return new TextChunk
        {
            Content = content,
            TokenCount = TokenHelper.CountTokens(content),
            SectionPath = GetSectionPath(extractionResult)
        };
    }

    private static List<string> GetOverlapSentences(List<string> sentences, int overlapTokens)
    {
        var overlap = new List<string>();
        int tokens = 0;

        for (int i = sentences.Count - 1; i >= 0 && tokens < overlapTokens; i--)
        {
            var sentenceTokens = TokenHelper.CountTokens(sentences[i]);
            if (tokens + sentenceTokens > overlapTokens && overlap.Count > 0)
                break;
            overlap.Insert(0, sentences[i]);
            tokens += sentenceTokens;
        }

        return overlap;
    }

    private static string? GetSectionPath(ExtractionResult result)
    {
        var headings = result.Sections
            .Where(s => s.Heading is not null)
            .Select(s => s.Heading!)
            .Distinct()
            .Take(3);

        var path = string.Join(" > ", headings);
        return string.IsNullOrEmpty(path) ? null : path;
    }

    private static List<string> SplitIntoSentences(string text)
    {
        var sentences = new List<string>();
        int start = 0;

        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] is '.' or '!' or '?' or '\n')
            {
                // Look for end of sentence (period followed by space/newline or end of text)
                if (i == text.Length - 1 || i + 1 < text.Length && char.IsWhiteSpace(text[i + 1]))
                {
                    var sentence = text[start..(i + 1)].Trim();
                    if (!string.IsNullOrWhiteSpace(sentence))
                        sentences.Add(sentence);
                    start = i + 1;
                }
            }
        }

        // Remaining text
        if (start < text.Length)
        {
            var remaining = text[start..].Trim();
            if (!string.IsNullOrWhiteSpace(remaining))
                sentences.Add(remaining);
        }

        return sentences;
    }
}
