using System.Text.Json.Serialization;

namespace PinballWizard.Core.Models;

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

    /// <summary>Where this game was listed: games_listing, archive, vault.</summary>
    public List<string> DiscoveredOn { get; set; } = [];

    /// <summary>Current availability: available, sold_out, vault, etc.</summary>
    public string? Status { get; set; }

    /// <summary>
    /// First-published date from JSON-LD <c>WebPage.datePublished</c> on the
    /// game page. Stern uses this to mark when the page went live, which
    /// approximates (but is not identical to) the game's launch date.
    /// </summary>
    public DateTime? DatePublished { get; set; }

    /// <summary>Convenience: year component of <see cref="DatePublished"/>.</summary>
    public int? ReleaseYear { get; set; }

    public List<EditionInfo> Editions { get; set; } = [];

    public GameSourceInfo? Source { get; set; }

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
    public required string ScrapedFrom { get; init; }
    public DateTime ScrapedAt { get; set; }
}
