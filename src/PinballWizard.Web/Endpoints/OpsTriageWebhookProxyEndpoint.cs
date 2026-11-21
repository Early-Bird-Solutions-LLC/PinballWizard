using System.Net.Http.Headers;

namespace PinballWizard.Web.Endpoints;

/// <summary>
/// Public front door for Azure Monitor → Ops Triage (ADR-0064).
/// Api ingress is internal-only; the Wizard ACA app is external. This proxy
/// forwards <c>POST /api/ops/alerts/webhook</c> (query + body) to the Api via
/// Aspire service discovery so Monitor can reach triage without opening Api
/// to the internet. Auth remains the Api shared-secret <c>code</c> check.
/// </summary>
public static class OpsTriageWebhookProxyEndpoint
{
    public const string HttpClientName = "OpsTriageWebhookProxy";

    /// <summary>Max body mirrored from Api webhook (256 KiB).</summary>
    public const int MaxBodyBytes = 256 * 1024;

    public static IEndpointRouteBuilder MapOpsTriageWebhookProxyEndpoint(
        this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints.MapPost("/api/ops/alerts/webhook", HandleAsync)
            .WithName("OpsTriageWebhookProxy")
            .AllowAnonymous();

        return endpoints;
    }

    private static async Task<IResult> HandleAsync(
        HttpRequest request,
        IHttpClientFactory httpClientFactory,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        var logger = loggerFactory.CreateLogger("OpsTriageWebhookProxy");

        if (request.ContentLength is > MaxBodyBytes)
        {
            return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
        }

        byte[] body;
        await using (var ms = new MemoryStream(capacity: (int)Math.Min(request.ContentLength ?? 4096, MaxBodyBytes)))
        {
            await request.Body.CopyToAsync(ms, cancellationToken).ConfigureAwait(false);
            if (ms.Length > MaxBodyBytes)
            {
                return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
            }

            body = ms.ToArray();
        }

        var client = httpClientFactory.CreateClient(HttpClientName);
        using var forward = new HttpRequestMessage(HttpMethod.Post, BuildForwardPath(request.QueryString));
        forward.Content = new ByteArrayContent(body);
        if (!string.IsNullOrWhiteSpace(request.ContentType))
        {
            forward.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(request.ContentType);
        }
        else
        {
            forward.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        }

        HttpResponseMessage upstream;
        try
        {
            upstream = await client.SendAsync(
                    forward,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            logger.LogWarning(ex, "Ops triage webhook proxy could not reach pinwiz-api.");
            return Results.Json(
                new { error = "Ops triage Api unreachable from Wizard proxy." },
                statusCode: StatusCodes.Status502BadGateway);
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Ops triage webhook proxy timed out reaching pinwiz-api.");
            return Results.Json(
                new { error = "Ops triage Api timed out." },
                statusCode: StatusCodes.Status504GatewayTimeout);
        }

        using (upstream)
        {
            var responseBytes = await upstream.Content.ReadAsByteArrayAsync(cancellationToken)
                .ConfigureAwait(false);
            var contentType = upstream.Content.Headers.ContentType?.ToString() ?? "application/json";
            return new ProxyPassthroughResult((int)upstream.StatusCode, contentType, responseBytes);
        }
    }

    /// <summary>Preserves Monitor's <c>?code=</c> (and any other query) for Api auth.</summary>
    internal static string BuildForwardPath(QueryString query) =>
        "/api/ops/alerts/webhook" + query.Value;

    private sealed class ProxyPassthroughResult(
        int statusCode,
        string contentType,
        byte[] body) : IResult
    {
        public async Task ExecuteAsync(HttpContext httpContext)
        {
            httpContext.Response.StatusCode = statusCode;
            httpContext.Response.ContentType = contentType;
            if (body.Length > 0)
            {
                await httpContext.Response.Body.WriteAsync(body).ConfigureAwait(false);
            }
        }
    }
}
