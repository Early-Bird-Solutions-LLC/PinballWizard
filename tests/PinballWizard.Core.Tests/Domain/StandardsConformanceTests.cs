using System.Text.RegularExpressions;
using Xunit;

namespace PinballWizard.Core.Tests.Domain;

/// <summary>
/// Standing-state guard for the .claude/standards system. Asserts the rule
/// namespace is well-formed: unique append-only IDs, every rule indexed in its
/// REQUIREMENTS.md, and every INVARIANTS.md entry tracked (links a rule or is
/// marked "standard pending"). Mirrors DocConformanceTests' repo-root pattern.
/// </summary>
public sealed class StandardsConformanceTests
{
    private static string StandardsDir() =>
        Path.Combine(DocConformanceTests.FindRepoRoot(), ".claude", "standards");

    // \s*$ absorbs a trailing \r on CRLF files (a bare $ after \) would not
    // match when the line ends \r\n, since ) is not immediately before $).
    private static readonly Regex RuleHeader = new(
        @"^\*\*RULE ([A-Z]+-\d{2})\*\* \(([a-z0-9-]+)\)\s*$",
        RegexOptions.Compiled | RegexOptions.Multiline);

    private static IEnumerable<string> StandardFiles() =>
        Directory.EnumerateFiles(StandardsDir(), "STANDARD.md", SearchOption.AllDirectories);

    [Fact]
    public void EveryRuleId_IsUnique()
    {
        var seen = new Dictionary<string, string>(StringComparer.Ordinal);
        var dupes = new List<string>();

        foreach (var file in StandardFiles())
        {
            var text = File.ReadAllText(file);
            foreach (Match m in RuleHeader.Matches(text))
            {
                var id = m.Groups[1].Value;
                if (seen.TryGetValue(id, out var first))
                    dupes.Add($"{id} in {file} (also in {first})");
                else
                    seen[id] = file;
            }
        }

        Assert.True(dupes.Count == 0,
            "Duplicate rule IDs (IDs are append-only and unique):\n  " + string.Join("\n  ", dupes));
        Assert.NotEmpty(seen);
    }

    [Fact]
    public void EveryRule_HasARowInItsRequirementsIndex()
    {
        var orphans = new List<string>();

        foreach (var file in StandardFiles())
        {
            var dir = Path.GetDirectoryName(file)!;
            var reqPath = Path.Combine(dir, "REQUIREMENTS.md");
            Assert.True(File.Exists(reqPath), $"Missing REQUIREMENTS.md next to {file}");
            var reqText = File.ReadAllText(reqPath);

            foreach (Match m in RuleHeader.Matches(File.ReadAllText(file)))
            {
                var id = m.Groups[1].Value;
                if (!reqText.Contains(id, StringComparison.Ordinal))
                    orphans.Add($"{id} ({Path.GetFileName(dir)}) — not indexed in REQUIREMENTS.md");
            }
        }

        Assert.True(orphans.Count == 0,
            "Rules with no REQUIREMENTS.md row:\n  " + string.Join("\n  ", orphans));
    }

    [Fact]
    public void EveryInvariantEntry_IsTracked()
    {
        var root = DocConformanceTests.FindRepoRoot();
        var invariants = File.ReadAllLines(Path.Combine(root, ".claude", "INVARIANTS.md"));

        // A numbered entry line starts with "<n>. ". It is tracked if it
        // references a real rule ID or carries the pending marker. The prefix
        // set is explicit so an ADR link (e.g. ADR-0012) is NOT mistaken for a
        // rule reference — only a genuine PROV-/POLITE-/COSMOS-/OBS-/TEST-/DLV-
        // reference counts as "links a rule".
        var entryStart = new Regex(@"^\d+\.\s", RegexOptions.Compiled);
        var ruleRef = new Regex(@"\b(PROV|POLITE|COSMOS|OBS|TEST|DLV)-\d{2}\b", RegexOptions.Compiled);

        var untracked = invariants
            .Where(l => entryStart.IsMatch(l))
            .Where(l => !ruleRef.IsMatch(l) &&
                        !l.Contains("standard pending", StringComparison.OrdinalIgnoreCase))
            .ToList();

        Assert.True(untracked.Count == 0,
            "INVARIANTS.md entries that neither link a rule nor are marked 'standard pending':\n  "
            + string.Join("\n  ", untracked));
    }
}
