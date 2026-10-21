using PinballWizard.Core.Configuration;

namespace PinballWizard.Infrastructure.Scraping.Ap;

// Host rules shared by every American Pinball scraper. Page hosts decide which
// discovered page URLs are fetched; document hosts decide which file links are
// kept. One definition keeps the game-page and bulletin scrapers from drifting.
public static class ApHosts
{
    // Both registrable domains. After the www redirect pages live on
    // americanpinball.com; flyers still live on american-pinball.com and
    // legacy bulletins on its s4 CDN subdomain. A suffix match without the
    // leading dot would also accept not-american-pinball.com, so the dot is
    // required.
    private static readonly string[] ApDocumentDomains =
    [
        "americanpinball.com",
        "american-pinball.com",
    ];

    // Hot Wheels and Barry O's release notes are served from this HubSpot
    // custom domain (/hubfs/...), not from hubspotusercontent.
    private const string OrbitGamesHubSpotHost = "my.orbitgames.fun";

    // HubSpot's file CDN zone observed on the live manuals and bulletins
    // ({portal}.fs1.hubspotusercontent-na1.net). Same equals-or-subdomain
    // rule as the AP domains, so files.hubspotusercontent-evil.net and
    // hubspotusercontent-na1.net.evil.example do not match.
    private static readonly string[] HubSpotFileCdnZones = ["hubspotusercontent-na1.net"];

    // True when a page URL is HTTPS on the configured AP base host or one of
    // ApOptions.SitemapHosts.
    public static bool IsAllowedPageUrl(Uri uri, ApOptions options)
    {
        ArgumentNullException.ThrowIfNull(uri);
        ArgumentNullException.ThrowIfNull(options);

        if (!uri.IsAbsoluteUri) return false;
        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)) return false;

        if (Uri.TryCreate(options.BaseUrl, UriKind.Absolute, out var baseUri)
            && string.Equals(baseUri.Host, uri.Host, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (options.SitemapHosts is null) return false;
        foreach (var host in options.SitemapHosts)
        {
            if (!string.IsNullOrWhiteSpace(host)
                && string.Equals(host.Trim(), uri.Host, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    // True when a downloadable file on this host is an American Pinball
    // document: either AP domain (any subdomain), the Orbit Games HubSpot
    // domain, or HubSpot's file CDN.
    public static bool IsAllowedDocumentHost(string host)
    {
        ArgumentNullException.ThrowIfNull(host);

        foreach (var domain in ApDocumentDomains)
        {
            if (IsDomainOrSubdomain(host, domain)) return true;
        }

        if (host.Equals(OrbitGamesHubSpotHost, StringComparison.OrdinalIgnoreCase)) return true;

        foreach (var zone in HubSpotFileCdnZones)
        {
            if (IsDomainOrSubdomain(host, zone)) return true;
        }

        return false;
    }

    private static bool IsDomainOrSubdomain(string host, string domain) =>
        host.Equals(domain, StringComparison.OrdinalIgnoreCase)
        || host.EndsWith("." + domain, StringComparison.OrdinalIgnoreCase);
}
