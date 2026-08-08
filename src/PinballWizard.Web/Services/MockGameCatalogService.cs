using PinballWizard.Domain.Models;

namespace PinballWizard.Web.Services;

public sealed class MockGameCatalogService : IGameCatalogService
{
    private static readonly List<GameSummary> Games =
    [
        new()
        {
            GameId = "game_medieval-madness", Title = "Medieval Madness", Slug = "medieval-madness",
            Manufacturer = "Williams", Year = 1997, MachineType = "DMD", DocumentCount = 12,
            Editions = [new EditionInfo { Name = "Standard" }, new EditionInfo { Name = "Royal Edition (Remake)", Msrp = "$8,999" }]
        },
        new()
        {
            GameId = "game_the-addams-family", Title = "The Addams Family", Slug = "the-addams-family",
            Manufacturer = "Bally/Midway", Year = 1992, MachineType = "DMD", DocumentCount = 8,
            Editions = [new EditionInfo { Name = "Standard" }, new EditionInfo { Name = "Gold" }]
        },
        new()
        {
            GameId = "game_twilight-zone", Title = "Twilight Zone", Slug = "twilight-zone",
            Manufacturer = "Bally/Midway", Year = 1993, MachineType = "DMD", DocumentCount = 10,
            Editions = [new EditionInfo { Name = "Standard" }]
        },
        new()
        {
            GameId = "game_attack-from-mars", Title = "Attack from Mars", Slug = "attack-from-mars",
            Manufacturer = "Bally", Year = 1995, MachineType = "DMD", DocumentCount = 9,
            Editions = [new EditionInfo { Name = "Standard" }, new EditionInfo { Name = "Remake" }]
        },
        new()
        {
            GameId = "game_iron-maiden", Title = "Iron Maiden: Legacy of the Beast", Slug = "iron-maiden",
            Manufacturer = "Stern", Year = 2018, MachineType = "LCD", DocumentCount = 15,
            Editions = [new EditionInfo { Name = "Pro" }, new EditionInfo { Name = "Premium" }, new EditionInfo { Name = "LE", LimitedQuantity = 500 }]
        },
        new()
        {
            GameId = "game_deadpool", Title = "Deadpool", Slug = "deadpool",
            Manufacturer = "Stern", Year = 2018, MachineType = "LCD", DocumentCount = 11,
            Editions = [new EditionInfo { Name = "Pro" }, new EditionInfo { Name = "Premium" }, new EditionInfo { Name = "LE", LimitedQuantity = 500 }]
        },
        new()
        {
            GameId = "game_godzilla", Title = "Godzilla", Slug = "godzilla",
            Manufacturer = "Stern", Year = 2021, MachineType = "LCD", DocumentCount = 14,
            Editions = [new EditionInfo { Name = "Pro" }, new EditionInfo { Name = "Premium" }, new EditionInfo { Name = "LE", LimitedQuantity = 1000 }]
        },
        new()
        {
            GameId = "game_foo-fighters", Title = "Foo Fighters", Slug = "foo-fighters",
            Manufacturer = "Stern", Year = 2023, MachineType = "LCD", DocumentCount = 7,
            Editions = [new EditionInfo { Name = "Pro" }, new EditionInfo { Name = "Premium" }, new EditionInfo { Name = "LE", LimitedQuantity = 500 }]
        },
        new()
        {
            GameId = "game_theatre-of-magic", Title = "Theatre of Magic", Slug = "theatre-of-magic",
            Manufacturer = "Bally", Year = 1995, MachineType = "DMD", DocumentCount = 6,
            Editions = [new EditionInfo { Name = "Standard" }]
        },
        new()
        {
            GameId = "game_monster-bash", Title = "Monster Bash", Slug = "monster-bash",
            Manufacturer = "Williams", Year = 1998, MachineType = "DMD", DocumentCount = 9,
            Editions = [new EditionInfo { Name = "Standard" }, new EditionInfo { Name = "Remake" }]
        },
        new()
        {
            GameId = "game_indiana-jones", Title = "Indiana Jones: The Pinball Adventure", Slug = "indiana-jones",
            Manufacturer = "Williams", Year = 1993, MachineType = "DMD", DocumentCount = 7,
            Editions = [new EditionInfo { Name = "Standard" }]
        },
        new()
        {
            GameId = "game_whitewater", Title = "White Water", Slug = "whitewater",
            Manufacturer = "Williams", Year = 1993, MachineType = "DMD", DocumentCount = 5,
            Editions = [new EditionInfo { Name = "Standard" }]
        }
    ];

    public Task<List<GameSummary>> SearchGamesAsync(string? query = null, string? manufacturer = null, int? year = null, CancellationToken cancellationToken = default)
    {
        var results = Games.AsEnumerable();

        if (!string.IsNullOrWhiteSpace(query))
        {
            var q = query.ToLowerInvariant();
            results = results.Where(g =>
                g.Title.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                (g.Manufacturer?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false));
        }

        if (!string.IsNullOrWhiteSpace(manufacturer))
            results = results.Where(g => g.Manufacturer?.Equals(manufacturer, StringComparison.OrdinalIgnoreCase) ?? false);

        if (year.HasValue)
            results = results.Where(g => g.Year == year.Value);

        return Task.FromResult(results.ToList());
    }

    public Task<GameSummary?> GetGameBySlugAsync(string slug, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(Games.FirstOrDefault(g => g.Slug.Equals(slug, StringComparison.OrdinalIgnoreCase)));
    }

    public Task<List<string>> GetManufacturersAsync(CancellationToken cancellationToken = default)
    {
        var manufacturers = Games
            .Where(g => g.Manufacturer is not null)
            .Select(g => g.Manufacturer!)
            .Distinct()
            .OrderBy(m => m)
            .ToList();

        return Task.FromResult(manufacturers);
    }

    public Task<GameCatalogStats> GetStatsAsync(CancellationToken cancellationToken = default)
    {
        return Task.FromResult(new GameCatalogStats
        {
            TotalGames = Games.Count,
            TotalDocuments = Games.Sum(g => g.DocumentCount),
            TotalQuestionsAnswered = 1_247
        });
    }
}
