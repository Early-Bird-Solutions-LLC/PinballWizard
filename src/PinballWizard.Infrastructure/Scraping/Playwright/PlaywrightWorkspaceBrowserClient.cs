using Azure.Developer.Playwright;
using Microsoft.Playwright;

namespace PinballWizard.Infrastructure.Scraping.Playwright;

// Seam for the Entra handshake Azure.Developer.Playwright 1.0.0 requires.
// GetConnectOptionsAsync does not call TokenCredential. It reads
// PLAYWRIGHT_SERVICE_ACCESS_TOKEN and throws "Could not authenticate with the
// service" when that variable is empty. InitializeAsync is the method that
// fetches the Entra token (scope https://management.core.windows.net/.default)
// and stores it where GetConnectOptionsAsync reads it. The workspace sets
// localAuth Disabled, so a service access token is never supplied.
internal interface IPlaywrightWorkspaceBrowserClient
{
    Task InitializeAsync(CancellationToken cancellationToken = default);

    Task<ConnectOptions<BrowserTypeConnectOptions>> GetConnectOptionsAsync(
        CancellationToken cancellationToken = default);
}

internal sealed class PlaywrightServiceWorkspaceBrowserClient : IPlaywrightWorkspaceBrowserClient
{
    private readonly PlaywrightServiceBrowserClient _client;

    public PlaywrightServiceWorkspaceBrowserClient(PlaywrightServiceBrowserClient client)
    {
        _client = client;
    }

    public Task InitializeAsync(CancellationToken cancellationToken = default) =>
        _client.InitializeAsync(cancellationToken);

    public Task<ConnectOptions<BrowserTypeConnectOptions>> GetConnectOptionsAsync(
        CancellationToken cancellationToken = default) =>
        _client.GetConnectOptionsAsync<BrowserTypeConnectOptions>(cancellationToken: cancellationToken);
}
