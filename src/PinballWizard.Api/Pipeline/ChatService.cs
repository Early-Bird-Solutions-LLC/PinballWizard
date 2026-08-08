using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Azure.Identity;
using Microsoft.Extensions.Options;
using PinballWizard.Domain.Models;

namespace PinballWizard.Api.Pipeline;

public interface IChatService
{
    IAsyncEnumerable<ChatStreamEvent> StreamAsync(
        List<PromptMessage> messages,
        List<ContextBlock> sourceBlocks,
        CancellationToken ct = default);
}

public sealed class ChatService(
    IHttpClientFactory httpClientFactory,
    IOptions<ApiSettings> settings,
    ILogger<ChatService> logger) : IChatService
{
    private static readonly DefaultAzureCredential Credential = new();
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public async IAsyncEnumerable<ChatStreamEvent> StreamAsync(
        List<PromptMessage> messages,
        List<ContextBlock> sourceBlocks,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        // Emit sources first
        var citations = sourceBlocks.Select(b => new SourceCitation
        {
            Index = b.Index,
            Title = b.SourceName,
            Url = b.SourceUrl ?? "",
            DocumentType = b.DocumentType,
            GameTitle = b.GameTitle,
            SectionPath = b.SectionPath,
            Score = b.Score
        }).ToList();

        yield return new ChatStreamEvent
        {
            Type = ChatStreamEventType.Sources,
            Sources = citations
        };

        // Get Azure AD token for Foundry endpoint
        var apiSettings = settings.Value;
        var tokenResult = await Credential.GetTokenAsync(
            new Azure.Core.TokenRequestContext(["https://cognitiveservices.azure.com/.default"]),
            ct);

        var client = httpClientFactory.CreateClient("Foundry");
        var endpoint = $"{apiSettings.FoundryEndpoint.TrimEnd('/')}/openai/deployments/{apiSettings.FoundryModelId}/chat/completions?api-version=2024-12-01-preview";

        var requestBody = new
        {
            messages = messages.Select(m => new { role = m.Role, content = m.Content }),
            stream = true,
            max_tokens = 4096
        };

        var content = new StringContent(
            JsonSerializer.Serialize(requestBody, JsonOptions),
            Encoding.UTF8,
            "application/json");

        var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = content
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tokenResult.Token);

        HttpResponseMessage? response = null;
        string? errorMessage = null;
        try
        {
            response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to call Azure AI Foundry endpoint");
            errorMessage = "Failed to connect to the AI service. Please try again.";
        }

        if (errorMessage is not null)
        {
            yield return new ChatStreamEvent
            {
                Type = ChatStreamEventType.Error,
                Error = errorMessage
            };
            yield break;
        }

        // Stream SSE response
        await using var stream = await response!.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream);

        string? line;
        while ((line = await reader.ReadLineAsync(ct)) is not null)
        {
            ct.ThrowIfCancellationRequested();

            if (string.IsNullOrEmpty(line))
                continue;

            if (!line.StartsWith("data: ", StringComparison.Ordinal))
                continue;

            var data = line["data: ".Length..];
            if (data == "[DONE]")
                break;

            string? deltaContent = null;
            try
            {
                using var doc = JsonDocument.Parse(data);
                deltaContent = doc.RootElement
                    .GetProperty("choices")[0]
                    .GetProperty("delta")
                    .TryGetProperty("content", out var contentElement)
                    ? contentElement.GetString()
                    : null;
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Failed to parse SSE chunk: {Data}", data);
                continue;
            }

            if (!string.IsNullOrEmpty(deltaContent))
            {
                yield return new ChatStreamEvent
                {
                    Type = ChatStreamEventType.TextDelta,
                    Text = deltaContent
                };
            }
        }

        yield return new ChatStreamEvent
        {
            Type = ChatStreamEventType.Complete
        };
    }
}
