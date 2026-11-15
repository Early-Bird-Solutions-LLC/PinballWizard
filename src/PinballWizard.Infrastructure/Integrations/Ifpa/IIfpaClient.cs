namespace PinballWizard.Infrastructure.Integrations.Ifpa;

// Typed HTTP client for IFPA player endpoints (ADR-0067).
public interface IIfpaClient
{
    Task<IReadOnlyList<IfpaPlayerSearchHitWireDto>> SearchPlayersAsync(
        string name,
        string? country = null,
        string? stateProv = null,
        CancellationToken cancellationToken = default);

    Task<IfpaPlayerWireDto?> GetPlayerAsync(
        int playerId,
        CancellationToken cancellationToken = default);
}
