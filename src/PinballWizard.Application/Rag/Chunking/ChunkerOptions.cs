using System.ComponentModel.DataAnnotations;

namespace PinballWizard.Application.Rag.Chunking;

// Configuration for the Phase 4 hybrid chunker (ADR-0019). Defaults
// match the ADR — 512-token target, ~10% overlap — so a vanilla DI
// registration with no Configure call produces correct chunks on the
// curated 7-machine subset. Override-from-config is wired so H3
// calibration can tune without code changes.
public sealed class ChunkerOptions
{
    public const string SectionName = "Rag:Chunker";

    // Target token budget per chunk. ADR-0019 § Algorithm step 3 pins
    // ~512; the embedding model (`text-embedding-3-large` per ADR-0020)
    // accepts up to 8191 tokens but retrieval quality is best with
    // smaller, focused chunks. Heading-prefix tokens (~5–15 per chunk
    // when ApplyHeadingPrefix is true) aren't subtracted from this
    // budget — slop is bounded by the prefix length and the model
    // tolerates the overage easily. Calibration in H3 may move this.
    [Range(64, 2048)]
    public int TargetTokens { get; set; } = 512;

    // Overlap between consecutive chunks within the same section.
    // ADR-0019 § Algorithm step 3 pins ~10% (51 tokens at TargetTokens=512).
    // Overlap mitigates the "fact straddles a chunk boundary" failure
    // mode at retrieval time. Higher overlap = more redundancy = more
    // index bloat; lower = cleaner index but worse boundary-recall.
    [Range(0, 256)]
    public int OverlapTokens { get; set; } = 51;

    // Minimum number of pages required before the header/footer
    // detector engages. With fewer pages there isn't enough signal
    // to distinguish boilerplate (repeating page header) from content
    // (a single-section bulletin whose first line happens to repeat
    // its title). Service bulletins (typically 1–2 pages) skip
    // detection entirely; manuals (typically ≥10 pages) trigger it.
    [Range(2, 20)]
    public int HeaderFooterMinPages { get; set; } = 3;

    // Fraction of pages on which the same first-line (or last-line)
    // text must appear to be classified as a repeating header
    // (or footer) and stripped before chunking. 0.5 = ">50% of
    // pages" — safe default that catches Stern's running headers
    // ("STERN PINBALL — GODZILLA OPERATING MANUAL") without
    // false-positiving on a chapter title that happens to lead two
    // pages. Per the customer-delight refinement: contaminated
    // citation snippets read amateurish even when retrieval is
    // correct, so this strip is a showcase polish gate as much as a
    // retrieval optimization.
    [Range(0.1, 1.0)]
    public double HeaderFooterRepeatThreshold { get; set; } = 0.5;

    // When true (default), prepends the section heading to each
    // chunk's text as a markdown H2 (`## Heading\n\n…`). The heading
    // also lives in `Chunk.SectionHeading` for the citation surface;
    // duplicating it inside the text gives the embedding model
    // additional lexical signal — improves retrieval on
    // heading-anchored queries ("how does Foo Mode work?") since the
    // section name carries vocabulary that may not appear verbatim in
    // the section body. ~5–15 token overhead per chunk; flagged so
    // ablation studies in H3 can compare with/without.
    public bool ApplyHeadingPrefix { get; set; } = true;

    // Minimum size for the FINAL chunk of a section. The window steps by
    // (TargetTokens − OverlapTokens), so a section whose token count
    // lands just past a step boundary emits a trailing window that is
    // mostly — sometimes entirely — overlap already carried by its
    // predecessor. At the defaults (step 461) a 974-token section yields
    // a 52-token tail of which 51 duplicate the previous chunk: one
    // novel token, but a full embedding call and a top-k retrieval slot
    // contested on a lopsided vector. When the trailing window falls
    // below this floor it is folded into the preceding chunk of the same
    // section instead of being emitted.
    //
    // Applies only to a section's trailing window. A section whose
    // ENTIRE content is below the floor still emits one small chunk —
    // that text is genuinely short, not duplicated, and merging it would
    // require crossing a section boundary and corrupting SectionHeading.
    //
    // Consequence: a merged chunk may exceed TargetTokens by up to
    // (MinTrailingChunkTokens − 1). `text-embedding-3-large` accepts 8191
    // tokens (ADR-0020), so the overage is immaterial to embedding; it is
    // a deliberate trade of a slightly-over-target chunk for the removal
    // of a near-duplicate one. Values ≥ TargetTokens are clamped at use.
    //
    // Set to 0 to disable (restores pre-refinement behavior) so H3
    // calibration can ablate it like the other refinements.
    //
    // The Range cap is deliberately below the largest legal TargetTokens
    // (2048): a floor above 1024 would be merging half a maximum-size
    // window, which is a chunking-strategy change rather than a
    // degenerate-tail guard. The runtime clamp handles the remainder.
    [Range(0, 1024)]
    public int MinTrailingChunkTokens { get; set; } = 128;

    // When true (default), service bulletins are treated as a single
    // section regardless of outline. Bulletins are short, single-issue
    // documents whose sub-headings (Symptom / Cause / Resolution)
    // over-fragment retrieval if treated as section boundaries — a
    // user querying "X symptom" wants to retrieve the whole bulletin,
    // not just the Symptom paragraph. The sub-headings remain in
    // chunk text. ADR-0019 § Algorithm step 4 (small-section handling)
    // covered the static case; this option covers the document-type
    // policy. Set false to fall back to ADR-0019's strict
    // section-bounded chunking for bulletins.
    public bool BulletinTreatAsSingleSection { get; set; } = true;
}
