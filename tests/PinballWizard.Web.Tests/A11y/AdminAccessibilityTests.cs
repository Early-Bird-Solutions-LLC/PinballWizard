using Microsoft.Playwright;
using Xunit;

namespace PinballWizard.Web.Tests.A11y;

[Trait("Category", "Accessibility")]
public sealed class AdminAccessibilityTests(AdminAccessibilityTests.AdminPlaywrightFactory factory)
    : IClassFixture<AdminAccessibilityTests.AdminPlaywrightFactory>
{
    // Distinct fixture type so xUnit builds an admin-mode host separate from the
    // public anonymous one.
    public sealed class AdminPlaywrightFactory() : PlaywrightWebApplicationFactory(adminMode: true);

    [Fact]
    public async Task AdminDashboard_RendersUnder200_NotAChallengeRedirect()
    {
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
        var page = await browser.NewPageAsync();

        var response = await page.GotoAsync(
            $"{factory.ServerAddress}/admin",
            new() { WaitUntil = WaitUntilState.DOMContentLoaded });

        Assert.NotNull(response);
        Assert.Equal(200, response!.Status); // permissive AdminOnly → renders, no 302/401
    }
}
