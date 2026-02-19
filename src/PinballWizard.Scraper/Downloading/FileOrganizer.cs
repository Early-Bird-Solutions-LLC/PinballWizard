using PinballWizard.Domain.Models;

namespace PinballWizard.Scraper.Downloading;

/// <summary>
/// Maps file URLs to organized local paths based on source type and game context.
///
/// Layout:
///   downloads/manuals/{filename}
///   downloads/games/{slug}/{tab}/{filename}
///   downloads/service-bulletins/{filename}
/// </summary>
public static class FileOrganizer
{
    /// <summary>
    /// Determines the local storage path for a downloaded file.
    /// </summary>
    public static string GetLocalPath(string fileUrl, SourceType sourceType, string? gameSlug = null, string? tab = null)
    {
        var filename = GetSafeFilename(fileUrl);

        return sourceType switch
        {
            SourceType.ManualsPage => Path.Combine("manuals", filename),

            SourceType.GamePage when !string.IsNullOrEmpty(gameSlug) =>
                Path.Combine("games", gameSlug, NormalizeTabName(tab), filename),

            SourceType.GamePage =>
                Path.Combine("games", "_unknown", filename),

            SourceType.ServiceBulletinPage =>
                Path.Combine("service-bulletins", filename),

            // API-based sources
            SourceType.OpdbApi => Path.Combine("opdb", filename),
            SourceType.PinballMapApi => Path.Combine("pinball-map", filename),
            SourceType.IfpaApi => Path.Combine("ifpa", filename),

            // Community sources
            SourceType.TiltForums => Path.Combine("tilt-forums", "rulesheets", filename),
            SourceType.PinWiki => Path.Combine("pinwiki", filename),
            SourceType.PinballArchive => Path.Combine("pinball-archive", filename),

            // Static HTML sources
            SourceType.ClaysRepairGuides => Path.Combine("clays-repair", filename),
            SourceType.InternetArchive => Path.Combine("internet-archive", filename),
            SourceType.ArcadeManualArchive => Path.Combine("arcade-manual-archive", filename),

            // Reference & strategy sources
            SourceType.WikipediaGlossary => Path.Combine("glossary", filename),
            SourceType.StrategyGuide => Path.Combine("strategy-guides", filename),

            // Manufacturer support sites
            SourceType.ManufacturerSite => Path.Combine("manufacturers", filename),

            // Rich media
            SourceType.YouTubeChannel => Path.Combine("youtube", filename),

            _ => Path.Combine("other", filename)
        };
    }

    /// <summary>
    /// Extracts a safe filename from a URL, preserving the original name where possible.
    /// </summary>
    private static string GetSafeFilename(string url)
    {
        try
        {
            var uri = new Uri(url);
            var filename = Path.GetFileName(uri.LocalPath);

            if (string.IsNullOrWhiteSpace(filename))
            {
                // Fallback: hash the URL
                filename = $"{DocumentRecord.GenerateId(url)}.bin";
            }

            // Sanitize: remove characters that are invalid in file paths
            var invalidChars = Path.GetInvalidFileNameChars();
            foreach (var c in invalidChars)
            {
                filename = filename.Replace(c, '_');
            }

            // URL-decode common patterns
            filename = Uri.UnescapeDataString(filename);
            // Re-sanitize after decode
            foreach (var c in invalidChars)
            {
                filename = filename.Replace(c, '_');
            }

            return filename;
        }
        catch
        {
            return $"{DocumentRecord.GenerateId(url)}.bin";
        }
    }

    private static string NormalizeTabName(string? tab)
    {
        if (string.IsNullOrWhiteSpace(tab)) return "other";

        return tab switch
        {
            nameof(GamePageTab.PromotionalMaterials) or "Promotional Materials" => "promotional",
            nameof(GamePageTab.GameCode) or "Game Code" => "game-code",
            nameof(GamePageTab.SpecsAndManual) or "Specs & Manual" => "specs-manual",
            _ => tab.ToLowerInvariant().Replace(' ', '-')
        };
    }
}
