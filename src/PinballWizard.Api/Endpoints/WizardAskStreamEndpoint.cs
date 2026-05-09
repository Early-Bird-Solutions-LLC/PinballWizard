using System.Text.Json;
using System.Text.Json.Serialization;
using PinballWizard.Application.Ai;

namespace PinballWizard.Api.Endpoints;

// POST /api/wizard/ask:stream — Server-Sent Events endpoint.
//
// Per ADR-0026 § 2: SSE (text/event-stream) is the locked transport for
// the streaming Wizard surface. SignalR and WebSocket are explicitly NOT
// adopted (no reconnection bundle needed; SSE is HTTP-cacheable at
// Cloudflare; trivially reproducible with curl — all match the showcase
// posture for an anonymous read surface).
//
// Wire format per ADR-0026 § 4: every SSE event payload is AnswerChunk-
// shaped JSON serialized via System.Text.Json with the "$type" discriminator
// (configured via [JsonPolymorphic] + [JsonDerivedType] attributes on
// AnswerChunk). Raw text deltas, plain strings, or any non-discriminator
// wire format are 🔴 per local-review category #12 sub-rule (f).
//
// Response example for a TextDelta chunk:
//   event: text_delta
//   data: {"$type":"text_delta","Text":"Hello"}
//   (blank line)
//
// The stream always ends with a "final" event then an "end" event —
// confirmed by AnswerChunkJsonContractTests and the endpoint unit tests.
//
// Wave 2 PR-D3 replaces the minimal 503 body with RFC 9457 ProblemDetails.
// Wave 2 PR-S2 swaps the router call to RunStreamingAsync.
public static class WizardAskStreamEndpoint
{
    // Shared JsonSerializerOptions: camelCase property names + polymorphic
    // AnswerChunk serialization. The polymorphism is driven entirely by
    // [JsonPolymorphic] / [JsonDerivedType] attributes on AnswerChunk, so
    // no manual type-switch is needed here. Options is static to avoid
    // per-request allocation — JsonSerializerOptions is thread-safe after
    // construction.
    private static readonly JsonSerializerOptions SseJsonOptions = new(JsonSerializerDefaults.Web)
    {
        // DefaultIgnoreCondition omits null fields so the SSE payload
        // stays compact (Citation.MachineId, PageStart, etc. are nullable).
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static IEndpointRouteBuilder MapWizardStreamingEndpoints(
        this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints.MapPost("/api/wizard/ask:stream", HandleStreamAsync)
            .WithName("WizardAskStream")
            .WithDisplayName("Wizard Ask — SSE stream")
            .WithDescription(
                "Streams an answer to a pinball question as Server-Sent Events. " +
                "Each event carries a JSON-serialized AnswerChunk (ADR-0026 § 4). " +
                "The stream always terminates with a 'final' event then an 'end' event.")
            // No [Authorize] — /api/wizard/ask:stream is public/anonymous per ADR-0026 § 1.
            .AllowAnonymous();

        return endpoints;
    }

    private static async Task HandleStreamAsync(
        HttpContext context,
        IServiceProvider services,
        CancellationToken cancellationToken)
    {
        // ── Foundry not wired? Return 503 with Retry-After ───────────
        // Wave 1 baseline — Wave 2 PR-D3 replaces with RFC 9457 ProblemDetails.
        var router = services.GetService<IAiRouter>();
        if (router is null)
        {
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            context.Response.Headers.RetryAfter = "60";
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync(
                """{"error":"wizard_unavailable","retryAfterSeconds":60}""",
                cancellationToken).ConfigureAwait(false);
            return;
        }

        // ── Parse request body ────────────────────────────────────────
        WizardAskRequest? request;
        try
        {
            request = await context.Request.ReadFromJsonAsync<WizardAskRequest>(
                SseJsonOptions, cancellationToken).ConfigureAwait(false);
        }
        catch (JsonException)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync(
                """{"error":"invalid_request","detail":"Request body must be JSON with a 'question' string field."}""",
                cancellationToken).ConfigureAwait(false);
            return;
        }

        if (request is null || string.IsNullOrWhiteSpace(request.Question))
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync(
                """{"error":"invalid_request","detail":"'question' is required and must not be empty."}""",
                cancellationToken).ConfigureAwait(false);
            return;
        }

        // ── SSE response headers ──────────────────────────────────────
        // text/event-stream with no buffering. X-Accel-Buffering disables
        // Nginx/Cloudflare proxy buffering so deltas reach the browser
        // immediately rather than accumulating until a TCP packet fills.
        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = "text/event-stream; charset=utf-8";
        context.Response.Headers.CacheControl = "no-cache";
        context.Response.Headers.Append("X-Accel-Buffering", "no");

        // ── Stream AnswerChunk events ─────────────────────────────────
        // Each chunk maps to an SSE event name (snake_case). The event name
        // mirrors the JSON "$type" discriminator so JS clients can
        // addEventListener("text_delta", …) without a JSON switch per event.
        await foreach (var chunk in router
            .AnswerStreamingAsync(request.Question, cancellationToken)
            .ConfigureAwait(false))
        {
            var eventName = chunk switch
            {
                AnswerChunk.TextDelta         => "text_delta",
                AnswerChunk.ToolCallStarted   => "tool_call_started",
                AnswerChunk.ToolCallCompleted => "tool_call_completed",
                AnswerChunk.CitationArrived   => "citation_arrived",
                AnswerChunk.Refusal           => "refusal",
                AnswerChunk.Final             => "final",
                _                            => throw new InvalidOperationException(
                    $"Unhandled AnswerChunk kind: {chunk.GetType().Name}. " +
                    "Add the new kind to WizardAskStreamEndpoint's event-name switch " +
                    "and add a [JsonDerivedType] entry to AnswerChunk."),
            };

            var json = JsonSerializer.Serialize<AnswerChunk>(chunk, SseJsonOptions);
            await context.Response.WriteAsync(
                $"event: {eventName}\ndata: {json}\n\n",
                cancellationToken).ConfigureAwait(false);
            await context.Response.Body.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        // ── Stream terminator ─────────────────────────────────────────
        // The "end" event signals to clients that the connection will close
        // normally. Always emitted — refusal paths send final then end.
        await context.Response.WriteAsync(
            "event: end\ndata: {}\n\n",
            cancellationToken).ConfigureAwait(false);
        await context.Response.Body.FlushAsync(cancellationToken).ConfigureAwait(false);
    }
}

// Request body shape for POST /api/wizard/ask:stream.
// Sealed record: STJ round-trips cleanly via positional ctor.
internal sealed record WizardAskRequest(string Question);
