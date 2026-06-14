using System.Text.RegularExpressions;
using Xunit;

namespace PinballWizard.Web.Tests.StaticAssets;

// Guards against the PR #401 prod outage class: "Missing <MudPopoverProvider />"
// circuit crash caused by a render-mode mismatch between providers and pages.
//
// Background: pages like Index and Wizard opt into interactivity per-page
// (@rendermode InteractiveServer), while content pages like About stay static.
// Global interactivity was rejected because it would force the deliberately-static
// content pages into an interactive mode (PreRenderedDiagramTests pins About as
// having no @rendermode). MainLayout itself is static (inherits LayoutComponentBase
// with no rendermode), so its MudBlazor providers must be pinned to
// @rendermode="InteractiveServer" — a static provider cannot host a popover
// rendered inside an interactive page island, which crashes the circuit.
//
// AdminLayout is the deliberate asymmetry: all /admin/* pages are static (no
// @rendermode directive), so a static MudBlazor provider is correct there.
// If any admin page ever adds @rendermode, those providers must be pinned too.
//
// ADR-0034 documents the full render-mode strategy.
public sealed class LayoutProviderRenderModeTests
{
    // All four MudBlazor providers in MainLayout must carry @rendermode="InteractiveServer"
    // on their element. A bare <MudPopoverProvider /> (static) will crash with
    // "Missing <MudPopoverProvider />" the moment an interactive page island tries
    // to use it. We assert each provider tag has the rendermode attribute adjacent
    // to it so a revert to the static form is caught immediately.
    [Theory]
    [InlineData("MudThemeProvider")]
    [InlineData("MudPopoverProvider")]
    [InlineData("MudDialogProvider")]
    [InlineData("MudSnackbarProvider")]
    public void MainLayout_Provider_HasInteractiveServerRenderMode(string providerName)
    {
        var mainLayout = File.ReadAllText(MainLayoutPath());

        // Match the provider tag with @rendermode="InteractiveServer" somewhere on the
        // same element. We allow attributes in any order and both self-closing and open
        // forms, so we look for the provider name and the rendermode attribute within
        // the same opening tag span (no newlines between < and >).
        var pattern = $@"<{Regex.Escape(providerName)}\b[^>]*@rendermode=""InteractiveServer""[^>]*/?>|<{Regex.Escape(providerName)}\b[^>]*/?>.*@rendermode=""InteractiveServer""";
        var hasInteractiveRenderMode = Regex.IsMatch(mainLayout, $@"<{Regex.Escape(providerName)}[^>]*@rendermode=""InteractiveServer""");

        Assert.True(
            hasInteractiveRenderMode,
            $"MainLayout.razor must declare <{providerName} @rendermode=\"InteractiveServer\" ... />. " +
            $"A static {providerName} crashes the Blazor circuit with 'Missing <{providerName} />' " +
            $"when any per-page interactive island (Index, Wizard) tries to use it (PR #401 outage). " +
            $"See ADR-0034 for the full render-mode strategy.");
    }

    // AdminLayout's providers must NOT carry @rendermode — they are intentionally
    // static because every /admin/* page is static. This assertion documents the
    // deliberate asymmetry so a future "make admin interactive" change explicitly
    // revisits whether to pin these providers too.
    [Theory]
    [InlineData("MudThemeProvider")]
    [InlineData("MudPopoverProvider")]
    [InlineData("MudDialogProvider")]
    [InlineData("MudSnackbarProvider")]
    public void AdminLayout_Provider_IsStaticNoRenderMode(string providerName)
    {
        var adminLayout = File.ReadAllText(AdminLayoutPath());

        // A provider line in AdminLayout must not carry @rendermode. We only look at
        // the section of the file that contains the provider tag to avoid false
        // positives from comments or documentation prose.
        var providerLinePattern = new Regex($@"<{Regex.Escape(providerName)}\b[^>]*/?>", RegexOptions.Singleline);
        var match = providerLinePattern.Match(adminLayout);

        Assert.True(
            match.Success,
            $"AdminLayout.razor must contain a <{providerName} /> element.");

        Assert.DoesNotContain(
            "@rendermode",
            match.Value,
            StringComparison.Ordinal);

        // Explanatory failure message for the DoesNotContain assertion above:
        // If this assertion fails, an /admin/* page was likely given @rendermode
        // InteractiveServer without updating the providers. See ADR-0034.
    }

    private static string MainLayoutPath() =>
        Path.Combine(RepoRoot(), "src", "PinballWizard.Web", "Components", "Layout", "MainLayout.razor");

    private static string AdminLayoutPath() =>
        Path.Combine(RepoRoot(), "src", "PinballWizard.Web", "Components", "Layout", "AdminLayout.razor");

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
