using Microsoft.AspNetCore.Http;
using PinballWizard.Web.Endpoints;
using Xunit;

namespace PinballWizard.Web.Tests.Endpoints;

public sealed class OpsTriageWebhookProxyEndpointTests
{
    [Fact]
    public void BuildForwardPath_preserves_code_query()
    {
        var path = OpsTriageWebhookProxyEndpoint.BuildForwardPath(
            QueryString.Create("code", "abc123"));

        Assert.Equal("/api/ops/alerts/webhook?code=abc123", path);
    }

    [Fact]
    public void BuildForwardPath_empty_query_is_bare_path()
    {
        var path = OpsTriageWebhookProxyEndpoint.BuildForwardPath(QueryString.Empty);

        Assert.Equal("/api/ops/alerts/webhook", path);
    }

    [Fact]
    public void MaxBodyBytes_matches_Api_bound()
    {
        // Api OpsTriageWebhookEndpoint uses 256 KiB; proxy must not accept more.
        Assert.Equal(256 * 1024, OpsTriageWebhookProxyEndpoint.MaxBodyBytes);
    }

    [Fact]
    public async Task ReadBodyCappedAsync_accepts_body_at_cap()
    {
        var payload = new byte[64];
        Random.Shared.NextBytes(payload);
        await using var source = new MemoryStream(payload);

        var read = await OpsTriageWebhookProxyEndpoint.ReadBodyCappedAsync(
            source, maxBytes: 64, CancellationToken.None);

        Assert.Equal(payload, read);
    }

    [Fact]
    public async Task ReadBodyCappedAsync_rejects_oversize_without_buffering_rest()
    {
        // Chunked-style stream larger than the cap — must throw before reading all.
        await using var source = new MemoryStream(new byte[128]);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            OpsTriageWebhookProxyEndpoint.ReadBodyCappedAsync(
                source, maxBytes: 64, CancellationToken.None));

        // Stopped after exceeding the cap (one extra byte), not after full drain.
        Assert.True(source.Position <= 65, $"Position was {source.Position}");
    }
}
