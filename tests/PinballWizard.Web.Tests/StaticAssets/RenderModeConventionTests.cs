using System.Text.RegularExpressions;
using Xunit;

namespace PinballWizard.Web.Tests.StaticAssets;

// Enforces the ADR-0034 render-mode doctrine: a routable page (@page) that
// carries a genuine interactivity signal MUST declare @rendermode, or its
// controls are silently dead on a static render (compiles fine, no runtime
// error, the control just never responds — the bug class the 2026-06-17
// amendment fixes).
//
// Signals checked: @onclick / OnClick= (event handlers), @bind-Value (two-way
// binding), IDialogService / .ShowAsync( / .ShowMessageBoxAsync( / <MudDialog
// (dialogs). Static-SSR-safe constructs are deliberately NOT flagged: EditForm
// + [SupplyParameterFromForm] (forms work under static SSR), plain Href/anchor
// navigation, and comment prose (comments are stripped before scanning).
//
// Scope is page-level by design. An interactive *component* hosted only on
// static pages (e.g. TiltErrorBoundary) needs a usage graph; that is the
// deferred stretch (ADR-0034 amendment §3.6) covered by the /local-review
// backstop. Precedent guardrail-as-test: LayoutProviderRenderModeTests,
// PreRenderedDiagramTests.
public sealed class RenderModeConventionTests
{
    private static readonly Regex CommentBlock =
        new(@"@\*.*?\*@", RegexOptions.Singleline | RegexOptions.Compiled);

    // Genuine interactivity signals. Each is matched against comment-stripped
    // content. OnClick= matches the MudBlazor parameter and @onclick the HTML
    // attribute; RowClick= is the MudDataGrid row-click handler (dead on a static
    // render, same class as OnClick); <MudDialog matches an inline dialog element;
    // the IDialogService trio matches programmatic dialogs.
    private static readonly (string Name, Regex Pattern)[] Signals =
    [
        ("@onclick",          new Regex(@"@onclick\b", RegexOptions.Compiled)),
        ("OnClick=",          new Regex(@"\bOnClick=", RegexOptions.Compiled)),
        ("RowClick=",         new Regex(@"\bRowClick=", RegexOptions.Compiled)),
        ("@bind-Value",       new Regex(@"@bind-Value\b", RegexOptions.Compiled)),
        ("IDialogService",    new Regex(@"\bIDialogService\b", RegexOptions.Compiled)),
        (".ShowAsync(",       new Regex(@"\.ShowAsync\(", RegexOptions.Compiled)),
        (".ShowMessageBoxAsync(", new Regex(@"\.ShowMessageBoxAsync\(", RegexOptions.Compiled)),
        ("<MudDialog",        new Regex(@"<MudDialog\b", RegexOptions.Compiled)),
    ];

    [Fact]
    public void EveryInteractivePage_DeclaresRenderMode()
    {
        var componentsDir = Path.Combine(
            RepoRoot(), "src", "PinballWizard.Web", "Components");

        var violations = new List<string>();

        foreach (var file in Directory.EnumerateFiles(componentsDir, "*.razor", SearchOption.AllDirectories))
        {
            var raw = File.ReadAllText(file);
            var content = CommentBlock.Replace(raw, string.Empty);

            // Only routable pages are in scope (component-only interactivity is
            // the deferred stretch). A real @page directive starts a line.
            var isPage = Regex.IsMatch(content, @"(?m)^\s*@page\b");
            if (!isPage)
            {
                continue;
            }

            var hasRenderMode = Regex.IsMatch(content, @"@rendermode\b");
            if (hasRenderMode)
            {
                continue;
            }

            var hit = Signals.FirstOrDefault(s => s.Pattern.IsMatch(content));
            if (hit.Name is not null)
            {
                violations.Add(
                    $"  {Path.GetFileName(file)} — interactivity signal '{hit.Name}' but no @rendermode");
            }
        }

        Assert.True(
            violations.Count == 0,
            "These routable pages carry interactive controls but render statically, so " +
            "the controls are silently dead (ADR-0034 doctrine). Add '@rendermode " +
            "InteractiveServer' (and ensure the layout's MudBlazor providers are pinned " +
            "interactive), or make the control static-friendly (a real Href/anchor):\n" +
            string.Join("\n", violations));
    }

    // Opening tag of an AppDataGrid, capturing its attribute span (up to the
    // first '>'). Attribute values on the grid tag never contain '>', so [^>]*
    // is safe; it spans newlines because the tag is often multi-line.
    private static readonly Regex AppDataGridOpenTag =
        new(@"<AppDataGrid\b[^>]*>", RegexOptions.Compiled);

    private static readonly Regex ShowPagerFalse =
        new(@"ShowPager\s*=\s*""false""", RegexOptions.Compiled);

    // The explicit MudBlazor pager element, when a page drops to a bare
    // <MudDataGrid> and wires the pager by hand.
    private static readonly Regex MudDataGridPagerElement =
        new(@"<MudDataGridPager\b", RegexOptions.Compiled);

    // A data-grid pager is interactive by construction: page navigation and the
    // rows-per-page selector need a Blazor circuit. On a static-SSR page they
    // render but are inert — the pager buttons do nothing (the /admin/sources
    // regression, 2026-07-02). Unlike @onclick this is INTERNAL to MudDataGrid,
    // so the token scan above cannot see it; this check looks at the grid tag
    // itself. AppDataGrid ships a live pager by default (ShowPager=true), so a
    // static page must either declare @rendermode or set ShowPager="false".
    [Fact]
    public void EveryStaticPage_WithLiveGridPager_DeclaresRenderMode()
    {
        var componentsDir = Path.Combine(
            RepoRoot(), "src", "PinballWizard.Web", "Components");

        var violations = new List<string>();

        foreach (var file in Directory.EnumerateFiles(componentsDir, "*.razor", SearchOption.AllDirectories))
        {
            var raw = File.ReadAllText(file);
            var content = CommentBlock.Replace(raw, string.Empty);

            var isPage = Regex.IsMatch(content, @"(?m)^\s*@page\b");
            if (!isPage || Regex.IsMatch(content, @"@rendermode\b"))
            {
                continue;
            }

            // Any AppDataGrid on the page that does not explicitly suppress its
            // pager renders a live pager.
            var hasLivePager =
                AppDataGridOpenTag.Matches(content).Any(m => !ShowPagerFalse.IsMatch(m.Value))
                || MudDataGridPagerElement.IsMatch(content);

            if (hasLivePager)
            {
                violations.Add(
                    $"  {Path.GetFileName(file)} — data-grid pager on a static page (no @rendermode)");
            }
        }

        Assert.True(
            violations.Count == 0,
            "These routable pages render a MudDataGrid pager under static SSR, so the pager's " +
            "page-navigation and rows-per-page controls are silently dead (ADR-0034 doctrine, the " +
            "/admin/sources regression). Add '@rendermode InteractiveServer' (matching " +
            "AdminManufacturers), or set ShowPager=\"false\" if the grid is small and fixed " +
            "(matching AdminCorpus):\n" +
            string.Join("\n", violations));
    }

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
