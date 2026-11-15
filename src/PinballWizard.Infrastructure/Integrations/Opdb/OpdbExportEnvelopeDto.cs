using System.Text.Json.Serialization;

namespace PinballWizard.Infrastructure.Integrations.Opdb;

/// <summary>
/// Wire DTO for the Match Play OPDB V1 bulk export
/// (<c>https://mp-data.sfo3.cdn.digitaloceanspaces.com/latest-opdb.json</c>).
/// </summary>
/// <remarks>
/// Since 2026-10-01 the opdb.org <c>/api/*</c> surface (including
/// <c>/api/export</c>) is shut down. Match Play hosts the catalog as a
/// daily CDN JSON export. The V1 shape keeps machines, aliases, and
/// machine-groups in separate arrays — groups were previously only
/// reachable via per-segment <c>GET /api/machines/{segment}</c>.
/// <para>
/// V2 (<c>opdb-v2.json</c>) uses a different unified <c>entries</c>
/// envelope and is intentionally NOT accepted here: wrong shape must
/// fail loudly rather than silently yield an empty catalog.
/// </para>
/// </remarks>
public sealed class OpdbExportEnvelopeDto
{
    [JsonPropertyName("machines")]
    public List<OpdbMachineDto> Machines { get; init; } = [];

    [JsonPropertyName("aliases")]
    public List<OpdbMachineDto> Aliases { get; init; } = [];

    [JsonPropertyName("machineGroups")]
    public List<OpdbMachineGroupDto> MachineGroups { get; init; } = [];
}
