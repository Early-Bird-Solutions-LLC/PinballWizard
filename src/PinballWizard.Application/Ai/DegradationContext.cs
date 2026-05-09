namespace PinballWizard.Application.Ai;

// Per ADR-0026 § 9. Downstream hiccup → degrade gracefully (continue
// with narrower answer + DegradationContext) OR refuse with
// RefusalCategory.UpstreamThrottled when failure prevents grounding.
// Frontend OutageBanner.razor (Wave 2 PR-D-degraded) renders the
// recovery hint. Wave 1 ships the type. PR-D2 wires SearchUnavailable
// into SearchCorpusTool. The 429 / UpstreamThrottled path is wired in
// THIS PR (refusal at the AiRouter layer).
public sealed record DegradationContext(
    DegradationMode Mode,
    string? Detail,
    int? RetryAfterSeconds);

public enum DegradationMode
{
    None = 0,
    SearchUnavailable = 1,
    UpstreamThrottled = 2,
    PartialResults = 3,
}
