using System.Net;
using System.Text;

namespace PinballWizard.Infrastructure.Tests.Scraping._TestInfra;

/// <summary>
/// Test fake for <see cref="HttpMessageHandler"/>. Maps absolute URLs
/// to pre-canned <see cref="HttpResponseMessage"/>s; unmapped requests
/// throw <see cref="UnexpectedRequestException"/> so a regression that
/// fetches an unintended URL fails loudly instead of silently
/// 404-ing.
/// </summary>
/// <remarks>
/// Designed for scraper-pipeline integration tests that need to
/// exercise the full <see cref="ISourceScraper.ScrapeAsync"/> flow
/// (discovery → per-page fetch → yield) without real HTTP. Records
/// every request URL in <see cref="Requests"/> so tests can assert
/// the scraper hit the expected pages in the expected order.
/// </remarks>
public sealed class QueueingHttpMessageHandler : HttpMessageHandler
{
    private readonly Dictionary<string, Func<HttpRequestMessage, HttpResponseMessage>> _responses
        = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Every request URL the handler received, in order.</summary>
    public List<Uri> Requests { get; } = [];

    /// <summary>
    /// Map an absolute URL to a 200 OK response with the supplied HTML body.
    /// </summary>
    public QueueingHttpMessageHandler MapHtml(string url, string html)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(url);
        ArgumentNullException.ThrowIfNull(html);
        return Map(url, _ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(html, Encoding.UTF8, "text/html"),
        });
    }

    /// <summary>
    /// Map an absolute URL to a 200 OK response with the supplied JSON body.
    /// </summary>
    public QueueingHttpMessageHandler MapJson(string url, string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(url);
        ArgumentNullException.ThrowIfNull(json);
        return Map(url, _ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        });
    }

    /// <summary>
    /// Map an absolute URL to a 200 OK response with the supplied XML body.
    /// </summary>
    public QueueingHttpMessageHandler MapXml(string url, string xml)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(url);
        ArgumentNullException.ThrowIfNull(xml);
        return Map(url, _ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(xml, Encoding.UTF8, "application/xml"),
        });
    }

    /// <summary>
    /// Map an absolute URL to a response built by the supplied
    /// factory. The factory is called per request, so it can return
    /// a fresh <see cref="HttpResponseMessage"/> (necessary because
    /// <see cref="HttpResponseMessage"/> is single-use).
    /// </summary>
    public QueueingHttpMessageHandler Map(string url, Func<HttpRequestMessage, HttpResponseMessage> factory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(url);
        ArgumentNullException.ThrowIfNull(factory);
        _responses[url] = factory;
        return this;
    }

    /// <inheritdoc />
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var url = request.RequestUri ?? throw new InvalidOperationException("Request must have a RequestUri.");
        Requests.Add(url);

        if (!_responses.TryGetValue(url.AbsoluteUri, out var factory))
        {
            throw new UnexpectedRequestException(url, _responses.Keys);
        }

        return Task.FromResult(factory(request));
    }
}

/// <summary>
/// Thrown by <see cref="QueueingHttpMessageHandler"/> when a test
/// scraper fetches a URL the test didn't map. The exception message
/// lists the mapped URLs so the test author can see why the lookup
/// failed at a glance.
/// </summary>
public sealed class UnexpectedRequestException : Exception
{
    /// <summary>Initializes a new <see cref="UnexpectedRequestException"/>.</summary>
    public UnexpectedRequestException(Uri url, IEnumerable<string> mappedUrls)
        : base(Build(url, mappedUrls))
    {
        Url = url;
    }

    /// <summary>The URL the test scraper attempted to fetch.</summary>
    public Uri Url { get; }

    private static string Build(Uri url, IEnumerable<string> mappedUrls)
    {
        var mapped = string.Join("\n  ", mappedUrls.OrderBy(u => u));
        return $"Test scraper fetched an unmapped URL: {url}\n\nMapped URLs:\n  {mapped}";
    }
}
