using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using PinballWizard.Application.Ai;

namespace PinballWizard.Web.Components.Wizard;

// HttpClient-based implementation of IWizardStreamingClient.
//
// Sends POST /api/wizard/ask:stream to PinballWizard.Api and reads the
// Server-Sent Events response, parsing each "event: <name>\ndata: <json>"
// pair into an AnswerChunk variant via the [JsonPolymorphic] discriminator.
//
// Aspire service discovery: registered with the "pinwiz-api" service name
// in Program.cs. In local dev, Aspire injects the services__pinwiz-api__*
// env vars so the HttpClient resolves to the Api's Kestrel port. In Azure,
// the ACA internal FQDN is used instead.
//
// Graceful fallback: when the Api returns 503 (Foundry not configured) or
// when the Api is unreachable, this client yields a hardcoded 3-chunk stream
// so the dev experience demonstrates the wire format end-to-end without a
// deployed Foundry endpoint. This fallback is Wave 1 bridging — removed in
// Wave 2 when Foundry is always configured in the deployed environment.
//
// C# does not permit yield inside a catch clause — the TrySendAsync helper
// returns null on HttpRequestException so the iterator body stays outside
// the try-catch. See: https://learn.microsoft.com/en-us/dotnet/csharp/language-reference/keywords/yield
//
// ADR-0026 § 2 — SSE transport
// ADR-0026 § 4 — AnswerChunk discriminated union
public sealed class WizardStreamingClient : IWizardStreamingClient
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<WizardStreamingClient> _logger;

    // Shared JsonSerializerOptions: web defaults (camelCase) + polymorphic
    // AnswerChunk deserialization. Static field avoids per-request
    // allocation; JsonSerializerOptions is thread-safe after construction.
    private static readonly JsonSerializerOptions SseJsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public WizardStreamingClient(
        HttpClient httpClient,
        ILogger<WizardStreamingClient> logger)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(logger);
        _httpClient = httpClient;
        _logger = logger;
    }

    public async IAsyncEnumerable<AnswerChunk> StreamAsync(
        string question,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(question);

        // C# prohibits yield inside catch — send the request in a non-iterator
        // helper that returns null on transport failure, then decide whether to
        // fall back outside the try-catch.
        var response = await TrySendAsync(question, cancellationToken).ConfigureAwait(false);

        if (response is null)
        {
            // TrySendAsync already logged the error.
            await foreach (var chunk in FallbackStreamAsync(cancellationToken).ConfigureAwait(false))
            {
                yield return chunk;
            }
            yield break;
        }

        // ── 503: Foundry not configured (dev mode, no endpoint set) ───
        // Yield hardcoded hello-world stream so the dev experience proves
        // the wire format end-to-end without a Foundry deployment.
        if ((int)response.StatusCode == 503)
        {
            _logger.LogInformation(
                "Api returned 503 (Foundry not wired). Streaming hardcoded demo response.");
            response.Dispose();
            await foreach (var chunk in FallbackStreamAsync(cancellationToken).ConfigureAwait(false))
            {
                yield return chunk;
            }
            yield break;
        }

        response.EnsureSuccessStatusCode();

        // ── Parse SSE response stream ─────────────────────────────────
        // SSE format per spec:
        //   event: <name>\n
        //   data: <json>\n
        //   \n
        // We accumulate lines per-event, flush on blank line.
        await using var stream = await response.Content
            .ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);

        using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);

        string? eventName = null;
        string? dataLine = null;

        while (!cancellationToken.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (line is null)
            {
                // End of stream — server closed the connection normally.
                break;
            }

            if (line.StartsWith("event: ", StringComparison.Ordinal))
            {
                eventName = line["event: ".Length..];
            }
            else if (line.StartsWith("data: ", StringComparison.Ordinal))
            {
                dataLine = line["data: ".Length..];
            }
            else if (line.Length == 0)
            {
                // Blank line = event dispatch. "end" signals normal server close.
                if (eventName is "end")
                {
                    break;
                }

                if (dataLine is not null && eventName is not null and not "end")
                {
                    var chunk = DeserializeChunk(dataLine, eventName);
                    if (chunk is not null)
                    {
                        yield return chunk;
                    }
                }

                eventName = null;
                dataLine = null;
            }
            // Comment lines (":" prefix) and SSE retry directives are ignored.
        }

        response.Dispose();
    }

    // Helper to send the HTTP request and return null on transport failure.
    // Keeps the iterator body free of try-catch (C# language constraint).
    private async Task<HttpResponseMessage?> TrySendAsync(
        string question,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/wizard/ask:stream")
        {
            Content = JsonContent.Create(
                new { question },
                options: SseJsonOptions),
        };
        request.Headers.Accept.ParseAdd("text/event-stream");

        try
        {
            return await _httpClient
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "Api unreachable — falling back to hardcoded demo stream.");
            return null;
        }
    }

    private AnswerChunk? DeserializeChunk(string json, string eventName)
    {
        try
        {
            return JsonSerializer.Deserialize<AnswerChunk>(json, SseJsonOptions);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(
                ex,
                "Failed to deserialize SSE event '{EventName}'. Skipping chunk. JSON: {Json}",
                eventName,
                json.Length > 200 ? json[..200] + "…" : json);
            return null;
        }
    }

    // ── Fallback stream (Wave 1 bridging, dev experience only) ───────────
    // Demonstrates the wire format when the Api's Foundry router is absent.
    // The [EnumeratorCancellation] attribute ensures a cancelled token
    // short-circuits iteration without an unobserved task.
    // Wave 2 removes this when Foundry is always configured in Azure.
    private static async IAsyncEnumerable<AnswerChunk> FallbackStreamAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        // Tiny yield so the iterator is genuinely async — matches how the
        // real stream reader yields on I/O reads. Without this, the
        // compiler-generated state machine returns synchronously and
        // components wouldn't get a chance to render intermediate states.
        await Task.Yield();
        cancellationToken.ThrowIfCancellationRequested();

        yield return new AnswerChunk.TextDelta("Hello");

        await Task.Yield();
        cancellationToken.ThrowIfCancellationRequested();

        yield return new AnswerChunk.TextDelta(" world!");

        await Task.Yield();
        cancellationToken.ThrowIfCancellationRequested();

        // Final carries a placeholder WizardAnswer so the client code path
        // that reads Final.Answer exercises the real AnswerChunk.Final type.
        var placeholderAnswer = new WizardAnswer(
            Text: "Hello world! (Wave 1 placeholder — Foundry not configured.)",
            Citations: [],
            SubAgentUsed: "wizard",
            Confidence: 1.0,
            Escalated: false,
            IsRefusal: false,
            RefusalCategory: null,
            PromptVersion: "wave1-placeholder",
            FoundryThreadId: null);

        yield return new AnswerChunk.Final(placeholderAnswer);
    }
}
