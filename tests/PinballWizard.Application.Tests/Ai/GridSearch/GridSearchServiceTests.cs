using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using PinballWizard.Application.Ai;
using PinballWizard.Application.Ai.GridSearch;
using Xunit;

namespace PinballWizard.Application.Tests.Ai.GridSearch;

public sealed class GridSearchServiceTests
{
    // AIAgent's public RunAsync overloads are non-virtual; the override point is the
    // protected RunCoreAsync (confirmed via reflection: RunCoreAsync is the sole
    // virtual+abstract member). Mirrors the CapturingAgent pattern already used in
    // PinballWizard.Infrastructure.Tests/Ai/AiRouterMultiTurnTests.cs, scoped down to
    // exactly what GridSearchService needs (single non-streaming RunAsync call).
    private sealed class FakeAgent : AIAgent
    {
        private readonly string? _responseText;
        private readonly Exception? _throws;

        public static FakeAgent Returning(string text) => new(text, null);
        public static FakeAgent Throwing(Exception ex) => new(null, ex);

        private FakeAgent(string? responseText, Exception? throws)
        {
            _responseText = responseText;
            _throws = throws;
        }

        protected override Task<AgentResponse> RunCoreAsync(
            IEnumerable<ChatMessage> messages,
            AgentSession? session = null,
            AgentRunOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            if (_throws is not null) throw _throws;
            return Task.FromResult(new AgentResponse(new ChatMessage(ChatRole.Assistant, _responseText)));
        }

        protected override IAsyncEnumerable<AgentResponseUpdate> RunCoreStreamingAsync(
            IEnumerable<ChatMessage> messages,
            AgentSession? session = null,
            AgentRunOptions? options = null,
            CancellationToken cancellationToken = default) =>
            throw new NotImplementedException("GridSearchService does not use streaming.");

        protected override ValueTask<AgentSession> CreateSessionCoreAsync(CancellationToken cancellationToken) =>
            throw new NotImplementedException("GridSearchService does not use sessions.");

        protected override ValueTask<System.Text.Json.JsonElement> SerializeSessionCoreAsync(
            AgentSession session, System.Text.Json.JsonSerializerOptions? jsonSerializerOptions,
            CancellationToken cancellationToken) =>
            throw new NotImplementedException("GridSearchService does not use sessions.");

        protected override ValueTask<AgentSession> DeserializeSessionCoreAsync(
            System.Text.Json.JsonElement serializedState, System.Text.Json.JsonSerializerOptions? jsonSerializerOptions,
            CancellationToken cancellationToken) =>
            throw new NotImplementedException("GridSearchService does not use sessions.");
    }

    private static (GridSearchService Service, IFoundryAgentFactory Factory) MakeService(AIAgent agent)
    {
        var factory = Substitute.For<IFoundryAgentFactory>();
        factory.GetAgent(AgentName.GridSearch).Returns(agent);
        var service = new GridSearchService(factory, NullLogger<GridSearchService>.Instance);
        return (service, factory);
    }

    [Fact]
    public async Task SearchAsync_WellFormedJson_ParsesFiltersAndExplanation()
    {
        var json = """
            {"filters":[{"column":"Manufacturer","operator":"equals","value":"Bally"}],"explanation":"Bally machines.","isSemanticSearch":false,"semanticQuery":null}
            """;
        var (service, _) = MakeService(FakeAgent.Returning(json));

        var result = await service.SearchAsync("Bally machines", "admin-machines", CancellationToken.None);

        Assert.Single(result.Filters);
        Assert.Equal("Manufacturer", result.Filters[0].Column);
        Assert.Equal("Bally machines.", result.Explanation);
        Assert.False(result.IsSemanticSearch);
    }

    [Fact]
    public async Task SearchAsync_MarkdownFencedJson_ExtractsAndParses()
    {
        var fenced = """
            Here's the filter:
            ```json
            {"filters":[],"explanation":"Semantic search for sci-fi.","isSemanticSearch":true,"semanticQuery":"sci-fi"}
            ```
            """;
        var (service, _) = MakeService(FakeAgent.Returning(fenced));

        var result = await service.SearchAsync("sci-fi games", "admin-machines", CancellationToken.None);

        Assert.True(result.IsSemanticSearch);
        Assert.Equal("sci-fi", result.SemanticQuery);
    }

    [Fact]
    public async Task SearchAsync_NonJsonResponse_ReturnsExplanatoryResponse_NotException()
    {
        var (service, _) = MakeService(FakeAgent.Returning("I don't understand the query."));

        var result = await service.SearchAsync("???", "admin-machines", CancellationToken.None);

        Assert.Empty(result.Filters);
        Assert.Contains("couldn't parse", result.Explanation, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SearchAsync_AgentThrows_ReturnsExplanatoryResponse_NotException()
    {
        var (service, _) = MakeService(FakeAgent.Throwing(new InvalidOperationException("Foundry unavailable")));

        var result = await service.SearchAsync("Bally machines", "admin-machines", CancellationToken.None);

        Assert.Empty(result.Filters);
        Assert.Contains("error occurred", result.Explanation, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SearchAsync_UsesGridSearchAgentName()
    {
        var (service, factory) = MakeService(FakeAgent.Returning("""{"filters":[],"explanation":"x","isSemanticSearch":false,"semanticQuery":null}"""));

        await service.SearchAsync("anything", "admin-machines", CancellationToken.None);

        factory.Received(1).GetAgent(AgentName.GridSearch);
    }
}
