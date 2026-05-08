using Azure.Search.Documents.Indexes.Models;

namespace PinballWizard.Infrastructure.Rag.Indexing;

// `pinwiz-rag-v1` schema definition per ADR-0021. Pure data — no I/O,
// no Azure service calls. The indexer (W2-3) constructs the
// `SearchIndex` via `Build` and hands it to `SearchIndexClient`'s
// CreateOrUpdate flow on first run.
//
// Schema version is encoded in the index NAME (`pinwiz-rag-v1`,
// `…-v2`, …) rather than a field in the index, per the ADR's
// versioning strategy: schema-breaking changes spin up a new index
// and dual-read during cutover; this builder belongs to one specific
// schema version. A future v2 schema clones + amends this file under
// a new namespace + name; the retriever swaps which version it
// reads, and the old index is deleted after cutover stable.
//
// Shared field names live in `Retrieval.AiSearchIndexFields` (read
// side) — duplicating the constants here would invite drift, so the
// `internal` accessor on the retriever-side class is widened via
// `InternalsVisibleTo` (see csproj) instead.
internal static class AiSearchIndexSchema
{
    // 3072 matches `text-embedding-3-large` per ADR-0020. Held as a
    // const (not a config knob) because changing it is a v1→v2
    // schema cutover — the index can't be migrated in place when the
    // vector dimensionality changes.
    public const int EmbeddingDimensions = 3072;

    // HNSW vector profile name. Referenced by the `content_embedding`
    // field's `VectorSearchProfileName`; the profile in turn
    // references the algorithm-config name.
    public const string VectorProfileName = "pinwiz-rag-vector-profile-v1";
    public const string HnswAlgorithmConfigName = "pinwiz-rag-hnsw-v1";

    // Build the v1 `SearchIndex` for `indexName`. `semanticConfigName`
    // is parameterized so DI can pass the configured value (defaults
    // to `pinwiz-rag-semantic-v1` per `AiSearchOptions`); the index
    // name is similarly parameterized so a v1.5 / v2 cutover can use
    // a different name without modifying the schema body. Mirror the
    // ADR-0021 § Schema table column-for-column — adding a field here
    // requires updating the ADR table in the same PR.
    public static SearchIndex Build(string indexName, string semanticConfigName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(indexName);
        ArgumentException.ThrowIfNullOrWhiteSpace(semanticConfigName);

        var fields = new List<SearchField>
        {
            // chunk_id (key). Filterable so cleanup passes can target
            // specific keys; not searchable (it's a hash, search
            // engines don't tokenize hashes usefully).
            new(Retrieval.AiSearchIndexFields.ChunkId, SearchFieldDataType.String)
            {
                IsKey = true,
                IsFilterable = true,
            },

            // machine_id — facet + filter only. Not free-text searchable.
            new(Retrieval.AiSearchIndexFields.MachineId, SearchFieldDataType.String)
            {
                IsFilterable = true,
                IsFacetable = true,
            },

            // machine_title — searchable + filterable + facetable +
            // sortable. Semantic ranker uses this as a prioritized
            // keyword field; users frequently search by machine name.
            new(Retrieval.AiSearchIndexFields.MachineTitle, SearchFieldDataType.String)
            {
                IsSearchable = true,
                IsFilterable = true,
                IsFacetable = true,
                IsSortable = true,
            },

            // manufacturer — facet + filter + sortable. Not free-text
            // searchable; the machine_title carries the manufacturer
            // for keyword search anyway.
            new(Retrieval.AiSearchIndexFields.Manufacturer, SearchFieldDataType.String)
            {
                IsFilterable = true,
                IsFacetable = true,
                IsSortable = true,
            },

            // document_id — filter only.
            new(Retrieval.AiSearchIndexFields.DocumentId, SearchFieldDataType.String)
            {
                IsFilterable = true,
            },

            // document_url — pure projection; never filtered, never
            // searched. The Wizard renders it in citations.
            new(Retrieval.AiSearchIndexFields.DocumentUrl, SearchFieldDataType.String),

            // document_type — facet + filter. Sub-agent-aware retrieval
            // filters by manual / service_bulletin / metadata_card.
            new(Retrieval.AiSearchIndexFields.DocumentType, SearchFieldDataType.String)
            {
                IsFilterable = true,
                IsFacetable = true,
            },

            // page_start / page_end — filter + facet + sortable so a
            // future "give me the chunk on page 42" surface works.
            new(Retrieval.AiSearchIndexFields.PageStart, SearchFieldDataType.Int32)
            {
                IsFilterable = true,
                IsFacetable = true,
                IsSortable = true,
            },
            new(Retrieval.AiSearchIndexFields.PageEnd, SearchFieldDataType.Int32)
            {
                IsFilterable = true,
                IsFacetable = true,
                IsSortable = true,
            },

            // section_heading — searchable + filterable + facetable.
            // Semantic ranker's `title_field` per ADR-0021.
            new(Retrieval.AiSearchIndexFields.SectionHeading, SearchFieldDataType.String)
            {
                IsSearchable = true,
                IsFilterable = true,
                IsFacetable = true,
            },

            // content — searchable; not filterable (too large for
            // OData filters, defeats the index purpose).
            new(Retrieval.AiSearchIndexFields.Content, SearchFieldDataType.String)
            {
                IsSearchable = true,
            },

            // content_embedding — 3072-d vector field. Required at
            // index time, never projected at read time.
            new(
                Retrieval.AiSearchIndexFields.ContentEmbedding,
                SearchFieldDataType.Collection(SearchFieldDataType.Single))
            {
                IsSearchable = true,
                VectorSearchDimensions = EmbeddingDimensions,
                VectorSearchProfileName = VectorProfileName,
            },
        };

        var index = new SearchIndex(indexName, fields)
        {
            // Vector configuration: HNSW + cosine similarity per
            // ADR-0021. Cosine matches `text-embedding-3-large`'s
            // norm-invariant behavior. The HnswParameters left at
            // SDK defaults — m / efConstruction / efSearch defaults
            // are well-tuned for Basic SKU at curated-subset volume;
            // revisit at Phase 4.5 corpus scaling.
            VectorSearch = new VectorSearch
            {
                Profiles =
                {
                    new VectorSearchProfile(VectorProfileName, HnswAlgorithmConfigName),
                },
                Algorithms =
                {
                    new HnswAlgorithmConfiguration(HnswAlgorithmConfigName)
                    {
                        Parameters = new HnswParameters
                        {
                            Metric = VectorSearchAlgorithmMetric.Cosine,
                        },
                    },
                },
            },

            // Semantic search configuration per ADR-0021 § Semantic
            // ranker configuration. `prioritized_content_fields` =
            // [content]; `prioritized_keyword_fields` = [machine_title,
            // section_heading]; `title_field` = section_heading.
            SemanticSearch = new SemanticSearch
            {
                Configurations =
                {
                    new SemanticConfiguration(
                        semanticConfigName,
                        new SemanticPrioritizedFields
                        {
                            TitleField = new SemanticField(Retrieval.AiSearchIndexFields.SectionHeading),
                            ContentFields =
                            {
                                new SemanticField(Retrieval.AiSearchIndexFields.Content),
                            },
                            KeywordsFields =
                            {
                                new SemanticField(Retrieval.AiSearchIndexFields.MachineTitle),
                                new SemanticField(Retrieval.AiSearchIndexFields.SectionHeading),
                            },
                        }),
                },
            },
        };

        return index;
    }
}
