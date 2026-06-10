using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using PinballWizard.Core.Domain;

namespace PinballWizard.Application.Landing;

// Loads featured_machines.v1.json from the repo's data/seeds/ directory.
//
// Path resolution matches the SeedQuestionLoader pattern: the manifest path
// is resolved relative to the current working directory, which the operator
// sets to the repo root. Tests resolve the path via an internal constructor
// that accepts a temp file path — see FeaturedMachineSeedLoaderTests.
//
// Validation is fail-fast and throws on first schema violation; callers
// (--seed-featured-machines CLI verb) surface the exception as a startup
// error rather than silently seeding invalid data. Duplicate-slug detection
// prevents the operator from accidentally seeding two entries with the same
// Cosmos document id, which would silently collapse into one on upsert.
public sealed class FeaturedMachineSeedLoader : IFeaturedMachineSeedLoader
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private const string DefaultRelativePath = "data/seeds/featured_machines.v1.json";

    private readonly string _manifestPath;
    private readonly ILogger<FeaturedMachineSeedLoader> _logger;

    public FeaturedMachineSeedLoader(ILogger<FeaturedMachineSeedLoader> logger)
        : this(SeedData.SeedPathResolver.Resolve(DefaultRelativePath), logger)
    {
    }

    // Internal constructor so FeaturedMachineSeedLoaderTests can inject a
    // temp file path without touching the file system at the default location.
    internal FeaturedMachineSeedLoader(string manifestPath, ILogger<FeaturedMachineSeedLoader> logger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(manifestPath);
        ArgumentNullException.ThrowIfNull(logger);
        _manifestPath = manifestPath;
        _logger = logger;
    }

    public async Task<IReadOnlyList<FeaturedMachineDocument>> LoadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_manifestPath))
        {
            throw new FileNotFoundException(
                $"Featured machine manifest not found at '{_manifestPath}'. " +
                "Run from the repo root where data/seeds/ resides, or set the path explicitly.",
                _manifestPath);
        }

        var json = await File.ReadAllTextAsync(_manifestPath, cancellationToken)
            .ConfigureAwait(false);

        FeaturedMachineManifest? manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<FeaturedMachineManifest>(json, JsonOptions);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                $"Featured machine manifest at '{_manifestPath}' is not valid JSON: {ex.Message}",
                ex);
        }

        if (manifest is null || manifest.FeaturedMachines is null || manifest.FeaturedMachines.Count == 0)
        {
            _logger.LogInformation(
                "Featured machine manifest at '{ManifestPath}' is empty; no featured machines loaded.",
                _manifestPath);
            return [];
        }

        var seenSlugs = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<FeaturedMachineDocument>(manifest.FeaturedMachines.Count);

        foreach (var dto in manifest.FeaturedMachines)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (string.IsNullOrWhiteSpace(dto.Slug))
            {
                throw new InvalidOperationException(
                    $"Featured machine manifest at '{_manifestPath}' contains an entry with a null or whitespace slug.");
            }

            if (string.IsNullOrWhiteSpace(dto.Title))
            {
                throw new InvalidOperationException(
                    $"Featured machine manifest at '{_manifestPath}' contains entry with slug '{dto.Slug}' " +
                    "that has a null or whitespace title.");
            }

            if (dto.DisplayOrder <= 0)
            {
                throw new InvalidOperationException(
                    $"Featured machine manifest at '{_manifestPath}' contains entry with slug '{dto.Slug}' " +
                    $"that has a non-positive display_order ({dto.DisplayOrder}). Values must be >= 1.");
            }

            if (!seenSlugs.Add(dto.Slug))
            {
                throw new InvalidOperationException(
                    $"Featured machine manifest at '{_manifestPath}' contains duplicate slug '{dto.Slug}'. " +
                    "Each slug must be unique — duplicates would collapse into a single document on upsert.");
            }

            result.Add(new FeaturedMachineDocument
            {
                Id = dto.Slug,
                PartitionKey = dto.Slug,
                Title = dto.Title,
                OpdbId = string.IsNullOrWhiteSpace(dto.OpdbId) ? null : dto.OpdbId,
                DisplayOrder = dto.DisplayOrder,
                Tagline = dto.Tagline ?? string.Empty,
            });
        }

        _logger.LogInformation(
            "Loaded {Count} featured machine(s) from '{ManifestPath}'.",
            result.Count, _manifestPath);

        return result.AsReadOnly();
    }

    // ── JSON DTOs ─────────────────────────────────────────────────────────────

    private sealed class FeaturedMachineManifest
    {
        [JsonPropertyName("schema_version")]
        public int SchemaVersion { get; set; }

        [JsonPropertyName("featured_machines")]
        public List<FeaturedMachineDto>? FeaturedMachines { get; set; }
    }

    private sealed class FeaturedMachineDto
    {
        [JsonPropertyName("slug")]
        public string? Slug { get; set; }

        [JsonPropertyName("title")]
        public string? Title { get; set; }

        [JsonPropertyName("opdb_id")]
        public string? OpdbId { get; set; }

        [JsonPropertyName("display_order")]
        public int DisplayOrder { get; set; }

        [JsonPropertyName("tagline")]
        public string? Tagline { get; set; }
    }
}
