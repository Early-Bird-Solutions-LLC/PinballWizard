using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using PinballWizard.Application.Ai;

namespace PinballWizard.Application.Landing;

// Loads wizard_seed_questions.v1.json from the repo's data/seeds/ directory.
//
// Path resolution matches the IngestionSourceSeeder pattern: the manifest
// path is resolved relative to the current working directory, which the
// operator sets to the repo root. Tests resolve the path via FindRepoRoot()
// (walk up from AppContext.BaseDirectory to the .slnx file) — see
// SeedQuestionsContractTests for the production-manifest pin.
//
// Validation is fail-fast and throws on first schema violation; callers
// (ILandingService) surface the exception as a startup error rather than
// silently returning empty seed questions. Unknown TargetSubAgent values
// are caught here rather than at runtime so mis-spellings in the JSON are
// caught before the service starts handling requests.
public sealed class SeedQuestionLoader : ISeedQuestionLoader
{
    // Canonical set of valid TargetSubAgent values (from AgentName constants).
    private static readonly HashSet<string> ValidSubAgents =
        new(StringComparer.Ordinal)
        {
            AgentName.Wizard,
            AgentName.Valuation,
            AgentName.Rules,
            AgentName.Repair,
        };

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private const string DefaultRelativePath = "data/seeds/wizard_seed_questions.v1.json";

    private readonly string _manifestPath;
    private readonly ILogger<SeedQuestionLoader> _logger;

    public SeedQuestionLoader(ILogger<SeedQuestionLoader> logger)
        : this(SeedData.SeedPathResolver.Resolve(DefaultRelativePath), logger)
    {
    }

    // Internal constructor so SeedQuestionLoaderTests can inject a temp
    // file path without touching the file system at the default location.
    internal SeedQuestionLoader(string manifestPath, ILogger<SeedQuestionLoader> logger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(manifestPath);
        ArgumentNullException.ThrowIfNull(logger);
        _manifestPath = manifestPath;
        _logger = logger;
    }

    public async Task<IReadOnlyList<SeedQuestion>> LoadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_manifestPath))
        {
            throw new FileNotFoundException(
                $"Seed question manifest not found at '{_manifestPath}'. " +
                "Run from the repo root where data/seeds/ resides, or set the path explicitly.",
                _manifestPath);
        }

        var json = await File.ReadAllTextAsync(_manifestPath, cancellationToken)
            .ConfigureAwait(false);

        SeedQuestionManifest? manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<SeedQuestionManifest>(json, JsonOptions);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                $"Seed question manifest at '{_manifestPath}' is not valid JSON: {ex.Message}",
                ex);
        }

        if (manifest is null || manifest.Questions is null || manifest.Questions.Count == 0)
        {
            _logger.LogInformation(
                "Seed question manifest at '{ManifestPath}' is empty; no seed questions loaded.",
                _manifestPath);
            return [];
        }

        var result = new List<SeedQuestion>(manifest.Questions.Count);

        foreach (var dto in manifest.Questions)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (string.IsNullOrWhiteSpace(dto.Slug))
            {
                throw new InvalidOperationException(
                    $"Seed question manifest at '{_manifestPath}' contains an entry with a null or whitespace slug.");
            }

            if (string.IsNullOrWhiteSpace(dto.Question))
            {
                throw new InvalidOperationException(
                    $"Seed question manifest at '{_manifestPath}' contains entry with slug '{dto.Slug}' " +
                    "that has a null or whitespace question.");
            }

            if (string.IsNullOrWhiteSpace(dto.TargetSubAgent))
            {
                throw new InvalidOperationException(
                    $"Seed question manifest at '{_manifestPath}' contains entry with slug '{dto.Slug}' " +
                    "that has a null or whitespace target_sub_agent.");
            }

            if (!ValidSubAgents.Contains(dto.TargetSubAgent))
            {
                throw new InvalidOperationException(
                    $"Seed question manifest at '{_manifestPath}' contains entry with slug '{dto.Slug}' " +
                    $"with unknown target_sub_agent '{dto.TargetSubAgent}'. " +
                    $"Valid values are: {string.Join(", ", ValidSubAgents)}.");
            }

            result.Add(new SeedQuestion(
                Slug: dto.Slug,
                Question: dto.Question,
                TargetSubAgent: dto.TargetSubAgent,
                Description: dto.Description ?? string.Empty));
        }

        _logger.LogInformation(
            "Loaded {Count} seed question(s) from '{ManifestPath}'.",
            result.Count, _manifestPath);

        return result.AsReadOnly();
    }

    // ── JSON DTOs ────────────────────────────────────────────────────────────

    private sealed class SeedQuestionManifest
    {
        [JsonPropertyName("schema_version")]
        public int SchemaVersion { get; set; }

        [JsonPropertyName("questions")]
        public List<SeedQuestionDto>? Questions { get; set; }
    }

    private sealed class SeedQuestionDto
    {
        [JsonPropertyName("slug")]
        public string? Slug { get; set; }

        [JsonPropertyName("question")]
        public string? Question { get; set; }

        [JsonPropertyName("target_sub_agent")]
        public string? TargetSubAgent { get; set; }

        [JsonPropertyName("description")]
        public string? Description { get; set; }
    }
}
