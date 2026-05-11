using PinballWizard.Infrastructure.Scraping.ChicagoGaming;
using Xunit;

namespace PinballWizard.Tests.Unit.Infrastructure.Scraping.ChicagoGaming;

/// <summary>
/// Tests for <see cref="CgcGamePageExtractor"/>. CGC pages have no
/// JSON-LD or Open Graph product tags — the title comes from the
/// page <c>&lt;title&gt;</c> with the manufacturer suffix stripped,
/// fallback to <c>&lt;h1&gt;</c>, fallback to prettified slug.
/// Downloads are scanned via every <c>&lt;a&gt;</c> with a
/// same-host PDF href.
/// </summary>
public sealed class CgcGamePageExtractorTests
{
    private static readonly Uri SampleUrl = new("https://www.chicago-gaming.com/coinop/medieval-madness");

    [Fact]
    public void ExtractGame_StripsManufacturerSuffixFromTitle()
    {
        const string html = """
            <html>
              <head><title>Medieval Madness Merlin Edition Pinball | Chicago Gaming Company</title></head>
              <body><h1>Medieval Madness</h1></body>
            </html>
            """;

        var record = CgcGamePageExtractor.ExtractGame(html, SampleUrl);

        Assert.NotNull(record);
        Assert.Equal("Medieval Madness Merlin Edition Pinball", record!.Title);
        Assert.Equal("medieval-madness", record.Slug);
        Assert.Equal("game_cgc_medieval-madness", record.GameId);
        Assert.Equal(SampleUrl.ToString(), record.GamePageUrl);
        Assert.Equal(["cgc_coinop"], record.DiscoveredOn);
    }

    [Fact]
    public void ExtractGame_NoTitle_FallsBackToH1()
    {
        const string html = """<html><head></head><body><h1>Pulp Fiction</h1></body></html>""";

        var record = CgcGamePageExtractor.ExtractGame(html,
            new Uri("https://www.chicago-gaming.com/coinop/pulp-fiction"));

        Assert.NotNull(record);
        Assert.Equal("Pulp Fiction", record!.Title);
    }

    [Fact]
    public void ExtractGame_NoTitleNoH1_PrettifiesSlug()
    {
        const string html = "<html><head></head><body></body></html>";

        var record = CgcGamePageExtractor.ExtractGame(html,
            new Uri("https://www.chicago-gaming.com/coinop/cactus-canyon"));

        Assert.NotNull(record);
        Assert.Equal("Cactus Canyon", record!.Title);
    }

    [Fact]
    public void ExtractGame_NoSlug_ReturnsNull()
    {
        const string html = "<html><head><title>Home | Chicago Gaming Company</title></head></html>";
        var record = CgcGamePageExtractor.ExtractGame(html, new Uri("https://www.chicago-gaming.com/"));
        Assert.Null(record);
    }

    [Fact]
    public void ExtractDownloads_FindsSameHostPdfsOnly()
    {
        // Mirrors the real Pulp Fiction page download set: brochure,
        // deposit agreement, feature matrix, rules manual, warranty.
        const string html = """
            <html><body>
              <a href="/brochures/Pulp_Fiction_Brochure.pdf">Brochure</a>
              <a href="/brochures/Pulp_Fiction_Pinball_Feature_Matrix.pdf">Feature Matrix</a>
              <a href="/brochures/Pulp_Fiction_Pinball_Rules_Manual.pdf">Rules Manual</a>
              <a href="/warranties/Pulp_Fiction_Pinball_Warranty.pdf">Warranty</a>
              <a href="https://example.com/external.pdf">External PDF</a>
              <a href="/coinop/pulp-fiction/update">Update page (no extension)</a>
            </body></html>
            """;

        var url = new Uri("https://www.chicago-gaming.com/coinop/pulp-fiction");
        var links = CgcGamePageExtractor.ExtractDownloads(html, url);

        Assert.Equal(4, links.Count);
        Assert.All(links, l => Assert.Equal("pulp-fiction", l.GameSlug));
        Assert.All(links, l => Assert.Equal("Chicago Gaming Game Page", l.DiscoveryContext));
        Assert.Contains(links, l => l.FileUrl.EndsWith("Pulp_Fiction_Brochure.pdf", StringComparison.Ordinal));
        Assert.Contains(links, l => l.FileUrl.EndsWith("Pulp_Fiction_Pinball_Warranty.pdf", StringComparison.Ordinal));
        Assert.DoesNotContain(links, l => l.FileUrl.Contains("example.com", StringComparison.Ordinal));
    }

    [Fact]
    public void ExtractDownloads_DeduplicatesRepeatedHrefs()
    {
        const string html = """
            <html><body>
              <a href="/manuals/MMR_Manual.pdf">Manual (top)</a>
              <a href="/manuals/MMR_Manual.pdf">Manual (bottom)</a>
            </body></html>
            """;

        var links = CgcGamePageExtractor.ExtractDownloads(html, SampleUrl);
        Assert.Single(links);
    }

    [Theory]
    [InlineData("https://www.chicago-gaming.com/coinop/medieval-madness", "medieval-madness")]
    [InlineData("https://www.chicago-gaming.com/coinop/pulp-fiction", "pulp-fiction")]
    [InlineData("https://www.chicago-gaming.com/coinop/", null)]
    [InlineData("https://www.chicago-gaming.com/", null)]
    [InlineData("https://www.chicago-gaming.com/arcade/something", null)]
    public void ExtractSlug_ReturnsExpected(string url, string? expected)
    {
        Assert.Equal(expected, CgcGamePageExtractor.ExtractSlug(new Uri(url)));
    }

    [Fact]
    public void ExtractGame_NullArgs_Throw()
    {
        Assert.Throws<ArgumentNullException>(() => CgcGamePageExtractor.ExtractGame(null!, SampleUrl));
        Assert.Throws<ArgumentNullException>(() => CgcGamePageExtractor.ExtractGame("<html/>", null!));
    }

    [Fact]
    public void ExtractDownloads_NullArgs_Throw()
    {
        Assert.Throws<ArgumentNullException>(() => CgcGamePageExtractor.ExtractDownloads(null!, SampleUrl));
        Assert.Throws<ArgumentNullException>(() => CgcGamePageExtractor.ExtractDownloads("<html/>", null!));
    }

    [Fact]
    public void ExtractSlug_NullArg_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => CgcGamePageExtractor.ExtractSlug(null!));
    }
}
