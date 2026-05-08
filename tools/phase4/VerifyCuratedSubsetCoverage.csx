// tools/phase4/VerifyCuratedSubsetCoverage.csx
// ---------------------------------------------------------------------------
// Verifies the Phase 4 curated 7-machine slate against the file-system
// document catalog. For each machine in `data/phase4/curated-subset.v1.json`,
// scans `data/metadata/catalog.json` for documents whose game.title /
// game.slug + source.discovery_url host match the manifest entry, and
// reports whether each expectedDocumentType has ≥1 match.
//
// WHY THIS EXISTS
//   Build-spec § Phase 4 § Scope item 7 + § Exit criteria require a
//   verifier that confirms each curated machine has the expected
//   document coverage. v1 ships a file-system-catalog-backed checker
//   (the only document inventory that exists today — `scraped_documents`
//   Cosmos container is provisioned later by W3-2 / scope item 18). v2
//   adds the Cosmos check once that container exists; the manifest
//   shape is stable across both.
//
// SCOPE & LIMITATIONS (v1)
//   - Reads `data/metadata/catalog.json` (Phase 1 file-system catalog).
//   - The 2026-05-02 catalog snapshot is Stern-only (440 documents, all
//     sternpinball.com). The five non-Stern manufacturer scrapers exist
//     in code (per CLAUDE.md § Source manufacturers) but their docs
//     have not yet been written into a catalog.json. For non-Stern
//     manifest entries, this script reports `⏸ awaiting non-Stern
//     catalog or scraped_documents container`. Phase 4.5 corpus
//     expansion + W3-2 Cosmos Change Feed Function close that gap.
//   - The script does NOT query deployed Cosmos. Adding that is a v2
//     extension once `scraped_documents` is provisioned.
//   - Match heuristic: case-insensitive title containment +
//     manufacturer-host filter on source.discovery_url. Edition is not
//     enforced — a Stern Godzilla manual without an explicit
//     game.edition still satisfies a "Premium" expectation, since the
//     manifest is intent-declarative not edition-strict.
//
// USAGE
//   From the repo root:
//     dotnet script tools/phase4/VerifyCuratedSubsetCoverage.csx
//   Optional flags:
//     --manifest <path>   Override the manifest path (default
//                         data/phase4/curated-subset.v1.json).
//     --catalog <path>    Override the catalog path (default
//                         data/metadata/catalog.json).
//     --strict-stern      Treat any Stern slate machine missing
//                         expected documents as a failure (exit 1).
//                         Default is "report-only" — exits 0 even on
//                         missing coverage so CI can run the script
//                         while the catalog is still mid-population.
//
// EXIT CODES
//   0 — all checks passed (or report-only mode regardless of coverage)
//   1 — strict mode AND at least one Stern machine is missing expected
//       documents
//   2 — bad invocation (unknown flag, missing file, etc.)
//   3 — manifest schema validation failed
//
// NOT A PRODUCTION CODE PATH
//   This script does not compile as part of PinballWizard.slnx. It is
//   a dotnet-script .csx file invoked manually or from CI.
// ---------------------------------------------------------------------------

#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

// ---- Resolve script location → repo paths ---------------------------------

string ScriptDir() {
    var loc = GetScriptFilePath();
    return Path.GetDirectoryName(loc) ?? Directory.GetCurrentDirectory();
}
string GetScriptFilePath([System.Runtime.CompilerServices.CallerFilePath] string path = "") => path;
string repoRoot = Path.GetFullPath(Path.Combine(ScriptDir(), "..", ".."));
string defaultManifestPath = Path.Combine(repoRoot, "data", "phase4", "curated-subset.v1.json");
string defaultCatalogPath = Path.Combine(repoRoot, "data", "metadata", "catalog.json");

// ---- Parse args -----------------------------------------------------------

var argv = Args.ToList();
bool strictStern = argv.Remove("--strict-stern");
string manifestPath = ExtractValueArg(argv, "--manifest") ?? defaultManifestPath;
string catalogPath = ExtractValueArg(argv, "--catalog") ?? defaultCatalogPath;

if (argv.Count > 0)
{
    Console.Error.WriteLine($"Unrecognized arguments: {string.Join(" ", argv)}");
    Console.Error.WriteLine("Usage: dotnet script tools/phase4/VerifyCuratedSubsetCoverage.csx -- [--manifest <path>] [--catalog <path>] [--strict-stern]");
    Environment.Exit(2);
}

string? ExtractValueArg(List<string> args, string flag)
{
    var idx = args.IndexOf(flag);
    if (idx < 0) return null;
    if (idx + 1 >= args.Count) {
        Console.Error.WriteLine($"Flag {flag} requires a value.");
        Environment.Exit(2);
    }
    var value = args[idx + 1];
    args.RemoveAt(idx + 1);
    args.RemoveAt(idx);
    return value;
}

// ---- Manufacturer slug → known host substring -----------------------------
// Hardcoded mirror of `data/seeds/ingestion_sources.v1.json` baseUrl hosts.
// Hardcoding (vs. parsing the seed file) keeps the script self-contained;
// the runtime drift-check below guarantees the hardcoded values stay in
// sync with the seed file by loading the seed and asserting each
// manufacturerHost substring appears in the corresponding seed entry's
// baseUrl. A drift triggers a fatal error so seed renames surface
// loudly rather than as quiet false-positive ⏸ "no docs in catalog"
// reports.

var manufacturerHosts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
{
    ["stern"] = "sternpinball.com",
    ["jjp"] = "jerseyjackpinball.com",
    ["ap"] = "american-pinball.com",
    ["spooky"] = "spookypinball.com",
    ["pinballbrothers"] = "pinballbrothers.com",
    ["barrelsoffun"] = "kollectfun.com",
    ["multimorphic"] = "multimorphic.com",
    ["cgc"] = "chicago-gaming.com",
};

string seedPath = Path.Combine(repoRoot, "data", "seeds", "ingestion_sources.v1.json");
if (File.Exists(seedPath))
{
    try
    {
        var seedDoc = JsonDocument.Parse(File.ReadAllText(seedPath));
        var seedById = seedDoc.RootElement.EnumerateArray()
            .Where(e => e.TryGetProperty("id", out _) && e.TryGetProperty("baseUrl", out _))
            .ToDictionary(
                e => e.GetProperty("id").GetString() ?? "",
                e => e.GetProperty("baseUrl").GetString() ?? "",
                StringComparer.OrdinalIgnoreCase);

        foreach (var (slug, hostFragment) in manufacturerHosts)
        {
            if (!seedById.TryGetValue(slug, out var baseUrl))
            {
                Console.Error.WriteLine($"FATAL: manufacturerHosts has slug '{slug}' but data/seeds/ingestion_sources.v1.json does not. Seed file must add this slug or hardcoded list must drop it.");
                Environment.Exit(3);
            }
            if (!baseUrl.Contains(hostFragment, StringComparison.OrdinalIgnoreCase))
            {
                Console.Error.WriteLine($"FATAL: manufacturerHosts['{slug}'] = '{hostFragment}' but seed baseUrl is '{baseUrl}'. Hardcoded host fragment is stale.");
                Environment.Exit(3);
            }
        }
    }
    catch (JsonException ex)
    {
        Console.Error.WriteLine($"WARN: seed file at '{seedPath}' is not valid JSON; skipping drift check: {ex.Message}");
    }
}
else
{
    Console.Error.WriteLine($"WARN: seed file not found at '{seedPath}'; skipping drift check (this should only happen in a partial-checkout scenario).");
}

// ---- Load + validate manifest --------------------------------------------

if (!File.Exists(manifestPath))
{
    Console.Error.WriteLine($"FATAL: manifest not found at '{manifestPath}'.");
    Environment.Exit(2);
}

JsonDocument manifestDoc;
try
{
    manifestDoc = JsonDocument.Parse(File.ReadAllText(manifestPath));
}
catch (JsonException ex)
{
    Console.Error.WriteLine($"FATAL: manifest at '{manifestPath}' is not valid JSON: {ex.Message}");
    Environment.Exit(3);
    throw; // unreachable; satisfies compiler
}

var manifestRoot = manifestDoc.RootElement;
if (!manifestRoot.TryGetProperty("manifestVersion", out var versionEl) || versionEl.GetString() != "v1")
{
    Console.Error.WriteLine("FATAL: manifest must declare manifestVersion = \"v1\".");
    Environment.Exit(3);
}
if (!manifestRoot.TryGetProperty("machines", out var machinesEl) || machinesEl.ValueKind != JsonValueKind.Array)
{
    Console.Error.WriteLine("FATAL: manifest must contain an array property `machines`.");
    Environment.Exit(3);
}

var machines = new List<ManifestMachine>();
foreach (var entry in machinesEl.EnumerateArray())
{
    var entrySnapshot = entry; // capture for the local function (cannot capture loop variable directly)
    string Required(string field)
    {
        if (entrySnapshot.TryGetProperty(field, out var v) && v.ValueKind == JsonValueKind.String)
        {
            return v.GetString() ?? string.Empty;
        }
        Console.Error.WriteLine($"FATAL: manifest machine entry missing required string field '{field}'.");
        Environment.Exit(3);
        return string.Empty; // unreachable
    }

    var slug = Required("manufacturerSlug");
    if (!manufacturerHosts.ContainsKey(slug))
    {
        Console.Error.WriteLine($"FATAL: manifest entry '{Required("title")}' has unknown manufacturerSlug '{slug}'. Known: {string.Join(", ", manufacturerHosts.Keys)}");
        Environment.Exit(3);
    }
    if (!entry.TryGetProperty("expectedDocumentTypes", out var typesEl) || typesEl.ValueKind != JsonValueKind.Array || typesEl.GetArrayLength() == 0)
    {
        Console.Error.WriteLine($"FATAL: manifest entry '{Required("title")}' must declare a non-empty expectedDocumentTypes array.");
        Environment.Exit(3);
    }
    var expectedTypes = typesEl.EnumerateArray()
        .Where(e => e.ValueKind == JsonValueKind.String)
        .Select(e => e.GetString()!)
        .ToList();

    var title = Required("title");
    // Guard the naive title→slug derivation (`Replace(' ', '-')`) used
    // in the catalog match below. If a manifest title contains
    // characters outside `[A-Za-z0-9 ]`, the derived slug won't match
    // the catalog's slug field (e.g., "AC/DC" → "ac/dc", not "ac-dc").
    // Surface as a fatal error rather than a silent false-negative
    // ⚠️ "partial coverage" report. If a future slate entry needs
    // these characters, replace this guard with a proper slugifier.
    foreach (var ch in title)
    {
        if (!(char.IsLetterOrDigit(ch) || ch == ' '))
        {
            Console.Error.WriteLine($"FATAL: manifest entry '{title}' contains character '{ch}' outside the supported [A-Za-z0-9 ] range. Either rewrite the title or extend this script with a real slugifier.");
            Environment.Exit(3);
        }
    }

    machines.Add(new ManifestMachine(
        Title: title,
        ManufacturerSlug: slug,
        ManufacturerHost: manufacturerHosts[slug],
        ExpectedDocumentTypes: expectedTypes));
}

if (machines.Count == 0)
{
    Console.Error.WriteLine("FATAL: manifest contains zero machines — nothing to verify.");
    Environment.Exit(3);
}

int sternCount = machines.Count(m => m.ManufacturerSlug == "stern");
Console.WriteLine($"Loaded manifest: {machines.Count} machines, {sternCount} Stern.");

// ---- Load catalog ---------------------------------------------------------

if (!File.Exists(catalogPath))
{
    Console.Error.WriteLine($"FATAL: catalog not found at '{catalogPath}'.");
    Environment.Exit(2);
}

JsonDocument catalogDoc;
try
{
    catalogDoc = JsonDocument.Parse(File.ReadAllText(catalogPath));
}
catch (JsonException ex)
{
    Console.Error.WriteLine($"FATAL: catalog at '{catalogPath}' is not valid JSON: {ex.Message}");
    Environment.Exit(2);
    throw;
}

if (!catalogDoc.RootElement.TryGetProperty("documents", out var docsEl) || docsEl.ValueKind != JsonValueKind.Array)
{
    Console.Error.WriteLine("FATAL: catalog missing top-level `documents` array.");
    Environment.Exit(2);
}

var catalogDocs = docsEl.EnumerateArray()
    .Select(d => new CatalogDoc(
        DocumentId: d.GetProperty("document_id").GetString() ?? "",
        DocumentType: d.TryGetProperty("classification", out var cl) && cl.TryGetProperty("document_type", out var dt) ? dt.GetString() ?? "" : "",
        DiscoveryUrlHost: TryHost(d.TryGetProperty("source", out var src) && src.TryGetProperty("discovery_url", out var u) ? u.GetString() : null),
        GameTitle: d.TryGetProperty("game", out var g) && g.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "",
        GameSlug: d.TryGetProperty("game", out var g2) && g2.TryGetProperty("slug", out var s) ? s.GetString() ?? "" : ""))
    .ToList();

Console.WriteLine($"Loaded catalog: {catalogDocs.Count} documents.");
Console.WriteLine();

static string TryHost(string? url)
{
    if (string.IsNullOrWhiteSpace(url)) return string.Empty;
    if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return string.Empty;
    return uri.Host.ToLowerInvariant();
}

// ---- Per-machine coverage check ------------------------------------------

int sternStrictFailures = 0;
int totalMissingCoverage = 0;

foreach (var m in machines)
{
    // Filter catalog to docs from the manufacturer's host. Empty list
    // indicates the manufacturer hasn't been scraped into this catalog
    // snapshot — distinct from "scraped but no matching machine title".
    var hostDocs = catalogDocs.Where(d => d.DiscoveryUrlHost.Contains(m.ManufacturerHost, StringComparison.OrdinalIgnoreCase)).ToList();
    if (hostDocs.Count == 0)
    {
        Console.WriteLine($"⏸ {m.Title} ({m.ManufacturerSlug}) — no {m.ManufacturerSlug} docs in catalog yet (awaiting non-Stern scrape into catalog or W3-2 scraped_documents container)");
        // Not counted as failure — the manifest's intent is preserved;
        // verification surface evolves with W3-2.
        continue;
    }

    // Title containment: case-insensitive substring match on game.title
    // OR game.slug. The slug is normalized (e.g., "ac-dc") so we
    // also try a lowercased+hyphenated form of the manifest title.
    var titleLower = m.Title.ToLowerInvariant();
    var titleSlug = titleLower.Replace(' ', '-');
    var matches = hostDocs.Where(d =>
        d.GameTitle.Contains(m.Title, StringComparison.OrdinalIgnoreCase)
        || d.GameSlug.Contains(titleSlug, StringComparison.OrdinalIgnoreCase)).ToList();

    if (matches.Count == 0)
    {
        Console.WriteLine($"🔴 {m.Title} ({m.ManufacturerSlug}) — no matching docs in catalog (host had {hostDocs.Count} docs but none matched title/slug)");
        totalMissingCoverage++;
        if (m.ManufacturerSlug == "stern" && strictStern)
        {
            sternStrictFailures++;
        }
        continue;
    }

    var typeStatuses = new List<string>();
    var missingTypes = new List<string>();
    foreach (var expected in m.ExpectedDocumentTypes)
    {
        var ofType = matches.Count(d => string.Equals(d.DocumentType, expected, StringComparison.OrdinalIgnoreCase));
        if (ofType > 0)
        {
            typeStatuses.Add($"{expected}={ofType}");
        }
        else
        {
            typeStatuses.Add($"{expected}=0");
            missingTypes.Add(expected);
        }
    }

    if (missingTypes.Count == 0)
    {
        Console.WriteLine($"✅ {m.Title} ({m.ManufacturerSlug}) — covered: {string.Join(", ", typeStatuses)}");
    }
    else
    {
        Console.WriteLine($"⚠️ {m.Title} ({m.ManufacturerSlug}) — partial: {string.Join(", ", typeStatuses)}; missing {string.Join(", ", missingTypes)}");
        totalMissingCoverage++;
        if (m.ManufacturerSlug == "stern" && strictStern)
        {
            sternStrictFailures++;
        }
    }
}

Console.WriteLine();
Console.WriteLine($"Summary: {machines.Count} machines verified; {totalMissingCoverage} with missing/partial coverage.");

if (strictStern && sternStrictFailures > 0)
{
    Console.WriteLine($"FAIL (strict-stern): {sternStrictFailures} Stern machine(s) failed coverage.");
    Environment.Exit(1);
}

Console.WriteLine("OK (report-only mode; non-Stern coverage validates after W3-2 lands the scraped_documents container).");
Environment.Exit(0);

// ---- Types ----------------------------------------------------------------

public sealed record ManifestMachine(
    string Title,
    string ManufacturerSlug,
    string ManufacturerHost,
    IReadOnlyList<string> ExpectedDocumentTypes);

public sealed record CatalogDoc(
    string DocumentId,
    string DocumentType,
    string DiscoveryUrlHost,
    string GameTitle,
    string GameSlug);
