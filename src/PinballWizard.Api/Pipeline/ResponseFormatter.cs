using System.Text.RegularExpressions;
using PinballWizard.Domain.Models;

namespace PinballWizard.Api.Pipeline;

public interface IResponseFormatter
{
    ChatResponse Format(string conversationId, string rawText, List<ContextBlock> contextBlocks);
}

public sealed partial class ResponseFormatter : IResponseFormatter
{
    public ChatResponse Format(string conversationId, string rawText, List<ContextBlock> contextBlocks)
    {
        var validIndices = contextBlocks.Select(b => b.Index).ToHashSet();

        // Find all citation references [N] in the text
        var cleanedText = CitationRegex().Replace(rawText, match =>
        {
            if (int.TryParse(match.Groups[1].Value, out var index) && validIndices.Contains(index))
                return match.Value; // Keep valid citation
            return ""; // Remove invalid citation
        });

        // Build source citations for indices actually referenced in the cleaned text
        var referencedIndices = new HashSet<int>();
        foreach (Match match in CitationRegex().Matches(cleanedText))
        {
            if (int.TryParse(match.Groups[1].Value, out var index))
                referencedIndices.Add(index);
        }

        var sources = contextBlocks
            .Where(b => referencedIndices.Contains(b.Index))
            .Select(b => new SourceCitation
            {
                Index = b.Index,
                Title = b.SourceName,
                Url = b.SourceUrl ?? "",
                DocumentType = b.DocumentType,
                GameTitle = b.GameTitle,
                SectionPath = b.SectionPath,
                Score = b.Score
            })
            .OrderBy(s => s.Index)
            .ToList();

        return new ChatResponse
        {
            ConversationId = conversationId,
            Answer = cleanedText.Trim(),
            Sources = sources
        };
    }

    [GeneratedRegex(@"\[(\d+)\]")]
    private static partial Regex CitationRegex();
}
