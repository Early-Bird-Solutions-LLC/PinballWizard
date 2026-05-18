using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using PinballWizard.Application.Ai.Tools;
using PinballWizard.Application.Observability;
using PinballWizard.Application.Persistence;
using PinballWizard.Core.Domain;
using Xunit;

namespace PinballWizard.Scraper.Tests.Ai;

public sealed class MachineGroundingToolTests
{
    [Fact]
    public async Task GetMachineByTitleAsync_ReturnsFirstMatch_MappedToDto()
    {
        var machine = new Machine
        {
            Id = "GRBN-MQR4P",
            PartitionKey = "stern",
            ManufacturerDisplayName = "Stern Pinball",
            Title = "Foo Fighters",
            Year = 2023,
            Themes = ["rock", "music"],
            Designers = ["George Gomez"],
            OpdbSourceUrl = "https://opdb.org/machines/GRBN-MQR4P",
            Editions =
            [
                new MachineEdition { Name = "Pro", Msrp = "$7,000" },
                new MachineEdition { Name = "Premium", Msrp = "$9,500" },
            ],
            FirstSeenAt = DateTimeOffset.UtcNow,
            LastSeenAt = DateTimeOffset.UtcNow,
        };

        var repo = Substitute.For<IMachineRepository>();
        repo.QueryByTitleAsync("Foo Fighters", Arg.Any<CancellationToken>())
            .Returns(ToAsyncEnumerable(machine));

        var tool = new MachineGroundingTool(repo, Substitute.For<IMachineTitleLookupRepository>(), NullLogger<MachineGroundingTool>.Instance);
        var result = await tool.GetMachineByTitleAsync("Foo Fighters", CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("GRBN-MQR4P", result!.OpdbId);
        Assert.Equal("Foo Fighters", result.Title);
        Assert.Equal("Stern Pinball", result.Manufacturer);
        Assert.Equal(2023, result.Year);
        Assert.Equal(["rock", "music"], result.Themes);
        Assert.Equal(["George Gomez"], result.Designers);
        Assert.Equal("https://opdb.org/machines/GRBN-MQR4P", result.OpdbSourceUrl);
        Assert.Equal(2, result.Editions.Count);
        Assert.Equal("Pro", result.Editions[0].Name);
        Assert.Equal("$7,000", result.Editions[0].Msrp);
    }

    [Fact]
    public async Task GetMachineByTitleAsync_NoMatch_ReturnsNull()
    {
        var repo = Substitute.For<IMachineRepository>();
        repo.QueryByTitleAsync("nonexistent", Arg.Any<CancellationToken>())
            .Returns(ToAsyncEnumerable<Machine>());

        var tool = new MachineGroundingTool(repo, Substitute.For<IMachineTitleLookupRepository>(), NullLogger<MachineGroundingTool>.Instance);
        var result = await tool.GetMachineByTitleAsync("nonexistent", CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task GetMachineByTitleAsync_EmptyTitle_ReturnsNullWithoutQuerying()
    {
        var repo = Substitute.For<IMachineRepository>();

        var tool = new MachineGroundingTool(repo, Substitute.For<IMachineTitleLookupRepository>(), NullLogger<MachineGroundingTool>.Instance);
        var result = await tool.GetMachineByTitleAsync(string.Empty, CancellationToken.None);

        Assert.Null(result);
        repo.DidNotReceiveWithAnyArgs().QueryByTitleAsync(default!, default);
    }

    [Fact]
    public async Task GetMachineByTitleAsync_WhitespaceTitle_ReturnsNullWithoutQuerying()
    {
        var repo = Substitute.For<IMachineRepository>();

        var tool = new MachineGroundingTool(repo, Substitute.For<IMachineTitleLookupRepository>(), NullLogger<MachineGroundingTool>.Instance);
        var result = await tool.GetMachineByTitleAsync("   ", CancellationToken.None);

        Assert.Null(result);
        repo.DidNotReceiveWithAnyArgs().QueryByTitleAsync(default!, default);
    }

    [Fact]
    public async Task GetMachineByTitleAsync_MultipleMatches_ReturnsFirst()
    {
        var first = NewMachine("GRBN-MQR4P", "Foo Fighters", 2023);
        var second = NewMachine("GRBN-XYZZZ", "Foo Fighters", 1992); // hypothetical re-issue

        var repo = Substitute.For<IMachineRepository>();
        repo.QueryByTitleAsync("Foo Fighters", Arg.Any<CancellationToken>())
            .Returns(ToAsyncEnumerable(first, second));

        var tool = new MachineGroundingTool(repo, Substitute.For<IMachineTitleLookupRepository>(), NullLogger<MachineGroundingTool>.Instance);
        var result = await tool.GetMachineByTitleAsync("Foo Fighters", CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("GRBN-MQR4P", result!.OpdbId);
    }

    // ── ADR-0025 § 4 point-read path ─────────────────────────────────────
    // PR 5 of the Cosmos for User Delight track replaces the cross-
    // partition `STRINGEQUALS` query with a two-point-read path: first
    // the `machine_title_lookups` materialized view, then `machines`
    // by (opdb_id, manufacturer). The cross-partition query survives as
    // a logged-warning fallback for the unmigrated-lookup case.

    [Fact]
    public async Task GetMachineByTitleAsync_LookupHit_UsesPointReadAndDoesNotQueryFallback()
    {
        var lookup = new MachineTitleLookup
        {
            Id = MachineTitleLookup.NormalizeTitle("Foo Fighters"),
            PartitionKey = MachineTitleLookup.NormalizeTitle("Foo Fighters"),
        };
        lookup.UpsertEntry("GRBN-MQR4P", "stern");

        var lookups = Substitute.For<IMachineTitleLookupRepository>();
        lookups.GetByTitleAsync("Foo Fighters", Arg.Any<CancellationToken>()).Returns(lookup);

        var repo = Substitute.For<IMachineRepository>();
        repo.GetByOpdbIdAsync("GRBN-MQR4P", "stern", Arg.Any<CancellationToken>())
            .Returns(NewMachine("GRBN-MQR4P", "Foo Fighters", 2023));

        var tool = new MachineGroundingTool(repo, lookups, NullLogger<MachineGroundingTool>.Instance);
        var result = await tool.GetMachineByTitleAsync("Foo Fighters", CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("GRBN-MQR4P", result!.OpdbId);
        // Point-read path bypasses QueryByTitleAsync entirely.
        repo.DidNotReceiveWithAnyArgs().QueryByTitleAsync(default!, default);
    }

    [Fact]
    public async Task GetMachineByTitleAsync_LookupHitButMachineMissing_FallsBackAndStillResolves()
    {
        // Stale lookup — the lookup row points at a machine that no
        // longer exists in the `machines` container. The tool must
        // fall through to the cross-partition fallback so the user
        // query still resolves; the next OPDB sync will fix the row.
        var lookup = new MachineTitleLookup
        {
            Id = MachineTitleLookup.NormalizeTitle("Stranger Things"),
            PartitionKey = MachineTitleLookup.NormalizeTitle("Stranger Things"),
        };
        lookup.UpsertEntry("GRBN-STALE", "stern");

        var lookups = Substitute.For<IMachineTitleLookupRepository>();
        lookups.GetByTitleAsync("Stranger Things", Arg.Any<CancellationToken>()).Returns(lookup);

        var repo = Substitute.For<IMachineRepository>();
        // The lookup-pointed-at machine is gone (point-read returns null).
        repo.GetByOpdbIdAsync("GRBN-STALE", "stern", Arg.Any<CancellationToken>()).Returns((Machine?)null);
        // The fallback cross-partition query still finds a (different) match.
        var current = NewMachine("GRBN-CURRENT", "Stranger Things", 2024);
        repo.QueryByTitleAsync("Stranger Things", Arg.Any<CancellationToken>())
            .Returns(ToAsyncEnumerable(current));

        var tool = new MachineGroundingTool(repo, lookups, NullLogger<MachineGroundingTool>.Instance);
        var result = await tool.GetMachineByTitleAsync("Stranger Things", CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("GRBN-CURRENT", result!.OpdbId);
        repo.Received(1).QueryByTitleAsync("Stranger Things", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetMachineByTitleAsync_LookupMissing_FallsBackToCrossPartitionQuery()
    {
        // Lookup row doesn't exist (post-deploy backfill pending, or a
        // transient lookup-write failure during OPDB sync). The tool
        // must still resolve the answer via the fallback path.
        var lookups = Substitute.For<IMachineTitleLookupRepository>();
        lookups.GetByTitleAsync("Godzilla", Arg.Any<CancellationToken>()).Returns((MachineTitleLookup?)null);

        var repo = Substitute.For<IMachineRepository>();
        repo.QueryByTitleAsync("Godzilla", Arg.Any<CancellationToken>())
            .Returns(ToAsyncEnumerable(NewMachine("GRBN-GODZ", "Godzilla", 2021)));

        var tool = new MachineGroundingTool(repo, lookups, NullLogger<MachineGroundingTool>.Instance);
        var result = await tool.GetMachineByTitleAsync("Godzilla", CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("GRBN-GODZ", result!.OpdbId);
        repo.Received(1).QueryByTitleAsync("Godzilla", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetMachineByTitleAsync_LookupHitWithCollision_ReturnsFirstEntry()
    {
        // Title collisions (e.g. multiple "Godzilla" releases) are
        // stored as parallel entries on the same row. The tool returns
        // the first entry's machine, matching the existing first-hit
        // semantics from the cross-partition query path.
        var lookup = new MachineTitleLookup
        {
            Id = MachineTitleLookup.NormalizeTitle("Godzilla"),
            PartitionKey = MachineTitleLookup.NormalizeTitle("Godzilla"),
        };
        lookup.UpsertEntry("GRBN-G1995", "sega");
        lookup.UpsertEntry("GRBN-G2021", "stern");

        var lookups = Substitute.For<IMachineTitleLookupRepository>();
        lookups.GetByTitleAsync("Godzilla", Arg.Any<CancellationToken>()).Returns(lookup);

        var repo = Substitute.For<IMachineRepository>();
        repo.GetByOpdbIdAsync("GRBN-G1995", "sega", Arg.Any<CancellationToken>())
            .Returns(NewMachine("GRBN-G1995", "Godzilla", 1995));

        var tool = new MachineGroundingTool(repo, lookups, NullLogger<MachineGroundingTool>.Instance);
        var result = await tool.GetMachineByTitleAsync("Godzilla", CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("GRBN-G1995", result!.OpdbId);
        // Only the first entry's GetByOpdbIdAsync should fire — no
        // second-entry follow-up unless the first one was missing.
        await repo.DidNotReceive().GetByOpdbIdAsync("GRBN-G2021", "stern", Arg.Any<CancellationToken>());
    }

    // ── ADR-0029 S5: sibling-returning behavior ───────────────────────
    // After resolving the primary machine, if it has a GroupId the tool
    // fetches all base-machine records in the same group so the agent
    // can ask a targeted clarifying question for version-dependent
    // questions. The primary machine is excluded from Siblings.

    [Fact]
    public async Task GetMachineByTitleAsync_WithGroupId_FetchesSiblingsAndExcludesPrimary()
    {
        // Fixture: Godzilla Pro (GweeP-MW95j) resolved as primary.
        // Group GweeP also contains Premium/LE (GweeP-Ml9pZ).
        // Sibling fetch returns both; only the non-primary should land in Siblings.
        var primary = new Machine
        {
            Id = "GweeP-MW95j",
            PartitionKey = "stern",
            ManufacturerDisplayName = "Stern Pinball",
            Title = "Godzilla Pro",
            Year = 2021,
            GroupId = "GweeP",
            Editions = [new MachineEdition { Name = "Pro", Msrp = "$7,999" }],
            FirstSeenAt = DateTimeOffset.UtcNow,
            LastSeenAt = DateTimeOffset.UtcNow,
        };
        var sibling = new Machine
        {
            Id = "GweeP-Ml9pZ",
            PartitionKey = "stern",
            ManufacturerDisplayName = "Stern Pinball",
            Title = "Godzilla Premium/LE",
            Year = 2021,
            GroupId = "GweeP",
            Editions = [new MachineEdition { Name = "Premium", Msrp = "$9,999" }],
            FirstSeenAt = DateTimeOffset.UtcNow,
            LastSeenAt = DateTimeOffset.UtcNow,
        };

        var lookups = Substitute.For<IMachineTitleLookupRepository>();
        lookups.GetByTitleAsync("Godzilla", Arg.Any<CancellationToken>())
            .Returns((MachineTitleLookup?)null);

        var repo = Substitute.For<IMachineRepository>();
        repo.QueryByTitleAsync("Godzilla", Arg.Any<CancellationToken>())
            .Returns(ToAsyncEnumerable(primary));
        // GetSiblingsByGroupIdAsync returns both primary and sibling —
        // the tool must exclude the primary from Siblings.
        repo.GetSiblingsByGroupIdAsync("GweeP", Arg.Any<CancellationToken>())
            .Returns(ToAsyncEnumerable(primary, sibling));

        var tool = new MachineGroundingTool(repo, lookups, NullLogger<MachineGroundingTool>.Instance);
        var result = await tool.GetMachineByTitleAsync("Godzilla", CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("GweeP-MW95j", result!.OpdbId);
        Assert.Equal("GweeP", result.GroupId);

        // Siblings contains the Premium/LE but NOT the primary Pro.
        Assert.Single(result.Siblings);
        Assert.Equal("GweeP-Ml9pZ", result.Siblings[0].OpdbId);
        Assert.Equal("Godzilla Premium/LE", result.Siblings[0].Title);
        Assert.Equal("Premium", result.Siblings[0].Editions[0].Name);
    }

    [Fact]
    public async Task GetMachineByTitleAsync_NoGroupId_ReturnEmptySiblings()
    {
        // A machine with no GroupId (solo title, no OPDB group) must
        // return an empty Siblings list — not null — so the agent prompt
        // can safely check Siblings.Count without null-checking.
        var machine = NewMachine("GRBN-SOLO", "Solo Title", 2020);
        // GroupId is null by default in NewMachine.

        var repo = Substitute.For<IMachineRepository>();
        repo.QueryByTitleAsync("Solo Title", Arg.Any<CancellationToken>())
            .Returns(ToAsyncEnumerable(machine));

        var tool = new MachineGroundingTool(repo, Substitute.For<IMachineTitleLookupRepository>(), NullLogger<MachineGroundingTool>.Instance);
        var result = await tool.GetMachineByTitleAsync("Solo Title", CancellationToken.None);

        Assert.NotNull(result);
        Assert.Null(result!.GroupId);
        Assert.Empty(result.Siblings);
        // GetSiblingsByGroupIdAsync must NOT be called when GroupId is absent.
        repo.DidNotReceiveWithAnyArgs().GetSiblingsByGroupIdAsync(default!, default);
    }

    [Fact]
    public async Task GetMachineByTitleAsync_SiblingFetchFails_StillReturnsPrimary()
    {
        // Sibling fetch is best-effort. A Cosmos failure during sibling
        // fetch must NOT prevent the primary machine from being returned.
        // The tool degrades to single-machine mode (empty Siblings).
        var primary = new Machine
        {
            Id = "GweeP-MW95j",
            PartitionKey = "stern",
            ManufacturerDisplayName = "Stern Pinball",
            Title = "Godzilla Pro",
            Year = 2021,
            GroupId = "GweeP",
            FirstSeenAt = DateTimeOffset.UtcNow,
            LastSeenAt = DateTimeOffset.UtcNow,
        };

        var repo = Substitute.For<IMachineRepository>();
        repo.QueryByTitleAsync("Godzilla Pro", Arg.Any<CancellationToken>())
            .Returns(ToAsyncEnumerable(primary));
        repo.GetSiblingsByGroupIdAsync("GweeP", Arg.Any<CancellationToken>())
            .Returns(_ => throw new InvalidOperationException("Cosmos unavailable"));

        var tool = new MachineGroundingTool(repo, Substitute.For<IMachineTitleLookupRepository>(), NullLogger<MachineGroundingTool>.Instance);

        // Must not throw — degrades gracefully.
        var result = await tool.GetMachineByTitleAsync("Godzilla Pro", CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("GweeP-MW95j", result!.OpdbId);
        Assert.Empty(result.Siblings);
    }

    [Fact]
    public async Task GetMachineByTitleAsync_GroupWithOnlyPrimary_ReturnEmptySiblings()
    {
        // Group fetch returns only the primary machine (sole member of
        // its group in the catalog — not yet backfilled with siblings).
        // Siblings must be empty, not contain the primary itself.
        var primary = new Machine
        {
            Id = "GweeP-MW95j",
            PartitionKey = "stern",
            ManufacturerDisplayName = "Stern Pinball",
            Title = "Godzilla Pro",
            Year = 2021,
            GroupId = "GweeP",
            FirstSeenAt = DateTimeOffset.UtcNow,
            LastSeenAt = DateTimeOffset.UtcNow,
        };

        var repo = Substitute.For<IMachineRepository>();
        repo.QueryByTitleAsync("Godzilla Pro", Arg.Any<CancellationToken>())
            .Returns(ToAsyncEnumerable(primary));
        // Only the primary in the group (sibling fetch returns only it).
        repo.GetSiblingsByGroupIdAsync("GweeP", Arg.Any<CancellationToken>())
            .Returns(ToAsyncEnumerable(primary));

        var tool = new MachineGroundingTool(repo, Substitute.For<IMachineTitleLookupRepository>(), NullLogger<MachineGroundingTool>.Instance);
        var result = await tool.GetMachineByTitleAsync("Godzilla Pro", CancellationToken.None);

        Assert.NotNull(result);
        Assert.Empty(result!.Siblings);
    }

    [Fact]
    public void Ctor_NullRepository_Throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new MachineGroundingTool(null!, Substitute.For<IMachineTitleLookupRepository>(), NullLogger<MachineGroundingTool>.Instance));
    }

    [Fact]
    public void Ctor_NullTitleLookups_Throws()
    {
        var repo = Substitute.For<IMachineRepository>();
        Assert.Throws<ArgumentNullException>(() =>
            new MachineGroundingTool(repo, null!, NullLogger<MachineGroundingTool>.Instance));
    }

    [Fact]
    public void Ctor_NullLogger_Throws()
    {
        var repo = Substitute.For<IMachineRepository>();
        Assert.Throws<ArgumentNullException>(() =>
            new MachineGroundingTool(repo, Substitute.For<IMachineTitleLookupRepository>(), null!));
    }

    // ── pinwiz.ai.tool_duration_ms emission ──────────────────────────────
    // Stopwatch + try/finally produce exactly one tool_duration_ms sample
    // per invocation, tagged tool=getMachineByTitle. The whitespace-input
    // short-circuit fires before the Stopwatch starts so no-op prompts
    // don't poison the latency distribution.

    [Fact]
    public async Task GetMachineByTitleAsync_Match_EmitsToolDurationMs_TaggedGetMachineByTitle()
    {
        var repo = Substitute.For<IMachineRepository>();
        repo.QueryByTitleAsync("Foo Fighters", Arg.Any<CancellationToken>())
            .Returns(ToAsyncEnumerable(NewMachine("GRBN-MQR4P", "Foo Fighters", 2023)));

        var samples = CollectToolDurationSamples(out var listener);
        using (listener)
        {
            var tool = new MachineGroundingTool(repo, Substitute.For<IMachineTitleLookupRepository>(), NullLogger<MachineGroundingTool>.Instance);
            await tool.GetMachineByTitleAsync("Foo Fighters", CancellationToken.None);
        }

        AssertOurToolEmittedAtLeastOnce(samples);
    }

    [Fact]
    public async Task GetMachineByTitleAsync_NoMatch_StillEmitsToolDurationMs()
    {
        // The "no match" path returns null but spends real wall-clock on
        // the repository query. Latency is still operationally meaningful
        // — slow misses are a different signal from fast misses — so the
        // emission must fire here too.
        var repo = Substitute.For<IMachineRepository>();
        repo.QueryByTitleAsync("nonexistent", Arg.Any<CancellationToken>())
            .Returns(ToAsyncEnumerable<Machine>());

        var samples = CollectToolDurationSamples(out var listener);
        using (listener)
        {
            var tool = new MachineGroundingTool(repo, Substitute.For<IMachineTitleLookupRepository>(), NullLogger<MachineGroundingTool>.Instance);
            var result = await tool.GetMachineByTitleAsync("nonexistent", CancellationToken.None);
            Assert.Null(result);
        }

        AssertOurToolEmittedAtLeastOnce(samples);
    }

    [Fact]
    public async Task GetMachineByTitleAsync_WhitespaceTitle_DoesNotEmitToolDurationMs()
    {
        // Whitespace short-circuit fires before Stopwatch.StartNew, so
        // dashboards don't see noise samples for inputs the orchestrator
        // accidentally produced. Pinned so a future refactor doesn't add
        // a no-op-path sample inadvertently.
        var repo = Substitute.For<IMachineRepository>();
        var samples = CollectToolDurationSamples(out var listener);
        using (listener)
        {
            var tool = new MachineGroundingTool(repo, Substitute.For<IMachineTitleLookupRepository>(), NullLogger<MachineGroundingTool>.Instance);
            await tool.GetMachineByTitleAsync("   ", CancellationToken.None);
        }

        Assert.DoesNotContain(samples, s => s.ToolTag == MachineGroundingTool.ToolTagValue);
    }

    // The instrument is a single process-global Meter, so emissions from
    // SearchCorpusToolTests running in parallel with this class will land
    // in our listener too. We assert only on emissions tagged with this
    // tool's name — that's what the test actually cares about. We also
    // do NOT assert sample count == 1, because in parallel test runs
    // the same tool may emit again from concurrent test executions; the
    // emission *contract* is "tool fires this metric on every call" and
    // a Contains assertion captures that without coupling to scheduler.
    private static void AssertOurToolEmittedAtLeastOnce(
        IEnumerable<(double Value, string? ToolTag)> samples)
    {
        Assert.Contains(
            samples,
            s => s.ToolTag == MachineGroundingTool.ToolTagValue && s.Value >= 0);
    }

    private static ConcurrentBag<(double Value, string? ToolTag)> CollectToolDurationSamples(out MeterListener listener)
    {
        // Force `PinballWizardTelemetry`'s static cctor to complete first
        // so the instrument exists when we wire the listener. Explicitly
        // enabling the named instrument after `Start()` is more
        // deterministic than relying on the `InstrumentPublished` delivery
        // path. ConcurrentBag handles the concurrency: parallel test
        // classes that emit to the same process-global Meter cause
        // concurrent measurement callbacks on this listener (each test
        // class emits to AiToolDurationMs from its own threads), so the
        // sample collection must be thread-safe.
        var samples = new ConcurrentBag<(double Value, string? ToolTag)>();
        var l = new MeterListener();
        l.SetMeasurementEventCallback<double>((_, value, tags, _) =>
        {
            string? toolTag = null;
            foreach (var t in tags)
            {
                if (t.Key == "tool")
                {
                    toolTag = t.Value as string;
                }
            }
            samples.Add((value, toolTag));
        });
        l.Start();
        l.EnableMeasurementEvents(PinballWizardTelemetry.AiToolDurationMs);
        listener = l;
        return samples;
    }

    private static Machine NewMachine(string id, string title, int year) => new()
    {
        Id = id,
        PartitionKey = "stern",
        ManufacturerDisplayName = "Stern Pinball",
        Title = title,
        Year = year,
        FirstSeenAt = DateTimeOffset.UtcNow,
        LastSeenAt = DateTimeOffset.UtcNow,
    };

    private static async IAsyncEnumerable<T> ToAsyncEnumerable<T>(params T[] items)
    {
        foreach (var item in items)
        {
            yield return item;
        }
        await Task.CompletedTask;
    }
}