using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using Markdig;

namespace PinballWizard.Web.Engineering;

/// <summary>
/// Loads and parses all engineering docs and ADRs from the Web assembly's embedded
/// resources exactly once in the constructor. Registered as a singleton so every
/// subsequent call to <see cref="Docs"/>, <see cref="Adrs"/>, <see cref="BySlug"/>, or
/// <see cref="ByNumber"/> is a pure in-memory read with zero I/O.
///
/// Logical name conventions (from PinballWizard.Web.csproj):
///   - Manifest:  PinballWizard.Web.docs.engineering-manifest.json
///   - Doc pages: PinballWizard.Web.docs.{slug}.md
///   - ADRs:      PinballWizard.Web.docs.adr.{filename}.md
/// </summary>
public sealed partial class EngineeringDocsProvider : IEngineeringDocsProvider
{
    private const string GitHubBase = "https://github.com/Early-Bird-Solutions-LLC/PinballWizard/blob/main/";
    private const string AdrPrefix = "PinballWizard.Web.docs.adr.";
    private const string ManifestResource = "PinballWizard.Web.docs.engineering-manifest.json";

    public IReadOnlyList<EngineeringDoc> Docs { get; }
    public IReadOnlyList<AdrEntry> Adrs { get; }
    public string SourceCommit { get; }
    public string BuildDate { get; }

    public EngineeringDocsProvider()
    {
        var assembly = typeof(EngineeringDocsProvider).Assembly;
        var pipeline = new MarkdownPipelineBuilder()
            .UsePipeTables()
            .UseAutoLinks()
            .Build();

        // --- Assembly-metadata: commit + build date stamped at build time ---------------
        // Keys verified from PinballWizard.Web.csproj StampEngineeringMetadata target:
        // _Parameter1 = "EngineeringSourceCommit" and "EngineeringBuildDate".
        var attrs = assembly.GetCustomAttributes<AssemblyMetadataAttribute>();
        SourceCommit = attrs.FirstOrDefault(a => a.Key == "EngineeringSourceCommit")?.Value
                       ?? string.Empty;
        BuildDate = attrs.FirstOrDefault(a => a.Key == "EngineeringBuildDate")?.Value
                    ?? string.Empty;

        // --- Manifest: slug/title/group/order/sourcePath --------------------------------
        // The manifest JSON is embedded as PinballWizard.Web.docs.engineering-manifest.json
        // so the provider needs no filesystem path at runtime.
        var manifestEntries = LoadManifest(assembly);

        // --- Docs: one embedded resource per manifest entry -----------------------------
        var docs = new List<EngineeringDoc>(manifestEntries.Count);
        foreach (var (slug, sourcePath, title, group, order) in manifestEntries)
        {
            // Logical name derived from slug — matches the explicit LogicalName attributes
            // set in the csproj EmbeddedResource items.
            var logicalName = $"PinballWizard.Web.docs.{slug}.md";
            var text = ReadResource(assembly, logicalName);
            if (text is null) continue;

            var ast = Markdig.Markdown.Parse(text, pipeline);
            var url = $"{GitHubBase}{sourcePath}";
            docs.Add(new EngineeringDoc(slug, title, group, order, ast, url));
        }
        Docs = docs.AsReadOnly();

        // --- ADRs: all embedded resources under the adr. prefix -------------------------
        var adrs = new List<AdrEntry>();
        var adrNames = assembly.GetManifestResourceNames()
            .Where(n => n.StartsWith(AdrPrefix, StringComparison.Ordinal))
            .OrderBy(n => n, StringComparer.Ordinal);

        foreach (var resourceName in adrNames)
        {
            // Filename portion after prefix: e.g. "0001-record-architecture-decisions.md"
            var filename = resourceName[AdrPrefix.Length..];

            var numberMatch = AdrNumberPattern().Match(filename);
            if (!numberMatch.Success) continue;
            var number = int.Parse(numberMatch.Groups[1].Value, CultureInfo.InvariantCulture);

            // Slug = filename without .md extension
            var slug = filename.EndsWith(".md", StringComparison.OrdinalIgnoreCase)
                ? filename[..^3]
                : filename;

            var text = ReadResource(assembly, resourceName);
            if (text is null) continue;

            var ast = Markdig.Markdown.Parse(text, pipeline);
            var (title, status, date) = ParseAdrMetadata(text);

            adrs.Add(new AdrEntry(number, title, status, date, slug, ast));
        }
        Adrs = adrs.AsReadOnly();
    }

    public EngineeringDoc? BySlug(string slug) =>
        Docs.FirstOrDefault(d => string.Equals(d.Slug, slug, StringComparison.OrdinalIgnoreCase));

    public AdrEntry? ByNumber(int number) =>
        Adrs.FirstOrDefault(a => a.Number == number);

    // ── Helpers ──────────────────────────────────────────────────────────────────────

    private static List<(string Slug, string SourcePath, string Title, string Group, int Order)> LoadManifest(
        Assembly assembly)
    {
        using var stream = assembly.GetManifestResourceStream(ManifestResource);
        if (stream is null) return [];

        using var doc = JsonDocument.Parse(stream);
        var list = new List<(string, string, string, string, int)>();
        foreach (var e in doc.RootElement.GetProperty("docs").EnumerateArray())
        {
            list.Add((
                e.GetProperty("slug").GetString()!,
                e.GetProperty("sourcePath").GetString()!,
                e.GetProperty("title").GetString()!,
                e.GetProperty("group").GetString()!,
                e.GetProperty("order").GetInt32()
            ));
        }
        return list;
    }

    private static string? ReadResource(Assembly assembly, string resourceName)
    {
        using var stream = assembly.GetManifestResourceStream(resourceName);
        if (stream is null) return null;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    /// <summary>
    /// Extracts Title, Status, and Date from an ADR's raw markdown text.
    /// All three fields fall back to <see cref="string.Empty"/> when absent so
    /// an ADR missing a Status or Date line does not throw.
    ///
    /// Expected ADR format (ADR-0001 is the reference):
    ///   # NNNN — Title text
    ///   **Status:** Accepted
    ///   **Date:** 2026-05-02
    /// </summary>
    private static (string Title, string Status, string Date) ParseAdrMetadata(string text)
    {
        // Leading H1 — everything after "# " on the first heading line
        var titleMatch = FirstH1Pattern().Match(text);
        var title = titleMatch.Success ? titleMatch.Groups[1].Value.Trim() : string.Empty;

        // **Status:** value — capture trailing text on the same line; missing = empty string
        var statusMatch = StatusLinePattern().Match(text);
        var status = statusMatch.Success ? statusMatch.Groups[1].Value.Trim() : string.Empty;

        // **Date:** value
        var dateMatch = DateLinePattern().Match(text);
        var date = dateMatch.Success ? dateMatch.Groups[1].Value.Trim() : string.Empty;

        return (title, status, date);
    }

    // Source-generated regex helpers (compile-once, no allocation per call)
    [GeneratedRegex(@"^#\s+(.+)$", RegexOptions.Multiline)]
    private static partial Regex FirstH1Pattern();

    [GeneratedRegex(@"\*\*Status:\*\*\s*(.+)", RegexOptions.IgnoreCase)]
    private static partial Regex StatusLinePattern();

    [GeneratedRegex(@"\*\*Date:\*\*\s*(.+)", RegexOptions.IgnoreCase)]
    private static partial Regex DateLinePattern();

    [GeneratedRegex(@"^(\d+)-")]
    private static partial Regex AdrNumberPattern();
}
