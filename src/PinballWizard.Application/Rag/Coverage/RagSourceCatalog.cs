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
        new(IngestionSourceIds.Opdb, None, "meta_", None, ExpectedNonEmpty: true),
        new(IngestionSourceIds.Opdb, None, "overview_", None, ExpectedNonEmpty: true),

        // Scraped manufacturers — manufacturer value AND the doc_ prefix.
        new(IngestionSourceIds.Stern, ["Stern"], "doc_", None, true),
        new(IngestionSourceIds.Jjp, ["Jersey Jack"], "doc_", None, true),
        new(IngestionSourceIds.JjpSupportDocs, ["Jersey Jack"], "doc_", None, false),
        new(IngestionSourceIds.Ap, ["American Pinball"], "doc_", None, true),
        new(IngestionSourceIds.ApBulletins, ["American Pinball"], "doc_", None, false),
        new(IngestionSourceIds.Spooky, ["Spooky", "Spooky Pinball"], "doc_", None, true),
        new(IngestionSourceIds.SpookySupport, ["Spooky", "Spooky Pinball"], "doc_", None, false),
        new(IngestionSourceIds.PinballBrothers, ["Pinball Brothers"], "doc_", None, true),
        new(IngestionSourceIds.PinballBrothersDocuments, ["Pinball Brothers"], "doc_", None, false),
        new(IngestionSourceIds.BarrelsOfFun, ["Barrels of Fun"], "doc_", None, false),
        new(IngestionSourceIds.Multimorphic, ["Multimorphic"], "doc_", None, false),
        new(IngestionSourceIds.Cgc, ["Chicago Gaming", "Chicago Gaming Company"], "doc_", None, true),

        // Synthesized sources — identified by document_id prefix only.
        new(IngestionSourceIds.Kineticist, None, SynthesizedSourceDescriptors.Kineticist.DocumentIdPrefix, None, true),
        new(IngestionSourceIds.TiltForumsRulesheets, None, SynthesizedSourceDescriptors.TiltForums.DocumentIdPrefix, None, true),
        new(IngestionSourceIds.Twip, None, SynthesizedSourceDescriptors.Twip.DocumentIdPrefix, ["pinball_news"], false),
        new(IngestionSourceIds.PinballBrothersFreshdesk, None, SynthesizedSourceDescriptors.PbFreshdesk.DocumentIdPrefix, ["pb_support"], false),
        new(IngestionSourceIds.MultimorphicP3Sdk, None, "p3sdk_", None, false),
    ];
}
