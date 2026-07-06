using System.Net;
using Xunit;

namespace PinballWizard.Web.Tests.A11y;

// Server-side-render smoke test for the /engineering routes, using the same
// minimal host as the accessibility + snapshot suites. A browser-free guard:
// it asserts the routes return 200 (not a 500 from a missing DI registration),
// which is the class of failure that otherwise only surfaces as an empty
// document in the much slower, browser-gated axe scan.
//
// Root cause this guards against: the /engineering pages inject
// IEngineeringDocsProvider. The real app registers it in Program.cs, but this
// minimal test host builds its own service list — if a page's dependency is
// missing here, SSR throws and axe sees <html><head></head><body></body></html>.
[Trait("Category", "Accessibility")]
public sealed class EngineeringSsrSmokeTests(PlaywrightWebApplicationFactory factory)
    : IClassFixture<PlaywrightWebApplicationFactory>
{
    [Theory]
    [InlineData("/engineering")]
    [InlineData("/engineering/docs/glossary")]
    [InlineData("/engineering/adr")]
    public async Task EngineeringRoute_RendersWithoutServerError(string path)
    {
        using var client = new HttpClient();

        var response = await client.GetAsync($"{factory.ServerAddress}{path}");
        var body = await response.Content.ReadAsStringAsync();

        Assert.True(
            response.StatusCode == HttpStatusCode.OK,
            $"{path} returned {(int)response.StatusCode} {response.StatusCode}; " +
            $"expected 200. First 500 chars of body:\n{body[..Math.Min(500, body.Length)]}");
    }

    // Browser-free guard for the axe [aria-input-field-name] + [nested-interactive]
    // violations: those come from MudList's role="listbox" on static content. The
    // /engineering pages and the markdown renderer emit native <ul>/<ol> instead,
    // so the rendered HTML must contain no listbox role.
    [Theory]
    [InlineData("/engineering")]
    [InlineData("/engineering/docs/glossary")]
    public async Task EngineeringRoute_HasNoInteractiveListboxRole(string path)
    {
        using var client = new HttpClient();

        var body = await client.GetStringAsync($"{factory.ServerAddress}{path}");

        Assert.DoesNotContain("role=\"listbox\"", body);
    }
}
