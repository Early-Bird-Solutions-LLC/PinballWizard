using PinballWizard.Api.Services;

namespace PinballWizard.Api.Endpoints;

public static class GameEndpoints
{
    public static void Map(RouteGroupBuilder group)
    {
        group.MapGet("/games", HandleGetAll)
            .RequireRateLimiting("general");

        group.MapGet("/games/{slug}", HandleGetBySlug)
            .RequireRateLimiting("general");
    }

    private static async Task<IResult> HandleGetAll(
        IGameService gameService,
        CancellationToken ct)
    {
        var games = await gameService.GetAllGamesAsync(ct);
        return Results.Ok(games);
    }

    private static async Task<IResult> HandleGetBySlug(
        string slug,
        IGameService gameService,
        CancellationToken ct)
    {
        var game = await gameService.GetGameBySlugAsync(slug, ct);
        return game is null ? Results.NotFound() : Results.Ok(game);
    }
}
