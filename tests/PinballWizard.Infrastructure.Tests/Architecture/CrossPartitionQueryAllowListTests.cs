using System.Text.RegularExpressions;
using Xunit;

namespace PinballWizard.Infrastructure.Tests.Architecture;

// Enforces ADR-0036: cross-partition Cosmos queries (Tier 2) are a conscious,
// reviewed exception. Two mechanisms exist for issuing a cross-partition query:
//
//   (a) StreamCrossPartitionAsync — the repository base method added by ADR-0036,
//       which routes through the shared async-enumerable iterator and per-page
//       metrics emission.
//
//   (b) Container.GetItemQueryIterator<T> called directly with no PartitionKey —
//       a direct SDK escape hatch used when the method needs custom per-page logic
//       (e.g. MaxItemCount tuning, non-generic projection). Routing these through
//       the base is a tracked future cleanup (see allow-list justifications below).
//
// Every .cs file in the Cosmos repository directory (other than CosmosRepository.cs,
// which is the definition site for both patterns) that uses EITHER mechanism MUST
// appear in AllowList with a justification. A new cross-partition site in a file
// not yet in the list fails this test until a reviewer consciously adds it here —
// forcing cross-partition queries to be an explicit, documented decision.
public sealed class CrossPartitionQueryAllowListTests
{
    // Keys: file names (not paths) of every .cs file in the Cosmos persistence
    // directory that legitimately issues a cross-partition query.
    // Values: short justification — which pattern(s) and why it is bounded/Tier-2.
    private static readonly Dictionary<string, string> AllowList = new(StringComparer.Ordinal)
    {
        // StreamCrossPartitionAsync — StreamAllAsync scans all manufacturers in
        // one pass for the linker initialiser. Direct-iterator GetItemQueryIterator
        // used for QueryByTitleAsync (MaxItemCount=1, equality match, ~2,400 docs)
        // and GetSiblingsByGroupIdAsync (MaxItemCount=10, equality match, 1–10 docs).
        // Both direct-iterator callers emit metrics via ExecuteWithMetricsAsync.
        // Routing them through the base is a tracked cleanup (ADR-0025 § 4 PR-5).
        ["MachineRepository.cs"] =
            "StreamCrossPartitionAsync (StreamAllAsync) + direct GetItemQueryIterator<Machine> " +
            "(QueryByTitleAsync MaxItemCount=1, GetSiblingsByGroupIdAsync MaxItemCount=10); " +
            "bounded equality matches, metered; direct-iterator routing is tracked cleanup.",

        // Direct-iterator GetItemQueryIterator<string> — projects only machine_id
        // (not SELECT *) to enumerate fan-out rows for one document_id across
        // machine-id partitions. Admin/re-link path; handful of rows per doc.
        ["CosmosScrapedDocumentRepository.cs"] =
            "Direct GetItemQueryIterator<string> in StreamByDocumentIdAsync; cross-partition " +
            "by design (fan-out rows for one document_id live in different machine_id partitions); " +
            "admin / --relink-all path; projects VALUE c.machine_id only, not SELECT *.",

        // StreamCrossPartitionAsync — StreamByStatusAsync (linker batch via IN clause),
        // StreamAllAsync (full raw-doc scan for admin/export), and
        // StreamBySourcePatternAsync (linker pattern matching). All are back-office
        // / batch paths, not user-facing query hot paths.
        ["CosmosRawDocumentRepository.cs"] =
            "StreamCrossPartitionAsync in StreamByStatusAsync (linker IN-clause batch), " +
            "StreamAllAsync (admin full-scan), and StreamBySourcePatternAsync (linker " +
            "CONTAINS pattern match); all are back-office / batch paths.",

        // StreamCrossPartitionAsync — GetAllDocumentsAsync reads ~6 curated docs
        // for the landing page strip. Bounded to ~6 entries; ADR-0025 § 6 notes
        // cross-partition is acceptable for small write-rarely containers.
        ["FeaturedMachineRepository.cs"] =
            "StreamCrossPartitionAsync in GetAllDocumentsAsync; bounded ~6-doc container " +
            "for the landing page strip; acceptable per ADR-0025 § 6.",

        // StreamCrossPartitionAsync — LoadAllAsync eagerly loads all link overrides
        // at startup (<1,000 records) into the in-process linker cache to avoid
        // per-resolution latency.
        ["CosmosLinkOverrideRepository.cs"] =
            "StreamCrossPartitionAsync in LoadAllAsync; startup-time eager load of " +
            "all link overrides (<1,000 docs) into the linker cache.",

        // StreamCrossPartitionAsync — GetAllAsync loads all admin settings for the
        // settings UI (bypasses the per-instance TTL cache to show truth; tens of
        // docs at most).
        ["CosmosAdminSettingsRepository.cs"] =
            "StreamCrossPartitionAsync in GetAllAsync; uncached settings-page load " +
            "showing truth; tens of documents at most.",
    };

    [Fact]
    public void EveryCrossPartitionCallSite_IsInTheAllowList()
    {
        var cosmosDir = Path.Combine(RepoRoot(), "src", "PinballWizard.Infrastructure", "Persistence", "Cosmos");

        // Detect EITHER cross-partition pattern:
        //   (a) .StreamCrossPartitionAsync( — the ADR-0036 base method
        //   (b) .GetItemQueryIterator< — the direct SDK call (generic form used in all current sites)
        var patterns = new Regex(
            @"\.StreamCrossPartitionAsync\s*\(|\.GetItemQueryIterator\s*<",
            RegexOptions.Compiled);

        var offenders = new List<string>();

        foreach (var file in Directory.EnumerateFiles(cosmosDir, "*.cs", SearchOption.AllDirectories))
        {
            var name = Path.GetFileName(file);

            // CosmosRepository.cs is the definition site for StreamCrossPartitionAsync
            // and also calls GetItemQueryIterator internally — exclude it from scanning.
            if (name == "CosmosRepository.cs")
                continue;

            if (!patterns.IsMatch(File.ReadAllText(file)))
                continue;

            if (!AllowList.ContainsKey(name))
                offenders.Add(name);
        }

        Assert.True(
            offenders.Count == 0,
            "New cross-partition Cosmos query outside the ADR-0036 allow-list: " +
            string.Join(", ", offenders) +
            ". If justified Tier 2, add it to AllowList with a bound/justification; " +
            "if user-facing/unbounded, use a Tier 3 projection instead.");
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "PinballWizard.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException(
            "Could not locate repo root (no PinballWizard.slnx found walking up from test assembly).");
    }
}
