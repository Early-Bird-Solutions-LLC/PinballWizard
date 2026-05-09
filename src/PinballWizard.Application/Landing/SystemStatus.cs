namespace PinballWizard.Application.Landing;

// Placeholder type for PR-L1. PR-L3 wires the SystemStatus composition
// (IAzureFoundrySmokeProbe + IAzureAiSearchSmokeProbe + CosmosHealthCheck)
// and the /api/wizard/landing endpoint. All fields nullable so callers
// can distinguish "not-yet-checked" from "known-healthy/unhealthy".
public sealed record SystemStatus(
    bool? CosmosHealthy,
    bool? FoundryHealthy,
    bool? AiSearchHealthy);
