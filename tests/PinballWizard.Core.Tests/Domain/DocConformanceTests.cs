using System.Text.RegularExpressions;
using Xunit;

namespace PinballWizard.Core.Tests.Domain;

/// <summary>
/// Standing-state doc-conformance tests.  These guard against the silent drift
/// that point-in-time diff reviews miss: a project added to src/ but not to
/// CLAUDE.md, a stale project name left in CLAUDE.md after a rename, an ADR
/// written but never added to the README index.
///
/// Each test locates the repository root at runtime so the suite works from
/// any working directory (IDE test runner, CLI, CI).
/// </summary>
public sealed class DocConformanceTests
{
    // -------------------------------------------------------------------------
    // Repo-root helper
    // -------------------------------------------------------------------------

    /// <summary>
    /// Walks up from <see cref="AppContext.BaseDirectory"/> until a directory
    /// containing <c>PinballWizard.slnx</c> is found.  Fails with a clear
    /// message if the sentinel file cannot be located.
    /// </summary>
    internal static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "PinballWizard.slnx")))
                return dir.FullName;
            dir = dir.Parent;
        }

        throw new InvalidOperationException(
            "Could not locate the repository root by walking up from " +
            $"AppContext.BaseDirectory ({AppContext.BaseDirectory}). " +
            "Expected to find 'PinballWizard.slnx' in an ancestor directory.");
    }

    // -------------------------------------------------------------------------
    // Unit test for the repo-root helper itself
    // -------------------------------------------------------------------------

    [Fact]
    public void FindRepoRoot_ReturnsDirectoryContainingSlnx()
    {
        var root = FindRepoRoot();

        Assert.True(
            File.Exists(Path.Combine(root, "PinballWizard.slnx")),
            $"FindRepoRoot returned '{root}' but PinballWizard.slnx is not present there.");
    }

    // -------------------------------------------------------------------------
    // Test 1: every real src/ and tests/ project appears in CLAUDE.md
    // -------------------------------------------------------------------------

    [Fact]
    public void ClaudeMd_SolutionLayout_MentionsEveryRealProject()
    {
        // Guard: catches the "doc omits four projects" failure mode found
        // 2026-06-10 (Api, Web.Client, RagIngestionWorker, Web were absent).
        // Adding a new project without updating CLAUDE.md is a silent omission;
        // this test makes it a build-time failure.
        //
        // Convention: src/ projects are checked by their full directory name
        // (e.g. "PinballWizard.Api").  tests/ projects are checked by their
        // src-counterpart name (strip the ".Tests" suffix) because CLAUDE.md
        // documents them as "per-layer test projects" referencing the layer
        // by its src name rather than the full test-project name.

        var root = FindRepoRoot();
        var claudeMdPath = Path.Combine(root, "CLAUDE.md");
        Assert.True(File.Exists(claudeMdPath), $"CLAUDE.md not found at: {claudeMdPath}");

        var claudeMdContent = File.ReadAllText(claudeMdPath);

        var missing = new List<string>();

        // src/ — full directory name must appear
        var srcPath = Path.Combine(root, "src");
        if (Directory.Exists(srcPath))
        {
            foreach (var dir in Directory.GetDirectories(srcPath))
            {
                var name = Path.GetFileName(dir);
                if (!name.StartsWith("PinballWizard.", StringComparison.Ordinal)) continue;

                if (!claudeMdContent.Contains(name, StringComparison.Ordinal))
                    missing.Add($"src/{name}  (not found in CLAUDE.md)");
            }
        }

        // tests/ — check src-counterpart name (strip ".Tests" suffix)
        var testsPath = Path.Combine(root, "tests");
        if (Directory.Exists(testsPath))
        {
            foreach (var dir in Directory.GetDirectories(testsPath))
            {
                var name = Path.GetFileName(dir);
                if (!name.StartsWith("PinballWizard.", StringComparison.Ordinal)) continue;

                // Derive the src-counterpart name by stripping the ".Tests" suffix
                var srcCounterpart = name.EndsWith(".Tests", StringComparison.Ordinal)
                    ? name[..^".Tests".Length]
                    : name;

                if (!claudeMdContent.Contains(srcCounterpart, StringComparison.Ordinal))
                    missing.Add($"tests/{name}  (src-counterpart '{srcCounterpart}' not found in CLAUDE.md)");
            }
        }

        Assert.True(
            missing.Count == 0,
            "The following project directories have no mention in CLAUDE.md. " +
            "Update the 'Solution layout' section:\n  " +
            string.Join("\n  ", missing));
    }

    // -------------------------------------------------------------------------
    // Test 2: no phantom projects in CLAUDE.md's solution-layout fenced block
    // -------------------------------------------------------------------------

    [Fact]
    public void ClaudeMd_SolutionLayout_NamesNoPhantomProjects()
    {
        // Guard: catches stale references such as "PinballWizard.Scraper.Tests"
        // which remained in the layout block after the test projects were split
        // into seven per-layer projects (ADR-0030).
        //
        // Only the first ```text fenced block after the "Solution layout" heading
        // is inspected so incidental prose references elsewhere do not fire.

        var root = FindRepoRoot();
        var claudeMdPath = Path.Combine(root, "CLAUDE.md");
        var claudeMdContent = File.ReadAllText(claudeMdPath);

        var layoutBlock = ExtractFirstFencedBlock(claudeMdContent, "Solution layout");
        Assert.False(
            string.IsNullOrWhiteSpace(layoutBlock),
            "Could not find a ```text fenced block after the 'Solution layout' heading in CLAUDE.md. " +
            "If the heading was renamed, update this test.");

        // Collect every PinballWizard.<Name> token from the block; trim trailing dots
        var tokenPattern = new Regex(
            @"PinballWizard\.[A-Za-z.]+",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        var tokens = tokenPattern.Matches(layoutBlock)
            .Select(m => m.Value.TrimEnd('.'))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(t => t)
            .ToList();

        Assert.NotEmpty(tokens);

        // Build the set of real directory names across src/ and tests/
        var realDirs = new HashSet<string>(StringComparer.Ordinal);
        foreach (var searchRoot in new[] { "src", "tests" })
        {
            var searchPath = Path.Combine(root, searchRoot);
            if (!Directory.Exists(searchPath)) continue;

            foreach (var dir in Directory.GetDirectories(searchPath))
            {
                var name = Path.GetFileName(dir);
                if (name.StartsWith("PinballWizard.", StringComparison.Ordinal))
                    realDirs.Add(name);
            }
        }

        var phantoms = tokens
            .Where(t => !realDirs.Contains(t))
            .ToList();

        Assert.True(
            phantoms.Count == 0,
            "CLAUDE.md's solution-layout block references project name(s) with no corresponding " +
            "directory under src/ or tests/. Remove or rename these stale references:\n  " +
            string.Join("\n  ", phantoms));
    }

    // -------------------------------------------------------------------------
    // Test 3: docs/adr/README.md indexes every ADR file
    // -------------------------------------------------------------------------

    [Fact]
    public void AdrReadme_IndexesEveryAdrFile()
    {
        // Guard: catches an ADR committed without being added to the README
        // index.  The README is the navigable entry point; an un-indexed ADR
        // is effectively invisible to reviewers browsing docs/adr/.

        var root = FindRepoRoot();
        var adrDir = Path.Combine(root, "docs", "adr");
        Assert.True(Directory.Exists(adrDir), $"docs/adr/ not found at: {adrDir}");

        var readmePath = Path.Combine(adrDir, "README.md");
        Assert.True(File.Exists(readmePath), $"docs/adr/README.md not found at: {readmePath}");

        var readmeContent = File.ReadAllText(readmePath);

        // Collect all NNNN-*.md files (4-digit prefix; exclude README.md itself)
        var adrFilePattern = new Regex(@"^\d{4}-.*\.md$", RegexOptions.Compiled);
        var adrFiles = Directory.GetFiles(adrDir, "*.md")
            .Select(Path.GetFileName)
            .Where(f => adrFilePattern.IsMatch(f!))
            .OrderBy(f => f)
            .ToList();

        Assert.NotEmpty(adrFiles);

        var unindexed = adrFiles
            .Where(f => !readmeContent.Contains(f!, StringComparison.Ordinal))
            .ToList();

        Assert.True(
            unindexed.Count == 0,
            "The following ADR file(s) are not referenced by filename in docs/adr/README.md. " +
            "Add an index row for each:\n  " +
            string.Join("\n  ", unindexed));
    }

    // -------------------------------------------------------------------------
    // Test 4: CLAUDE.md contains no hardcoded ADR numeric range
    // -------------------------------------------------------------------------

    [Fact]
    public void ClaudeMd_DoesNotHardcodeAdrRange()
    {
        // Guard: CLAUDE.md used to say "ADRs live in docs/adr/ (0001–0013)" and
        // "docs/adr/ (0001–0028)".  Those ranges drifted silently as new ADRs
        // landed.  This branch removed them and points at the README index
        // instead; this test prevents the pattern from being re-introduced.

        var root = FindRepoRoot();
        var claudeMdPath = Path.Combine(root, "CLAUDE.md");
        var claudeMdContent = File.ReadAllText(claudeMdPath);

        // Pattern 1: four-digit range with em-dash, en-dash, or hyphen  e.g. "0001–0013"
        var dashRangePattern = new Regex(
            @"00\d\d[–\-]00\d\d",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        // Pattern 2: parenthesised range starting at 0001  e.g. "(0001–0030)"
        var parenRangePattern = new Regex(
            @"\(0001[–\-]\d{4}\)",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        var dashMatch = dashRangePattern.Match(claudeMdContent);
        var parenMatch = parenRangePattern.Match(claudeMdContent);

        Assert.True(
            !dashMatch.Success && !parenMatch.Success,
            "CLAUDE.md contains a hardcoded ADR numeric range that will drift as new ADRs are " +
            "added. Reference the README index instead of a hard numeric range. " +
            "Found: " + (dashMatch.Success ? $"'{dashMatch.Value}'" : $"'{parenMatch.Value}'"));
    }

    // -------------------------------------------------------------------------
    // Helper: extract first ```...``` fenced block after a heading keyword
    // -------------------------------------------------------------------------

    private static string ExtractFirstFencedBlock(string markdown, string afterHeading)
    {
        var headingIdx = markdown.IndexOf(afterHeading, StringComparison.OrdinalIgnoreCase);
        if (headingIdx < 0) return string.Empty;

        var openFence = markdown.IndexOf("```", headingIdx, StringComparison.Ordinal);
        if (openFence < 0) return string.Empty;

        // Move past the opening fence line (language specifier is irrelevant)
        var afterOpen = markdown.IndexOf('\n', openFence);
        if (afterOpen < 0) return string.Empty;
        afterOpen++;

        var closeFence = markdown.IndexOf("```", afterOpen, StringComparison.Ordinal);
        if (closeFence < 0) return string.Empty;

        return markdown[afterOpen..closeFence];
    }
}
