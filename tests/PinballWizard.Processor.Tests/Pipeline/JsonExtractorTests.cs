using System.Text;
using PinballWizard.Processor.Pipeline;
using Xunit;

namespace PinballWizard.Processor.Tests.Pipeline;

public class JsonExtractorTests
{
    private readonly JsonExtractor _sut = new();

    [Theory]
    [InlineData("application/json", ".json", true)]
    [InlineData("application/pdf", ".pdf", false)]
    [InlineData("text/html", ".html", false)]
    public void CanExtract_ReturnsExpected(string mimeType, string extension, bool expected)
    {
        Assert.Equal(expected, _sut.CanExtract(mimeType, extension));
    }

    [Fact]
    public async Task ExtractAsync_OpdbRecord_ExtractsFields()
    {
        var json = """
            {
                "opdb_id": "abc123",
                "name": "Stranger Things",
                "manufacturer": "Stern",
                "machine_type": "Solid State Electronic",
                "year": "2019",
                "design_by": "John Borg"
            }
            """;
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));

        var result = await _sut.ExtractAsync(stream, "opdb.json");

        Assert.Contains("Machine Name: Stranger Things", result.Text);
        Assert.Contains("Manufacturer: Stern", result.Text);
        Assert.Contains("Designed By: John Borg", result.Text);
        Assert.Equal("OpdbApi", result.Metadata["sourceType"]);
    }

    [Fact]
    public async Task ExtractAsync_PinballMapRecord_ExtractsLocationFields()
    {
        var json = """
            {
                "name": "Quarters Arcade",
                "city": "Portland",
                "state": "OR",
                "num_machines": 25,
                "location": true,
                "machine_conditions": [
                    { "name": "Medieval Madness", "condition": "Great" }
                ]
            }
            """;
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));

        var result = await _sut.ExtractAsync(stream, "pinballmap.json");

        Assert.Contains("Location Name: Quarters Arcade", result.Text);
        Assert.Contains("City: Portland", result.Text);
        Assert.Contains("Medieval Madness: Great", result.Text);
        Assert.Equal("PinballMapApi", result.Metadata["sourceType"]);
    }

    [Fact]
    public async Task ExtractAsync_IfpaRecord_ExtractsPlayerFields()
    {
        var json = """
            {
                "player_id": "12345",
                "first_name": "Keith",
                "last_name": "Elwin",
                "wppr_rank": "1",
                "wppr_points": "2500.50",
                "country_name": "United States"
            }
            """;
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));

        var result = await _sut.ExtractAsync(stream, "ifpa.json");

        Assert.Contains("First Name: Keith", result.Text);
        Assert.Contains("Last Name: Elwin", result.Text);
        Assert.Contains("WPPR Rank: 1", result.Text);
        Assert.Equal("IfpaApi", result.Metadata["sourceType"]);
    }

    [Fact]
    public async Task ExtractAsync_GenericJson_ExtractsAllFields()
    {
        var json = """{ "key1": "value1", "key2": 42 }""";
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));

        var result = await _sut.ExtractAsync(stream, "generic.json");

        Assert.Contains("key1: value1", result.Text);
        Assert.Contains("key2: 42", result.Text);
    }

    [Fact]
    public async Task ExtractAsync_SetsExtractorMetadata()
    {
        var json = """{ "test": true }""";
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));

        var result = await _sut.ExtractAsync(stream, "test.json");

        Assert.Equal("JsonExtractor", result.Metadata["extractor"]);
        Assert.Equal("test.json", result.Metadata["filename"]);
    }
}
