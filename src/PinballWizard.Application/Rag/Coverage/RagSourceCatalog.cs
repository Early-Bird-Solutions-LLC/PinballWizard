using PinballWizard.Application.Documents;
using PinballWizard.Application.Persistence;

namespace PinballWizard.Application.Rag.Coverage;

// Authoritative registry of RAG-indexed sources for the corpus-coverage probe.
// Kept next to IngestionSourceIds + SynthesizedSourceDescriptors; the contract
// test (RagSourceCatalogTests) fails if a new IngestionSourceId is added without
// a RagSource here. ExpectedNonEmpty=false marks sources that are wired but may
// legitimately have no content yet (their empty state is reported, not a gap).
public static class RagSourceCatalog
{
    private static readonly string[] None = [];

    public static readonly IReadOnlyList<RagSource> All =
    [
        // OPDB is not a scraped-doc source; its indexed content is the
        // per-machine synthesized metadata cards (meta_) and game overviews
        // (overview_). Represent those two synthesized classes as their own
        // sources keyed off the Opdb id.
        new(IngestionSourceIds.Opdb, None, "meta_", ExpectedNonEmpty: true),
        new(IngestionSourceIds.Opdb, None, "overview_", ExpectedNonEmpty: true),

        // Scraped manufacturers — manufacturer value AND the doc_ prefix.
        // Sub-doc sources (JjpSupportDocs, ApBulletins, SpookySupport, PinballBrothersDocuments)
        // are omitted: their chunks carry the parent manufacturer value + doc_ prefix, making
        // them indistinguishable from the parent RagSource in the index. They are covered by
        // the parent entry and excluded from the drift-guard test accordingly.
        new(IngestionSourceIds.Stern, ["Stern"], "doc_", true),
        new(IngestionSourceIds.Jjp, ["Jersey Jack Pinball"], "doc_", true),
        new(IngestionSourceIds.Ap, ["American Pinball"], "doc_", true),
        new(IngestionSourceIds.Spooky, ["Spooky", "Spooky Pinball"], "doc_", true),
        new(IngestionSourceIds.PinballBrothers, ["Pinball Brothers"], "doc_", true),
        new(IngestionSourceIds.BarrelsOfFun, ["Barrels of Fun"], "doc_", false),
        new(IngestionSourceIds.Multimorphic, ["Multimorphic"], "doc_", false),
        new(IngestionSourceIds.Cgc, ["Chicago Gaming", "Chicago Gaming Company"], "doc_", true),

        // Synthesized sources — identified by document_id prefix only.
        // TWIP and PB Freshdesk are recognised solely by their document_id prefix
        // (twip_ / pb_freshdesk_); the former MachineIdSentinels were never used
        // by Matches or BuildSourceFilter and have been removed (YAGNI).
        new(IngestionSourceIds.Kineticist, None, SynthesizedSourceDescriptors.Kineticist.DocumentIdPrefix, true),
        new(IngestionSourceIds.TiltForumsRulesheets, None, SynthesizedSourceDescriptors.TiltForums.DocumentIdPrefix, true),
        new(IngestionSourceIds.Twip, None, SynthesizedSourceDescriptors.Twip.DocumentIdPrefix, false),
        new(IngestionSourceIds.PinballBrothersFreshdesk, None, SynthesizedSourceDescriptors.PbFreshdesk.DocumentIdPrefix, false),
        new(IngestionSourceIds.MultimorphicP3Sdk, None, "p3sdk_", false),
    ];
}
