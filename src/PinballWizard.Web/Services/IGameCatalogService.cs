using PinballWizard.Domain.Models;

namespace PinballWizard.Web.Services;

public interface IGameCatalogService
{
    Task<List<GameSummary>> SearchGamesAsync(string? query = null, string? manufacturer = null, int? year = null, CancellationToken cancellationToken = default);
    Task<GameSummary?> GetGameBySlugAsync(string slug, CancellationToken cancellationToken = default);
    Task<List<string>> GetManufacturersAsync(CancellationToken cancellationToken = default);
    Task<GameCatalogStats> GetStatsAsync(CancellationToken cancellationToken = default);
}

public sealed class GameCatalogStats
{
    public int TotalGames { get; init; }
    public int TotalDocuments { get; init; }
    public int TotalQuestionsAnswered { get; init; }
}
