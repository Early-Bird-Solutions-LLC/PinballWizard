using System.Text.Json;
using PinballWizard.Application.Landing;

namespace PinballWizard.Api.Endpoints;

// GET /api/wizard/landing — public anonymous landing-surface endpoint.
//
// Per ADR-0026 § Landing surface: returns a LandingResponse carrying
// SeedQuestions, FeaturedMachines, and SystemStatus so the Blazor
// landing page (PR-D-landing) can render the starter UX without a
// separate API call per component.
//
// Response shape:
// {
//   "seedQuestions":    [...],
//   "featuredMachines": [...] | null,
//   "systemStatus": {
//     "cosmosHealthy":    true | false | null,
//     "foundryHealthy":   true | false | null,
//     "aiSearchHealthy":  true | false | null
//   } | null
// }
//
// null fields are explicit: null means "unknown / dependency not wired"
// so the frontend can distinguish a healthy false from a not-checked null.
// The frontend (PR-D-landing) renders unknown as a neutral indicator.
//
// 503 + Retry-After fallback: when ILandingService is absent (Cosmos and
// Foundry both unwired in local dev), the endpoint returns 503 with a
// Retry-After header and a minimal JSON error body. This mirrors the
// WizardAskStreamEndpoint 503 pattern (Wave 1 baseline).
// Wave 2 PR-D3 replaces this minimal 503 body with RFC 9457 ProblemDetails
// middleware — at that point the explicit 503 here becomes redundant and
// should be removed in favour of the unified exception handler.
//
// No auth: /api/wizard/landing is public per ADR-0026 § 1.
// Rate limiting: deferred — no rate limiter wired on the Api host yet.
public static class WizardLandingEndpoint
{
    // Shared JsonSerializerOptions: camelCase without any null-suppression.
    //
    // We intentionally do NOT set DefaultIgnoreCondition here. The
    // LandingResponse contract is null-meaningful: SystemStatus fields
    // (bool?) use null to signal "unknown / dependency not wired", distinct
    // from false ("known-unhealthy"). If we applied WhenWritingNull or
    // WhenWritingDefault, null bool? fields would be suppressed and the
    // frontend could not distinguish null from false. Emitting all fields
    // (including nulls) keeps the contract unambiguous.
    private static readonly JsonSerializerOptions LandingJsonOptions = new(JsonSerializerDefaults.Web);

    public static IEndpointRouteBuilder MapWizardLandingEndpoint(
        this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints.MapGet("/api/wizard/landing", HandleAsync)
            .WithName("WizardLanding")
            .WithDisplayName("Wizard Landing")
            .WithDescription(
                "Returns the landing-page payload: seed questions, featured machines, " +
                "and Azure dependency health status. All fields are nullable — null means " +
                "'unknown / dependency not wired', not 'unhealthy'. Per ADR-0026 § Landing surface.")
            // No [Authorize] — public anonymous per ADR-0026 § 1.
            .AllowAnonymous();

        return endpoints;
    }

    private static async Task HandleAsync(
        HttpContext context,
        IServiceProvider services,
        CancellationToken cancellationToken)
    {
        // ── Landing service not wired? Return 503 with Retry-After ────
        // Wave 1 baseline — Wave 2 PR-D3 replaces with RFC 9457
        // ProblemDetails middleware.
        var landingService = services.GetService<ILandingService>();
        if (landingService is null)
        {
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            context.Response.Headers.RetryAfter = "60";
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync(
                """{"error":"landing_unavailable","retryAfterSeconds":60}""",
                cancellationToken).ConfigureAwait(false);
            return;
        }

        var landing = await landingService
            .GetLandingAsync(cancellationToken)
            .ConfigureAwait(false);

        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = "application/json; charset=utf-8";

        var json = JsonSerializer.Serialize(landing, LandingJsonOptions);
        await context.Response.WriteAsync(json, cancellationToken).ConfigureAwait(false);
    }
}
