using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace PinballWizard.Application.Ai.Refusal;

// Loads community_resources.v1.json from the repo's data/seeds/ directory.
//
// Path resolution matches the SeedQuestionLoader / FeaturedMachineSeedLoader
// pattern: the manifest path is resolved relative to the current working
// directory (the repo root). Tests inject a temp file path via the internal
// constructor — see CommunityResourceLoaderTests.
//
// Validation is fail-fast and throws on first schema violation so mis-edits
// are caught at startup rather than at refusal time. Plurality minimums
// (marketplace ≥ 3, machine_reference ≥ 2) are enforced here so a silent
// seed edit that breaks ADR-0026 § 5 plurality crashes the loader, not a
// runtime refusal.
//
// The loaded list is cached in memory (lazily on first call). The loader is
// registered as a singleton in DI; LoadAsync is thread-safe by construction
// (SemaphoreSlim guards the lazy-init path).
public sealed class CommunityResourceLoader : ICommunityResourceLoader, IDisposable
{
    // Canonical JSON category strings → enum values.
    // Extend here when adding new categories to the seed file.
    private static readonly Dictionary<string, CommunityResourceCategory> CategoryMap =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["marketplace"] = CommunityResourceCategory.Marketplace,
            ["machine_reference"] = CommunityResourceCategory.MachineReference,
            ["news_and_culture"] = CommunityResourceCategory.NewsAndCulture,
            ["forums"] = CommunityResourceCategory.Forums,
            ["tournament_and_play"] = CommunityResourceCategory.TournamentAndPlay,
            ["manufacturer_pages"] = CommunityResourceCategory.ManufacturerPages,
        };

    // Per ADR-0026 § 5 / feedback_destination_plurality.md.
    // Any seed edit that drops a category below its minimum will throw at
    // startup — the fail-fast guard is the silent-edit protection.
    private static readonly Dictionary<CommunityResourceCategory, int> CategoryMinimums =
        new()
        {
            [CommunityResourceCategory.Marketplace] = 3,
            [CommunityResourceCategory.MachineReference] = 2,
        };

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private const string DefaultRelativePath = "data/seeds/community_resources.v1.json";

    private readonly string _manifestPath;
    private readonly ILogger<CommunityResourceLoader> _logger;

    // Lazy cache — guarded by _lock. Set once on first successful LoadAsync.
    private IReadOnlyList<CommunityResource>? _cached;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public CommunityResourceLoader(ILogger<CommunityResourceLoader> logger)
        : this(DefaultRelativePath, logger)
    {
    }

    // Internal constructor so CommunityResourceLoaderTests can inject a
    // temp file path without touching the file system at the default location.
    internal CommunityResourceLoader(string manifestPath, ILogger<CommunityResourceLoader> logger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(manifestPath);
        ArgumentNullException.ThrowIfNull(logger);
        _manifestPath = manifestPath;
        _logger = logger;
    }

    public async Task<IReadOnlyList<CommunityResource>> LoadAsync(CancellationToken cancellationToken)
    {
        // Fast path: already loaded.
        if (_cached is not null)
            return _cached;

        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Async double-checked locking: re-read after acquiring the semaphore.
            // Two callers may both pass the fast-path null check before either thread
            // reaches WaitAsync; reading _cached again here prevents a double call to
            // LoadCoreAsync. The local read breaks the static-analysis alias chain that
            // causes cs/constant-condition false-positives on _cached re-checks.
            var cached = _cached;
            if (cached is not null)
                return cached;

            _cached = await LoadCoreAsync(cancellationToken).ConfigureAwait(false);
            return _cached;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<IReadOnlyList<CommunityResource>> LoadByCategoryAsync(
        CommunityResourceCategory category,
        CancellationToken cancellationToken)
    {
        var all = await LoadAsync(cancellationToken).ConfigureAwait(false);

        // CategoryName returns the JSON string form used in CommunityResource.Category.
        var categoryString = CategoryToString(category);

        return all
            .Where(r => string.Equals(r.Category, categoryString, StringComparison.OrdinalIgnoreCase))
            .ToList()
            .AsReadOnly();
    }

    private async Task<IReadOnlyList<CommunityResource>> LoadCoreAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_manifestPath))
        {
            throw new FileNotFoundException(
                $"Community resource manifest not found at '{_manifestPath}'. " +
                "Run from the repo root where data/seeds/ resides, or set the path explicitly.",
                _manifestPath);
        }

        var json = await File.ReadAllTextAsync(_manifestPath, cancellationToken)
            .ConfigureAwait(false);

        CommunityResourceManifest? manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<CommunityResourceManifest>(json, JsonOptions);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                $"Community resource manifest at '{_manifestPath}' is not valid JSON: {ex.Message}",
                ex);
        }

        if (manifest is null || manifest.Resources is null || manifest.Resources.Count == 0)
        {
            throw new InvalidOperationException(
                $"Community resource manifest at '{_manifestPath}' is empty or missing the 'resources' array. " +
                "The manifest must contain at least one entry per minimum-plurality category.");
        }

        var result = new List<CommunityResource>(manifest.Resources.Count);

        foreach (var dto in manifest.Resources)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (string.IsNullOrWhiteSpace(dto.Name))
            {
                throw new InvalidOperationException(
                    $"Community resource manifest at '{_manifestPath}' contains an entry with a null or whitespace name.");
            }

            if (string.IsNullOrWhiteSpace(dto.Url))
            {
                throw new InvalidOperationException(
                    $"Community resource manifest at '{_manifestPath}' contains entry '{dto.Name}' " +
                    "with a null or whitespace url.");
            }

            if (!Uri.TryCreate(dto.Url, UriKind.Absolute, out _))
            {
                throw new InvalidOperationException(
                    $"Community resource manifest at '{_manifestPath}' contains entry '{dto.Name}' " +
                    $"with malformed url '{dto.Url}'. URLs must be absolute (e.g., https://example.com).");
            }

            if (string.IsNullOrWhiteSpace(dto.Category))
            {
                throw new InvalidOperationException(
                    $"Community resource manifest at '{_manifestPath}' contains entry '{dto.Name}' " +
                    "with a null or whitespace category.");
            }

            if (!CategoryMap.ContainsKey(dto.Category))
            {
                throw new InvalidOperationException(
                    $"Community resource manifest at '{_manifestPath}' contains entry '{dto.Name}' " +
                    $"with unknown category '{dto.Category}'. " +
                    $"Valid values are: {string.Join(", ", CategoryMap.Keys)}.");
            }

            result.Add(new CommunityResource(
                Name: dto.Name,
                Url: dto.Url,
                Category: dto.Category.ToLowerInvariant(),
                Description: dto.Description));
        }

        // Sort alphabetically by name within each category — preserves the
        // no-favoritism ordering rule from feedback_avoid_appearance_of_favoritism.md.
        result.Sort((a, b) =>
        {
            var categoryCompare = string.Compare(a.Category, b.Category, StringComparison.OrdinalIgnoreCase);
            return categoryCompare != 0
                ? categoryCompare
                : string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
        });

        // Enforce per-category plurality minimums (fail-fast).
        var byCategoryCount = result
            .GroupBy(r => r.Category, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);

        foreach (var (category, minimum) in CategoryMinimums)
        {
            var categoryString = CategoryToString(category);
            var actualCount = byCategoryCount.TryGetValue(categoryString, out var count) ? count : 0;

            if (actualCount < minimum)
            {
                throw new InvalidOperationException(
                    $"Community resource manifest at '{_manifestPath}' contains only {actualCount} " +
                    $"'{categoryString}' entries but requires at least {minimum} " +
                    $"(per ADR-0026 § 5 plurality invariant). " +
                    "Add more entries or this refusal will silently under-serve users.");
            }
        }

        _logger.LogInformation(
            "Loaded {Count} community resource(s) from '{ManifestPath}'.",
            result.Count, _manifestPath);

        return result.AsReadOnly();
    }

    // Returns the canonical JSON string for a CommunityResourceCategory enum value.
    // Must stay in sync with CategoryMap.
    internal static string CategoryToString(CommunityResourceCategory category) =>
        category switch
        {
            CommunityResourceCategory.Marketplace => "marketplace",
            CommunityResourceCategory.MachineReference => "machine_reference",
            CommunityResourceCategory.NewsAndCulture => "news_and_culture",
            CommunityResourceCategory.Forums => "forums",
            CommunityResourceCategory.TournamentAndPlay => "tournament_and_play",
            CommunityResourceCategory.ManufacturerPages => "manufacturer_pages",
            _ => throw new ArgumentOutOfRangeException(nameof(category), category, null),
        };

    public void Dispose() => _lock.Dispose();

    // ── JSON DTOs ────────────────────────────────────────────────────────────

    private sealed class CommunityResourceManifest
    {
        [JsonPropertyName("schema_version")]
        public int SchemaVersion { get; set; }

        [JsonPropertyName("resources")]
        public List<CommunityResourceDto>? Resources { get; set; }
    }

    private sealed class CommunityResourceDto
    {
        [JsonPropertyName("category")]
        public string? Category { get; set; }

        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("url")]
        public string? Url { get; set; }

        [JsonPropertyName("description")]
        public string? Description { get; set; }

        // covers_question_types is informational — not validated or surfaced
        // to application consumers (the loader discards it after JSON parse).
        [JsonPropertyName("covers_question_types")]
        public List<string>? CoversQuestionTypes { get; set; }
    }
}
