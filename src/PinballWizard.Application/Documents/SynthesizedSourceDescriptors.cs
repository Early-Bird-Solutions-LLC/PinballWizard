using PinballWizard.Core.Models;

namespace PinballWizard.Application.Documents;

// Single source of truth for the four synthesized content sources (Kineticist
// tutorials, Tilt Forums rulesheets, TWIP newsletters, Pinball Brothers Freshdesk
// articles). Both paths that mint a synthesized DocumentRecord consume this table
// so they cannot drift:
//   - the live sync verbs (--sync-kineticist-tutorials, etc.) read the provenance
//     constants (discovery context, document type, file format, manufacturer
//     override) when they persist a raw doc alongside each AI Search upsert;
//   - the index-scan backfill (--backfill-synthesized-raw-docs) reads the SAME
//     constants plus the two backfill-only fields the AI Search index cannot
//     supply (the doc-id prefix that identifies the source, and the title-suffix
//     to strip when recovering the human title from the indexed content).
//
// Before this table those constants were inline string literals repeated across
// four sync call sites; a backfill copy would have been a fifth. Centralizing them
// removes the sibling-drift risk (/local-review cat 4).
public sealed record SynthesizedSourceDescriptor(
    // Deterministic doc-id prefix the source mints (e.g. "kineticist_"). Identifies
    // which source an indexed document belongs to during the backfill scan.
    string DocumentIdPrefix,
    // Source.DiscoveryContext stamped on the provenance row (e.g. "Kineticist Tutorial").
    string DiscoveryContext,
    // Classification.DocumentType for this source's documents.
    DocumentType DocumentType,
    // Classification.FileFormat (the synthesized body's format: "md" / "html").
    string FileFormat,
    // Manufacturer to stamp when the source is not machine-specific (TWIP → "Kineticist").
    // Null means "use the per-document manufacturer" (the resolved machine's manufacturer).
    string? ManufacturerOverride,
    // Suffix appended to the human title inside the indexed content's "# " header that
    // must be stripped to recover the original title (Tilt Forums content is
    // "# {GameTitle} — Rulesheet"; the sync verb stored just "{GameTitle}"). Null for
    // sources whose content header is exactly the title.
    string? ContentTitleSuffixToStrip);

public static class SynthesizedSourceDescriptors
{
    public static readonly SynthesizedSourceDescriptor Kineticist = new(
        DocumentIdPrefix: "kineticist_",
        DiscoveryContext: "Kineticist Tutorial",
        DocumentType: DocumentType.Rulesheet,
        FileFormat: "md",
        ManufacturerOverride: null,
        ContentTitleSuffixToStrip: null);

    public static readonly SynthesizedSourceDescriptor TiltForums = new(
        DocumentIdPrefix: "tiltforums_",
        DiscoveryContext: "Tilt Forums Rulesheet",
        DocumentType: DocumentType.Rulesheet,
        FileFormat: "html",
        ManufacturerOverride: null,
        ContentTitleSuffixToStrip: " — Rulesheet");

    public static readonly SynthesizedSourceDescriptor Twip = new(
        DocumentIdPrefix: "twip_",
        DiscoveryContext: "TWIP Newsletter",
        DocumentType: DocumentType.NewsDigest,
        FileFormat: "html",
        ManufacturerOverride: "Kineticist",
        ContentTitleSuffixToStrip: null);

    public static readonly SynthesizedSourceDescriptor PbFreshdesk = new(
        DocumentIdPrefix: "pb_freshdesk_",
        DiscoveryContext: "Pinball Brothers Freshdesk Article",
        DocumentType: DocumentType.SupportArticle,
        FileFormat: "html",
        ManufacturerOverride: null,
        ContentTitleSuffixToStrip: null);

    public static readonly IReadOnlyList<SynthesizedSourceDescriptor> All =
        [Kineticist, TiltForums, Twip, PbFreshdesk];

    // Synthetic machine ids the synthesizers use for content that is NOT specific to
    // a single machine (TWIP weekly news → "pinball_news"; Pinball Brothers
    // General-category support articles → "pb_support", per Program.cs). A document
    // carrying one of these has no game reference — Game must stay null so the detail
    // page doesn't render a bogus "game" block for a newsletter or a general FAQ.
    public static readonly IReadOnlySet<string> NonMachineMachineIds =
        new HashSet<string>(StringComparer.Ordinal) { "pinball_news", "pb_support" };

    // Returns the descriptor whose prefix the id starts with, or null when the id is
    // not one of the four synthesized classes (e.g. a scraped "doc_" id).
    public static SynthesizedSourceDescriptor? ForDocumentId(string documentId) =>
        All.FirstOrDefault(d => documentId.StartsWith(d.DocumentIdPrefix, StringComparison.Ordinal));
}
