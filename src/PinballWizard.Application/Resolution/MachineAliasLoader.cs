using System.Text.Json;
using Microsoft.Extensions.Logging;
using PinballWizard.Application.SeedData;

namespace PinballWizard.Application.Resolution;

// Loads data/seeds/machine_aliases.v1.json and validates every entry
// fail-fast at startup. Mirrors the CommunityResourceLoader pattern:
// lazy singleton, async double-checked locking, internal constructor for
// test injection.
//
// Validation rules (all throw InvalidOperationException on violation):
//   1. Alias is not null/whitespace.
//   2. Alias normalizes to at least one token (MachineTextNormalizer).
//   3. ManufacturerKey is not null/whitespace.
//   4. Exactly one of OpdbGroupId / MachineId is set (not both, not neither).
//   5. No duplicate (alias, manufacturerKey) pairs.
//   6. The referenced OpdbGroupId or MachineId exists in the catalog
//      (via IMachineAliasCatalog) — a dangling alias is the same class of
//      silent lie as a test that asserts against a URL which does not exist
//      (#758). Fail CI instead.
public sealed class MachineAliasLoader : IMachineAliasLoader, IDisposable
{
    private const string DefaultRelativePath = "data/seeds/machine_aliases.v1.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly string _seedPath;
    private readonly IMachineAliasCatalog _catalog;
    private readonly ILogger<MachineAliasLoader> _logger;

    // Lazy cache — guarded by _lock. Set once on first successful LoadAsync.
    private IReadOnlyList<MachineAliasEntry>? _cached;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public MachineAliasLoader(IMachineAliasCatalog catalog, ILogger<MachineAliasLoader> logger)
        : this(SeedPathResolver.Resolve(DefaultRelativePath), catalog, logger)
    {
    }

    // Internal constructor so tests can inject a temp file path without
    // touching the file system at the default location.
    internal MachineAliasLoader(string seedPath, IMachineAliasCatalog catalog, ILogger<MachineAliasLoader> logger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(seedPath);
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(logger);
        _seedPath = seedPath;
        _catalog = catalog;
        _logger = logger;
    }

    public async Task<IReadOnlyList<MachineAliasEntry>> LoadAsync(CancellationToken cancellationToken)
    {
        // Fast path: already loaded.
        if (_cached is not null)
            return _cached;

        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Async double-checked locking — two callers may both pass the
            // fast-path null check before either reaches WaitAsync.
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

    private async Task<IReadOnlyList<MachineAliasEntry>> LoadCoreAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_seedPath))
        {
            throw new FileNotFoundException(
                $"Machine alias seed not found at '{_seedPath}'. " +
                "Run from the repo root where data/seeds/ resides, or set the path explicitly.",
                _seedPath);
        }

        var json = await File.ReadAllTextAsync(_seedPath, cancellationToken).ConfigureAwait(false);

        MachineAliasSeedFile? seed;
        try
        {
            seed = JsonSerializer.Deserialize<MachineAliasSeedFile>(json, JsonOptions);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                $"Machine alias seed at '{_seedPath}' is not valid JSON: {ex.Message}", ex);
        }

        if (seed is null || seed.Aliases is null || seed.Aliases.Count == 0)
        {
            throw new InvalidOperationException(
                $"Machine alias seed at '{_seedPath}' is empty or missing the 'aliases' array. " +
                "The seed must contain at least one entry.");
        }

        // Tracks (manufacturerKey → set of aliases) for duplicate detection.
        var seenByManufacturer = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in seed.Aliases)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Rule 1: alias is not null/whitespace.
            if (string.IsNullOrWhiteSpace(entry.Alias))
            {
                throw new InvalidOperationException(
                    $"Machine alias seed at '{_seedPath}' contains an entry with a null or whitespace alias.");
            }

            // Rule 2: alias normalizes to at least one token.
            var tokens = MachineTextNormalizer.Tokenize(entry.Alias);
            if (tokens.Count == 0)
            {
                throw new InvalidOperationException(
                    $"Machine alias seed at '{_seedPath}' contains alias '{entry.Alias}' " +
                    "that normalizes to zero tokens and cannot be matched.");
            }

            // Rule 3: manufacturerKey is not null/whitespace.
            if (string.IsNullOrWhiteSpace(entry.ManufacturerKey))
            {
                throw new InvalidOperationException(
                    $"Machine alias seed at '{_seedPath}' contains alias '{entry.Alias}' " +
                    "with a null or whitespace manufacturerKey. " +
                    "An unscoped alias can collide across manufacturers.");
            }

            // Rule 4: exactly one of OpdbGroupId / MachineId is set.
            if (entry.OpdbGroupId is null && entry.MachineId is null)
            {
                throw new InvalidOperationException(
                    $"Machine alias seed at '{_seedPath}' contains alias '{entry.Alias}' " +
                    "with both OpdbGroupId and MachineId null. Exactly one must be set.");
            }

            if (entry.OpdbGroupId is not null && entry.MachineId is not null)
            {
                throw new InvalidOperationException(
                    $"Machine alias seed at '{_seedPath}' contains alias '{entry.Alias}' " +
                    "with both OpdbGroupId and MachineId set. Exactly one must be set.");
            }

            // Rule 5: no duplicate (alias, manufacturerKey).
            if (!seenByManufacturer.TryGetValue(entry.ManufacturerKey, out var aliasSet))
            {
                aliasSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                seenByManufacturer[entry.ManufacturerKey] = aliasSet;
            }

            if (!aliasSet.Add(entry.Alias))
            {
                throw new InvalidOperationException(
                    $"Machine alias seed at '{_seedPath}' contains duplicate alias '{entry.Alias}' " +
                    $"for manufacturer '{entry.ManufacturerKey}'. " +
                    "Each (alias, manufacturerKey) pair must be unique.");
            }

            // Rule 6: catalog existence — a dangling alias silently does nothing
            // at resolution time; fail-fast here so CI catches it instead.
            if (entry.OpdbGroupId is not null)
            {
                var exists = await _catalog
                    .GroupExistsAsync(entry.OpdbGroupId, entry.ManufacturerKey, cancellationToken)
                    .ConfigureAwait(false);

                if (!exists)
                {
                    throw new InvalidOperationException(
                        $"Machine alias seed at '{_seedPath}': alias '{entry.Alias}' " +
                        $"references OpdbGroupId '{entry.OpdbGroupId}' " +
                        $"(manufacturer '{entry.ManufacturerKey}') which does not exist in the catalog. " +
                        "Verify the GroupId from the live catalog or remove this entry.");
                }
            }
            else if (entry.MachineId is not null)
            {
                var exists = await _catalog
                    .MachineExistsAsync(entry.MachineId, entry.ManufacturerKey, cancellationToken)
                    .ConfigureAwait(false);

                if (!exists)
                {
                    throw new InvalidOperationException(
                        $"Machine alias seed at '{_seedPath}': alias '{entry.Alias}' " +
                        $"references MachineId '{entry.MachineId}' " +
                        $"(manufacturer '{entry.ManufacturerKey}') which does not exist in the catalog. " +
                        "Verify the MachineId from the live catalog or remove this entry.");
                }
            }
        }

        _logger.LogInformation(
            "Loaded {Count} machine alias(es) from '{SeedPath}'.",
            seed.Aliases.Count, _seedPath);

        return seed.Aliases;
    }

    public void Dispose() => _lock.Dispose();
}
