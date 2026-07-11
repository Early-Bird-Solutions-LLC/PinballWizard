using System.Text.RegularExpressions;
using Xunit;

namespace PinballWizard.Web.Tests.StaticAssets;

// Guards the "no JS mutation of Blazor/MudBlazor-owned DOM" invariant.
//
// Why this exists: a collapsible-sidebar attempt (later reverted) shipped JS that
// did `document.querySelector('.mud-drawer').style.width = …` and
// `document.querySelector('.mud-main-content').style.paddingLeft = …`. Blazor and
// MudBlazor OWN those elements — Blazor reconciles the drawer/main-content DOM
// against its render tree, and MudBlazor already drives the very same width and
// margin off CSS variables (`--mud-drawer-width-left` / `--mud-drawer-width-mini-left`,
// verified in MudBlazor 9.5.0 MudBlazor.min.css). Hand-written inline styles fight
// that reconciliation and stop the admin circuit from hydrating — the whole page
// then renders but never becomes interactive.
//
// That failure mode is nearly invisible to the rest of the suite: bUnit hand-
// registers DI and never runs the real first-paint scripts; the a11y/snapshot host
// serves no JS; and the CI Circuit tests catch only the SYMPTOM (a dead circuit),
// after the fact and with a misleading signal (they look like a DI/render failure,
// not "the JS mutated Mud DOM"). This test catches the PATTERN at its source,
// deterministically, with no browser.
//
// The rule: app-authored JavaScript may toggle classes / data-attributes / CSS
// custom properties on `<html>` (document.documentElement) and may read or write
// the app's own `[data-testid]` / `[data-pw-*]` hook elements, but must NEVER
// select or mutate a MudBlazor-rendered element (any `.mud-*` class). To influence
// MudBlazor layout, set the CSS variable it already consumes (in CSS, not JS) or
// bind the component parameter — never reach into its DOM.
//
// Scope — every place app-authored JS can live in the Web project, so the guard
// cannot be sidestepped by moving the code:
//   • every *.js under wwwroot (the original vector — admin-nav-collapse.js lived here)
//   • every colocated Blazor JS-isolation file (Components/**/*.razor.js)
//   • every inline <script> block in any component (Components/**/*.razor) — the most
//     natural next-attempt spot ("I'll just drop a <script> in AdminLayout")
// The framework's own bundles (blazor.web.js, MudBlazor.min.js) are referenced via
// <script src=…>, which the inline-block matcher excludes, and are not app-authored
// source here — naturally out of scope.
//
// Sibling posture: ScopedCssBundleTests / CspPolicySyncTests / RenderModeConventionTests
// — deterministic static-analysis gates over the Web project's assets and conventions.
public sealed class NoJsMutationOfBlazorOwnedDomTests
{
    // A `mud-` class token appearing in JS. MudBlazor prefixes every rendered class
    // with `mud-`; app JS has no legitimate reason to name one, so the mere presence
    // of the token in a script is the violation (the reach is the hazard, whether the
    // mutation is `.style`, `.setAttribute`, `.classList`, `.innerHTML`, or a removal).
    // Bare `mud-` (no trailing letter class) so a split selector — `'.mud-' + 'drawer'`,
    // `` `${'mud-'}main-content` `` — can't slip through; `MudBlazor` (no hyphen) and
    // words like "muddy" don't match, and no legitimate app string here contains `mud-`.
    private static readonly Regex MudSelectorToken =
        new(@"mud-", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // `// line` and `/* block */` comments — stripped before scanning so an explanatory
    // note that names a Mud class doesn't trip the guard; only executable text counts.
    private static readonly Regex CommentSpan =
        new(@"//[^\n]*|/\*.*?\*/", RegexOptions.Singleline | RegexOptions.Compiled);

    // Inline <script>…</script> bodies in a .razor host page (excludes <script src=…>).
    private static readonly Regex InlineScriptBlock =
        new(@"<script(?![^>]*\bsrc=)[^>]*>(.*?)</script>",
            RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

    [Fact]
    public void AppAuthoredJavaScript_DoesNotReachIntoMudBlazorOwnedDom()
    {
        var offenders = new List<string>();

        foreach (var (source, body) in AppAuthoredScripts())
        {
            // Strip comments across the whole body first (block comments can span
            // lines), then scan line-by-line so offenders keep a useful line number.
            var stripped = CommentSpan.Replace(body, m => BlankButKeepNewlines(m.Value));
            var strippedLines = ScriptLines(stripped).ToList();
            var rawLines = ScriptLines(body).ToList();

            for (var i = 0; i < strippedLines.Count; i++)
            {
                if (MudSelectorToken.IsMatch(strippedLines[i].Text))
                {
                    offenders.Add($"{source}:{rawLines[i].Number}: {rawLines[i].Text.Trim()}");
                }
            }
        }

        Assert.True(
            offenders.Count == 0,
            "App-authored JavaScript must never select or mutate MudBlazor-owned DOM (any `.mud-*` "
            + "element). Blazor reconciles that DOM against its render tree and MudBlazor drives its "
            + "layout off CSS variables (e.g. --mud-drawer-width-left); reaching in with JS fights "
            + "hydration and can silently kill the admin circuit. Toggle a class / CSS custom property "
            + "on <html> or set the CSS variable MudBlazor already consumes instead. Offending lines:"
            + Environment.NewLine
            + string.Join(Environment.NewLine, offenders));
    }

    // The corollary guard: prove the invariant is real by asserting the sanctioned
    // seam stays in use. App JS drives layout/theme state through the <html> element
    // (`document.documentElement`) — if a refactor ever removed that seam, the rule
    // above would be trivially satisfiable by having no layout JS at all, which is not
    // the intent. Checking ALL app-authored scripts (not only App.razor's inline
    // block) keeps this honest across a valid "externalize the IIFE to app.js" move.
    [Fact]
    public void AppAuthoredJavaScript_DrivesLayoutStateThroughDocumentElement()
    {
        var scripts = AppAuthoredScripts().ToList();

        // There must be app-authored JS, and at least one script must operate on
        // documentElement — the sanctioned surface for layout/theme state.
        Assert.NotEmpty(scripts);
        Assert.Contains(scripts, s => s.Body.Contains("documentElement", StringComparison.Ordinal));
    }

    private static IEnumerable<(string Source, string Body)> AppAuthoredScripts()
    {
        var web = WebProjectRoot();

        // wwwroot/*.js (the original vector) + colocated *.razor.js JS-isolation files
        // anywhere under the project. .razor.js live next to their component, not in
        // wwwroot, so both roots must be walked.
        var jsFiles = Directory
            .EnumerateFiles(Path.Combine(web, "wwwroot"), "*.js", SearchOption.AllDirectories)
            .Concat(Directory.EnumerateFiles(web, "*.razor.js", SearchOption.AllDirectories))
            .Where(NotBuildOutput)
            .Distinct();

        foreach (var js in jsFiles)
        {
            yield return (Relative(js), File.ReadAllText(js));
        }

        // Inline <script> blocks in ANY component (App.razor's first-paint IIFE today,
        // but a future collapse attempt could just as easily land in a layout).
        var razorFiles = Directory
            .EnumerateFiles(Path.Combine(web, "Components"), "*.razor", SearchOption.AllDirectories)
            .Where(NotBuildOutput);

        foreach (var razor in razorFiles)
        {
            foreach (Match m in InlineScriptBlock.Matches(File.ReadAllText(razor)))
            {
                yield return ($"{Relative(razor)} (inline <script>)", m.Groups[1].Value);
            }
        }
    }

    private static bool NotBuildOutput(string path) =>
        !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
        && !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase);

    // Replace a matched comment span with blank text but preserve its newlines, so
    // line numbers downstream still line up with the original body.
    private static string BlankButKeepNewlines(string comment) =>
        new(comment.Select(c => c == '\n' ? '\n' : ' ').ToArray());

    private static IEnumerable<(int Number, string Text)> ScriptLines(string body)
    {
        var lines = body.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            yield return (i + 1, lines[i]);
        }
    }

    private static string Relative(string absolute) =>
        Path.GetRelativePath(RepoRoot(), absolute).Replace('\\', '/');

    private static string WebProjectRoot() =>
        Path.Combine(RepoRoot(), "src", "PinballWizard.Web");

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "PinballWizard.slnx")))
        {
            dir = dir.Parent;
        }
        if (dir is null)
        {
            throw new InvalidOperationException(
                "Could not locate repo root (no PinballWizard.slnx found walking up from test assembly).");
        }
        return dir.FullName;
    }
}
