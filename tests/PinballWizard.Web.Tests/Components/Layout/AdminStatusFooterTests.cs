using Bunit;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MudBlazor;
using MudBlazor.Services;
using NSubstitute;
using PinballWizard.Web.Components.Layout;
using PinballWizard.Web.Services;
using Xunit;

namespace PinballWizard.Web.Tests.Components.Layout;

// bUnit tests for AdminStatusFooter — the admin sidebar status strip.
//
// Tests: renders data-testid, shows environment + build string when deployed,
// shows local fallback when BuildTimeUtc is null (Invariant #17).
// Pattern: AsyncBunitContext + AddMudServices + registered BuildInfo.
public sealed class AdminStatusFooterTests : AsyncBunitContext
{
    public AdminStatusFooterTests()
    {
        Services.AddMudServices();
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    // Register a BuildInfo with controlled known values for each test scenario.
    private void RegisterBuildInfo(
        string sha = "",
        string? time = null,
        string environment = "Testing")
    {
        var dict = new Dictionary<string, string?> { ["PINWIZ_BUILD_SHA"] = sha };
        if (time is not null)
            dict["PINWIZ_BUILD_TIME"] = time;

        var config = new ConfigurationBuilder().AddInMemoryCollection(dict).Build();
        var hostEnv = Substitute.For<IHostEnvironment>();
        hostEnv.EnvironmentName.Returns(environment);
        Services.AddSingleton(new BuildInfo(config, hostEnv));
    }

    [Fact]
    public void Renders_DataTestId()
    {
        RegisterBuildInfo();
        var cut = Render<AdminStatusFooter>();
        cut.Find("[data-testid='admin-status-footer']");
    }

    [Fact]
    public void Renders_SuccessColorDot()
    {
        RegisterBuildInfo(sha: "abc1234def", time: "2026-07-10T12:30:00Z", environment: "Production");
        var cut = Render<AdminStatusFooter>();
        // Green dot rendered as MudIcon with Color.Success → mud-success CSS class.
        var icon = cut.FindComponent<MudIcon>();
        Assert.Equal(Color.Success, icon.Instance.Color);
    }

    [Fact]
    public void Renders_EnvironmentName_WhenDeployed()
    {
        RegisterBuildInfo(sha: "abc1234def5678", time: "2026-07-10T12:30:00Z", environment: "Production");
        var cut = Render<AdminStatusFooter>();
        Assert.Contains("Production", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void Renders_ShortSha_WhenDeployed()
    {
        RegisterBuildInfo(sha: "abc1234def5678", time: "2026-07-10T12:30:00Z", environment: "Production");
        var cut = Render<AdminStatusFooter>();
        // ShortSha = first 7 chars of the SHA
        Assert.Contains("abc1234", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("build", cut.Markup, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Renders_DeployedTimestamp_WhenBuildTimeUtcPresent()
    {
        RegisterBuildInfo(sha: "abc1234def5678", time: "2026-07-10T12:30:00Z", environment: "Production");
        var cut = Render<AdminStatusFooter>();
        // Format: "deployed MMM d, HH:mm UTC"
        Assert.Contains("deployed", cut.Markup, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("UTC", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void Renders_LocalFallback_WhenBuildTimeUtcIsNull()
    {
        // No time env var → BuildTimeUtc = null → local fallback path.
        RegisterBuildInfo(sha: "", time: null, environment: "Development");
        var cut = Render<AdminStatusFooter>();
        // Shows environment + "local" — never a fabricated version.
        Assert.Contains("Development", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("local", cut.Markup, StringComparison.OrdinalIgnoreCase);
        // Must NOT show "deployed" or a fake SHA.
        Assert.DoesNotContain("deployed", cut.Markup, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DoesNotRenderFakeVersion_WhenLocalDev()
    {
        // Invariant #17: no synthetic version string when env vars are absent.
        RegisterBuildInfo(sha: "", time: null, environment: "Development");
        var cut = Render<AdminStatusFooter>();
        // No "v2.", "v1.", or "v0." semver strings.
        Assert.DoesNotContain("v2.", cut.Markup, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void HasNoInteractiveTriggers_PureStaticMarkup()
    {
        // Circuit-safety: the component must emit no onclick attributes.
        RegisterBuildInfo(sha: "abc1234", time: "2026-07-10T12:30:00Z", environment: "Production");
        var cut = Render<AdminStatusFooter>();
        Assert.DoesNotContain("onclick", cut.Markup, StringComparison.OrdinalIgnoreCase);
    }
}
