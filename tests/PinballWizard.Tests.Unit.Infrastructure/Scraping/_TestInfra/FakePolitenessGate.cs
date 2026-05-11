using System.Net;
using PinballWizard.Infrastructure.Scraping.Polite;

namespace PinballWizard.Tests.Unit.Infrastructure.Scraping._TestInfra;

/// <summary>
/// Test fake for <see cref="IPolitenessGate"/>. Records every
/// acquire and report so tests can assert that the polite-scraping
/// invariants are honored end-to-end. Never throws
/// <see cref="PolitenessException"/> by default — tests that need
/// to exercise the abort path can opt-in via <see cref="ThrowOnAcquire"/>
/// or <see cref="ThrowOnReport"/>.
/// </summary>
/// <remarks>
/// The production <see cref="PolitenessGate"/> serializes per-origin
/// requests and applies a configurable delay. Production behavior is
/// covered by its own unit tests; this fake preserves only the
/// observable contract — every outbound HTTP request goes through
/// <see cref="AcquireForRequestAsync"/> + <see cref="ReportResponseAsync"/>
/// — without any real throttling.
/// </remarks>
public sealed class FakePolitenessGate : IPolitenessGate
{
    /// <summary>URLs passed to <see cref="AcquireForRequestAsync"/>, in order.</summary>
    public List<Uri> Acquired { get; } = [];

    /// <summary>URLs and statuses passed to <see cref="ReportResponseAsync"/>, in order.</summary>
    public List<(Uri Url, HttpStatusCode Status, TimeSpan? RetryAfter)> Reported { get; } = [];

    /// <summary>Number of leases that were disposed.</summary>
    public int LeasesDisposed { get; private set; }

    /// <summary>If non-null, <see cref="AcquireForRequestAsync"/> throws this exception.</summary>
    public Exception? ThrowOnAcquire { get; set; }

    /// <summary>If non-null, <see cref="ReportResponseAsync"/> throws this exception.</summary>
    public Exception? ThrowOnReport { get; set; }

    /// <inheritdoc />
    public Task<IAsyncDisposable> AcquireForRequestAsync(Uri url, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(url);
        cancellationToken.ThrowIfCancellationRequested();
        if (ThrowOnAcquire is { } ex) throw ex;
        Acquired.Add(url);
        return Task.FromResult<IAsyncDisposable>(new Lease(this));
    }

    /// <inheritdoc />
    public Task ReportResponseAsync(Uri url, HttpStatusCode statusCode, TimeSpan? retryAfter, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(url);
        cancellationToken.ThrowIfCancellationRequested();
        if (ThrowOnReport is { } ex) throw ex;
        Reported.Add((url, statusCode, retryAfter));
        return Task.CompletedTask;
    }

    private sealed class Lease(FakePolitenessGate parent) : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        {
            parent.LeasesDisposed++;
            return ValueTask.CompletedTask;
        }
    }
}
