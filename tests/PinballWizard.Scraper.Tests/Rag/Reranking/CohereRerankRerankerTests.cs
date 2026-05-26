using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PinballWizard.Application.Ai.Retrieval;
using PinballWizard.Core.Configuration;
using PinballWizard.Infrastructure.Rag.Reranking;
using Xunit;

namespace PinballWizard.Scraper.Tests.Rag.Reranking;

// Behaviour tests for CohereRerankReranker using a fake HttpMessageHandler
// that intercepts the Cohere Rerank API call without hitting the network
// (ADR-0024 W4 fix-up). The fake handler returns a canned Cohere JSON
// response so tests are deterministic and run in CI without Cohere credentials.
public sealed class CohereRerankRerankerTests
{
    private static RetrievedChunk MakeChunk(string id, double score = 0.5) =>
        new(ChunkId: id,
            MachineId: "mch_godzilla",
            MachineTitle: "Godzilla (Premium)",
            Manufacturer: "Stern Pinball",
            DocumentId: "doc_abc",
            DocumentUrl: "https://example.com/manual.pdf",
            DocumentType: "manual",
            PageStart: 1, PageEnd: 2,
            SectionHeading: "Rules",
            Content: $"Content for {id}",
            Score: score);

    private static CrossEncoderOptions EnabledOptions(string endpoint = "https://foundry.example.com/cohere/rerank") =>
        new() { Enabled = true, TopN = 5, ModelEndpoint = endpoint, ModelId = "rerank-english-v3.0" };

    private static HttpClient FakeHttpClient(string responseBody, HttpStatusCode statusCode = HttpStatusCode.OK)
    {
        var handler = new FakeHttpHandler(responseBody, statusCode);
        return new HttpClient(handler);
    }

    [Fact]
    public async Task RerankAsync_ReturnsChunksOrderedByCohereRelevanceScore()
    {
        // Cohere returns index 1 (score 0.95) before index 0 (score 0.40) —
        // the reranker should flip the input order accordingly.
        var cohereResponse = """
            {
              "results": [
                {"index": 1, "relevance_score": 0.95},
                {"index": 0, "relevance_score": 0.40}
              ]
            }
            """;
        var client = FakeHttpClient(cohereResponse);
        var sut = new CohereRerankReranker(client, Options.Create(EnabledOptions()),
            NullLogger<CohereRerankReranker>.Instance);
        var candidates = new[] { MakeChunk("chunk_A"), MakeChunk("chunk_B") };

        var result = await sut.RerankAsync("What is Kaiju multiball?", candidates, topN: 2, CancellationToken.None);

        Assert.Equal(2, result.Count);
        Assert.Equal("chunk_B", result[0].Chunk.ChunkId);    // index 1 → highest score
        Assert.Equal(0.95f, result[0].RelevanceScore, precision: 3);
        Assert.Equal("chunk_A", result[1].Chunk.ChunkId);    // index 0 → lower score
        Assert.Equal(0.40f, result[1].RelevanceScore, precision: 3);
    }

    [Fact]
    public async Task RerankAsync_RespectsTopNTruncation()
    {
        // Cohere returns 3 results; topN=2 → only the top 2 are returned.
        var cohereResponse = """
            {
              "results": [
                {"index": 2, "relevance_score": 0.99},
                {"index": 0, "relevance_score": 0.88},
                {"index": 1, "relevance_score": 0.55}
              ]
            }
            """;
        var client = FakeHttpClient(cohereResponse);
        var sut = new CohereRerankReranker(client, Options.Create(EnabledOptions()),
            NullLogger<CohereRerankReranker>.Instance);
        var candidates = new[] { MakeChunk("chunk_A"), MakeChunk("chunk_B"), MakeChunk("chunk_C") };

        var result = await sut.RerankAsync("query", candidates, topN: 2, CancellationToken.None);

        Assert.Equal(2, result.Count);
        Assert.Equal("chunk_C", result[0].Chunk.ChunkId);    // index 2
        Assert.Equal("chunk_A", result[1].Chunk.ChunkId);    // index 0
    }

    [Fact]
    public async Task RerankAsync_SendsPostToModelEndpoint()
    {
        var cohereResponse = """{"results": [{"index": 0, "relevance_score": 0.80}]}""";
        var handler = new CapturingFakeHttpHandler(cohereResponse);
        var client = new HttpClient(handler);
        var opts = EnabledOptions("https://foundry.example.com/cohere/rerank");
        var sut = new CohereRerankReranker(client, Options.Create(opts),
            NullLogger<CohereRerankReranker>.Instance);

        await sut.RerankAsync("query", new[] { MakeChunk("chunk_A") }, topN: 1, CancellationToken.None);

        Assert.Equal(HttpMethod.Post, handler.LastRequest!.Method);
        Assert.Equal("https://foundry.example.com/cohere/rerank", handler.LastRequest.RequestUri!.ToString());
    }

    [Fact]
    public async Task RerankAsync_RequestBodyContainsQueryAndDocuments()
    {
        var cohereResponse = """{"results": [{"index": 0, "relevance_score": 0.80}]}""";
        var handler = new CapturingFakeHttpHandler(cohereResponse);
        var client = new HttpClient(handler);
        var sut = new CohereRerankReranker(client, Options.Create(EnabledOptions()),
            NullLogger<CohereRerankReranker>.Instance);

        await sut.RerankAsync("Kaiju multiball query", new[] { MakeChunk("chunk_A") }, topN: 1, CancellationToken.None);

        var body = await handler.LastRequest!.Content!.ReadAsStringAsync();
        Assert.Contains("Kaiju multiball query", body);
        Assert.Contains("Content for chunk_A", body);
        Assert.Contains("rerank-english-v3.0", body);
    }

    [Fact]
    public async Task RerankAsync_HttpError_ThrowsHttpRequestException()
    {
        var client = FakeHttpClient("{\"error\": \"unauthorized\"}", HttpStatusCode.Unauthorized);
        var sut = new CohereRerankReranker(client, Options.Create(EnabledOptions()),
            NullLogger<CohereRerankReranker>.Instance);

        await Assert.ThrowsAsync<HttpRequestException>(() =>
            sut.RerankAsync("query", new[] { MakeChunk("chunk_A") }, topN: 1, CancellationToken.None));
    }

    // Minimal fake HTTP handler that returns a fixed response body.
    private sealed class FakeHttpHandler(string responseBody, HttpStatusCode statusCode) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(responseBody, Encoding.UTF8, "application/json"),
            };
            return Task.FromResult(response);
        }
    }

    // Capturing variant that records the last request for assertion.
    private sealed class CapturingFakeHttpHandler(string responseBody) : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseBody, Encoding.UTF8, "application/json"),
            };
            return Task.FromResult(response);
        }
    }
}
