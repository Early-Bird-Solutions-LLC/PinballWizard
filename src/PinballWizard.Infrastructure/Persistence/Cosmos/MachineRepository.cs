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

    // Cross-partition scan (ADR-0036 Tier 2) so all manufacturers are
    // covered in a single pass. RU cost scales with total item count —
    // acceptable for the infrequent InitializeAsync call on the linker.
    public IAsyncEnumerable<Machine> StreamAllAsync(CancellationToken cancellationToken) =>
        StreamCrossPartitionAsync(
            "SELECT * FROM c",
            parameters: null,
            cancellationToken);

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
    public IAsyncEnumerable<Machine> StreamByRunIdAsync(string runId, CancellationToken cancellationToken) =>
        StreamCrossPartitionAsync(
            "SELECT * FROM c WHERE c.run_id = @runId",
            new Dictionary<string, object> { ["runId"] = runId },
            cancellationToken);

    /// <summary>Hard ceiling on the forgiving substring scan.</summary>
    // A nickname/partial query should surface a handful of candidates for the
    // grounding tool to score and (if ambiguous) offer as a clarifying choice.
    // TOP 25 bounds the unindexed CONTAINS scan (ADR-0036 Tier-2 guard) well
    // above any realistic franchise family while keeping the RU cost of this
    // rare miss-only path small.
    private const int FuzzyTitleSearchLimit = 25;

    /// <inheritdoc />
    public async IAsyncEnumerable<Machine> SearchByTitleContainsAsync(
        string fragment,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fragment);

        // Case-insensitive substring match. CONTAINS(LOWER(c.title), @f) is
        // NOT index-backed — this is the forgiving fallback for getMachineByTitle
        // and fires only after all exact paths miss, so the cross-partition
        // unindexed scan is rare. SELECT TOP guards the result set per ADR-0036;
        // MaxItemCount keeps the first page small. Metered like the sibling
        // queries so the miss-path RU cost is observable, not hidden.
        var queryDefinition = new QueryDefinition(
            $"SELECT TOP {FuzzyTitleSearchLimit} * FROM c WHERE CONTAINS(LOWER(c.title), @fragment)")
            .WithParameter("@fragment", fragment.ToLowerInvariant());
        var requestOptions = new QueryRequestOptions { MaxItemCount = FuzzyTitleSearchLimit };

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
        // Premium / LE). TOP 50 is the hard result ceiling per ADR-0036
        // (Tier 2 cross-partition reads must carry a real TOP guard —
        // MaxItemCount controls page size but the HasMoreResults loop
        // would return all results without a TOP clause). 50 is
        // comfortably above any realistic edition family; MaxItemCount=10
        // keeps the first-page RU cost small for the typical 1–10 case.
        var queryDefinition = new QueryDefinition(
            "SELECT TOP 50 * FROM c WHERE c.groupId = @groupId")
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