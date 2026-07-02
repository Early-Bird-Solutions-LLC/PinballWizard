using System.Text.Json;
using PinballWizard.Application.Findability;

namespace PinballWizard.Api.Endpoints;

// GET /api/machines/suggest?q={query}&top={n} — public typeahead endpoint (ADR-0049 phase 3).
//
// Returns a ranked, edition-collapsed array of machine suggestions for a partial
// search query, consumed by the typeahead UI component in PinballWizard.Web.
//
// Contract (shared with the UI PR built in parallel — match precisely):
//   - 200 application/json: array of
//       { "opdbId": "GYWBZ-MkPrr", "title": "…", "manufacturer": "…", "year": 2019 }
//   - q shorter than 2 non-whitespace chars → 200 [] (no I/O, honest empty)
//   - top optional, default 8, hard-capped at 20
//   - Anonymous / public — no auth required per ADR-0026 § 1
//   - Index not configured → 200 [] (degrade, never 500 — invariant #17)
//
// Serialization: camelCase + null years preserved (no WhenWritingNull) so the UI can
// distinguish "year unknown" from a missing field. JsonSerializerDefaults.Web handles
// case-insensitive parsing of the result type and camelCase output.
//
// No rate limiting wired on the Api host yet — deferred (same posture as /api/wizard/landing).
public static class MachineSuggestEndpoint
{
    internal const int DefaultTop = 8;
    internal const int MaxTop = 20;

    // Shared options: camelCase (JsonSerializerDefaults.Web) without null suppression.
    // year can be null for OPDB entries lacking a release year — emit the field as null
    // so the UI can render "unknown year" rather than missing the field entirely.
    private static readonly JsonSerializerOptions SuggestJsonOptions =
        new(JsonSerializerDefaults.Web);

    public static IEndpointRouteBuilder MapMachineSuggestEndpoint(
        this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints.MapGet("/api/machines/suggest", HandleAsync)
            .WithName("MachineSuggest")
            .WithDisplayName("Machine Suggest")
            .WithDescription(
                "Returns ranked typeahead suggestions for machine names from the AI Search " +
                "machine findability index. Editions of the same machine are collapsed to one " +
                "suggestion (highest-scored edition wins). q < 2 non-whitespace chars or an " +
                "unconfigured index returns an empty array. Per ADR-0049 phase 3.")
            // No [Authorize] — /api/machines/suggest is public anonymous per ADR-0026 § 1.
            .AllowAnonymous();

        return endpoints;
    }

    private static async Task HandleAsync(
        HttpContext context,
        IServiceProvider services,
        CancellationToken cancellationToken)
    {
        var q = context.Request.Query["q"].ToString();

        // Short-query guard: fewer than 2 non-whitespace chars → 200 [] immediately.
        // Guard is also enforced inside IMachineSuggestService (defense-in-depth), but
        // placing it here keeps the endpoint contract self-evident and avoids a service
        // call for the common empty/single-char typeahead case.
        if (q.Count(static c => !char.IsWhiteSpace(c)) < 2)
        {
            context.Response.StatusCode = StatusCodes.Status200OK;
            context.Response.ContentType = "application/json; charset=utf-8";
            await context.Response.WriteAsync("[]", cancellationToken).ConfigureAwait(false);
            return;
        }

        // top: default 8, hard-capped at 20. Non-integer or out-of-range values
        // fall back to the default — the contract does not require a 400 for bad top.
        var top = DefaultTop;
        if (context.Request.Query.TryGetValue("top", out var topValues)
            && int.TryParse(topValues, out var parsedTop))
        {
            top = Math.Clamp(parsedTop, 1, MaxTop);
        }

        var suggestService = services.GetRequiredService<IMachineSuggestService>();

        var suggestions = await suggestService
            .SuggestAsync(q, top, cancellationToken)
            .ConfigureAwait(false);

        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = "application/json; charset=utf-8";

        var json = JsonSerializer.Serialize(suggestions, SuggestJsonOptions);
        await context.Response.WriteAsync(json, cancellationToken).ConfigureAwait(false);
    }
}
