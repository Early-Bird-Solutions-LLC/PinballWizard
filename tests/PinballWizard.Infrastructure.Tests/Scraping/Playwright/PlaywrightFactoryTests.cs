using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.Logging;
using Microsoft.Playwright;
using NSubstitute;
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

    // The string the Azure Playwright SDK throws. It is a bare System.Exception —
    // no status code, no inner exception — and it is byte-stable across "the
    // workspace was never contacted" (#920). Hardcoded here, not read from
    // PlaywrightFactory.WorkspaceAuthenticationFailureMarker, so a marker that
    // drifts away from the SDK text fails this test instead of following it.
    private const string SdkAuthenticationFailure =
        "Could not authenticate with the service.\nPlease refer to https://aka.ms/pww/docs/authentication";

    [Fact]
    public async Task AcquireBrowserAsync_WhenWorkspaceAuthenticationFails_MetersLogsAndPropagates()
    {
        var logger = new CapturingLogger();
        await using var factory = new PlaywrightFactory(logger);
        using var listener = StartConnectListener(out var samples);

        var connectCalled = false;
        var launchCalled = false;
        var playwright = Substitute.For<IPlaywright>();

        var thrown = await Assert.ThrowsAsync<Exception>(() =>
            factory.AcquireBrowserAsync(
                playwright,
                "wss://eastus.api.playwright.microsoft.com/playwrightworkspaces/test/browsers",
                _ =>
                {
                    connectCalled = true;
                    throw SdkAuthenticationException();
                },
                _ =>
                {
                    launchCalled = true;
                    return Task.FromResult(Substitute.For<IBrowser>());
                }));

        Assert.True(connectCalled);
        Assert.False(launchCalled);
        // The SDK throws a bare System.Exception. A catch that wrapped it, or
        // that launched local Chromium and returned, would not surface this type.
        Assert.IsType<Exception>(thrown);
        Assert.Contains(PlaywrightFactory.WorkspaceAuthenticationFailureMarker, thrown.Message, StringComparison.Ordinal);

        var sample = Assert.Single(samples);
        Assert.Equal(1, sample.Value);
        Assert.Equal("failure", sample.Outcome);
        Assert.Null(sample.Fallback);

        var error = Assert.Single(logger.Entries, e => e.Level == LogLevel.Error);
        Assert.NotNull(error.Exception);
        Assert.Contains(PlaywrightFactory.WorkspaceAuthenticationFailureMarker, error.Exception.Message, StringComparison.Ordinal);
        Assert.Contains("local Chromium will not be started", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("Falling back", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AcquireBrowserAsync_WhenWorkspaceConnectFailsForOtherReason_DoesNotLaunchLocalChromium()
    {
        var logger = new CapturingLogger();
        await using var factory = new PlaywrightFactory(logger);
        using var listener = StartConnectListener(out var samples);
        var launchCalled = false;

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            factory.AcquireBrowserAsync(
                Substitute.For<IPlaywright>(),
                "wss://eastus.api.playwright.microsoft.com/playwrightworkspaces/test/browsers",
                _ => throw new InvalidOperationException("workspace unreachable after authentication"),
                _ =>
                {
                    launchCalled = true;
                    return Task.FromResult(Substitute.For<IBrowser>());
                }));

        Assert.False(launchCalled);
        Assert.Equal("workspace unreachable after authentication", thrown.Message);
        Assert.DoesNotContain(logger.Entries, e => e.Level == LogLevel.Error);

        var sample = Assert.Single(samples);
        Assert.Equal("failure", sample.Outcome);
        Assert.Null(sample.Fallback);
    }

    [Fact]
    public async Task AcquireBrowserAsync_WhenWorkspaceConnectIsCanceled_DoesNotMeterOrFallBack()
    {
        var logger = new CapturingLogger();
        await using var factory = new PlaywrightFactory(logger);
        using var listener = StartConnectListener(out var samples);
        var launchCalled = false;

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            factory.AcquireBrowserAsync(
                Substitute.For<IPlaywright>(),
                "wss://eastus.api.playwright.microsoft.com/playwrightworkspaces/test/browsers",
                _ => throw new OperationCanceledException(),
                _ =>
                {
                    launchCalled = true;
                    return Task.FromResult(Substitute.For<IBrowser>());
                }));

        Assert.False(launchCalled);
        Assert.Empty(samples);
    }

    [Fact]
    public async Task AcquireBrowserAsync_WhenAuthenticationFailureIsWrapped_MetersLogsAndPropagates()
    {
        // The marker lives only on InnerException. The Error log still has to
        // name the authentication failure; the exception still has to propagate
        // and local Chromium still must not launch.
        var logger = new CapturingLogger();
        await using var factory = new PlaywrightFactory(logger);
        using var listener = StartConnectListener(out var samples);
        var launchCalled = false;

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            factory.AcquireBrowserAsync(
                Substitute.For<IPlaywright>(),
                WorkspaceUrl,
                _ => throw new InvalidOperationException("connect options failed", SdkAuthenticationException()),
                _ =>
                {
                    launchCalled = true;
                    return Task.FromResult(Substitute.For<IBrowser>());
                }));

        Assert.False(launchCalled);
        Assert.Equal("connect options failed", thrown.Message);
        Assert.NotNull(thrown.InnerException);
        Assert.Contains(PlaywrightFactory.WorkspaceAuthenticationFailureMarker, thrown.InnerException.Message, StringComparison.Ordinal);
        var sample = Assert.Single(samples);
        Assert.Equal("failure", sample.Outcome);
        Assert.Null(sample.Fallback);
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Error && e.Exception is InvalidOperationException);
    }

    [Fact]
    public async Task AcquireBrowserAsync_WhenWorkspaceConnectSucceeds_MetersSuccessAndSkipsRecycle()
    {
        await using var factory = new PlaywrightFactory(new CapturingLogger());
        using var listener = StartConnectListener(out var samples);
        var remote = Substitute.For<IBrowser>();
        var launchCalled = false;

        var browser = await factory.AcquireBrowserAsync(
            Substitute.For<IPlaywright>(),
            WorkspaceUrl,
            _ => Task.FromResult(remote),
            _ =>
            {
                launchCalled = true;
                return Task.FromResult(Substitute.For<IBrowser>());
            });

        Assert.False(launchCalled);
        Assert.Same(remote, browser);
        var sample = Assert.Single(samples);
        Assert.Equal("success", sample.Outcome);
        Assert.Null(sample.Fallback);

        await factory.RecycleBrowserAsync();
        await remote.Received(0).DisposeAsync();
    }

    [Fact]
    public async Task AcquireBrowserAsync_WhenWorkspaceUrlDisappearsBeforeConnect_DoesNotMeterOrFallBack()
    {
        await using var factory = new PlaywrightFactory(new CapturingLogger());
        using var listener = StartConnectListener(out var samples);
        var launchCalled = false;

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            factory.AcquireBrowserAsync(
                Substitute.For<IPlaywright>(),
                WorkspaceUrl,
                _ => throw new InvalidOperationException(
                    "PLAYWRIGHT_SERVICE_URL is not set. This environment attempted to connect to Azure Playwright Workspaces (ADR-0056)."),
                _ =>
                {
                    launchCalled = true;
                    return Task.FromResult(Substitute.For<IBrowser>());
                }));

        Assert.StartsWith("PLAYWRIGHT_SERVICE_URL is not set.", thrown.Message, StringComparison.Ordinal);
        Assert.False(launchCalled);
        Assert.Empty(samples);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task AcquireBrowserAsync_WhenConfiguredForLocalChromium_LaunchesLocalAndDoesNotConnect(string? playwrightServiceUrl)
    {
        await using var factory = new PlaywrightFactory(new CapturingLogger());
        using var listener = StartConnectListener(out var samples);
        var local = Substitute.For<IBrowser>();
        var connectCalled = false;

        var browser = await factory.AcquireBrowserAsync(
            Substitute.For<IPlaywright>(),
            playwrightServiceUrl,
            _ =>
            {
                connectCalled = true;
                return Task.FromResult(Substitute.For<IBrowser>());
            },
            _ => Task.FromResult(local));

        Assert.False(connectCalled);
        Assert.Same(local, browser);
        Assert.Empty(samples);

        await factory.RecycleBrowserAsync();
        await local.Received(1).DisposeAsync();
    }

    [Fact]
    public async Task AcquireBrowserAsync_WhenLocalChromiumFails_PropagatesAndDoesNotConnectToWorkspace()
    {
        await using var factory = new PlaywrightFactory(new CapturingLogger());
        using var listener = StartConnectListener(out var samples);
        var connectCalled = false;

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            factory.AcquireBrowserAsync(
                Substitute.For<IPlaywright>(),
                playwrightServiceUrl: null,
                _ =>
                {
                    connectCalled = true;
                    return Task.FromResult(Substitute.For<IBrowser>());
                },
                _ => throw new InvalidOperationException("chromium failed to launch")));

        Assert.False(connectCalled);
        Assert.Equal("chromium failed to launch", thrown.Message);
        Assert.Empty(samples);
    }

    private const string WorkspaceUrl =
        "wss://eastus.api.playwright.microsoft.com/playwrightworkspaces/test/browsers";

    // The SDK throws a bare System.Exception (no derived type). The fixture has
    // to throw that same type; a more specific exception would not be the
    // failure the jobs actually hit.
    private static Exception SdkAuthenticationException()
    {
#pragma warning disable CA2201
        return new Exception(SdkAuthenticationFailure);
#pragma warning restore CA2201
    }

    private static MeterListener StartConnectListener(
        out ConcurrentBag<(long Value, string? Outcome, string? Fallback)> samples)
    {
        var bag = new ConcurrentBag<(long Value, string? Outcome, string? Fallback)>();
        samples = bag;
        var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, meterListener) =>
        {
            if (instrument.Name == "pinwiz.scraper.workspace_connect_total")
                meterListener.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((_, value, tags, _) =>
        {
            string? outcome = null;
            string? fallback = null;
            foreach (var tag in tags)
            {
                if (tag.Key == "outcome") outcome = tag.Value?.ToString();
                else if (tag.Key == "fallback") fallback = tag.Value?.ToString();
            }
            bag.Add((value, outcome, fallback));
        });
        listener.Start();
        return listener;
    }

    private sealed class CapturingLogger : ILogger<PlaywrightFactory>
    {
        public List<(LogLevel Level, string Message, Exception? Exception)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Entries.Add((logLevel, formatter(state, exception), exception));
        }
    }
}
