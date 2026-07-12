using Azure.Search.Documents.Indexes.Models;
using PinballWizard.Infrastructure.Rag.Indexing;
using Xunit;

namespace PinballWizard.Infrastructure.Tests.Rag.Indexing;

// Behavior-asserting tests for the ADR-0049 machine findability index schema.
// Each test pins a specific design decision: field attributes, analyzer
// assignments, scoring profile configuration, or synonym map attachment.
// Adding a field to the schema requires either extending one of these tests
// or adding a new one. Drift here means the live index and the ADR disagree.
public sealed class MachineSearchIndexSchemaTests
{
    private const string IndexName = "pinwiz-machines-v1";

    private static SearchIndex Build() =>
        MachineSearchIndexSchema.Build(IndexName);

    [Fact]
    public void Build_NullIndexName_Throws()
    {
        // ArgumentException.ThrowIfNullOrWhiteSpace surfaces null as
        // ArgumentNullException (subclass) — ThrowsAny covers both branches.
        Assert.ThrowsAny<ArgumentException>(() =>
            MachineSearchIndexSchema.Build(indexName: null!));
    }

    [Fact]
    public void Build_WhiteSpaceIndexName_Throws()
    {
        Assert.Throws<ArgumentException>(() =>
            MachineSearchIndexSchema.Build(indexName: "   "));
    }

    [Fact]
    public void Build_PreservesIndexName()
    {
        var index = Build();
        Assert.Equal(IndexName, index.Name);
    }

    [Fact]
    public void Build_DeclaresAllAdrSchemaFields()
    {
        var index = Build();
        var fieldNames = index.Fields.Select(f => f.Name).ToHashSet();

        // Mirror ADR-0049 § Schema row-by-row. Adding a row to the ADR
        // table without updating this assertion means the field exists in
        // code but not in the schema lock-test.
        Assert.Contains(MachineSearchIndexFields.Id,              fieldNames);
        Assert.Contains(MachineSearchIndexFields.Title,           fieldNames);
        Assert.Contains(MachineSearchIndexFields.TitlePrefix,     fieldNames);
        Assert.Contains(MachineSearchIndexFields.TitlePhonetic,   fieldNames);
        Assert.Contains(MachineSearchIndexFields.Manufacturer,    fieldNames);
        Assert.Contains(MachineSearchIndexFields.ManufacturerKey, fieldNames);
        Assert.Contains(MachineSearchIndexFields.Designers,       fieldNames);
        Assert.Contains(MachineSearchIndexFields.Themes,          fieldNames);
        Assert.Contains(MachineSearchIndexFields.Year,            fieldNames);
        Assert.Contains(MachineSearchIndexFields.GroupId,         fieldNames);
        Assert.Contains(MachineSearchIndexFields.EditionLabel,    fieldNames);
        Assert.Contains(MachineSearchIndexFields.Completeness,    fieldNames);
        Assert.Contains(MachineSearchIndexFields.LastUpdatedUtc,  fieldNames);
    }

    [Fact]
    public void Build_IdIsKeyAndFilterable()
    {
        var index = Build();
        var id = index.Fields.Single(f => f.Name == MachineSearchIndexFields.Id);
        Assert.True(id.IsKey);
        Assert.True(id.IsFilterable);
    }

    [Fact]
    public void Build_TitleUsesAsciiFoldingAnalyzerAndSynonymMap()
    {
        // ADR-0049 + diacritic-fold fix: "title" uses the custom asciifold analyzer
        // for BM25 + synonym expansion so "Pokemon" query matches "Pokémon" catalog
        // entry. Both index-time and query-time fold diacritics because AnalyzerName
        // is a single analyzer that applies to both sides. Must be searchable +
        // filterable + sortable.
        var index = Build();
        var title = index.Fields.Single(f => f.Name == MachineSearchIndexFields.Title);
        Assert.True(title.IsSearchable);
        Assert.True(title.IsFilterable);
        Assert.True(title.IsSortable);
        Assert.Equal(
            new LexicalAnalyzerName(MachineSearchIndexSchema.AsciiFoldingAnalyzerName),
            title.AnalyzerName);
        // Single-analyzer path (not split) — so the one asciifold analyzer folds BOTH
        // index-time and query-time. If either split slot were set, AnalyzerName would
        // be ignored and one side could stop folding (the title_prefix test asserts the
        // mirror image: split slots set, AnalyzerName null).
        Assert.Null(title.IndexAnalyzerName);
        Assert.Null(title.SearchAnalyzerName);
        Assert.Contains(MachineSearchIndexSchema.SynonymMapName, title.SynonymMapNames);
    }

    [Fact]
    public void Build_TitlePrefixUsesSplitAsciiFoldIndexAndSearchAnalyzerWithoutSynonymMap()
    {
        // ADR-0049 + diacritic-fold fix: "title_prefix" uses split index/search analyzers.
        // At INDEX time: edge-n-gram+asciifold (folds diacritics BEFORE emitting n-grams
        //   so "Pokémon" produces n-grams "po","pok","poke",…,"pokemon" in the index).
        // At SEARCH time: asciifold (folds "Pokemon" → "pokemon"; does NOT apply edgengram
        //   at query time — that would n-gram the user's input and cause false positives).
        // No synonym map: abbreviation expansion is the "title" field's job.
        var index = Build();
        var titlePrefix = index.Fields.Single(f => f.Name == MachineSearchIndexFields.TitlePrefix);
        Assert.True(titlePrefix.IsSearchable);
        // Split analyzer pair — AnalyzerName must be null (single-analyzer path unused)
        Assert.Null(titlePrefix.AnalyzerName);
        Assert.Equal(
            new LexicalAnalyzerName(MachineSearchIndexSchema.EdgeNGramAsciiFoldAnalyzerName),
            titlePrefix.IndexAnalyzerName);
        Assert.Equal(
            new LexicalAnalyzerName(MachineSearchIndexSchema.AsciiFoldingAnalyzerName),
            titlePrefix.SearchAnalyzerName);
        // No synonym map on the prefix field
        Assert.Empty(titlePrefix.SynonymMapNames);
    }

    [Fact]
    public void Build_TitlePhoneticUsesPhoneticAnalyzerWithoutSynonymMap()
    {
        // ADR-0049: "title_phonetic" uses the custom doubleMetaphone analyzer.
        // No synonym map — phonetic + synonym expansion would double-compound.
        var index = Build();
        var titlePhonetic = index.Fields.Single(f => f.Name == MachineSearchIndexFields.TitlePhonetic);
        Assert.True(titlePhonetic.IsSearchable);
        Assert.Equal(
            new LexicalAnalyzerName(MachineSearchIndexSchema.PhoneticAnalyzerName),
            titlePhonetic.AnalyzerName);
        Assert.Empty(titlePhonetic.SynonymMapNames);
    }

    [Fact]
    public void Build_ManufacturerIsSearchableFilterableFacetable()
    {
        var index = Build();
        var mfr = index.Fields.Single(f => f.Name == MachineSearchIndexFields.Manufacturer);
        Assert.True(mfr.IsSearchable);
        Assert.True(mfr.IsFilterable);
        Assert.True(mfr.IsFacetable);
    }

    [Fact]
    public void Build_ManufacturerKeyIsFilterableOnly()
    {
        // Partition-key form — equality filter only, not searched.
        var index = Build();
        var key = index.Fields.Single(f => f.Name == MachineSearchIndexFields.ManufacturerKey);
        Assert.True(key.IsFilterable);
        Assert.NotEqual(true, key.IsSearchable);
    }

    [Fact]
    public void Build_ThemesIsSearchableFilterableFacetable()
    {
        var index = Build();
        var themes = index.Fields.Single(f => f.Name == MachineSearchIndexFields.Themes);
        Assert.Equal(
            SearchFieldDataType.Collection(SearchFieldDataType.String),
            themes.Type);
        Assert.True(themes.IsSearchable);
        Assert.True(themes.IsFilterable);
        Assert.True(themes.IsFacetable);
    }

    [Fact]
    public void Build_DesignersIsSearchableCollection()
    {
        var index = Build();
        var designers = index.Fields.Single(f => f.Name == MachineSearchIndexFields.Designers);
        Assert.Equal(
            SearchFieldDataType.Collection(SearchFieldDataType.String),
            designers.Type);
        Assert.True(designers.IsSearchable);
    }

    [Fact]
    public void Build_YearIsInt32FilterableSortable()
    {
        var index = Build();
        var year = index.Fields.Single(f => f.Name == MachineSearchIndexFields.Year);
        Assert.Equal(SearchFieldDataType.Int32, year.Type);
        Assert.True(year.IsFilterable);
        Assert.True(year.IsSortable);
        Assert.NotEqual(true, year.IsSearchable);
    }

    [Fact]
    public void Build_CompletenessIsDoubleFilterableSortable()
    {
        // MUST be filterable — scoring profile magnitude functions require it.
        var index = Build();
        var completeness = index.Fields.Single(f => f.Name == MachineSearchIndexFields.Completeness);
        Assert.Equal(SearchFieldDataType.Double, completeness.Type);
        Assert.True(completeness.IsFilterable);
        Assert.True(completeness.IsSortable);
    }

    [Fact]
    public void Build_LastUpdatedUtcIsDateTimeOffsetFilterableSortable()
    {
        // MUST be filterable — scoring profile freshness functions require it.
        var index = Build();
        var field = index.Fields.Single(f => f.Name == MachineSearchIndexFields.LastUpdatedUtc);
        Assert.Equal(SearchFieldDataType.DateTimeOffset, field.Type);
        Assert.True(field.IsFilterable);
        Assert.True(field.IsSortable);
        Assert.NotEqual(true, field.IsSearchable);
    }

    [Fact]
    public void Build_HasScoringProfileWithCompletenessAndFreshnessFunctions()
    {
        // ADR-0049 scoring profile "machine-content-intrinsic" — two functions:
        // magnitude(completeness) and freshness(last_updated_utc).
        var index = Build();
        var profile = Assert.Single(index.ScoringProfiles);
        Assert.Equal(MachineSearchIndexSchema.ScoringProfileName, profile.Name);
        Assert.Equal(ScoringFunctionAggregation.Sum, profile.FunctionAggregation);

        var functions = profile.Functions.ToList();
        Assert.Equal(2, functions.Count);

        var magnitude = Assert.Single(functions.OfType<MagnitudeScoringFunction>());
        Assert.Equal(MachineSearchIndexFields.Completeness, magnitude.FieldName);
        Assert.Equal(2.0, magnitude.Boost);
        Assert.Equal(0, magnitude.Parameters.BoostingRangeStart);
        Assert.Equal(1, magnitude.Parameters.BoostingRangeEnd);
        // Pin any completeness value outside [0,1] to the nearest endpoint.
        Assert.True(magnitude.Parameters.ShouldBoostBeyondRangeByConstant);

        var freshness = Assert.Single(functions.OfType<FreshnessScoringFunction>());
        Assert.Equal(MachineSearchIndexFields.LastUpdatedUtc, freshness.FieldName);
        Assert.Equal(1.5, freshness.Boost);
        // 60-day boosting window — tunable after Phase 2b A/B data accumulates.
        Assert.Equal(TimeSpan.FromDays(60), freshness.Parameters.BoostingDuration);
    }

    [Fact]
    public void Build_HasAllFourCustomAnalyzers()
    {
        // All custom analyzers must be registered so the index API accepts the
        // schema — referencing an undefined analyzer name causes a 400.
        var index = Build();
        var analyzerNames = index.Analyzers.Select(a => a.Name).ToHashSet();
        Assert.Contains(MachineSearchIndexSchema.EdgeNGramAnalyzerName,        analyzerNames);
        Assert.Contains(MachineSearchIndexSchema.PhoneticAnalyzerName,         analyzerNames);
        Assert.Contains(MachineSearchIndexSchema.AsciiFoldingAnalyzerName,     analyzerNames);
        Assert.Contains(MachineSearchIndexSchema.EdgeNGramAsciiFoldAnalyzerName, analyzerNames);
    }

    [Fact]
    public void Build_AsciiFoldingAnalyzerUsesLowercaseThenAsciiFoldingFilters()
    {
        // The diacritic-fold fix relies on this exact filter chain:
        //   lowercase (before fold, so "É" → "é" before "é" → "e")
        //   asciifolding (strips accents so "é" → "e")
        // Applied to "title" field (both index+query) and the search side of
        // "title_prefix" so "Pokemon" ↔ "Pokémon" match at both layers.
        var index = Build();
        var analyzer = index.Analyzers
            .OfType<CustomAnalyzer>()
            .Single(a => a.Name == MachineSearchIndexSchema.AsciiFoldingAnalyzerName);
        Assert.Equal(LexicalTokenizerName.Standard, analyzer.TokenizerName);
        var filters = analyzer.TokenFilters.ToList();
        Assert.Equal(2, filters.Count);
        Assert.Equal(TokenFilterName.Lowercase,     filters[0]);
        Assert.Equal(TokenFilterName.AsciiFolding,  filters[1]);
    }

    [Fact]
    public void Build_EdgeNGramAsciiFoldAnalyzerUsesLowercaseAsciiFoldThenEdgeNGramFilters()
    {
        // The index-side analyzer for "title_prefix" must fold diacritics BEFORE
        // generating n-grams: "Pokémon" → lowercase → "pokémon" → asciifold →
        // "pokemon" → n-grams → "po","pok","poke",…,"pokemon". Without the
        // asciifold step before edgengram the n-grams carry accented characters
        // and a query "Pokemon" → "pokemon" would not match them.
        var index = Build();
        var analyzer = index.Analyzers
            .OfType<CustomAnalyzer>()
            .Single(a => a.Name == MachineSearchIndexSchema.EdgeNGramAsciiFoldAnalyzerName);
        Assert.Equal(LexicalTokenizerName.Standard, analyzer.TokenizerName);
        var filters = analyzer.TokenFilters.ToList();
        Assert.Equal(3, filters.Count);
        Assert.Equal(TokenFilterName.Lowercase,                          filters[0]);
        Assert.Equal(TokenFilterName.AsciiFolding,                       filters[1]);
        Assert.Equal(new TokenFilterName(MachineSearchIndexSchema.EdgeNGramFilterName), filters[2]);
    }

    [Fact]
    public void Build_EdgeNGramFilterHasCorrectMinMaxGramAndFrontSide()
    {
        // minGram=2 / maxGram=25 — covers shortest meaningful prefix ("ba")
        // to longest pinball title without bloating the index.
        // Side=Front is explicit (the default, but stated in schema so intent
        // is unambiguous: n-grams generate from the START of each token for
        // typeahead, not from the end).
        var index = Build();
        var filter = index.TokenFilters
            .OfType<EdgeNGramTokenFilter>()
            .Single(f => f.Name == MachineSearchIndexSchema.EdgeNGramFilterName);
        Assert.Equal(2, filter.MinGram);
        Assert.Equal(25, filter.MaxGram);
        Assert.Equal(EdgeNGramTokenFilterSide.Front, filter.Side);
    }

    [Fact]
    public void Build_PhoneticFilterUsesDoubleMetaphone()
    {
        var index = Build();
        var filter = index.TokenFilters
            .OfType<PhoneticTokenFilter>()
            .Single(f => f.Name == MachineSearchIndexSchema.PhoneticFilterName);
        Assert.Equal(PhoneticEncoder.DoubleMetaphone, filter.Encoder);
        // ReplaceOriginalTokens=true: store ONLY the phonetic code so keyword
        // searches route to the standard "title" field.
        Assert.Equal(true, filter.ReplaceOriginalTokens);
    }
}
