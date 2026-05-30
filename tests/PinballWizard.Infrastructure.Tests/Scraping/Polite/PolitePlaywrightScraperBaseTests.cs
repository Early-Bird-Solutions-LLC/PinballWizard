using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Playwright;
using NSubstitute;
using PinballWizard.Core.Configuration;
using PinballWizard.Infrastructure.Scraping.Polite;
using PinballWizard.Infrastructure.Scraping.Playwright;
using Xunit;

namespace PinballWizard.Infrastructure.Tests.Scraping.Polite;

/// <summary>
/// Behavioral tests for <see cref="PolitePlaywrightScraperBase"/> and
/// <see cref="PolitePage"/>. Tests that can be exercised without a live
/// Playwright browser are covered here; the Stern Playwright asymmetry
/// (GamePageScraper / ServiceBulletinScraper not covered by the
/// queueing HttpClient fake) is documented in
/// <see cref="Stern.SternPlaywrightAsymmetryDocumentationTests"/>.
/// </summary>
public sealed class PolitePlaywrightScraperBaseTests
{
    private static PolitenessOptions DefaultOptions => new()
    {
        UserAgent = "PinballWizard/test",
        RequestDelayMs = 0,
        Max429Streak = 3,
    };

    // --------------- PolitePage tests ---------------

    [Fact]
    public async Task PolitePage_DisposeAsync_ReleasesLease()
    {
        // Arrange — construct PolitePage via its internal constructor
        // (InternalsVisibleTo "PinballWizard.Infrastructure.Tests" is declared in
        //  PinballWizard.Infrastructure.csproj).
        var leaseDisposed = false;
        var page = Substitute.For<IPage>();
        // CloseAsync is called on the page during dispose; let it complete.
        page.CloseAsync().Returns(Task.CompletedTask);

        var lease = new TrackingLease(() => leaseDisposed = true);
        var politePage = new PolitePage(page, lease);

        // Act
        await politePage.DisposeAsync();

        // Assert
        Assert.True(leaseDisposed, "PolitePage.DisposeAsync must release the politeness lease.");
    }

    [Fact]
    public async Task PolitePage_DisposeAsync_IsIdempotent_DoesNotDoubleDisposeLease()
    {
        // Arrange
        int disposeCount = 0;
        var page = Substitute.For<IPage>();
        page.CloseAsync().Returns(Task.CompletedTask);

        var lease = new TrackingLease(() => disposeCount++);
        var politePage = new PolitePage(page, lease);

        // Act — dispose twice
        await politePage.DisposeAsync();
        await politePage.DisposeAsync();

        // Assert — the Interlocked.Exchange guard must prevent double-release
        Assert.Equal(1, disposeCount);
    }

    [Fact]
    public async Task PolitePage_DisposeAsync_ClosesPageBeforeReleasingLease()
    {
        // Arrange — verify close happens before lease release (fail-loudly on wrong order)
        var events = new List<string>();
        var page = Substitute.For<IPage>();
        page.CloseAsync().Returns(_ =>
        {
            events.Add("page_closed");
            return Task.CompletedTask;
        });

        var lease = new TrackingLease(() => events.Add("lease_released"));
        var politePage = new PolitePage(page, lease);

        // Act
        await politePage.DisposeAsync();

        // Assert — page must be closed before lease is released
        Assert.Equal(2, events.Count);
        Assert.Equal("page_closed", events[0]);
        Assert.Equal("lease_released", events[1]);
    }

    [Fact]
    public async Task PolitePage_DisposeAsync_ReleasesLease_EvenWhenPageCloseThrows()
    {
        // Arrange — page.CloseAsync throws; lease must still be released (finally block)
        var leaseDisposed = false;
        var page = Substitute.For<IPage>();
        page.CloseAsync().Returns(Task.FromException(new InvalidOperationException("browser crashed")));

        var lease = new TrackingLease(() => leaseDisposed = true);
        var politePage = new PolitePage(page, lease);

        // Act — even though CloseAsync throws, DisposeAsync propagates via finally
        await Assert.ThrowsAsync<InvalidOperationException>(() => politePage.DisposeAsync().AsTask());

        // Assert
        Assert.True(leaseDisposed, "Politeness lease must be released even when page.CloseAsync throws.");
    }

    // --------------- PolitePlaywrightScraperBase dispose tests ---------------

    [Fact]
    public async Task PolitePlaywrightScraperBase_DisposeAsync_IsIdempotent()
    {
        // Arrange — a concrete scraper that doesn't open any pages
        var gate = Substitute.For<IPolitenessGate>();
        var factory = new PlaywrightFactory(NullLogger<PlaywrightFactory>.Instance);
        var scraper = new NopPlaywrightScraper(factory, gate, DefaultOptions);

        // Act — dispose twice; second call must not throw (DisposeAsyncCore guarded by _disposed flag)
        await scraper.DisposeAsync();
        var ex = await Record.ExceptionAsync(() => scraper.DisposeAsync().AsTask());

        // Assert
        Assert.Null(ex);
    }

    // --- helpers ---

    private sealed class TrackingLease(Action onDispose) : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        {
            onDispose();
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>
    /// Minimal concrete subclass of <see cref="PolitePlaywrightScraperBase"/>
    /// used in tests that need an instance without triggering any browser
    /// navigation (i.e. no <c>NewPolitePageAsync</c> calls).
    /// </summary>
    private sealed class NopPlaywrightScraper(
        PlaywrightFactory factory,
        IPolitenessGate gate,
        PolitenessOptions options)
        : PolitePlaywrightScraperBase(factory, gate, options, NullLogger<NopPlaywrightScraper>.Instance);
}
