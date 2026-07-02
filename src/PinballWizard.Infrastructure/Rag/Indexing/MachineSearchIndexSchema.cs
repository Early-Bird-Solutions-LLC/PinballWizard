using Azure.Search.Documents.Indexes.Models;

namespace PinballWizard.Infrastructure.Rag.Indexing;

// ADR-0049 phase 2a: machine findability index schema. Index name from
// AiSearchOptions.MachineIndexName (default "pinwiz-machines-v1").
//
// Design rationale for the three title variants:
//   "title"          — standard analyzer: BM25 + synonyms + semantic ranker.
//                      This is the primary relevance field.
//   "title_prefix"   — custom edge-n-gram (minGram=2, maxGram=25): emits
//                      n-grams at INDEX time so standard prefix queries and
//                      typeahead work without SEARCH="ba*" syntax. Without
//                      it, the default standard analyzer destroys partial-
//                      token structure.
//   "title_phonetic" — custom doubleMetaphone: indexes phonetic codes so
//                      "Medievel Madness" (typo) still matches
//                      "Medieval Madness". DoubleMetaphone is the dual-code
//                      variant (two codes per token) for higher recall.
//
// Scoring profile "machine-content-intrinsic":
//   magnitude(completeness, boost=2)  — canonical/complete records rank higher
//   freshness(last_updated_utc, boost=1.5) — more recently synced records rank higher
//   These are calibration baselines — tunable once Phase 2b A/B data accumulates.
//   Both fields are filterable (mandatory requirement for scoring profile functions).
//
// Synonym map "pinwiz-machine-synonyms-v1" is created separately (requires a
// CreateOrUpdateSynonymMap call) and attached to "title" and "title_prefix".
// The synonyms file is seeded from data/seeds/machine_synonyms.v1.txt.
//
// Schema versioning: index name encodes the version (pinwiz-machines-v1).
// A schema-breaking change spins up -v2, dual-reads during cutover, then
// drops -v1 — same pattern as the corpus index (ADR-0021).
internal static class MachineSearchIndexSchema
{
    // Custom analyzer names defined on this index
    internal const string EdgeNGramAnalyzerName   = "pinwiz-machines-edgengram-v1";
    internal const string PhoneticAnalyzerName    = "pinwiz-machines-phonetic-v1";

    // Custom token filter names
    internal const string EdgeNGramFilterName     = "pinwiz-machines-edgengram-filter-v1";
    internal const string PhoneticFilterName      = "pinwiz-machines-phonetic-filter-v1";

    // Scoring profile name
    internal const string ScoringProfileName      = "machine-content-intrinsic";

    // Synonym map name — created separately via SearchIndexClient, attached to fields
    internal const string SynonymMapName          = "pinwiz-machine-synonyms-v1";

    public static SearchIndex Build(string indexName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(indexName);

        var fields = new List<SearchField>
        {
            // id — OPDB canonical ID; the key field. Filterable so Phase 2b
            // can fetch by specific OPDB ID (point-read equivalent in Search).
            new(MachineSearchIndexFields.Id, SearchFieldDataType.String)
            {
                IsKey        = true,
                IsFilterable = true,
            },

            // title — standard analyzer (BM25). The primary relevance field
            // that semantic ranker and synonym expansion operate on. Sortable
            // + filterable for admin queries by exact title; facetable so the
            // frontend can bucket results by exact title match.
            // Synonym map attached so abbreviations expand at query time.
            new(MachineSearchIndexFields.Title, SearchFieldDataType.String)
            {
                IsSearchable  = true,
                IsFilterable  = true,
                IsSortable    = true,
                IsFacetable   = false,  // title has too many distinct values for a useful facet
                AnalyzerName  = LexicalAnalyzerName.StandardLucene,
                SynonymMapNames = { SynonymMapName },
            },

            // title_prefix — edge-n-gram analyzer. Emits n-grams 2..25 chars
            // at index time so prefix queries and typeahead work without
            // wildcard syntax. Not filterable/sortable (the standard "title"
            // field covers those use-cases). Synonym map attached so
            // abbreviations also expand on prefix field.
            new(MachineSearchIndexFields.TitlePrefix, SearchFieldDataType.String)
            {
                IsSearchable  = true,
                AnalyzerName  = new LexicalAnalyzerName(EdgeNGramAnalyzerName),
                SynonymMapNames = { SynonymMapName },
            },

            // title_phonetic — doubleMetaphone phonetic analyzer. Indexes
            // phonetic codes so typos/homophones surface. Not filterable/
            // sortable (the "title" field handles those). No synonym map —
            // phonetic expansion + synonym expansion would double-compound;
            // the phonetic field is orthogonal to abbreviation lookup.
            new(MachineSearchIndexFields.TitlePhonetic, SearchFieldDataType.String)
            {
                IsSearchable  = true,
                AnalyzerName  = new LexicalAnalyzerName(PhoneticAnalyzerName),
            },

            // manufacturer — display name (e.g. "Stern Pinball"). Searchable
            // for free-text queries; filterable + facetable for narrowing.
            new(MachineSearchIndexFields.Manufacturer, SearchFieldDataType.String)
            {
                IsSearchable  = true,
                IsFilterable  = true,
                IsFacetable   = true,
            },

            // manufacturer_key — partition-key form (e.g. "stern"). Filterable
            // only — used as an equality filter in Phase 2b queries, not searched.
            new(MachineSearchIndexFields.ManufacturerKey, SearchFieldDataType.String)
            {
                IsFilterable = true,
            },

            // designers — Collection(String). Empty in today's OPDB data (issue
            // #611) but modeled now so the field exists when data arrives.
            // Searchable for name-based designer queries.
            new(MachineSearchIndexFields.Designers,
                SearchFieldDataType.Collection(SearchFieldDataType.String))
            {
                IsSearchable  = true,
            },

            // themes — Collection(String) from OPDB. Searchable + facetable so
            // users can filter "show me horror-themed machines".
            new(MachineSearchIndexFields.Themes,
                SearchFieldDataType.Collection(SearchFieldDataType.String))
            {
                IsSearchable  = true,
                IsFilterable  = true,
                IsFacetable   = true,
            },

            // year — Int32. Filterable + sortable for "machines from 2024" or
            // sort-by-year queries. NOT searchable (numeric, text engine is wrong tool).
            new(MachineSearchIndexFields.Year, SearchFieldDataType.Int32)
            {
                IsFilterable = true,
                IsSortable   = true,
            },

            // group_id — leading OPDB segment (e.g. "GweeP" for all Godzilla
            // editions). Filterable so Phase 2b can pull sibling editions by
            // group (mirrors the Cosmos GetSiblingsByGroupIdAsync use-case but
            // served from the fast search tier).
            new(MachineSearchIndexFields.GroupId, SearchFieldDataType.String)
            {
                IsFilterable = true,
            },

            // edition_label — e.g. "Pro", "Premium/LE". Searchable + filterable
            // so edition-qualified queries (Phase 2b) can narrow to the right base.
            new(MachineSearchIndexFields.EditionLabel, SearchFieldDataType.String)
            {
                IsSearchable  = true,
                IsFilterable  = true,
            },

            // completeness — proportion of data-quality signals present [0.0, 1.0].
            // MUST be filterable: scoring profile magnitude functions require it.
            // NOT searchable (numeric scalar, not free text).
            new(MachineSearchIndexFields.Completeness, SearchFieldDataType.Double)
            {
                IsFilterable = true,
                IsSortable   = true,
            },

            // last_updated_utc — timestamp of most recent OPDB sync for this machine.
            // MUST be filterable: scoring profile freshness functions require it.
            // Sortable for explicit freshness-sort queries.
            new(MachineSearchIndexFields.LastUpdatedUtc, SearchFieldDataType.DateTimeOffset)
            {
                IsFilterable = true,
                IsSortable   = true,
            },
        };

        var index = new SearchIndex(indexName, fields)
        {
            // ── Custom token filters ────────────────────────────────────────────
            //
            // EdgeNGramTokenFilter emits n-grams at index time for prefix/typeahead
            // matching. minGram=2 gives "me", "med", …; maxGram=25 covers the
            // longest pinball titles. Side defaults to "front" (generate from
            // token start), which is correct for typeahead use.
            //
            // PhoneticTokenFilter with DoubleMetaphone encodes each token into two
            // phonetic codes (the "double" in doubleMetaphone). Higher recall than
            // single-code Metaphone for English machine names with variant spellings.
            TokenFilters =
            {
                // EdgeNGramTokenFilter (the SDK's single edge-n-gram token filter class
                // in v12; supports minGram up to 300 per the Azure REST spec). minGram=2
                // gives "me", "med", …; maxGram=25 covers the longest pinball titles
                // without bloating the index. Side defaults to "front" (generate from
                // the start of the token), which is the correct side for typeahead.
                new EdgeNGramTokenFilter(EdgeNGramFilterName)
                {
                    MinGram = 2,
                    MaxGram = 25,
                },
                new PhoneticTokenFilter(PhoneticFilterName)
                {
                    Encoder = PhoneticEncoder.DoubleMetaphone,
                    // ReplaceOriginalTokens=true: store ONLY the phonetic code, not
                    // the original token, so keyword searches fall through to the
                    // standard "title" field while the phonetic field covers
                    // sound-alikes. Setting this false would create collisions
                    // between exact-match and phonetic-match weights.
                    ReplaceOriginalTokens = true,
                },
            },

            // ── Custom analyzers ────────────────────────────────────────────────
            //
            // Both analyzers use the standard lucene tokenizer (whitespace + lower-
            // case + punctuation splitting) as the front-end so tokenization is
            // identical to the "title" field — only the token filtering step differs.
            Analyzers =
            {
                // Edge-n-gram: standard tokenizer → lowercase → edge-n-gram filter.
                // Lowercase BEFORE n-gram so "The" and "the" produce the same n-grams.
                new CustomAnalyzer(EdgeNGramAnalyzerName, LexicalTokenizerName.Standard)
                {
                    TokenFilters =
                    {
                        TokenFilterName.Lowercase,
                        new TokenFilterName(EdgeNGramFilterName),
                    },
                },

                // Phonetic: standard tokenizer → lowercase → doubleMetaphone filter.
                new CustomAnalyzer(PhoneticAnalyzerName, LexicalTokenizerName.Standard)
                {
                    TokenFilters =
                    {
                        TokenFilterName.Lowercase,
                        new TokenFilterName(PhoneticFilterName),
                    },
                },
            },

            // ── Scoring profile ─────────────────────────────────────────────────
            //
            // "machine-content-intrinsic" uses only data-intrinsic signals (no
            // engagement metrics — ADR-0049 explicitly excludes click/view counts).
            //
            // Calibration baselines (adjust after Phase 2b A/B data):
            //   magnitude boost=2.0 on completeness: a fully-complete record
            //     (completeness=1.0) scores 2× a zero-completeness record.
            //     MagnitudeScoringParameters.BoostingRangeStart=0, .End=1 maps
            //     the [0,1] domain correctly; ConstantBoostBeyondRange=true pins
            //     any completeness >1.0 to the same boost ceiling.
            //   freshness boost=1.5 on last_updated_utc: a machine updated today
            //     gets a 1.5× multiplier decaying toward 1.0 over BoostingDuration.
            //     60-day half-life (P60DT default) is tunable.
            ScoringProfiles =
            {
                new ScoringProfile(ScoringProfileName)
                {
                    Functions =
                    {
                        new MagnitudeScoringFunction(
                            fieldName: MachineSearchIndexFields.Completeness,
                            boost: 2.0,
                            parameters: new MagnitudeScoringParameters(
                                boostingRangeStart: 0,
                                boostingRangeEnd: 1)
                            {
                                // Pin any completeness value outside [0,1] to the
                                // nearest endpoint rather than interpolating past it.
                                ShouldBoostBeyondRangeByConstant = true,
                            }),

                        new FreshnessScoringFunction(
                            fieldName: MachineSearchIndexFields.LastUpdatedUtc,
                            boost: 1.5,
                            parameters: new FreshnessScoringParameters(
                                boostingDuration: TimeSpan.FromDays(60))),
                    },
                    // Linear interpolation — constant score within the boosting
                    // range, tapering to the floor outside it. Simpler than
                    // quadratic for the initial calibration pass.
                    FunctionAggregation = ScoringFunctionAggregation.Sum,
                },
            },
        };

        return index;
    }
}
