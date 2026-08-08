using System.Text.Json;
using Azure.Identity;
using Azure.Storage.Blobs;
using PinballWizard.Domain.Models;

namespace PinballWizard.Api.Services;

public interface IGameService
{
    Task<List<GameSummary>> GetAllGamesAsync(CancellationToken ct = default);
    Task<GameSummary?> GetGameBySlugAsync(string slug, CancellationToken ct = default);
}

public sealed class GameService(BlobContainerClient blobContainerClient, ILogger<GameService> logger) : IGameService
{
    private List<GameRecord>? _cachedGames;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public async Task<List<GameSummary>> GetAllGamesAsync(CancellationToken ct = default)
    {
        var games = await LoadGamesAsync(ct);
        return games.Select(ToSummary).ToList();
    }

    public async Task<GameSummary?> GetGameBySlugAsync(string slug, CancellationToken ct = default)
    {
        var games = await LoadGamesAsync(ct);
        var game = games.FirstOrDefault(g => g.Slug.Equals(slug, StringComparison.OrdinalIgnoreCase));
        return game is null ? null : ToSummary(game);
    }

    private async Task<List<GameRecord>> LoadGamesAsync(CancellationToken ct)
    {
        if (_cachedGames is not null) return _cachedGames;

        await _lock.WaitAsync(ct);
        try
        {
            if (_cachedGames is not null) return _cachedGames;

            var blobClient = blobContainerClient.GetBlobClient("games.json");
            var response = await blobClient.DownloadContentAsync(ct);
            var json = response.Value.Content.ToString();

            _cachedGames = JsonSerializer.Deserialize<List<GameRecord>>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            }) ?? [];

            logger.LogInformation("Loaded {Count} games from blob storage", _cachedGames.Count);
            return _cachedGames;
        }
        finally
        {
            _lock.Release();
        }
    }

    private static GameSummary ToSummary(GameRecord game) => new()
    {
        GameId = game.GameId,
        Title = game.Title,
        Slug = game.Slug,
        Manufacturer = game.Manufacturer,
        Year = game.Year,
        MachineType = game.MachineType,
        DocumentCount = 0, // Would need search index query for accurate count
        Editions = game.Editions
    };
}
