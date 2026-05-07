using Microsoft.Extensions.Options;
using PinballWizard.Application.Ai;
using PinballWizard.Core.Configuration;
using Xunit;

namespace PinballWizard.Scraper.Tests.Ai;

public sealed class SemanticAnswerCacheTests
{
    private static SemanticAnswerCache CreateCache(int maxEntries)
    {
        return new SemanticAnswerCache(Options.Create(new AiFoundryOptions
        {
            ProjectEndpoint = "https://example.com",
            SemanticCacheMaxEntries = maxEntries,
        }));
    }

    private static WizardAnswer CreateAnswer(string text)
    {
        return new WizardAnswer(
            Text: text,
            Citations: Array.Empty<Citation>(),
            SubAgentUsed: AgentName.Wizard,
            Confidence: 1.0,
            Escalated: false,
            IsRefusal: false,
            RefusalCategory: null,
            PromptVersion: "v1.test",
            FoundryThreadId: null);
    }

    [Fact]
    public void TryGet_EmptyCache_ReturnsFalse()
    {
        var cache = CreateCache(maxEntries: 8);

        var found = cache.TryGet("anything", "v1.test", out var answer);

        Assert.False(found);
        Assert.Null(answer);
    }

    [Fact]
    public void Set_ThenTryGet_ReturnsCachedAnswer()
    {
        var cache = CreateCache(maxEntries: 8);
        var answer = CreateAnswer("original");

        cache.Store("foo fighters", "v1.test", answer);
        var found = cache.TryGet("foo fighters", "v1.test", out var retrieved);

        Assert.True(found);
        Assert.Same(answer, retrieved);
    }

    [Fact]
    public void TryGet_DifferentPromptVersion_ReturnsFalse_DueToImplicitInvalidation()
    {
        var cache = CreateCache(maxEntries: 8);
        cache.Store("foo fighters", "v1.test", CreateAnswer("v1"));

        var found = cache.TryGet("foo fighters", "v2.test", out var answer);

        Assert.False(found);
        Assert.Null(answer);
    }

    [Fact]
    public void TryGet_DifferentNormalizedQuestion_ReturnsFalse()
    {
        var cache = CreateCache(maxEntries: 8);
        cache.Store("foo fighters", "v1.test", CreateAnswer("foo"));

        var found = cache.TryGet("metallica", "v1.test", out var answer);

        Assert.False(found);
        Assert.Null(answer);
    }

    [Fact]
    public void Set_BeyondCapacity_EvictsLeastRecentlyUsed()
    {
        var cache = CreateCache(maxEntries: 2);
        cache.Store("a", "v1.test", CreateAnswer("a"));
        cache.Store("b", "v1.test", CreateAnswer("b"));

        // Touch "a" to make it most-recently-used; that should make "b"
        // the LRU and the next Set should evict "b".
        Assert.True(cache.TryGet("a", "v1.test", out _));

        cache.Store("c", "v1.test", CreateAnswer("c"));

        Assert.True(cache.TryGet("a", "v1.test", out _));
        Assert.False(cache.TryGet("b", "v1.test", out _));
        Assert.True(cache.TryGet("c", "v1.test", out _));
    }

    [Fact]
    public void Set_SameKey_ReplacesValue()
    {
        var cache = CreateCache(maxEntries: 8);
        cache.Store("foo fighters", "v1.test", CreateAnswer("v1"));
        cache.Store("foo fighters", "v1.test", CreateAnswer("v2"));

        Assert.Equal(1, cache.Count);
        Assert.True(cache.TryGet("foo fighters", "v1.test", out var retrieved));
        Assert.Equal("v2", retrieved.Text);
    }

    [Fact]
    public void Set_WhenCacheCapacityIsZero_DoesNotStore()
    {
        var cache = CreateCache(maxEntries: 0);
        cache.Store("foo fighters", "v1.test", CreateAnswer("foo"));

        Assert.Equal(0, cache.Count);
        Assert.False(cache.TryGet("foo fighters", "v1.test", out _));
    }

    [Fact]
    public void Set_NullAnswer_Throws()
    {
        var cache = CreateCache(maxEntries: 8);

        Assert.Throws<ArgumentNullException>(() =>
            cache.Store("foo fighters", "v1.test", null!));
    }

    [Fact]
    public void Ctor_NullOptions_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new SemanticAnswerCache(null!));
    }
}
