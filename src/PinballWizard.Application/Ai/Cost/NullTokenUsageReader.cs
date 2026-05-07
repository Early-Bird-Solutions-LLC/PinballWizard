namespace PinballWizard.Application.Ai.Cost;

// The default ITokenUsageReader: returns null. Per the interface
// comment, Microsoft.Agents.AI 1.4.0 does not yet expose a stable
// usage surface on its response types. This impl makes the cost
// pipeline safe-to-ship: AiRouter's cost-ceiling path still runs,
// AiCostCalculator returns 0 cents on a null usage, and the
// machinery is ready for a follow-up PR that swaps in a provider-
// specific reader once the SDK adds the property.
public sealed class NullTokenUsageReader : ITokenUsageReader
{
    public TokenUsage? TryRead(object response, string deploymentName) => null;
}
