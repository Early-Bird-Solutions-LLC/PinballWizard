using System.Text;
using System.Text.Json;
using PinballWizard.Infrastructure.Persistence.Cosmos;
using Xunit;

namespace PinballWizard.Infrastructure.Tests.Persistence.Cosmos;

/// <summary>
/// Pins the read contract for <see cref="AgentPromptOverrideCosmosRecord"/>: every field is
/// <c>required</c>, so the record can only be deserialized from a FULL document. A query that
/// projects a subset (e.g. <c>SELECT c.version FROM c</c>) returns partial JSON and throws
/// <see cref="JsonException"/> on the missing required members.
///
/// This guards <c>CosmosAgentPromptOverrideRepository.SaveNewVersionAsync</c>, which reads the
/// current max version: it must select the full top document, not just <c>c.version</c>. (The
/// original `SELECT c.version` form threw at runtime the moment a second version was saved for any
/// agent — same required-field deserialization class as the #318 edition_scope incident.)
/// </summary>
public sealed class AgentPromptOverrideCosmosRecordTests
{
    private static SystemTextJsonCosmosSerializer Serializer() =>
        new(CosmosClientConfiguration.BuildJsonOptions());

    private static MemoryStream StreamOf(string json) =>
        new(Encoding.UTF8.GetBytes(json));

    [Fact]
    public void VersionOnlyProjection_Throws_SoTheQueryMustSelectTheFullDocument()
    {
        // What `SELECT c.version FROM c` returns — partial JSON.
        const string versionOnlyJson = """{ "version": 3 }""";

        Assert.Throws<JsonException>(() =>
            Serializer().FromStream<AgentPromptOverrideCosmosRecord>(StreamOf(versionOnlyJson)));
    }

    [Fact]
    public void FullDocument_DeserializesAndExposesVersion()
    {
        const string fullJson =
            """
            {
              "id": "Wizard:v3",
              "agent_name": "Wizard",
              "version": 3,
              "content": "prompt body",
              "is_active": true,
              "updated_at_utc": "2026-06-18T00:00:00+00:00",
              "updated_by": "jim@earlybirdsolutions.com"
            }
            """;

        var record = Serializer().FromStream<AgentPromptOverrideCosmosRecord>(StreamOf(fullJson));

        Assert.Equal(3, record.Version);
    }
}
