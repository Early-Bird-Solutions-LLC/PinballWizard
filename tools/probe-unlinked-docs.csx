#!/usr/bin/env dotnet-script
#r "nuget: PdfPig, 0.1.9"
#r "nuget: System.Text.Json, 9.0.0"

using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using UglyToad.PdfPig;

// ── Load catalog and games ────────────────────────────────────────────────────

var catalogJson = await File.ReadAllTextAsync("data/metadata/catalog.json");
var gamesJson   = await File.ReadAllTextAsync("data/metadata/games.json");

var catalogDoc  = JsonDocument.Parse(catalogJson);
var gamesDoc    = JsonDocument.Parse(gamesJson);

var games = gamesDoc.RootElement.GetProperty("games").EnumerateArray()
    .Select(g => new {
        Slug  = g.GetProperty("slug").GetString()!,
        Title = g.GetProperty("title").GetString()!
    })
    .Where(g => !string.IsNullOrEmpty(g.Slug) && !string.IsNullOrEmpty(g.Title))
    .ToList();

static string Norm(string s) =>
    Regex.Replace(s.ToLowerInvariant(), @"[^a-z0-9]+", " ").Trim();

var titleToSlug = games
    .GroupBy(g => Norm(g.Title))
    .ToDictionary(grp => grp.Key, grp => grp.Select(g => g.Slug).ToList());

var slugToSlug = games
    .GroupBy(g => Norm(g.Slug))
    .ToDictionary(grp => grp.Key, grp => grp.Select(g => g.Slug).ToList());

var unlinked = catalogDoc.RootElement.GetProperty("documents")
    .EnumerateArray()
    .Where(d => {
        if (!d.TryGetProperty("game", out var game)) return true;
        if (game.ValueKind == JsonValueKind.Null) return true;
        if (!game.TryGetProperty("slug", out var slug)) return true;
        return string.IsNullOrEmpty(slug.GetString());
    })
    .Select(d => new {
        DocumentId = d.GetProperty("document_id").GetString()!,
        FileUrl    = d.TryGetProperty("source", out var src)  && src.TryGetProperty("file_url",    out var fu) ? fu.GetString() ?? "" : "",
        LinkText   = d.TryGetProperty("source", out var src2) && src2.TryGetProperty("link_text",  out var lt) ? lt.GetString() ?? "" : "",
        SourceType = d.TryGetProperty("source", out var src3) && src3.TryGetProperty("source_type",out var st) ? st.GetString() ?? "" : ""
    })
    .Where(d => !string.IsNullOrEmpty(d.FileUrl))
    .ToList();

Console.WriteLine($"Unlinked docs to probe: {unlinked.Count}");
Console.WriteLine($"Games in catalog: {games.Count}");
Console.WriteLine();

// ── HTTP client ───────────────────────────────────────────────────────────────

var http = new HttpClient();
http.DefaultRequestHeaders.UserAgent.ParseAdd(
    "PinballWizard/0.1-probe (+https://github.com/Early-Bird-Solutions-LLC/PinballWizard; research-only)");
http.Timeout = TimeSpan.FromSeconds(30);

// ── Match helper ──────────────────────────────────────────────────────────────

List<string> FindMatches(string pageOneText)
{
    var normText = Norm(pageOneText);
    var matched  = new HashSet<string>();

    foreach (var (normTitle, slugs) in titleToSlug)
    {
        if (normTitle.Length < 4) continue;
        if (normText.Contains(normTitle))
            foreach (var s in slugs) matched.Add(s);
    }
    foreach (var (normSlug, slugs) in slugToSlug)
    {
        if (normSlug.Length < 4) continue;
        if (normText.Contains(normSlug))
            foreach (var s in slugs) matched.Add(s);
    }
    return matched.Order().ToList();
}

// ── Probe loop ────────────────────────────────────────────────────────────────

// Result type as a class (dotnet-script 2.x dislikes top-level record + using var in same scope)
class Result {
    public string DocId { get; init; } = "";
    public string SourceType { get; init; } = "";
    public string LinkText { get; init; } = "";
    public string FileUrl { get; init; } = "";
    public string Status { get; init; } = "";
    public List<string> Matches { get; init; } = new();
    public string Page1Snippet { get; init; } = "";
    public string Error { get; init; } = "";
}

var results = new List<Result>();
int done = 0;

foreach (var doc in unlinked)
{
    done++;
    Console.Write($"[{done,3}/{unlinked.Count}] {doc.DocumentId} ... ");

    string status = "unknown", snippet = "", error = "";
    var matches = new List<string>();

    try
    {
        using var resp = await http.GetAsync(doc.FileUrl, HttpCompletionOption.ResponseContentRead);
        if (!resp.IsSuccessStatusCode)
        {
            status = $"http_{(int)resp.StatusCode}";
            error  = resp.ReasonPhrase ?? "";
            Console.WriteLine($"HTTP {(int)resp.StatusCode}");
        }
        else
        {
            var bytes = await resp.Content.ReadAsByteArrayAsync();
            using var ms = new MemoryStream(bytes);
            try
            {
                using var pdf = PdfDocument.Open(ms);
                var text = pdf.GetPage(1).Text;
                snippet  = Regex.Replace(text.Length > 300 ? text[..300] : text, @"\s+", " ").Trim();
                matches  = FindMatches(text);
                status   = matches.Count switch { 0 => "no_match", 1 => "single_match", _ => "multi_match" };
                Console.WriteLine($"{status} [{string.Join(", ", matches)}]");
            }
            catch (Exception ex) { status = "pdf_error"; error = ex.Message; Console.WriteLine($"PDF error: {ex.Message}"); }
        }
    }
    catch (Exception ex) { status = "fetch_error"; error = ex.Message; Console.WriteLine($"Fetch error: {ex.Message}"); }

    results.Add(new Result {
        DocId = doc.DocumentId, SourceType = doc.SourceType,
        LinkText = doc.LinkText.Replace("\n", " ").Trim(), FileUrl = doc.FileUrl,
        Status = status, Matches = matches, Page1Snippet = snippet, Error = error
    });

    if (done < unlinked.Count) await Task.Delay(500);
}

// ── Summary ───────────────────────────────────────────────────────────────────

Console.WriteLine();
Console.WriteLine("══════════════════════════════════════════════════════");
Console.WriteLine("  RESULTS SUMMARY");
Console.WriteLine("══════════════════════════════════════════════════════");

foreach (var grp in results.GroupBy(r => r.Status).OrderByDescending(g => g.Count()))
    Console.WriteLine($"  {grp.Key,-20} {grp.Count(),3}");

Console.WriteLine();
Console.WriteLine("── Single matches (definitive) ──────────────────────");
foreach (var r in results.Where(r => r.Status == "single_match").OrderBy(r => r.Matches[0]))
    Console.WriteLine($"  {r.Matches[0],-40} {r.LinkText[..Math.Min(60, r.LinkText.Length)]}");

Console.WriteLine();
Console.WriteLine("── Multi-matches (review needed) ────────────────────");
foreach (var r in results.Where(r => r.Status == "multi_match"))
{
    Console.WriteLine($"  [{string.Join(", ", r.Matches)}]");
    Console.WriteLine($"    {r.LinkText[..Math.Min(80, r.LinkText.Length)]}");
}

Console.WriteLine();
Console.WriteLine("── No match ─────────────────────────────────────────");
foreach (var r in results.Where(r => r.Status == "no_match"))
{
    Console.WriteLine($"  {r.DocId}  {r.LinkText[..Math.Min(60, r.LinkText.Length)]}");
    if (r.Page1Snippet.Length > 0)
        Console.WriteLine($"    page1: {r.Page1Snippet[..Math.Min(120, r.Page1Snippet.Length)]}");
}

Console.WriteLine();
Console.WriteLine("── Errors ───────────────────────────────────────────");
foreach (var r in results.Where(r => r.Status is "fetch_error" or "pdf_error" || r.Status.StartsWith("http_")))
    Console.WriteLine($"  {r.Status,-15} {r.Error}  {r.FileUrl[..Math.Min(70, r.FileUrl.Length)]}");

// ── Write JSON ────────────────────────────────────────────────────────────────

var outPath = $"data/eval/results/probe-unlinked-{DateTime.UtcNow:yyyyMMddTHHmmss}Z.json";
Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
await File.WriteAllTextAsync(outPath, JsonSerializer.Serialize(
    results.Select(r => new { r.DocId, r.SourceType, r.LinkText, r.FileUrl, r.Status, r.Matches, r.Page1Snippet, r.Error }),
    new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine($"\nFull results → {outPath}");
