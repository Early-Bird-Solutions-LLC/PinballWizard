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
// AdminLayout's providers are now ALSO pinned @rendermode="InteractiveServer"
// (ADR-0034 amendment, 2026-06-17): several /admin/* pages are interactive
// (Settings, Triage, LinkOverrides, Machines, MachineDetail), so the layout's
// providers must match or those pages crash with "Missing <MudPopoverProvider />".
// Both layouts now assert the same interactive invariant; the former static-admin
// asymmetry is retired.
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

    // AdminLayout's providers MUST carry @rendermode="InteractiveServer" — the same
    // invariant as MainLayout. As of 2026-06-17 (ADR-0034 amendment), admin is
    // per-need render mode: several /admin/* pages are interactive, so their layout's
    // providers must match or those pages crash with "Missing <MudPopoverProvider />".
    // Both layouts now pin interactive providers; the former static-admin asymmetry is
    // retired. See ADR-0034.
    [Theory]
    [InlineData("MudThemeProvider")]
    [InlineData("MudPopoverProvider")]
    [InlineData("MudDialogProvider")]
    [InlineData("MudSnackbarProvider")]
    public void AdminLayout_Provider_HasInteractiveServerRenderMode(string providerName)
    {
        var adminLayout = File.ReadAllText(AdminLayoutPath());

        var hasInteractiveRenderMode = Regex.IsMatch(
            adminLayout,
            $@"<{Regex.Escape(providerName)}[^>]*@rendermode=""InteractiveServer""");

        Assert.True(
            hasInteractiveRenderMode,
            $"AdminLayout.razor must declare <{providerName} @rendermode=\"InteractiveServer\" ... />. " +
            $"Interactive /admin/* pages (Settings, Triage, LinkOverrides, Machines, MachineDetail) " +
            $"resolve their popover/dialog/snackbar services from this layout's providers; a static " +
            $"provider crashes the circuit with 'Missing <{providerName} />'. See ADR-0034.");
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
