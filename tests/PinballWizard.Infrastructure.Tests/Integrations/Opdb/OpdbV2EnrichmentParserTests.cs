using PinballWizard.Infrastructure.Integrations.Opdb;
using Xunit;

namespace PinballWizard.Infrastructure.Tests.Integrations.Opdb;

public sealed class OpdbV2EnrichmentParserTests
{
    [Fact]
    public void Parse_MachineRow_KeepsDesignRoleInIndexOrderAndPrimaryBackglass()
    {
        const string json = """
            {
              "entries": [
                {
                  "opdbId": "G5pe4-Mx",
                  "entryType": "machine",
                  "people": [
                    {"name": "Second", "role": "design", "index": 2},
                    {"name": "Artist", "role": "art", "index": 1},
                    {"name": "First", "role": "design", "index": 0},
                    {"name": "First", "role": "design", "index": 3}
                  ],
                  "images": [
                    {"primary": false, "type": "backglass", "urls": {"large": "https://img.opdb.org/other.jpg"}},
                    {"primary": true, "type": "backglass", "urls": {"small": "https://img.opdb.org/small.jpg", "large": "https://img.opdb.org/large.jpg"}}
                  ]
                },
                {"opdbId": "G5pe4", "entryType": "machineGroup", "people": [{"name": "Group Designer", "role": "design", "index": 0}]}
              ]
            }
            """;

        var parsed = OpdbV2EnrichmentParser.Parse(json);

        var row = Assert.Single(parsed);
        Assert.Equal("G5pe4-Mx", row.Key);
        Assert.Equal(["First", "Second"], row.Value.Designers);
        Assert.Equal("https://img.opdb.org/large.jpg", row.Value.ImageUrl);
    }

    [Fact]
    public void Parse_MissingEntries_Throws()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => OpdbV2EnrichmentParser.Parse("""{"machines":[]}"""));
        Assert.Contains("entries", ex.Message, StringComparison.Ordinal);
    }
}
