using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Azure.Identity;
using Microsoft.Extensions.Options;

namespace PinballWizard.Api.Pipeline;

public interface IEmbeddingService
{
    Task<ReadOnlyMemory<float>> GetEmbeddingAsync(string text, CancellationToken ct = default);
}

public sealed class EmbeddingService(
    IHttpClientFactory httpClientFactory,
    IOptions<ApiSettings> settings) : IEmbeddingService
{
    private static readonly DefaultAzureCredential Credential = new();

    public async Task<ReadOnlyMemory<float>> GetEmbeddingAsync(string text, CancellationToken ct = default)
    {
        var apiSettings = settings.Value;
        var tokenResult = await Credential.GetTokenAsync(
            new Azure.Core.TokenRequestContext(["https://cognitiveservices.azure.com/.default"]),
            ct);

        var client = httpClientFactory.CreateClient("Foundry");
        var endpoint = $"{apiSettings.FoundryEndpoint.TrimEnd('/')}/openai/deployments/text-embedding-3-small/embeddings?api-version=2024-12-01-preview";

        var requestBody = new { input = text, model = "text-embedding-3-small" };
        var content = new StringContent(
            JsonSerializer.Serialize(requestBody),
            Encoding.UTF8,
            "application/json");

        var request = new HttpRequestMessage(HttpMethod.Post, endpoint) { Content = content };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tokenResult.Token);

        var response = await client.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(json);

        var embeddingArray = doc.RootElement
            .GetProperty("data")[0]
            .GetProperty("embedding");

        var floats = new float[embeddingArray.GetArrayLength()];
        var i = 0;
        foreach (var element in embeddingArray.EnumerateArray())
        {
            floats[i++] = element.GetSingle();
        }

        return new ReadOnlyMemory<float>(floats);
    }
}
