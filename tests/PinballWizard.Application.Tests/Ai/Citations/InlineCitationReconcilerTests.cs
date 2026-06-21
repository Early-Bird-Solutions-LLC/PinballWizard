using PinballWizard.Application.Ai;
using PinballWizard.Application.Ai.Citations;
using Xunit;

namespace PinballWizard.Application.Tests.Ai.Citations;

public sealed class InlineCitationReconcilerTests
{
    private static Citation Cite(string url) => new("t", url);

    [Fact]
    public void Maps_k_to_card_ordinal_by_sourceurl()
    {
        // sources: k1=urlA, k2=urlB. citations render order: [urlB, urlA] => urlB=N1, urlA=N2.
        var citations = new[] { Cite("https://b/1"), Cite("https://a/1") };
        var sourceIndex = new[] { "https://a/1", "https://b/1" };
        var r = InlineCitationReconciler.Reconcile("X [[cite:1]] and Y [[cite:2]].", citations, sourceIndex);
        Assert.Equal("X [[cite:2]] and Y [[cite:1]].", r.RewrittenText); // k1(urlA)->N2, k2(urlB)->N1
        Assert.Equal(new HashSet<int> { 1, 2 }, r.MarkedOrdinals);
        Assert.Equal(2, r.RenderedTokens);
        Assert.Equal(0, r.DroppedTokens);
    }

    [Fact]
    public void Drops_unmatched_token_and_counts_it()
    {
        var citations = new[] { Cite("https://a/1") };          // only urlA -> N1
        var sourceIndex = new[] { "https://a/1", "https://z/9" }; // k2=urlZ has no citation
        var r = InlineCitationReconciler.Reconcile("A [[cite:1]] Z [[cite:2]].", citations, sourceIndex);
        Assert.Equal("A [[cite:1]] Z .", r.RewrittenText);       // k2 dropped (token removed)
        Assert.Equal(new HashSet<int> { 1 }, r.MarkedOrdinals);
        Assert.Equal(2, r.TotalTokens);
        Assert.Equal(1, r.RenderedTokens);
        Assert.Equal(1, r.DroppedTokens);
    }

    [Fact]
    public void Out_of_range_or_garbage_k_is_dropped()
    {
        var citations = new[] { Cite("https://a/1") };
        var sourceIndex = new[] { "https://a/1" };
        var r = InlineCitationReconciler.Reconcile("[[cite:9]] [[cite:x]]", citations, sourceIndex);
        Assert.Equal(" [[cite:x]]", r.RewrittenText); // k=9 out of range -> dropped; [[cite:x]] not a valid token -> left literal
        Assert.Equal(1, r.TotalTokens);               // only [[cite:9]] counts as a cite token; [[cite:x]] is non-numeric
        Assert.Equal(1, r.DroppedTokens);
    }

    [Fact]
    public void StripCiteTokens_removes_all_numeric_cite_markers()
    {
        var text = "Answer [[cite:1]] with detail [[cite:42]] and non-cite [[cite:x]] unchanged.";
        var stripped = InlineCitationReconciler.StripCiteTokens(text);
        Assert.Equal("Answer  with detail  and non-cite [[cite:x]] unchanged.", stripped);
    }
}
