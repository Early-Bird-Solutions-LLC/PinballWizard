using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NSubstitute;
using PinballWizard.Api.Endpoints;
using PinballWizard.Application.Ai.GridSearch;
using Xunit;

namespace PinballWizard.Api.Tests.Api;

// Integration tests for GET /api/search/grid.
//
// Coverage:
//   - Missing/blank q -> 400, service never called.
//   - Recognized context -> 200, service called with the raw query + context.
//   - Unrecognized/missing context -> 400, service never called (allow-list boundary check;
//     see the comment on GridSearchEndpoint.ValidGridContexts for why this exists).
//
// These tests use TestServer (in-process) - same pattern as MachineSuggestEndpointTests.
public sealed class GridSearchEndpointTests : IDisposable
{
    [Fact]
    public async Task Search_MissingQuery_Returns400_ServiceNotCalled()
    {
        var service = Substitute.For<IGridSearchService>();
        using var server = BuildServer(service);
        using var client = server.CreateClient();

        var response = await client.GetAsync("/api/search/grid?context=admin-machines");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await service.DidNotReceiveWithAnyArgs()
            .SearchAsync(default!, default!, default);
    }

    [Fact]
    public async Task Search_RecognizedContext_Returns200_ForwardsQueryAndContext()
    {
        var service = Substitute.For<IGridSearchService>();
        service.SearchAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new GridSearchResponse([], "ok")));
        using var server = BuildServer(service);
        using var client = server.CreateClient();

        var response = await client.GetAsync("/api/search/grid?q=Stern+machines&context=admin-machines");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await service.Received(1).SearchAsync("Stern machines", "admin-machines", Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("not-a-real-grid")]
    [InlineData("")]
    public async Task Search_UnrecognizedContext_Returns400_ServiceNotCalled(string context)
    {
        var service = Substitute.For<IGridSearchService>();
        using var server = BuildServer(service);
        using var client = server.CreateClient();

        var response = await client.GetAsync($"/api/search/grid?q=Stern+machines&context={Uri.EscapeDataString(context)}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await service.DidNotReceiveWithAnyArgs()
            .SearchAsync(default!, default!, default);
    }

    public void Dispose() { }

    private static TestServer BuildServer(IGridSearchService searchService)
    {
        var builder = new HostBuilder()
            .ConfigureWebHost(webBuilder =>
            {
                webBuilder.UseTestServer();
                webBuilder.ConfigureServices(services =>
                {
                    services.AddRouting();
                    services.AddLogging();
                    services.AddSingleton(searchService);
                });
                webBuilder.Configure(app =>
                {
                    app.UseRouting();
                    app.UseEndpoints(endpoints =>
                    {
                        endpoints.MapGridSearchEndpoint();
                    });
                });
            });

        var host = builder.Build();
        host.Start();
        return host.GetTestServer();
    }
}
