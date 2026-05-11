using Bunit;
using Bunit.TestDoubles;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;
using NSubstitute;
using PinballWizard.Web.Components.Pages;
using PinballWizard.Web.Services;
using Xunit;

namespace PinballWizard.Web.Tests.Pages;

// Smoke + behavior tests for the Settings page.
// Verifies chrome structure (three sections present), theme card rendering,
// and that user interactions invoke the correct IUserPreferencesService methods.
//
// docs/ui/screens/settings.md — full spec
// ADR-0008 — MudBlazor strict (no custom components for chrome)
public sealed class SettingsTests : TestContext
{
    private readonly IUserPreferencesService _prefs;

    public SettingsTests()
    {
        _prefs = Substitute.For<IUserPreferencesService>();
        _prefs.CurrentTheme.Returns(ThemeNames.ModernLcd);
        _prefs.CurrentMotion.Returns("match");
        _prefs.CurrentSound.Returns("muted");
        _prefs.StorageAvailable.Returns(true);

        Services.AddMudServices();
        Services.AddSingleton(_prefs);
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    [Fact]
    public void Settings_RendersWithoutError()
    {
        var cut = RenderComponent<Settings>();
        Assert.NotNull(cut.Markup);
    }

    [Fact]
    public void Settings_RendersAllThemeCards_AlphabeticalOrder()
    {
        var cut = RenderComponent<Settings>();

        // Alphabetical: Backbox (B) → Cabinet (C) → Daytime Route (D) → Modern LCD (M)
        var cards = cut.FindAll("[data-testid^='theme-card-']").ToList();
        Assert.Equal(4, cards.Count);
        Assert.Equal("theme-card-backbox",       cards[0].GetAttribute("data-testid"));
        Assert.Equal("theme-card-cabinet",       cards[1].GetAttribute("data-testid"));
        Assert.Equal("theme-card-daytime-route", cards[2].GetAttribute("data-testid"));
        Assert.Equal("theme-card-modern-lcd",    cards[3].GetAttribute("data-testid"));
    }

    [Fact]
    public void Settings_DaytimeRouteCard_HasBetaTag()
    {
        var cut = RenderComponent<Settings>();

        var daytimeCard = cut.Find("[data-testid='theme-card-daytime-route']");
        Assert.Contains("BETA", daytimeCard.TextContent);
    }

    [Fact]
    public void Settings_ModernLcdCard_HasNoBetaTag()
    {
        var cut = RenderComponent<Settings>();

        var modernCard = cut.Find("[data-testid='theme-card-modern-lcd']");
        Assert.DoesNotContain("BETA", modernCard.TextContent);
    }

    [Fact]
    public void Settings_ThemeCard_MarksActiveThemeAsChecked()
    {
        _prefs.CurrentTheme.Returns(ThemeNames.ModernLcd);
        var cut = RenderComponent<Settings>();

        var modernRadio = cut.Find("input[id='theme-modern-lcd']");
        Assert.True(modernRadio.HasAttribute("checked"));

        var daytimeRadio = cut.Find("input[id='theme-daytime-route']");
        Assert.False(daytimeRadio.HasAttribute("checked"));
    }

    [Fact]
    public async Task Settings_SelectingDaytimeRouteCard_CallsSetThemeAsync()
    {
        var cut = RenderComponent<Settings>();

        var daytimeRadio = cut.Find("input[id='theme-daytime-route']");
        await cut.InvokeAsync(() => daytimeRadio.Change(ThemeNames.DaytimeRoute));

        await _prefs.Received(1).SetThemeAsync(ThemeNames.DaytimeRoute);
    }

    [Fact]
    public void Settings_ThreeMotionSections_Present()
    {
        var cut = RenderComponent<Settings>();

        // Three sections identified by headings
        var headings = cut.FindAll("h5, h6")
            .Select(h => h.TextContent.Trim())
            .ToArray();

        Assert.Contains("THEME", headings);
        Assert.Contains("MOTION", headings);
        Assert.Contains("SOUND", headings);
    }

    [Fact]
    public void Settings_SoundToggle_ShowsMutedLabel_WhenSoundIsOff()
    {
        _prefs.CurrentSound.Returns("muted");
        var cut = RenderComponent<Settings>();

        // MudSwitch label text reflects the current state
        Assert.Contains("Muted", cut.Markup);
        Assert.DoesNotContain("Sound on", cut.Markup);
    }

    [Fact]
    public void Settings_StorageUnavailableCaption_HiddenWhenStorageAvailable()
    {
        _prefs.StorageAvailable.Returns(true);
        var cut = RenderComponent<Settings>();

        Assert.DoesNotContain("Browser storage is disabled", cut.Markup);
    }

    [Fact]
    public void Settings_StorageUnavailableCaption_ShownWhenStorageUnavailable()
    {
        _prefs.StorageAvailable.Returns(false);
        var cut = RenderComponent<Settings>();

        Assert.Contains("Browser storage is disabled", cut.Markup);
    }
}
