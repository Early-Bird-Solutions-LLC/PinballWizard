using PinballWizard.Infrastructure.Rag.Indexing;
using Xunit;

namespace PinballWizard.Infrastructure.Tests.Rag.Indexing;

// Behavior-asserting tests for the empty-synonym guard in
// MachineSearchIndexBootstrapper (ADR-0049 phase 2a).
//
// The guard (ShouldUpsertSynonymMap) is exposed as an internal static method
// so it can be tested without a live SearchIndexClient — same pattern as
// MachineSearchIndexProjector.ComputeCompleteness. Testing it in isolation
// avoids the need for a mock or test-double of the Azure SDK client.
public sealed class MachineSearchIndexBootstrapperTests
{
    // ── Empty-synonym guard (⚠️ 3 local-review finding) ─────────────────────

    [Fact]
    public void ShouldUpsertSynonymMap_EmptyString_ReturnsFalse()
    {
        // AI Search rejects an empty synonym map body with 400; an empty seed
        // file (or a missing one) must NOT trigger the upsert call.
        Assert.False(MachineSearchIndexBootstrapper.ShouldUpsertSynonymMap(string.Empty));
    }

    [Fact]
    public void ShouldUpsertSynonymMap_WhitespaceOnly_ReturnsFalse()
    {
        // A file that contains only whitespace is functionally empty.
        Assert.False(MachineSearchIndexBootstrapper.ShouldUpsertSynonymMap("   \t\n  "));
    }

    [Fact]
    public void ShouldUpsertSynonymMap_NullInput_ReturnsFalse()
    {
        // Null is treated as empty (the caller passes string.Empty when the
        // seed file is absent, but defensive null-tolerance matches the guard
        // signature).
        Assert.False(MachineSearchIndexBootstrapper.ShouldUpsertSynonymMap(null));
    }

    [Fact]
    public void ShouldUpsertSynonymMap_ValidSynonymLines_ReturnsTrue()
    {
        // A non-empty synonym text (matching the production seed format) must
        // pass the guard so the upsert proceeds.
        const string synonyms = "mm, medieval madness\nafm, attack from mars";
        Assert.True(MachineSearchIndexBootstrapper.ShouldUpsertSynonymMap(synonyms));
    }
}
