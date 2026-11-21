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
}
