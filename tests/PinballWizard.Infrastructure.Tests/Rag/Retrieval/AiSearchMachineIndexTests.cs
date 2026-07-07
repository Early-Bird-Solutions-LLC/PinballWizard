using Azure.Search.Documents.Models;
using PinballWizard.Infrastructure.Rag.Indexing;
using PinballWizard.Infrastructure.Rag.Retrieval;
using Xunit;

namespace PinballWizard.Infrastructure.Tests.Rag.Retrieval;

// Behavior-asserting tests for AiSearchMachineIndex (ADR-0049 phase 2b).
// BuildSearchOptions and MapToHit are internal static — testable without a live
// SearchClient. End-to-end integration against the deployed pinwiz-machines-v1
// index lives in live tests gated by PINBALL_WIZARD_LIVE_MACHINE_INDEX_TESTS=1.
public sealed class AiSearchMachineIndexTests
{
    // ── BuildSearchOptions ────────────────────────────────────────────────────

    [Fact]
    public void BuildSearchOptions_QueryType_IsSimple()
    {
        // Simple mode is required for synonym map expansion (abbreviations,
        // common nicknames) and co-application with phonetic analysis.
        // QueryType.Full (Lucene) was evaluated and rejected — see AiSearchMachineIndex
        // class comment for the rationale.
        var options = AiSearchMachineIndex.BuildSearchOptions(top: 5, manufacturerKey: null);

        Assert.Equal(SearchQueryType.Simple, options.QueryType);
    }

    [Fact]
    public void BuildSearchOptions_ScoringProfile_IsMachineContentIntrinsic()
    {
        var options = AiSearchMachineIndex.BuildSearchOptions(top: 5, manufacturerKey: null);

        Assert.Equal(MachineSearchIndexSchema.ScoringProfileName, options.ScoringProfile);
    }

    [Fact]
    public void BuildSearchOptions_Size_MatchesTopParameter()
    {
        var options = AiSearchMachineIndex.BuildSearchOptions(top: 7, manufacturerKey: null);

        Assert.Equal(7, options.Size);
    }

    [Fact]
    public void BuildSearchOptions_SearchFields_ContainsTitlePrefixAndPhonetic()
    {
        // The three search fields provide: BM25 + synonyms (title),
        // edge-n-gram prefix (title_prefix), phonetic typo tolerance (title_phonetic).
        var options = AiSearchMachineIndex.BuildSearchOptions(top: 5, manufacturerKey: null);

        Assert.Contains(MachineSearchIndexFields.Title,        options.SearchFields);
        Assert.Contains(MachineSearchIndexFields.TitlePrefix,  options.SearchFields);
        Assert.Contains(MachineSearchIndexFields.TitlePhonetic, options.SearchFields);
    }

    [Fact]
    public void BuildSearchOptions_Select_ContainsAllRequiredFields()
    {
        // The Select list must include every field MachineSearchResultDocument
        // deserializes so MapToHit can project identity + grounding data.
        var options = AiSearchMachineIndex.BuildSearchOptions(top: 5, manufacturerKey: null);

        Assert.Contains(MachineSearchIndexFields.Id,             options.Select);
        Assert.Contains(MachineSearchIndexFields.Title,          options.Select);
        Assert.Contains(MachineSearchIndexFields.Manufacturer,   options.Select);
        Assert.Contains(MachineSearchIndexFields.ManufacturerKey, options.Select);
        Assert.Contains(MachineSearchIndexFields.GroupId,        options.Select);
        Assert.Contains(MachineSearchIndexFields.Year,           options.Select);
    }

    // ── MapToHit ─────────────────────────────────────────────────────────────

    [Fact]
    public void MapToHit_ValidDocument_ProjectsAllFields()
    {
        var doc = new MachineSearchResultDocument
        {
            Id              = "GRBN-MQR4P",
            Title           = "Godzilla",
            Manufacturer    = "Stern Pinball",
            ManufacturerKey = "stern",
            GroupId         = "GRBN",
            Year            = 2021,
        };

        var hit = AiSearchMachineIndex.MapToHit(doc, score: 0.95);

        Assert.NotNull(hit);
        Assert.Equal("GRBN-MQR4P",   hit.OpdbId);
        Assert.Equal("Godzilla",      hit.Title);
        Assert.Equal("Stern Pinball", hit.ManufacturerDisplayName);
        Assert.Equal("stern",         hit.ManufacturerKey);
        Assert.Equal("GRBN",          hit.GroupId);
        Assert.Equal(2021,            hit.Year);
        Assert.Equal(0.95,            hit.Score, precision: 9);
    }

    [Fact]
    public void MapToHit_NullGroupId_ProjectsNullGroupId()
    {
        // GroupId is optional in the index; a solo-title machine has no group.
        var doc = new MachineSearchResultDocument
        {
            Id              = "SOLO-001",
            Title           = "Solo Machine",
            Manufacturer    = "Spooky Pinball",
            ManufacturerKey = "spooky",
            GroupId         = null,
            Year            = null,
        };

        var hit = AiSearchMachineIndex.MapToHit(doc, score: 0.80);

        Assert.NotNull(hit);
        Assert.Null(hit.GroupId);
        Assert.Null(hit.Year);
    }

    [Fact]
    public void MapToHit_MissingId_ReturnsNull()
    {
        // A document with no Id cannot be grounded — return null rather
        // than surface an unresolvable OPDB ID to the caller.
        var doc = new MachineSearchResultDocument
        {
            Id              = string.Empty,
            ManufacturerKey = "stern",
        };

        var hit = AiSearchMachineIndex.MapToHit(doc, score: 0.9);

        Assert.Null(hit);
    }

    [Fact]
    public void MapToHit_MissingManufacturerKey_ReturnsNull()
    {
        // ManufacturerKey is the Cosmos partition key for the point-read
        // GetByOpdbIdAsync call. Without it the grounding tool cannot
        // fetch the machine record, so null is the correct response.
        var doc = new MachineSearchResultDocument
        {
            Id              = "GRBN-MQR4P",
            ManufacturerKey = string.Empty,
        };

        var hit = AiSearchMachineIndex.MapToHit(doc, score: 0.9);

        Assert.Null(hit);
    }
}
