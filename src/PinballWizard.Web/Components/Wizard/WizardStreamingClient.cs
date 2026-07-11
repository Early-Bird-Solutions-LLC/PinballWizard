using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Hosting;
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
// Demo stream: ONLY when the Api explicitly answers 503 "Foundry not wired"
// AND the app is running in the Development environment does this client
// yield a hardcoded 3-chunk stream demonstrating the wire format. In all
// other environments a 503 propagates as HttpRequestException so the
// component renders the honest Error state. Transport failures always
// PROPAGATE — they used to ride the same demo stream, which let a fake
// uncited "Hello world!" answer render in production whenever the Api was
// struggling (2026-06-11 incident, #367). The WizardAnswerStream component
// owns failure UX (Error state + one deliberate fallback re-attempt).
//
// ADR-0026 § 2 — SSE transport
// ADR-0026 § 4 — AnswerChunk discriminated union
public sealed class WizardStreamingClient : IWizardStreamingClient
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<WizardStreamingClient> _logger;
    private readonly IHostEnvironment _hostEnvironment;

    // Shared JsonSerializerOptions: web defaults (camelCase) + polymorphic
    // AnswerChunk deserialization. Static field avoids per-request
    // allocation; JsonSerializerOptions is thread-safe after construction.
    private static readonly JsonSerializerOptions SseJsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public WizardStreamingClient(
        HttpClient httpClient,
        ILogger<WizardStreamingClient> logger,
        IHostEnvironment hostEnvironment)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(hostEnvironment);
        _httpClient = httpClient;
        _logger = logger;
        _hostEnvironment = hostEnvironment;
    }

    public IAsyncEnumerable<AnswerChunk> StreamAsync(
        string question,
        CancellationToken cancellationToken)
        => StreamAsync(question, history: null, machineId: null, cancellationToken);

    public IAsyncEnumerable<AnswerChunk> StreamAsync(
        string question,
        IReadOnlyList<ConversationTurn>? history,
        CancellationToken cancellationToken)
        => StreamAsync(question, history, machineId: null, cancellationToken);

    public async IAsyncEnumerable<AnswerChunk> StreamAsync(
        string question,
        IReadOnlyList<ConversationTurn>? history,
        string? machineId,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(question);

        // Transport failures (Api unreachable, connection timeout) PROPAGATE.
        // They previously rode the demo placeholder stream below, which let a
        // fake "Hello world!" answer with zero citations render in production
        // whenever the Api was struggling (2026-06-11 incident). The component
        // catches stream exceptions, shows the honest Error state, and owns
        // the one deliberate fallback re-attempt. The placeholder is reserved
        // for the explicit 503 "Foundry not wired" dev signal below.
        var response = await SendCoreAsync(question, history, machineId, cancellationToken).ConfigureAwait(false);

        // ── 503: Foundry not configured (Development env only) ───────────
        // Yield hardcoded hello-world stream so the dev experience proves
        // the wire format end-to-end without a Foundry deployment.
        // In non-Development environments (QA, Prod) the 503 propagates as
        // HttpRequestException so the WizardAnswerStream component renders
        // the honest Error state — never a fake uncited placeholder answer
        // (invariant #17, issue #367).
        if ((int)response.StatusCode == 503 && _hostEnvironment.IsDevelopment())
        {
            _logger.LogInformation(
                "Api returned 503 (Foundry not wired) in Development. Streaming hardcoded demo response.");
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

    // Sends the request and returns at response-headers arrival
    // (ResponseHeadersRead) — the Api flushes an SSE preamble at accept, so
    // this completes in ~RTT, not model latency. Transport failures
    // propagate to the caller (the component renders the Error state) —
    // see the comment at the top of StreamAsync.
    private async Task<HttpResponseMessage> SendCoreAsync(
        string question,
        IReadOnlyList<ConversationTurn>? history,
        string? machineId,
        CancellationToken cancellationToken)
    {
        // Null history and null machineId serialize away entirely (WhenWritingNull),
        // so the single-shot unscoped wire shape is byte-identical to the
        // pre-machine-scope contract.
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/wizard/ask:stream")
        {
            Content = JsonContent.Create(
                new { question, history, machineId },
                options: SseJsonOptions),
        };
        request.Headers.Accept.ParseAdd("text/event-stream");

        return await _httpClient
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
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

    // ── Demo stream (dev experience ONLY — explicit 503 path) ────────────
    // Demonstrates the wire format when the Api answers 503 "Foundry not
    // wired" (local dev without a Foundry endpoint). This is the ONLY path
    // that may yield it: transport failures propagate so production never
    // renders a fake answer (2026-06-11 incident — the placeholder
    // masqueraded as a real, uncited answer whenever the Api struggled).
    // The [EnumeratorCancellation] attribute ensures a cancelled token
    // short-circuits iteration without an unobserved task.
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
