using PinballWizard.Domain.Abstractions;

namespace PinballWizard.Processor.Chunking;

public sealed class SectionAwareChunker : IChunkingStrategy
{
    private const int MinTokens = 512;
    private const int MaxTokens = 1024;
    private const int Overlap = 64;

    public string Name => "SectionAwareChunker";

    public IReadOnlyList<TextChunk> Chunk(ExtractionResult extractionResult)
    {
        if (extractionResult.Sections.Count == 0)
            return [];

        var chunks = new List<TextChunk>();
        var sectionGroups = GroupBySection(extractionResult.Sections);

        foreach (var group in sectionGroups)
        {
            var sectionPath = group.SectionPath;
            var sectionText = string.Join("\n", group.Sections.Select(s => s.Content));
            var tokenCount = TokenHelper.CountTokens(sectionText);

            if (tokenCount <= MaxTokens)
            {
                // Section fits in one chunk
                chunks.Add(new TextChunk
                {
                    Content = sectionText,
                    SectionPath = sectionPath,
                    PageNumber = group.Sections.FirstOrDefault()?.PageNumber,
                    TokenCount = tokenCount
                });
            }
            else
            {
                // Section too large — split with sliding window within the section
                var subChunks = SplitLargeSection(sectionText, sectionPath, group.Sections.FirstOrDefault()?.PageNumber);
                chunks.AddRange(subChunks);
            }
        }

        // Merge small adjacent chunks if they share a heading and are under MinTokens
        return MergeSmallChunks(chunks);
    }

    private static List<SectionGroup> GroupBySection(List<TextSection> sections)
    {
        var groups = new List<SectionGroup>();
        string? currentHeading = null;
        List<TextSection> currentSections = [];

        foreach (var section in sections)
        {
            var heading = section.Heading ?? "(no heading)";

            if (heading != currentHeading && currentSections.Count > 0)
            {
                groups.Add(new SectionGroup(BuildSectionPath(currentHeading, currentSections), currentSections));
                currentSections = [];
            }

            currentHeading = heading;
            currentSections.Add(section);
        }

        if (currentSections.Count > 0)
        {
            groups.Add(new SectionGroup(BuildSectionPath(currentHeading, currentSections), currentSections));
        }

        return groups;
    }

    private static string? BuildSectionPath(string? heading, List<TextSection> sections)
    {
        if (heading is null) return null;

        // Find the deepest heading path using levels
        var headings = sections
            .Where(s => s.Heading is not null)
            .Select(s => s.Heading!)
            .Distinct();

        return string.Join(" > ", headings);
    }

    private static List<TextChunk> SplitLargeSection(string text, string? sectionPath, int? pageNumber)
    {
        var chunks = new List<TextChunk>();
        var sentences = text.Split(['\n', '.'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var currentSentences = new List<string>();
        int currentTokens = 0;

        foreach (var sentence in sentences)
        {
            var sentenceTokens = TokenHelper.CountTokens(sentence);

            if (currentTokens + sentenceTokens > MaxTokens && currentSentences.Count > 0)
            {
                var content = string.Join(" ", currentSentences);
                chunks.Add(new TextChunk
                {
                    Content = content,
                    SectionPath = sectionPath,
                    PageNumber = pageNumber,
                    TokenCount = TokenHelper.CountTokens(content)
                });

                // Overlap: keep the last few sentences
                currentSentences = GetOverlapSentences(currentSentences);
                currentTokens = currentSentences.Sum(s => TokenHelper.CountTokens(s));
            }

            currentSentences.Add(sentence);
            currentTokens += sentenceTokens;
        }

        if (currentSentences.Count > 0)
        {
            var content = string.Join(" ", currentSentences);
            chunks.Add(new TextChunk
            {
                Content = content,
                SectionPath = sectionPath,
                PageNumber = pageNumber,
                TokenCount = TokenHelper.CountTokens(content)
            });
        }

        return chunks;
    }

    private static List<string> GetOverlapSentences(List<string> sentences)
    {
        var overlap = new List<string>();
        int tokens = 0;

        for (int i = sentences.Count - 1; i >= 0 && tokens < Overlap; i--)
        {
            var t = TokenHelper.CountTokens(sentences[i]);
            if (tokens + t > Overlap && overlap.Count > 0) break;
            overlap.Insert(0, sentences[i]);
            tokens += t;
        }

        return overlap;
    }

    private static IReadOnlyList<TextChunk> MergeSmallChunks(List<TextChunk> chunks)
    {
        if (chunks.Count <= 1) return chunks;

        var merged = new List<TextChunk>();
        TextChunk? pending = null;

        foreach (var chunk in chunks)
        {
            if (pending is null)
            {
                pending = chunk;
                continue;
            }

            // Merge if both are small and share the same section path
            if (pending.TokenCount < MinTokens
                && chunk.TokenCount < MinTokens
                && pending.SectionPath == chunk.SectionPath)
            {
                var content = $"{pending.Content}\n{chunk.Content}";
                pending = new TextChunk
                {
                    Content = content,
                    SectionPath = pending.SectionPath,
                    PageNumber = pending.PageNumber,
                    TokenCount = TokenHelper.CountTokens(content)
                };
            }
            else
            {
                merged.Add(pending);
                pending = chunk;
            }
        }

        if (pending is not null)
            merged.Add(pending);

        return merged;
    }

    private sealed record SectionGroup(string? SectionPath, List<TextSection> Sections);
}
