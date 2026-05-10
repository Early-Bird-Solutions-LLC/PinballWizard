using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc;
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

    // JsonSerializerOptions for RFC 9457 ProblemDetails responses from this
    // endpoint (the 503 fallback and 400 parse-error path). Separate from
    // SseJsonOptions so the two concerns don't share an options object — SSE
    // payloads omit nulls; ProblemDetails payloads also omit nulls (extensions
    // like retryAfterSeconds are conditional). Same settings, different intent.
    private static readonly JsonSerializerOptions ProblemJsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static async Task HandleStreamAsync(
        HttpContext context,
        IServiceProvider services,
        CancellationToken cancellationToken)
    {
        // ── Foundry not wired? Return RFC 9457 503 ProblemDetails ─────
        // Wave 2 PR-D3: replaced the bare-JSON Wave 1 baseline with structured
        // application/problem+json per ADR-0026 § 9 + RFC 9457.
        var router = services.GetService<IAiRouter>();
        if (router is null)
        {
            await WriteProblemAsync(
                context,
                StatusCodes.Status503ServiceUnavailable,
                "https://pinballwizard.app/errors/wizard-unavailable",
                "Service Unavailable",
                "The Wizard AI router is not currently available. Please retry.",
                retryAfterSeconds: 60,
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
            // Log server-side; do NOT forward the parse error text to the
            // user — it may include user-submitted content.
            await WriteProblemAsync(
                context,
                StatusCodes.Status400BadRequest,
                "https://pinballwizard.app/errors/invalid-request",
                "Bad Request",
                "Request body must be JSON with a 'question' string field.",
                retryAfterSeconds: null,
                cancellationToken).ConfigureAwait(false);
            return;
        }

        if (request is null || string.IsNullOrWhiteSpace(request.Question))
        {
            await WriteProblemAsync(
                context,
                StatusCodes.Status400BadRequest,
                "https://pinballwizard.app/errors/invalid-request",
                "Bad Request",
                "'question' is required and must not be empty.",
                retryAfterSeconds: null,
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
        //
        // Mid-stream exception handling (Wave 2 PR-D3):
        //   Exceptions during streaming CANNOT be retroactively converted to
        //   ProblemDetails — headers (200 + text/event-stream) are already
        //   flushed by the time we know there is an error. Instead, we catch
        //   at the streaming loop level, emit a Refusal + Final chunk so the
        //   client receives a well-formed terminal sequence, log the error with
        //   the requestId, then close. This preserves the wire-format contract:
        //   every SSE stream terminates with a Final chunk (ADR-0026 § 4/5 +
        //   PR self-audit item 9(c)). NOT crashing the connection without a
        //   Final chunk is a 🔴 per item 9(c).
        var streamRequestId = Activity.Current?.TraceId.ToString()
                              ?? context.TraceIdentifier;
        // WizardAskStreamEndpoint is static so cannot be used as a generic type
        // argument. Use object as the category to keep a usable logger category.
        var logger = context.RequestServices.GetRequiredService<ILoggerFactory>()
            .CreateLogger("PinballWizard.Api.Endpoints.WizardAskStreamEndpoint");

        try
        {
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
        }
        catch (OperationCanceledException)
        {
            // Client disconnected mid-stream — do not attempt to write; the
            // connection is already closed on the client side.
            return;
        }
        catch (Exception ex)
        {
            // Broad catch: mid-stream exception — headers (200 + text/event-stream) are
            // already flushed, so ProblemDetails is not an option. Any failure type must
            // emit Refusal + Final so the client receives a well-formed terminal sequence
            // (ADR-0026 § 4/5 + PR self-audit item 9(c)). OOM/cancellation still propagate
            // via the runtime since OperationCanceledException is caught above this arm.
            // Mid-stream exception: headers are already flushed, so we cannot
            // return a ProblemDetails response. Emit a Refusal chunk then a
            // synthetic Final chunk so the client receives a well-formed
            // terminal sequence and can render the error gracefully.
            logger.LogError(
                ex,
                "Mid-stream exception interrupted the SSE stream. RequestId: {RequestId}",
                streamRequestId);

            // InsufficientGrounding is the closest semantic match for a mid-stream
            // error — the grounding pipeline was interrupted before completion.
            var refusalChunk = new AnswerChunk.Refusal(
                RefusalCategory.InsufficientGrounding,
                "An error interrupted streaming. Please retry.");
            var refusalJson = JsonSerializer.Serialize<AnswerChunk>(refusalChunk, SseJsonOptions);
            await context.Response.WriteAsync(
                $"event: refusal\ndata: {refusalJson}\n\n",
                CancellationToken.None).ConfigureAwait(false);

            var syntheticAnswer = new WizardAnswer(
                Text: string.Empty,
                Citations: [],
                SubAgentUsed: "unknown",
                Confidence: 0.0,
                Escalated: false,
                IsRefusal: true,
                RefusalCategory: RefusalCategory.InsufficientGrounding,
                PromptVersion: null,
                FoundryThreadId: null);
            var finalChunk = new AnswerChunk.Final(syntheticAnswer);
            var finalJson = JsonSerializer.Serialize<AnswerChunk>(finalChunk, SseJsonOptions);
            await context.Response.WriteAsync(
                $"event: final\ndata: {finalJson}\n\n",
                CancellationToken.None).ConfigureAwait(false);

            await context.Response.Body.FlushAsync(CancellationToken.None).ConfigureAwait(false);
        }

        // ── Stream terminator ─────────────────────────────────────────
        // The "end" event signals to clients that the connection will close
        // normally. Always emitted — refusal paths send final then end.
        await context.Response.WriteAsync(
            "event: end\ndata: {}\n\n",
            cancellationToken).ConfigureAwait(false);
        await context.Response.Body.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    // Writes an RFC 9457 application/problem+json response to the HttpContext.
    // Used by the pre-stream guard paths (router not wired, invalid request body)
    // where headers have NOT yet been flushed. For mid-stream exceptions see the
    // streaming loop's catch arm above.
    private static async Task WriteProblemAsync(
        HttpContext context,
        int statusCode,
        string type,
        string title,
        string detail,
        int? retryAfterSeconds,
        CancellationToken cancellationToken)
    {
        var requestId = Activity.Current?.TraceId.ToString()
                        ?? context.TraceIdentifier;

        var problem = new ProblemDetails
        {
            Type     = type,
            Title    = title,
            Status   = statusCode,
            Detail   = detail,
            Instance = context.Request.Path,
        };
        problem.Extensions["requestId"]    = requestId;
        problem.Extensions["timestampUtc"] = DateTimeOffset.UtcNow.ToString("O");
        if (retryAfterSeconds.HasValue)
        {
            problem.Extensions["retryAfterSeconds"] = retryAfterSeconds.Value;
            context.Response.Headers.RetryAfter =
                retryAfterSeconds.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        context.Response.StatusCode  = statusCode;
        context.Response.ContentType = "application/problem+json";

        await JsonSerializer.SerializeAsync(
            context.Response.Body,
            problem,
            ProblemJsonOptions,
            cancellationToken).ConfigureAwait(false);
    }
}

// Request body shape for POST /api/wizard/ask:stream.
// Sealed record: STJ round-trips cleanly via positional ctor.
internal sealed record WizardAskRequest(string Question);
