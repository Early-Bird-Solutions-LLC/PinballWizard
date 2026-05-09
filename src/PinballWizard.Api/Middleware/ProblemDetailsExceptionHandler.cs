using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Azure;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using PinballWizard.Application.Ai;
using PinballWizard.Application.Ai.Degradation;

namespace PinballWizard.Api.Middleware;

// RFC 9457 ProblemDetails exception handler for PinballWizard.Api.
//
// Per ADR-0026 § 9 + Wave 2 PR-D3. Implements IExceptionHandler (the
// ASP.NET Core 8+ pattern registered via builder.Services.AddExceptionHandler<T>()
// + app.UseExceptionHandler()). TryHandleAsync is called by the framework for
// every unhandled exception that escapes a middleware or endpoint handler.
//
// Response contract (RFC 9457):
//   Content-Type: application/problem+json
//   Body:
//     {
//       "type":      "stable URI identifying the problem class",
//       "title":     "short human-readable title (NOT the exception message)",
//       "status":    <HTTP status code>,
//       "detail":    "one-line non-leaky message (NEVER stack traces)",
//       "instance":  "<request path>",
//       "requestId": "<W3C trace ID from Activity.Current>",
//       "retryAfterSeconds": <int?>     (when applicable),
//       "timestampUtc":      "<ISO 8601>"
//     }
//
// Stack traces and internal paths NEVER appear in the 'detail' field.
// The no-leak contract is verified by ProblemDetailsExceptionHandlerTests
// which scans the body for "at " (stack-trace marker) and "Microsoft."
// (assembly name) and asserts absent.
//
// Exception → status code mapping:
//   Azure.RequestFailedException(429)  → 429 Too Many Requests + retryAfterSeconds
//   Azure.RequestFailedException(503)  → 503 Service Unavailable + retryAfterSeconds
//   KeyNotFoundException               → 404 Not Found
//   JsonException                      → 400 Bad Request (parse error not exposed)
//   OperationCanceledException         → let propagate (client closed connection;
//                                        not an internal server error)
//   Default fallback                   → 500 Internal Server Error
//
// Logging: Error for unhandled (500); Warning for known degradation paths
// (SearchUnavailable, UpstreamThrottled / 429). requestId is always in the
// structured log scope.
public sealed class ProblemDetailsExceptionHandler : IExceptionHandler
{
    // JsonSerializerOptions shared across responses. camelCase matches the
    // rest of the Api surface; WhenWritingNull omits the optional extensions
    // (retryAfterSeconds) when not applicable so the payload stays clean.
    private static readonly JsonSerializerOptions ProblemJsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    // Base URI for the problem type URIs. Stable — clients may bookmark them.
    private const string TypeBase = "https://pinballwizard.app/errors/";

    private readonly IDegradationContext _degradationContext;
    private readonly ILogger<ProblemDetailsExceptionHandler> _logger;

    public ProblemDetailsExceptionHandler(
        IDegradationContext degradationContext,
        ILogger<ProblemDetailsExceptionHandler> logger)
    {
        ArgumentNullException.ThrowIfNull(degradationContext);
        ArgumentNullException.ThrowIfNull(logger);
        _degradationContext = degradationContext;
        _logger = logger;
    }

    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        ArgumentNullException.ThrowIfNull(exception);

        // OperationCanceledException usually means the client disconnected.
        // Let it propagate — it is not an internal server error and the
        // framework will suppress it cleanly for cancelled requests.
        if (exception is OperationCanceledException)
        {
            return false;
        }

        var requestId = Activity.Current?.TraceId.ToString()
                        ?? httpContext.TraceIdentifier;

        var (statusCode, title, detail, typeSlug, retryAfterSeconds, isKnownDegradation) =
            MapException(exception);

        using (_logger.BeginScope(new Dictionary<string, object>
        {
            ["RequestId"] = requestId,
            ["ExceptionType"] = exception.GetType().Name,
        }))
        {
            if (isKnownDegradation)
            {
                _logger.LogWarning(
                    exception,
                    "Known degradation path — status {StatusCode}: {Detail}",
                    statusCode,
                    detail);
            }
            else
            {
                _logger.LogError(
                    exception,
                    "Unhandled exception — status {StatusCode}: {Title}",
                    statusCode,
                    title);
            }
        }

        var problem = new ProblemDetails
        {
            Type     = TypeBase + typeSlug,
            Title    = title,
            Status   = statusCode,
            Detail   = detail,
            Instance = httpContext.Request.Path,
        };

        // RFC 9457 extension members.
        problem.Extensions["requestId"]    = requestId;
        problem.Extensions["timestampUtc"] = DateTimeOffset.UtcNow.ToString("O");
        if (retryAfterSeconds.HasValue)
        {
            problem.Extensions["retryAfterSeconds"] = retryAfterSeconds.Value;

            // Set Retry-After header so HTTP clients can respect it without
            // parsing the body.
            httpContext.Response.Headers.RetryAfter =
                retryAfterSeconds.Value.ToString(CultureInfo.InvariantCulture);
        }

        httpContext.Response.StatusCode  = statusCode;
        httpContext.Response.ContentType = "application/problem+json";

        await JsonSerializer.SerializeAsync(
            httpContext.Response.Body,
            problem,
            ProblemJsonOptions,
            cancellationToken).ConfigureAwait(false);

        return true;
    }

    // Maps an exception to (statusCode, title, detail, typeSlug, retryAfterSeconds, isKnownDegradation).
    // Catches ONLY specific exception types — no bare catch {}.
    private (int Status, string Title, string Detail, string TypeSlug, int? RetryAfterSeconds, bool IsKnownDegradation)
        MapException(Exception exception)
    {
        // Azure SDK: 429 Too Many Requests (upstream throttled).
        if (exception is RequestFailedException { Status: 429 } throttled)
        {
            var retryAfter = ParseRetryAfterSeconds(throttled);
            return (429,
                    "Too Many Requests",
                    "AI Search is temporarily throttled. Please retry after the indicated interval.",
                    "upstream-throttled",
                    retryAfter ?? 60,
                    true);
        }

        // Azure SDK: 503 Service Unavailable.
        if (exception is RequestFailedException { Status: 503 } unavailable)
        {
            var retryAfter = ParseRetryAfterSeconds(unavailable);
            return (503,
                    "Service Unavailable",
                    "A dependent Azure service is temporarily unavailable. Please retry.",
                    "upstream-unavailable",
                    retryAfter ?? 60,
                    true);
        }

        // SearchUnavailable degradation (set by D2 in SearchCorpusTool tool boundary).
        // The degradation context carries the authoritative retry guidance.
        if (_degradationContext.Mode == DegradationMode.SearchUnavailable)
        {
            return (503,
                    "Service Unavailable",
                    "AI Search is temporarily unavailable.",
                    "search-unavailable",
                    _degradationContext.RetryAfterSeconds ?? 60,
                    true);
        }

        // JSON parse error from request body — do NOT expose the parse error
        // text (it can include user-submitted content).
        if (exception is JsonException)
        {
            return (400,
                    "Bad Request",
                    "The request body is not valid JSON. Ensure the body is JSON with a 'question' string field.",
                    "invalid-request",
                    null,
                    false);
        }

        // Missing key (upstream lookup — not a coding bug).
        if (exception is KeyNotFoundException)
        {
            return (404,
                    "Not Found",
                    "The requested resource was not found.",
                    "not-found",
                    null,
                    false);
        }

        // Default: 500 Internal Server Error.
        return (500,
                "Internal Server Error",
                "An unexpected error occurred. Please try again.",
                "internal-server-error",
                null,
                false);
    }

    // Parses the Retry-After header from an Azure RequestFailedException.
    // Returns null when the header is absent or unparseable.
    private static int? ParseRetryAfterSeconds(RequestFailedException ex)
    {
        // Azure SDK exposes response headers via ex.GetRawResponse()?.Headers.
        // The Retry-After header value is typically a delay-seconds integer.
        var raw = ex.GetRawResponse();
        if (raw is null)
        {
            return null;
        }

        if (raw.Headers.TryGetValue("Retry-After", out var retryAfterValue)
            && int.TryParse(retryAfterValue, out var seconds))
        {
            return seconds;
        }

        return null;
    }
}
