using System.Text;
using PinballWizard.Processor.Pipeline;
using Xunit;

namespace PinballWizard.Processor.Tests.Pipeline;

public class HtmlExtractorTests
{
    private readonly HtmlExtractor _sut = new();

    [Theory]
    [InlineData("text/html", ".html", true)]
    [InlineData("application/xhtml+xml", ".xhtml", true)]
    [InlineData("text/html", ".htm", true)]
    [InlineData("application/pdf", ".pdf", false)]
    [InlineData("application/json", ".json", false)]
    public void CanExtract_ReturnsExpected(string mimeType, string extension, bool expected)
    {
        Assert.Equal(expected, _sut.CanExtract(mimeType, extension));
    }

    [Fact]
    public async Task ExtractAsync_SimpleHtml_ExtractsBodyText()
    {
        var html = "<html><body><h1>Title</h1><p>Hello world</p></body></html>";
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(html));

        var result = await _sut.ExtractAsync(stream, "test.html");

        Assert.Contains(result.Sections, s => s.Content == "Title");
        Assert.Contains(result.Sections, s => s.Content == "Hello world");
        Assert.Contains("Title", result.Text);
        Assert.Contains("Hello world", result.Text);
    }

    [Fact]
    public async Task ExtractAsync_StripsNavAndFooter()
    {
        var html = """
            <html><body>
                <nav><a href="/">Home</a></nav>
                <article><p>Article content</p></article>
                <footer><p>Footer info</p></footer>
            </body></html>
            """;
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(html));

        var result = await _sut.ExtractAsync(stream, "test.html");

        Assert.Contains(result.Sections, s => s.Content == "Article content");
        Assert.DoesNotContain(result.Sections, s => s.Content.Contains("Home"));
        Assert.DoesNotContain(result.Sections, s => s.Content.Contains("Footer info"));
    }

    [Fact]
    public async Task ExtractAsync_PreservesHeadingHierarchy()
    {
        var html = """
            <html><body>
                <h1>Main Title</h1>
                <p>Intro text</p>
                <h2>Subtitle</h2>
                <p>Details</p>
            </body></html>
            """;
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(html));

        var result = await _sut.ExtractAsync(stream, "test.html");

        var h1Section = result.Sections.First(s => s.Content == "Main Title");
        Assert.Equal(1, h1Section.Level);

        var h2Section = result.Sections.First(s => s.Content == "Subtitle");
        Assert.Equal(2, h2Section.Level);

        var detailsSection = result.Sections.First(s => s.Content == "Details");
        Assert.Equal("Subtitle", detailsSection.Heading);
    }

    [Fact]
    public async Task ExtractAsync_EmptyBody_ReturnsEmptyResult()
    {
        var html = "<html><body></body></html>";
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(html));

        var result = await _sut.ExtractAsync(stream, "test.html");

        Assert.Empty(result.Sections);
        Assert.Empty(result.Text);
    }

    [Fact]
    public async Task ExtractAsync_ExcludesAdElements()
    {
        var html = """
            <html><body>
                <p>Real content</p>
                <div class="advertisement"><p>Buy stuff!</p></div>
            </body></html>
            """;
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(html));

        var result = await _sut.ExtractAsync(stream, "test.html");

        Assert.Contains(result.Sections, s => s.Content == "Real content");
        Assert.DoesNotContain(result.Sections, s => s.Content.Contains("Buy stuff"));
    }

    [Fact]
    public async Task ExtractAsync_SetsMetadata()
    {
        var html = "<html><head><title>Test Page</title></head><body><p>Content</p></body></html>";
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(html));

        var result = await _sut.ExtractAsync(stream, "page.html");

        Assert.Equal("HtmlExtractor", result.Metadata["extractor"]);
        Assert.Equal("page.html", result.Metadata["filename"]);
        Assert.Equal("Test Page", result.Metadata["title"]);
    }
}
