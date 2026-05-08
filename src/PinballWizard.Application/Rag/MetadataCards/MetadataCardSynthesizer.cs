using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.ML.Tokenizers;
using PinballWizard.Application.Rag.Chunking;
using PinballWizard.Core.Domain;

namespace PinballWizard.Application.Rag.MetadataCards;

// Default `IMetadataCardSynthesizer` implementation. Produces one
// ~150-token chunk per `Machine` for indexing into the Phase 4 RAG
// `pinwiz-rag-v1` AI Search index (ADR-0021). The output is plain
// prose readable as a citation snippet; the same fields drive both
// vector retrieval (the embedding sees the prose) and keyword
// retrieval (the prose contains the manufacturer / theme / designer
// vocabulary the searcher's query is likely to use).
//
// Pure transform — no I/O, no Cosmos, no AI Search. Reusable from
// the Cosmos Change Feed Function (W3-2) and ad-hoc scripts.
// Tokenization uses `Microsoft.ML.Tokenizers.TiktokenTokenizer`
// (cl100k_base) to match the chunker's token-budget arithmetic
// even though the synthesizer is a fixed-size single-chunk
// transform; per-chunk `TokenCount` is consumed downstream by the
// indexer's cost-projection log line and by the eval harness.
//
// Sparse machines (no editions, no designers, no themes) get a
// shorter card — the synthesizer skips empty sections rather than
// emitting "Designers: (none)" boilerplate that would dilute
// retrieval relevance and read amateurish in citations.
public sealed class MetadataCardSynthesizer : IMetadataCardSynthesizer
{
    // Section heading attached to the synthesized chunk. The chunker's
    // `Chunk.SectionHeading` field already carries "Metadata" semantics
    // for the citation surface; matching the value the indexer writes
    // into the index's `section_heading` field keeps the read-side
    // shape consistent across PDF-derived and synthetic chunks.
    private const string MetadataSectionHeading = "Metadata";

    private readonly TiktokenTokenizer _tokenizer;
    private readonly ILogger<MetadataCardSynthesizer> _logger;

    public MetadataCardSynthesizer(ILogger<MetadataCardSynthesizer> logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;

        // Tokenizer matches HybridChunker's choice (ADR-0019) — same
        // BPE encoding, same data package, same constructor pattern.
        // Thread-safe; cached for the singleton lifetime.
        _tokenizer = TiktokenTokenizer.CreateForEncoding("cl100k_base");
    }

    public Chunk Synthesize(Machine machine)
    {
        ArgumentNullException.ThrowIfNull(machine);

        var text = BuildText(machine);
        var tokenCount = _tokenizer.CountTokens(text);

        _logger.LogDebug(
            "Metadata card synthesized: machineId={MachineId} title={Title} tokens={TokenCount} editions={EditionCount}.",
            machine.Id,
            machine.Title,
            tokenCount,
            machine.Editions.Count);

        return new Chunk(
            ChunkIndex: 0,
            Text: text,
            SectionHeading: MetadataSectionHeading,
            PageStart: 0,
            PageEnd: 0,
            TokenCount: tokenCount);
    }

    private static string BuildText(Machine machine)
    {
        var sb = new StringBuilder(capacity: 512);

        sb.Append(machine.Title);
        if (machine.Year is int year)
        {
            sb.Append(" (").Append(year.ToString(CultureInfo.InvariantCulture)).Append(')');
        }
        sb.Append('\n');
        sb.Append(machine.ManufacturerDisplayName);

        if (machine.Themes.Count > 0)
        {
            // " · " (middle-dot, space-padded) reads well in citation
            // surfaces — one of the common "compact joiner" choices that
            // doesn't conflict with comma-separated lists elsewhere on
            // the card.
            sb.Append("\nThemes: ").Append(string.Join(" · ", machine.Themes));
        }

        if (machine.Designers.Count > 0)
        {
            sb.Append("\nDesigners: ").Append(string.Join(", ", machine.Designers));
        }

        if (machine.Editions.Count > 0)
        {
            sb.Append("\n\nEditions:");
            foreach (var edition in machine.Editions)
            {
                AppendEdition(sb, edition);
            }
        }

        if (!string.IsNullOrWhiteSpace(machine.OpdbSourceUrl))
        {
            sb.Append("\n\nSource: ").Append(machine.OpdbSourceUrl);
        }

        return sb.ToString();
    }

    private static void AppendEdition(StringBuilder sb, MachineEdition edition)
    {
        sb.Append("\n- ").Append(edition.Name);

        if (!string.IsNullOrWhiteSpace(edition.Availability))
        {
            // Availability ("In production", "Discontinued", etc.) is a
            // first-class retrieval signal for queries like "is Godzilla
            // still in production?" — bracketed inline against the
            // edition name so the snippet reads naturally.
            sb.Append(" [").Append(edition.Availability).Append(']');
        }

        if (!string.IsNullOrWhiteSpace(edition.Msrp))
        {
            sb.Append(" — MSRP ").Append(edition.Msrp);
        }

        if (edition.LimitedQuantity is int limited)
        {
            sb.Append(" (limited to ").Append(limited.ToString(CultureInfo.InvariantCulture)).Append(')');
        }

        if (!string.IsNullOrWhiteSpace(edition.Description))
        {
            sb.Append(": ").Append(edition.Description);
        }

        if (edition.UniqueFeatures.Count > 0)
        {
            sb.Append(" Features: ").Append(string.Join(", ", edition.UniqueFeatures)).Append('.');
        }

        if (!string.IsNullOrWhiteSpace(edition.OpdbSourceUrl))
        {
            // Per-edition OPDB URL (the alias record, e.g.
            // .../GRBN-MQR4P-A97X1) lets edition-specific queries
            // ("Premium-only features?") cite the alias record rather
            // than the parent machine's URL — the citation deep-links
            // to the exact record the answer references.
            sb.Append(" Source: ").Append(edition.OpdbSourceUrl);
        }
    }
}
