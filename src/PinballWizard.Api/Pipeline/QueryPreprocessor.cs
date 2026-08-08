using System.Text.RegularExpressions;

namespace PinballWizard.Api.Pipeline;

public sealed record PreprocessedQuery
{
    public required string OriginalQuery { get; init; }
    public required string ExpandedQuery { get; init; }
    public required QueryIntent Intent { get; init; }
    public List<string> GameSlugs { get; init; } = [];
    public List<string> Filters { get; init; } = [];
}

public enum QueryIntent
{
    Repair,
    Rules,
    History,
    General,
    Comparison,
    Troubleshooting,
    Strategy,
    Parts,
    Setup
}

public interface IQueryPreprocessor
{
    PreprocessedQuery Process(string query, string? gameFilter);
}

public sealed partial class QueryPreprocessor : IQueryPreprocessor
{
    private static readonly Dictionary<QueryIntent, string[]> IntentKeywords = new()
    {
        [QueryIntent.Repair] = ["fix", "repair", "broken", "replace", "worn", "stuck", "rebuild", "restore", "solder"],
        [QueryIntent.Troubleshooting] = ["troubleshoot", "diagnose", "problem", "issue", "error", "not working", "won't", "doesn't", "fault"],
        [QueryIntent.Rules] = ["rules", "rulesheet", "rule sheet", "how to play", "scoring", "modes", "multiball", "wizard mode", "combo"],
        [QueryIntent.History] = ["history", "when was", "who made", "year", "production", "manufacturer", "designed by", "created"],
        [QueryIntent.Comparison] = ["compare", "versus", "vs", "difference between", "better", "which one"],
        [QueryIntent.Strategy] = ["strategy", "tips", "how to beat", "high score", "best approach", "technique"],
        [QueryIntent.Parts] = ["part number", "parts list", "where to buy", "replacement part", "schematic", "wiring"],
        [QueryIntent.Setup] = ["setup", "install", "calibrate", "adjust", "configure", "level", "alignment"]
    };

    private static readonly Dictionary<string, string[]> Synonyms = new()
    {
        ["flipper"] = ["flipper", "flipper bat", "flipper assembly", "flipper coil"],
        ["bumper"] = ["bumper", "pop bumper", "jet bumper", "thumper bumper"],
        ["slingshot"] = ["slingshot", "sling", "kicker"],
        ["coil"] = ["coil", "solenoid"],
        ["target"] = ["target", "drop target", "standup target", "bullseye"],
        ["ramp"] = ["ramp", "wireform", "wire ramp"],
        ["switch"] = ["switch", "leaf switch", "microswitch", "opto", "optic switch"],
        ["display"] = ["display", "DMD", "dot matrix", "LCD", "score display", "backglass"],
        ["playfield"] = ["playfield", "playing field", "play surface"],
        ["rubber"] = ["rubber", "rubber ring", "rubber band", "rubber kit"],
    };

    // Well-known pinball games for extraction
    private static readonly Dictionary<string, string> KnownGames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["medieval madness"] = "medieval-madness",
        ["attack from mars"] = "attack-from-mars",
        ["theatre of magic"] = "theatre-of-magic",
        ["the addams family"] = "the-addams-family",
        ["twilight zone"] = "twilight-zone",
        ["monster bash"] = "monster-bash",
        ["tales of the arabian nights"] = "tales-of-the-arabian-nights",
        ["cirqus voltaire"] = "cirqus-voltaire",
        ["scared stiff"] = "scared-stiff",
        ["iron maiden"] = "iron-maiden",
        ["jurassic park"] = "jurassic-park",
        ["godzilla"] = "godzilla",
        ["foo fighters"] = "foo-fighters",
        ["rush"] = "rush",
        ["led zeppelin"] = "led-zeppelin",
        ["deadpool"] = "deadpool",
        ["venom"] = "venom",
        ["spider-man"] = "spider-man",
        ["batman"] = "batman",
        ["indiana jones"] = "indiana-jones",
        ["star wars"] = "star-wars",
        ["star trek"] = "star-trek",
        ["lord of the rings"] = "lord-of-the-rings",
        ["game of thrones"] = "game-of-thrones",
        ["metallica"] = "metallica",
        ["ac/dc"] = "ac-dc",
        ["guns n roses"] = "guns-n-roses",
        ["the mandalorian"] = "the-mandalorian",
        ["jaws"] = "jaws",
        ["james bond"] = "james-bond",
        ["alien"] = "alien",
        ["the wizard of oz"] = "the-wizard-of-oz",
        ["whitewater"] = "whitewater",
        ["black knight"] = "black-knight",
        ["terminator 2"] = "terminator-2",
    };

    public PreprocessedQuery Process(string query, string? gameFilter)
    {
        var intent = DetectIntent(query);
        var gameSlugs = ExtractGameSlugs(query, gameFilter);
        var expandedQuery = ExpandQuery(query, intent);
        var filters = BuildFilters(intent);

        return new PreprocessedQuery
        {
            OriginalQuery = query,
            ExpandedQuery = expandedQuery,
            Intent = intent,
            GameSlugs = gameSlugs,
            Filters = filters
        };
    }

    internal static QueryIntent DetectIntent(string query)
    {
        var lower = query.ToLowerInvariant();

        // Score each intent by keyword matches
        var scores = new Dictionary<QueryIntent, int>();
        foreach (var (intent, keywords) in IntentKeywords)
        {
            var score = keywords.Count(kw => lower.Contains(kw, StringComparison.OrdinalIgnoreCase));
            if (score > 0)
                scores[intent] = score;
        }

        if (scores.Count == 0)
            return QueryIntent.General;

        return scores.OrderByDescending(kv => kv.Value).First().Key;
    }

    internal static List<string> ExtractGameSlugs(string query, string? gameFilter)
    {
        var slugs = new List<string>();

        // If explicit filter provided, use it first
        if (!string.IsNullOrWhiteSpace(gameFilter))
        {
            slugs.Add(ToSlug(gameFilter));
        }

        // Try to find known game names in the query
        var lower = query.ToLowerInvariant();
        foreach (var (name, slug) in KnownGames)
        {
            if (lower.Contains(name, StringComparison.OrdinalIgnoreCase) && !slugs.Contains(slug))
            {
                slugs.Add(slug);
            }
        }

        return slugs;
    }

    internal static string ExpandQuery(string query, QueryIntent intent)
    {
        var parts = new List<string> { query };

        // Add intent-specific terms
        var intentTerms = intent switch
        {
            QueryIntent.Repair => "repair troubleshooting fix maintenance",
            QueryIntent.Troubleshooting => "troubleshooting diagnosis error fix",
            QueryIntent.Rules => "rules scoring modes gameplay",
            QueryIntent.History => "history production manufacturer year",
            QueryIntent.Strategy => "strategy tips technique scoring",
            QueryIntent.Parts => "parts schematic wiring diagram",
            QueryIntent.Setup => "setup installation calibration adjustment",
            _ => ""
        };

        if (!string.IsNullOrEmpty(intentTerms))
            parts.Add(intentTerms);

        // Add synonyms for pinball-specific terms found in query
        var lower = query.ToLowerInvariant();
        foreach (var (term, syns) in Synonyms)
        {
            if (lower.Contains(term, StringComparison.OrdinalIgnoreCase))
            {
                parts.AddRange(syns.Where(s => !lower.Contains(s, StringComparison.OrdinalIgnoreCase)));
            }
        }

        return string.Join(" ", parts);
    }

    private static List<string> BuildFilters(QueryIntent intent)
    {
        return intent switch
        {
            QueryIntent.Repair or QueryIntent.Troubleshooting => ["Manual", "RepairGuide", "ServiceBulletin"],
            QueryIntent.Rules or QueryIntent.Strategy => ["Rulesheet", "StrategyGuide", "Manual"],
            QueryIntent.Parts => ["Manual", "Schematic", "SpecSheet"],
            QueryIntent.History => ["WikiArticle", "MachineRecord"],
            _ => []
        };
    }

    private static string ToSlug(string input)
    {
        var slug = input.ToLowerInvariant().Trim();
        slug = SlugRegex().Replace(slug, "-");
        slug = MultiDashRegex().Replace(slug, "-");
        return slug.Trim('-');
    }

    [GeneratedRegex(@"[^a-z0-9\-]")]
    private static partial Regex SlugRegex();

    [GeneratedRegex(@"-{2,}")]
    private static partial Regex MultiDashRegex();
}
