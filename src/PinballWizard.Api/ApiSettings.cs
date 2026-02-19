namespace PinballWizard.Api;

public sealed class ApiSettings
{
    public string SearchEndpoint { get; set; } = string.Empty;
    public string SearchIndexName { get; set; } = "pinball-chunks";
    public string FoundryEndpoint { get; set; } = string.Empty;
    public string FoundryModelId { get; set; } = string.Empty;
    public int ContextTokenBudget { get; set; } = 12_000;
    public int MaxConversationTurns { get; set; } = 10;
    public string JwtSigningKey { get; set; } = string.Empty;
    public int JwtExpiryHours { get; set; } = 24;
}
