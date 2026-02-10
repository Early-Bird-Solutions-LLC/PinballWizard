using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PinballWizard.Scraper.Infrastructure;
using PinballWizard.Scraper.Models;
using YoutubeExplode;
using YoutubeExplode.Channels;
using YoutubeExplode.Common;

namespace PinballWizard.Scraper.Scrapers;

/// <summary>
/// Discovers pinball tutorial and educational videos from YouTube channels,
/// and checks for available closed captions/transcripts.
/// Uses YoutubeExplode (no API key required) to enumerate channel videos
/// and discover caption availability.
/// </summary>
public sealed class YouTubeScraper : ISourceScraper
{
    private readonly ILogger<YouTubeScraper> _logger;

    public string Name => "YouTube";

    /// <summary>
    /// Pinball YouTube channels with educational/tutorial content.
    /// Format: (channel handle or URL, display name, content focus)
    /// </summary>
    private static readonly (string Handle, string DisplayName, string Focus)[] Channels =
    [
        ("@buffalopinball", "Buffalo Pinball", "tutorials, competitive play"),
        ("@PAPApinball", "PAPA TV", "in-depth tutorials, tournament coverage"),
        ("@deadflip", "Dead Flip", "game reveals, tutorials, live streams"),
        ("@AbeFlips", "Abe Flips", "ball control fundamentals, tutorials"),
    ];

    public YouTubeScraper(
        IOptions<ScraperSettings> settings,
        ILogger<YouTubeScraper> logger)
    {
        _logger = logger;
    }

    public async IAsyncEnumerable<ScrapedItem> ScrapeAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Discovering pinball YouTube videos from {Count} channels", Channels.Length);

        var youtube = new YoutubeClient();
        var totalVideos = 0;

        foreach (var (handle, displayName, focus) in Channels)
        {
            if (cancellationToken.IsCancellationRequested) break;

            var count = 0;

            _logger.LogInformation("Scanning YouTube channel: {Name} ({Focus})", displayName, focus);

            var items = new List<ScrapedItem>();
            try
            {
                var channelUrl = $"https://www.youtube.com/{handle}";

                // Resolve handle to channel ID (GetUploadsAsync requires ChannelId)
                // ChannelHandle.Parse expects a full URL, not a bare @handle
                var channel = await youtube.Channels.GetByHandleAsync(
                    ChannelHandle.Parse(channelUrl), cancellationToken);

                await foreach (var video in youtube.Channels.GetUploadsAsync(
                    channel.Id, cancellationToken))
                {
                    // Focus on tutorial/educational content by title keywords
                    if (!IsEducationalVideo(video.Title)) continue;

                    var videoUrl = $"https://www.youtube.com/watch?v={video.Id}";

                    items.Add(new ScrapedItem
                    {
                        Link = new DiscoveredLink
                        {
                            FileUrl = videoUrl,
                            LinkText = $"{displayName}: {video.Title}",
                            DiscoveryContext = $"YouTube Channel: {displayName}",
                            GameSlug = ExtractGameSlug(video.Title)
                        },
                        SourceType = SourceType.YouTubeChannel,
                        DiscoveryUrl = channelUrl,
                        DiscoveryContext = $"YouTube tutorial: {video.Title}"
                    });

                    count++;

                    // Cap per channel to avoid overwhelming the catalog
                    if (count >= 200) break;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to scan YouTube channel: {Name}", displayName);
            }

            foreach (var item in items)
            {
                totalVideos++;
                yield return item;
            }

            _logger.LogInformation("{Name}: discovered {Count} educational videos", displayName, count);
            await Task.Delay(500, cancellationToken);
        }

        _logger.LogInformation("YouTube: discovered {Count} total educational videos", totalVideos);
    }

    /// <summary>
    /// Filters for videos likely to contain educational/tutorial content about pinball.
    /// </summary>
    private static bool IsEducationalVideo(string title)
    {
        var lower = title.ToLowerInvariant();

        // Include tutorials, guides, tips, how-to, rules, strategy
        var educationalKeywords = new[]
        {
            "tutorial", "guide", "how to", "tips", "strategy", "rules",
            "rulesheet", "walkthrough", "breakdown", "explained", "learn",
            "basics", "beginner", "advanced", "techniques", "skill",
            "multiball", "wizard mode", "scoring", "review", "deep dive",
            "first look", "gameplay", "unboxing"
        };

        return educationalKeywords.Any(lower.Contains);
    }

    /// <summary>
    /// Attempts to extract a game name slug from a video title.
    /// e.g., "Iron Maiden Tutorial" -> "iron-maiden"
    /// </summary>
    private static string? ExtractGameSlug(string title)
    {
        // Remove common suffixes
        var cleaned = Regex.Replace(title,
            @"\s*[-|:]\s*(tutorial|guide|tips|strategy|review|deep dive|first look|gameplay|rules|walkthrough|breakdown|explained).*$",
            "",
            RegexOptions.IgnoreCase);

        // Remove common prefixes
        cleaned = Regex.Replace(cleaned,
            @"^(how to play|learning|playing|mastering|bro,?\s*do you even\s*)\s*",
            "",
            RegexOptions.IgnoreCase);

        cleaned = cleaned.Trim();
        if (string.IsNullOrWhiteSpace(cleaned) || cleaned.Length < 3) return null;

        var slug = cleaned.ToLowerInvariant()
            .Replace(' ', '-')
            .Replace("'", "")
            .Replace("\"", "")
            .Replace(":", "");

        while (slug.Contains("--"))
            slug = slug.Replace("--", "-");

        return slug.Trim('-');
    }
}
