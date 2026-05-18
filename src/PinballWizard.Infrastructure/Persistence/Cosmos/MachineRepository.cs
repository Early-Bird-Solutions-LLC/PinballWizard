using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Logging;
using PinballWizard.Application.Persistence;
using PinballWizard.Core.Domain;

namespace PinballWizard.Infrastructure.Persistence.Cosmos;

/// <summary>
/// Cosmos-backed <see cref="IMachineRepository"/>.
/// </summary>
public sealed class MachineRepository : CosmosRepository<Machine>, IMachineRepository
{
    /// <summary>Initializes a new repository wrapping the <c>machines</c> container.</summary>
    public MachineRepository(Container container, ILogger<MachineRepository> logger)
        : base(container, logger)
    {
    }

    /// <inheritdoc />
    public Task<Machine?> GetByOpdbIdAsync(string opdbId, string manufacturer, CancellationToken cancellationToken) =>
        GetByIdAsync(opdbId, manufacturer, cancellationToken);

    /// <inheritdoc />
    public IAsyncEnumerable<Machine> StreamByManufacturerAsync(string manufacturer, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(manufacturer);
        return StreamAsync(
            "SELECT * FROM c",
            parameters: null,
            partitionKey: manufacturer,
            cancellationToken: cancellationToken);
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<Machine> QueryByTitleAsync(
        string title,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);

        // STRINGEQUALS with the third argument true performs a
        // case-insensitive comparison server-side, so "foo fighters"
        // matches a stored "Foo Fighters" without the function tool
        // having to know which casing was used by OPDB. Cross-partition
        // (partitionKey: null) — the function tool doesn't know the
        // manufacturer up front. At ~2,400 machines the RU cost of a
        // cross-partition equality match is small (single-digit RU
        // typical for sub-thousand-row scans).
        //
        // Per ADR-0025 § 4 this cross-partition query is the user-
        // delight bottleneck on the Wizard answer flow's cache-miss
        // path; PR 5 of the Cosmos delight track replaces it with a
        // point-read against a `machine_title_lookups` materialized
        // view. Until then, `MaxItemCount = 1` is the cheapest tuning
        // available — the only caller (`MachineGroundingTool`) breaks
        // on the first match, so a 1-doc page minimizes the per-page
        // RU cost without changing observable behavior.
        var queryDefinition = new QueryDefinition(
            "SELECT * FROM c WHERE STRINGEQUALS(c.title, @title, true)")
            .WithParameter("@title", title);
        var requestOptions = new QueryRequestOptions { MaxItemCount = 1 };

        // Per-page metric emission via the base helper so this
        // cross-partition query lands on `pinwiz.cosmos.query_duration_ms`
        // alongside generic `StreamAsync` calls. PR 5 of the Cosmos delight
        // track validates its point-read win against this path's pre-merge
        // p95 distribution — without metering, the win would only be
        // observable in synthetic benchmarks, not production traffic.
        using var iterator = Container.GetItemQueryIterator<Machine>(
            queryDefinition,
            requestOptions: requestOptions);
        while (iterator.HasMoreResults)
        {
            var page = await ExecuteWithMetricsAsync(
                "query",
                async ct =>
                {
                    var p = await iterator.ReadNextAsync(ct).ConfigureAwait(false);
                    return (p, p.RequestCharge);
                },
                cancellationToken).ConfigureAwait(false);
            foreach (var machine in page)
            {
                yield return machine;
            }
        }
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<Machine> GetSiblingsByGroupIdAsync(
        string groupId,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(groupId);

        // Cross-partition equality match on groupId. Expected cardinality
        // is 1–10 per ADR-0029 § data observation (typically 3: Pro /
        // Premium / LE). MaxItemCount=10 fetches all siblings in a single
        // page at the expected sizes; a second page fetch only occurs for
        // unusually large groups (>10 editions).
        var queryDefinition = new QueryDefinition(
            "SELECT * FROM c WHERE c.groupId = @groupId")
            .WithParameter("@groupId", groupId);
        var requestOptions = new QueryRequestOptions { MaxItemCount = 10 };

        using var iterator = Container.GetItemQueryIterator<Machine>(
            queryDefinition,
            requestOptions: requestOptions);
        while (iterator.HasMoreResults)
        {
            var page = await ExecuteWithMetricsAsync(
                "query",
                async ct =>
                {
                    var p = await iterator.ReadNextAsync(ct).ConfigureAwait(false);
                    return (p, p.RequestCharge);
                },
                cancellationToken).ConfigureAwait(false);
            foreach (var machine in page)
            {
                yield return machine;
            }
        }
    }
}