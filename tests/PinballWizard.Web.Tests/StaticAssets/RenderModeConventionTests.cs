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
// binding), IDialogService / .ShowAsync( / .ShowMessageBox( / <MudDialog
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
    // attribute; <MudDialog matches an inline dialog element; the IDialogService
    // trio matches programmatic dialogs.
    private static readonly (string Name, Regex Pattern)[] Signals =
    [
        ("@onclick",          new Regex(@"@onclick\b", RegexOptions.Compiled)),
        ("OnClick=",          new Regex(@"\bOnClick=", RegexOptions.Compiled)),
        ("@bind-Value",       new Regex(@"@bind-Value\b", RegexOptions.Compiled)),
        ("IDialogService",    new Regex(@"\bIDialogService\b", RegexOptions.Compiled)),
        (".ShowAsync(",       new Regex(@"\.ShowAsync\(", RegexOptions.Compiled)),
        (".ShowMessageBox(",  new Regex(@"\.ShowMessageBox\(", RegexOptions.Compiled)),
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
