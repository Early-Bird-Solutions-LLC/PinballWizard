using PinballWizard.Infrastructure.Scraping.Playwright;
using Xunit;

namespace PinballWizard.Infrastructure.Tests.Scraping.Playwright;

// The install-argument contract is asserted directly rather than by invoking
// PlaywrightFactory.InstallBrowsers, which shells out to a real ~150 MB browser
// download — untestable in CI and pointless to assert against.
//
// Why this contract is worth pinning: the ACA scraper jobs run in a container
// whose base image carries no Chromium OS libraries. Downloading the browser
// without those libraries produces an image that builds cleanly and then fails
// at scrape time with "Looks like Playwright was just installed or updated" —
// which is exactly how pinwiz-job-stern-bulletins came to fail 26 of 30 nightly
// runs while every build stayed green.
public sealed class PlaywrightFactoryTests
{
    [Fact]
    public void BuildInstallArgs_WithDeps_RequestsOperatingSystemDependencies()
    {
        var args = PlaywrightFactory.BuildInstallArgs(withDeps: true);

        // Full array, not just Contains: Playwright's CLI takes the browser as a
        // positional argument after the options, and asserting only on presence
        // would let ["install", "chromium", "--with-deps"] pass.
        Assert.Equal(["install", "--with-deps", "chromium"], args);
    }

    [Fact]
    public void BuildInstallArgs_WithoutDeps_OmitsOperatingSystemDependencies()
    {
        // The default path runs on a developer machine, which usually cannot
        // elevate and does not need the OS packages.
        var args = PlaywrightFactory.BuildInstallArgs(withDeps: false);

        Assert.DoesNotContain("--with-deps", args);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void BuildInstallArgs_AlwaysInstallsChromiumOnly(bool withDeps)
    {
        // Chromium is the only browser the scrapers launch; installing the full
        // set would triple image size for nothing.
        var args = PlaywrightFactory.BuildInstallArgs(withDeps);

        Assert.Equal("install", args[0]);
        Assert.Contains("chromium", args);
        Assert.DoesNotContain("firefox", args);
        Assert.DoesNotContain("webkit", args);
    }

    // Mirrors SharedAzureCredentialTests' pattern for BuildOptions: a pure,
    // internal-static decision function is the testable seam, rather than
    // asserting on GetBrowserAsync() itself, which would require either a real
    // Chromium launch or a real network call to Azure Playwright Workspaces —
    // neither belongs in a unit test. The manual trigger against the real
    // service (see the design doc's Rollout section) is what verifies the
    // actual connection succeeds.
    //
    // Gated on the PLAYWRIGHT_SERVICE_URL value itself, not on
    // SharedAzureCredential.IsDevelopment — an earlier revision gated on "is this
    // Development", which broke the documented standalone-CLI scrape path (no
    // launchSettings.json exists for the CLI project, so ASPNETCORE_ENVIRONMENT is
    // simply unset there) and meant an unconfigured deployed environment hard-failed
    // instead of behaving exactly as it did before this change. Pre-push review on
    // #855 caught both.
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void IsWorkspaceUrlConfigured_WhenNullOrWhitespace_ReturnsFalse(string? playwrightServiceUrl)
    {
        var result = PlaywrightFactory.IsWorkspaceUrlConfigured(playwrightServiceUrl);

        Assert.False(result);
    }

    [Fact]
    public void IsWorkspaceUrlConfigured_WhenSet_ReturnsTrue()
    {
        var result = PlaywrightFactory.IsWorkspaceUrlConfigured("wss://eastus.example.playwright.microsoft.com/accounts/abc123/browsers");

        Assert.True(result);
    }

    // Same pattern, for the recycle becoming a no-op in workspace mode: real coverage
    // for RecycleBrowserAsync's actual behavior would require either a real Chromium
    // process or a real Workspace connection to recycle, neither of which belongs in a
    // unit test — but the DECISION of whether to skip is a pure boolean this project's
    // established pattern (IsWorkspaceUrlConfigured, SharedAzureCredential.BuildOptions)
    // already extracts and tests directly. Pre-push review on #855 flagged that this
    // behavior — a change that silently disables the #862 recycle mitigation for every
    // job once a workspace is configured — had shipped with zero coverage; this closes
    // that gap for the part that can be closed without a live browser or connection.
    [Fact]
    public void ShouldSkipRecycle_InWorkspaceMode_ReturnsTrue()
    {
        var result = PlaywrightFactory.ShouldSkipRecycle(isWorkspaceConnection: true);

        Assert.True(result);
    }

    [Fact]
    public void ShouldSkipRecycle_InLocalChromiumMode_ReturnsFalse()
    {
        var result = PlaywrightFactory.ShouldSkipRecycle(isWorkspaceConnection: false);

        Assert.False(result);
    }
}
