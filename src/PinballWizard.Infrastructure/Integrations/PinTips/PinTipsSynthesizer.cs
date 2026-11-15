using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using PinballWizard.Application.Rag.Chunking;
using PinballWizard.Application.Rag.Extraction;
using PinballWizard.Core.Models;

namespace PinballWizard.Infrastructure.Integrations.PinTips;

public sealed class PinTipsSynthesizer
{
    private readonly IChunker _chunker;
    private readonly ILogger<PinTipsSynthesizer> _logger;

    public PinTipsSynthesizer(IChunker chunker, ILogger<PinTipsSynthesizer> logger)
    {
        ArgumentNullException.ThrowIfNull(chunker);
        ArgumentNullException.ThrowIfNull(logger);
        _chunker = chunker;
        _logger = logger;
    }

    public IReadOnlyList<Chunk> Synthesize(PinTip tip, string sourceUrl, ChunkRequest chunkRequest)
    {
        ArgumentNullException.ThrowIfNull(tip);
        ArgumentNullException.ThrowIfNull(chunkRequest);

        if (string.IsNullOrWhiteSpace(tip.Text))
        {
            _logger.LogWarning("PinTipsSynthesizer: tip {TipId} has empty text; skipping.", tip.TipId);
            return [];
        }

        var text = BuildText(tip, sourceUrl);
        var extracted = new ExtractedDocument(
            Status: ExtractionStatus.Success,
            Text: text,
            Pages: [new ExtractedPage(PageNumber: 1, Text: text)],
            Outline: [],
            Error: null);

        return _chunker.Chunk(extracted, chunkRequest);
    }

    public static string TitleFor(PinTip tip) =>
        string.Create(CultureInfo.InvariantCulture, $"PinTips {tip.Category} ({tip.OpdbId})");

    // SHA-256 of the text that gets indexed. Vote totals are included because
    // they are part of that text. A tip whose body, category, votes, and id
    // are unchanged produces the same hash, so a later sync can skip the embed.
    public static string ContentHash(PinTip tip, string sourceUrl)
    {
        ArgumentNullException.ThrowIfNull(tip);
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(BuildText(tip, sourceUrl)));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    public static bool IsUnchanged(string? storedHash, string currentHash) =>
        !string.IsNullOrWhiteSpace(storedHash)
        && string.Equals(storedHash, currentHash, StringComparison.Ordinal);

    private static string BuildText(PinTip tip, string sourceUrl)
    {
        var sb = new StringBuilder();
        sb.AppendLine(CultureInfo.InvariantCulture, $"# {TitleFor(tip)}");
        sb.AppendLine(CultureInfo.InvariantCulture,
            $"Play tip for OPDB {tip.OpdbId}, category {tip.Category}, {tip.VoteTotal} votes.");
        sb.AppendLine();
        sb.AppendLine(tip.Text);
        sb.AppendLine();
        sb.AppendLine(CultureInfo.InvariantCulture, $"Source: PinTips tip {tip.TipId}. Export: {sourceUrl}");
        return sb.ToString();
    }
}
