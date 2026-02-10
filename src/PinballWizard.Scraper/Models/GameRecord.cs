using System.Text.Json.Serialization;

namespace PinballWizard.Scraper.Models;

/// <summary>
/// Structured game metadata scraped from the game page itself (not from documents).
/// This feeds the RAG system directly — no PDF parsing needed for basic game facts.
/// </summary>
public sealed class GameRecord
{
    public required string GameId { get; init; }
    public required string Title { get; set; }
    public required string Slug { get; init; }
    public required string GamePageUrl { get; init; }

    /// <summary>Manufacturer: Stern, Williams, Bally, Gottlieb, Data East, Jersey Jack, etc.</summary>
    public string? Manufacturer { get; set; }

    /// <summary>Year of production.</summary>
    public int? Year { get; set; }

    /// <summary>Number of units produced (when known).</summary>
    public int? ProductionRun { get; set; }

    /// <summary>Machine type: SS (Solid State), EM (Electromechanical), DMD, LCD, etc.</summary>
    public string? MachineType { get; set; }

    /// <summary>IPDB number for cross-referencing.</summary>
    public int? IpdbNumber { get; set; }

    /// <summary>OPDB ID for cross-referencing.</summary>
    public string? OpdbId { get; set; }

    /// <summary>Where this game was listed: games_listing, archive, vault.</summary>
    public List<string> DiscoveredOn { get; set; } = [];

    /// <summary>Current availability: available, sold_out, vault, etc.</summary>
    public string? Status { get; set; }

    public List<EditionInfo> Editions { get; set; } = [];

    public GameSourceInfo Source { get; set; } = new();

    public static string GenerateId(string slug) => $"game_{slug}";
}

public sealed class EditionInfo
{
    public required string Name { get; set; }
    public string? Msrp { get; set; }
    public string? Availability { get; set; }
    public string? Description { get; set; }
    public List<string> UniqueFeatures { get; set; } = [];
    public int? LimitedQuantity { get; set; }
    public List<string> ImageUrls { get; set; } = [];
}

public sealed class GameSourceInfo
{
    public string ScrapedFrom { get; init; } = string.Empty;
    public DateTime ScrapedAt { get; set; }
}
