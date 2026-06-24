using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.ML.Tokenizers;
using PinballWizard.Application.Rag.Chunking;
using PinballWizard.Core.Domain;

namespace PinballWizard.Application.Rag.GameOverviews;

// Builds a chunked GameOverview document from a Machine's scraped game-page
// content. One chunk per semantic section: the shared overview prose, then one
// per edition carrying that edition's Description + UniqueFeatures. Per-edition
// chunking keeps edition-specific answers ("what's different about the LE?")
// retrievable as distinct units. No HybridChunker dependency — the sections ARE
// the chunk boundaries. Returns empty when there is nothing to say.
public sealed class GameOverviewSynthesizer : IGameOverviewSynthesizer
{
    private readonly TiktokenTokenizer _tokenizer;
    private readonly ILogger<GameOverviewSynthesizer> _logger;

    public GameOverviewSynthesizer(ILogger<GameOverviewSynthesizer> logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;

        // Tokenizer matches HybridChunker's choice (ADR-0019) — same
        // BPE encoding, same data package, same constructor pattern.
        // Thread-safe; cached for the singleton lifetime.
        _tokenizer = TiktokenTokenizer.CreateForEncoding("cl100k_base");
    }

    public IReadOnlyList<Chunk> Synthesize(Machine machine)
    {
        ArgumentNullException.ThrowIfNull(machine);
        var chunks = new List<Chunk>();
        var index = 0;

        if (!string.IsNullOrWhiteSpace(machine.OverviewProse))
        {
            var text = $"{machine.Title} — Overview\n{machine.OverviewProse.Trim()}";
            chunks.Add(new Chunk(index++, text, "Overview", 0, 0, _tokenizer.CountTokens(text)));
        }

        foreach (var edition in machine.Editions)
        {
            var body = BuildEditionBody(edition);
            if (body is null) continue;
            var text = $"{machine.Title} — {edition.Name}\n{body}";
            chunks.Add(new Chunk(index++, text, $"Edition: {edition.Name}", 0, 0, _tokenizer.CountTokens(text)));
        }

        _logger.LogDebug(
            "GameOverview synthesized: machineId={MachineId} title={Title} chunks={ChunkCount}.",
            machine.Id, machine.Title, chunks.Count);

        return chunks;
    }

    private static string? BuildEditionBody(MachineEdition edition)
    {
        var sb = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(edition.Description)) sb.Append(edition.Description.Trim());
        if (edition.UniqueFeatures.Count > 0)
        {
            if (sb.Length > 0) sb.Append(' ');
            sb.Append("Unique features: ").Append(string.Join(", ", edition.UniqueFeatures)).Append('.');
        }
        return sb.Length == 0 ? null : sb.ToString();
    }
}
