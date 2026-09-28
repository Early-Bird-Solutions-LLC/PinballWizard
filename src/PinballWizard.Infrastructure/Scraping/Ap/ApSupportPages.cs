using System.Text.Json;

namespace PinballWizard.Infrastructure.Scraping.Ap;

public sealed record ApSupportPage(string Slug, Uri Link);

// The per-game support hubs are WordPress child pages of /support/ whose
// slug is a game in the game-page category (/support/houdini/ for
// /houdini/). Other children (register, updates) are not per-game hubs.
// Pure functions over WordPress REST bodies — no I/O.
public static class ApSupportPageParser
{
    // Returns the id of the top-level page with this slug, or null when the
    // collection has none. A non-array body throws.
    public static int? ParseTopLevelPageId(string json, string slug)
    {
        ArgumentNullException.ThrowIfNull(json);
        ArgumentException.ThrowIfNullOrWhiteSpace(slug);

        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Array)
        {
            throw new JsonException("AP pages response was not a JSON array.");
        }

        foreach (var item in doc.RootElement.EnumerateArray())
        {
            if (!item.TryGetProperty("slug", out var slugElement)) continue;
            if (!string.Equals(slugElement.GetString(), slug, StringComparison.OrdinalIgnoreCase)) continue;
            if (!item.TryGetProperty("parent", out var parentElement)
                || !parentElement.TryGetInt32(out var parent)
                || parent != 0)
            {
                continue;
            }

            if (item.TryGetProperty("id", out var idElement) && idElement.TryGetInt32(out var id))
            {
                return id;
            }
        }

        return null;
    }

    // Returns (slug, link) for each page in a WordPress pages collection.
    // Entries missing either field are skipped. A non-array body throws.
    public static List<ApSupportPage> ParsePages(string json)
    {
        ArgumentNullException.ThrowIfNull(json);

        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Array)
        {
            throw new JsonException("AP pages response was not a JSON array.");
        }

        var pages = new List<ApSupportPage>();
        foreach (var item in doc.RootElement.EnumerateArray())
        {
            var slug = item.TryGetProperty("slug", out var slugElement) ? slugElement.GetString() : null;
            var link = item.TryGetProperty("link", out var linkElement) ? linkElement.GetString() : null;
            if (string.IsNullOrWhiteSpace(slug) || string.IsNullOrWhiteSpace(link)) continue;
            if (!Uri.TryCreate(link, UriKind.Absolute, out var uri)) continue;
            pages.Add(new ApSupportPage(slug, uri));
        }

        return pages;
    }

    // Keeps the child pages whose slug is a known game, in the order given.
    // Everything else lands in notGames so the caller can log what it skipped.
    public static List<ApSupportPage> SelectGameSupportPages(
        IEnumerable<ApSupportPage> childPages,
        IReadOnlySet<string> gameSlugs,
        ICollection<string>? notGames = null)
    {
        ArgumentNullException.ThrowIfNull(childPages);
        ArgumentNullException.ThrowIfNull(gameSlugs);

        var selected = new List<ApSupportPage>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var page in childPages)
        {
            if (!gameSlugs.Contains(page.Slug))
            {
                notGames?.Add(page.Slug);
                continue;
            }

            if (seen.Add(page.Slug))
            {
                selected.Add(page);
            }
        }

        return selected;
    }
}
