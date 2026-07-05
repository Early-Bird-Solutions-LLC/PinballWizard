using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using PinballWizard.Application.Ai.GridSearch;
using PinballWizard.Web.Clients;
using Xunit;

namespace PinballWizard.Web.Tests.Clients;

public sealed class GridSearchClientTests
{
    private sealed class FakeHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;
        public HttpRequestMessage? LastRequest { get; private set; }

        public FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) => _respond = respond;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            return Task.FromResult(_respond(request));
        }
    }

    private static GridSearchClient MakeClient(FakeHandler handler) =>
        new(new HttpClient(handler) { BaseAddress = new Uri("https://localhost/") },
            NullLogger<GridSearchClient>.Instance);

    [Fact]
    public async Task SearchAsync_EmptyQuery_ShortCircuits_NoHttpCall()
    {
        var handler = new FakeHandler(_ => throw new InvalidOperationException("should not be called"));
        var client = MakeClient(handler);

        var result = await client.SearchAsync("   ", "admin-machines", CancellationToken.None);

        Assert.Empty(result.Filters);
        Assert.Null(handler.LastRequest);
    }

    [Fact]
    public async Task SearchAsync_SuccessResponse_Deserializes()
    {
        var payload = new GridSearchResponse(
            [new GridFilter("Manufacturer", "equals", "Stern")], "Stern machines.", false, null);
        var handler = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(payload, options: new JsonSerializerOptions(JsonSerializerDefaults.Web)),
        });
        var client = MakeClient(handler);

        var result = await client.SearchAsync("Stern machines", "admin-machines", CancellationToken.None);

        Assert.Single(result.Filters);
        Assert.Equal("Stern", result.Filters[0].Value);
        Assert.NotNull(handler.LastRequest);
        Assert.Contains("q=Stern", handler.LastRequest!.RequestUri!.Query, StringComparison.Ordinal);
        Assert.Contains("context=admin-machines", handler.LastRequest.RequestUri.Query, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SearchAsync_HttpException_ReturnsExplanatoryResponse_NotThrows()
    {
        var handler = new FakeHandler(_ => throw new HttpRequestException("connection refused"));
        var client = MakeClient(handler);

        var result = await client.SearchAsync("Stern machines", "admin-machines", CancellationToken.None);

        Assert.Empty(result.Filters);
        Assert.Contains("Failed to connect", result.Explanation, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SearchAsync_NonSuccessStatusCode_ReturnsExplanatoryResponse_NotThrows()
    {
        var handler = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError));
        var client = MakeClient(handler);

        var result = await client.SearchAsync("Stern machines", "admin-machines", CancellationToken.None);

        Assert.Empty(result.Filters);
    }
}
