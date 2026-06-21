using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using PinballWizard.Application.Ai;
using PinballWizard.Application.Ai.Refusal;
using PinballWizard.Application.Observability;
using PinballWizard.Application.Persistence;
using PinballWizard.Core.Domain;
using Xunit;

namespace PinballWizard.Infrastructure.Tests.Ai;

// Behavioral tests for RefusalRecoveryService per ADR-0026 § 4 and the
// Wave 2 PR-R2/R3 spec. Each test exercises a distinct behavior path
// (token-overlap scoring, per-category community routing, cap enforcement,
// empty result, exception swallowing) to confirm that the recovery service
// enriches refusals correctly without ever breaking the primary path.
//
// IMachineRepository.QueryByTitleAsync is mocked with a synchronous
// async-iterator helper (ToAsyncEnumerable) — the same pattern used in
// MachineGroundingToolTests.
//
// ICommunityResourceLoader is mocked using NSubstitute. In tests where the
// community resource content is not the focus, the loader returns a minimal
// set that satisfies plurality minimums.
public sealed class RefusalRecoveryServiceTests
{
    // ──────────────────────────────────────────────────────────────────────
    // 1. OutOfScope → top-3 machines by token-overlap
    // ──────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task BuildRecoveryAsync_OutOfScope_Returns_Top3_Machines_By_Token_Overlap()
    {
        // Arrange: question "godzilla wizard mode tips" — tokens are
        // "godzilla", "wizard", "mode", "tips" (stop-words "and" etc. filtered).
        // Repository returns Godzilla for "godzilla" token only (1 hit),
        // Addams Family for "wizard" token only (1 hit),
        // Medieval Madness for both "wizard" and "mode" tokens (2 hits).
        // Expected order: Medieval Madness first (2), then Godzilla and Addams
        // Family (1 each).
        const string Question = "godzilla wizard mode tips";

        var godzilla = NewMachine("GRBN-GODZ", "Godzilla", "stern");
        var adamsFamily = NewMachine("GRBN-AFAM", "The Addams Family", "bally");
        var medievalMadness = NewMachine("GRBN-MMED", "Medieval Madness", "williams");

        var repo = Substitute.For<IMachineRepository>();

        // "godzilla" → Godzilla
        repo.QueryByTitleAsync("godzilla", Arg.Any<CancellationToken>())
            .Returns(ToAsyncEnumerable(godzilla));

        // "wizard" → Addams Family + Medieval Madness
        repo.QueryByTitleAsync("wizard", Arg.Any<CancellationToken>())
            .Returns(ToAsyncEnumerable(adamsFamily, medievalMadness));

        // "mode" → Medieval Madness only
        repo.QueryByTitleAsync("mode", Arg.Any<CancellationToken>())
            .Returns(ToAsyncEnumerable(medievalMadness));

        // "tips" → nothing
        repo.QueryByTitleAsync("tips", Arg.Any<CancellationToken>())
            .Returns(ToAsyncEnumerable<Machine>());

        var svc = new RefusalRecoveryService(repo, MinimalLoader(), NullLogger<RefusalRecoveryService>.Instance);

        // Act
        var detail = await svc.BuildRecoveryAsync(Question, RefusalCategory.OutOfScope, CancellationToken.None);

        // Assert
        Assert.NotNull(detail);
        Assert.NotNull(detail!.RelatedMachines);
        var machines = detail.RelatedMachines!;

        Assert.True(machines.Count >= 1, "Expected at least 1 related machine.");

        // Medieval Madness scored 2 overlapping tokens — must be first.
        Assert.Equal("GRBN-MMED", machines[0].MachineId);
        Assert.Equal("Medieval Madness", machines[0].Title);

        // Godzilla and Addams Family each scored 1. Both should appear.
        var ids = machines.Select(m => m.MachineId).ToHashSet();
        Assert.Contains("GRBN-GODZ", ids);
        Assert.Contains("GRBN-AFAM", ids);
    }

    // ──────────────────────────────────────────────────────────────────────
    // 2. Cap at 3 related machines even if more overlap
    // ──────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task BuildRecoveryAsync_LowConfidence_Caps_At_3_Related_Machines()
    {
        // Arrange: all 5 machines score 1 overlap each. Only 3 must appear
        // on the result regardless.
        const string Question = "stern pinball machine";

        var machines = Enumerable.Range(1, 5)
            .Select(i => NewMachine($"ID-{i}", $"Machine {i}", "stern"))
            .ToArray();

        var repo = Substitute.For<IMachineRepository>();

        // The token "stern" returns all 5 machines.
        repo.QueryByTitleAsync("stern", Arg.Any<CancellationToken>())
            .Returns(ToAsyncEnumerable(machines));

        // "pinball" and "machine" return nothing (filtering keeps "stern"
        // as the load-bearing token).
        repo.QueryByTitleAsync("pinball", Arg.Any<CancellationToken>())
            .Returns(ToAsyncEnumerable<Machine>());
        repo.QueryByTitleAsync("machine", Arg.Any<CancellationToken>())
            .Returns(ToAsyncEnumerable<Machine>());

        var svc = new RefusalRecoveryService(repo, MinimalLoader(), NullLogger<RefusalRecoveryService>.Instance);

        // Act
        var detail = await svc.BuildRecoveryAsync(Question, RefusalCategory.LowModelConfidence, CancellationToken.None);

        // Assert
        Assert.NotNull(detail);
        Assert.NotNull(detail!.RelatedMachines);
        Assert.Equal(3, detail.RelatedMachines!.Count);
    }

    // ──────────────────────────────────────────────────────────────────────
    // 3. UpstreamThrottled → text-only recovery; no machine lookups
    // ──────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task BuildRecoveryAsync_UpstreamThrottled_Returns_TextOnly_Recovery_Without_Querying_Repository()
    {
        // PR-R4: UpstreamThrottled is a transient infra fault. It still gets
        // a MissingWhat explanation (system-state prose) so RefusalPanel can
        // show the user "this is temporary, not a content gap." However, no
        // machine lookups or community routing fires (that would be misleading
        // for an infrastructure failure, not a corpus miss).
        var repo = Substitute.For<IMachineRepository>();

        var svc = new RefusalRecoveryService(repo, MinimalLoader(), NullLogger<RefusalRecoveryService>.Instance);

        var detail = await svc.BuildRecoveryAsync(
            "godzilla rules",
            RefusalCategory.UpstreamThrottled,
            CancellationToken.None);

        // Non-null — there IS useful text to surface.
        Assert.NotNull(detail);

        // MissingWhat is populated with the system-state explanation.
        Assert.NotNull(detail!.MissingWhat);
        Assert.NotEmpty(detail.MissingWhat!);

        // SuggestedRephrase is null — rephrase wouldn't help a rate-limit.
        Assert.Null(detail.SuggestedRephrase);

        // RelatedMachines and CommunityResources remain null — machine
        // lookups and community routing are not appropriate for transient
        // infrastructure failures.
        Assert.Null(detail.RelatedMachines);
        Assert.Null(detail.CommunityResources);

        // The repository must not be touched — category is filtered out
        // before any lookup.
        repo.DidNotReceiveWithAnyArgs().QueryByTitleAsync(default!, default);
    }

    // ──────────────────────────────────────────────────────────────────────
    // 4. No machines match any token → empty RelatedMachines (not null)
    // ──────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task BuildRecoveryAsync_NoMachinesMatch_Returns_RefusalDetail_With_Empty_RelatedMachines()
    {
        // When token-overlap scoring finds no candidate machines, the service
        // must still return a non-null RefusalDetail (the refusal was allowed)
        // with an empty (not null) RelatedMachines list. Null vs empty is a
        // meaningful distinction: null means "category unsupported or
        // exception"; empty means "supported category but nothing matched."
        var repo = Substitute.For<IMachineRepository>();

        // All token queries return nothing.
        repo.QueryByTitleAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ToAsyncEnumerable<Machine>());

        var svc = new RefusalRecoveryService(repo, MinimalLoader(), NullLogger<RefusalRecoveryService>.Instance);

        var detail = await svc.BuildRecoveryAsync(
            "xyzzy unobtainium",
            RefusalCategory.InsufficientGrounding,
            CancellationToken.None);

        Assert.NotNull(detail);
        Assert.NotNull(detail!.RelatedMachines);
        Assert.Empty(detail.RelatedMachines!);
    }

    // ──────────────────────────────────────────────────────────────────────
    // 5. Repository throws → exception swallowed, returns null
    // ──────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task BuildRecoveryAsync_RepositoryThrows_Swallows_Exception_And_Returns_Null()
    {
        // Best-effort guarantee: a repository failure must never surface to
        // the caller as an exception. The primary refusal is already
        // constructed; recovery is additive. Returning null means "no
        // enrichment available" — the caller emits the bare refusal.
        var repo = Substitute.For<IMachineRepository>();
        repo.QueryByTitleAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(_ => throw new InvalidOperationException("simulated Cosmos failure"));

        var svc = new RefusalRecoveryService(repo, MinimalLoader(), NullLogger<RefusalRecoveryService>.Instance);

        // Act: must not throw.
        var detail = await svc.BuildRecoveryAsync(
            "godzilla multiball",
            RefusalCategory.NoCitation,
            CancellationToken.None);

        Assert.Null(detail);
    }

    // ──────────────────────────────────────────────────────────────────────
    // 5b. Loader throws → counter incremented + returns null (OBS-01 / invariant #17)
    // ──────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task BuildRecoveryAsync_LoaderThrows_IncrementsErrorCounter_AndReturnsNull()
    {
        // OBS-01 / invariant #17: when ICommunityResourceLoader throws (e.g.
        // FileNotFoundException — the seed file is not on the resolved path),
        // RefusalRecoveryService must:
        //   (a) increment pinwiz.ai.community_resources_load_errors_total
        //   (b) return null (best-effort posture; primary refusal is unaffected)
        // NOT silently swallow the failure at Warning level with no metric
        // (which would make community-CTA absence look like "no resources for
        // this category" rather than "infrastructure failure").
        //
        // MeterListener pattern per project_meterlistener_test_pattern.md:
        // ConcurrentBag + Assert.Contains-with-predicate. Force the static
        // cctor on PinballWizardTelemetry before wiring the listener.
        _ = PinballWizardTelemetry.AiCommunityResourcesLoadErrors; // ensure instrument exists

        var samples = new ConcurrentBag<(long Value, string? Reason)>();
        using var listener = new MeterListener();
        listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
        {
            if (instrument.Name != "pinwiz.ai.community_resources_load_errors_total")
                return;
            string? reason = null;
            foreach (var t in tags)
            {
                if (t.Key == "reason") reason = t.Value as string;
            }
            samples.Add((value, reason));
        });
        listener.Start();
        listener.EnableMeasurementEvents(PinballWizardTelemetry.AiCommunityResourcesLoadErrors);

        // Arrange: the loader throws FileNotFoundException (the scenario when
        // SeedPathResolver fails to find the seed — e.g. a mis-packaged image).
        var loader = Substitute.For<ICommunityResourceLoader>();
        loader.LoadByCategoryAsync(Arg.Any<CommunityResourceCategory>(), Arg.Any<CancellationToken>())
            .Returns<Task<IReadOnlyList<CommunityResource>>>(_ =>
                throw new FileNotFoundException("Simulated missing community_resources.v1.json", "data/seeds/community_resources.v1.json"));

        // The repo is real but irrelevant — the loader throws before any
        // repo call can succeed.
        var repo = EmptyRepo();
        var svc = new RefusalRecoveryService(repo, loader, NullLogger<RefusalRecoveryService>.Instance);

        // Act: OutOfScope triggers community-resource routing; loader throws.
        var detail = await svc.BuildRecoveryAsync(
            "where can I buy a machine",
            RefusalCategory.OutOfScope,
            CancellationToken.None);

        // Assert (a): null — best-effort posture; primary refusal is unaffected.
        Assert.Null(detail);

        // Assert (b): the error counter was incremented with the correct reason tag.
        Assert.Contains(
            samples,
            s => s.Value == 1 && s.Reason == "FileNotFoundException");
    }

    // ──────────────────────────────────────────────────────────────────────
    // 6. OutOfScope → marketplace + machine_reference + manufacturer cards
    //    (PR-R3: CommunityResources routing)
    // ──────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task BuildRecoveryAsync_OutOfScope_Returns_Marketplace_And_MachineReference_Cards()
    {
        // Arrange: OutOfScope should route to marketplace, machine_reference,
        // and manufacturer_pages. Verify CommunityResources is non-null and
        // contains at least one card from each of the marketplace and
        // machine_reference categories.
        var repo = EmptyRepo();
        var loader = LoaderWithResources(
            Marketplace("Alpha Market", "https://alphamarket.example.com"),
            Marketplace("Beta Market", "https://betamarket.example.com"),
            Marketplace("Gamma Market", "https://gammamarket.example.com"),
            MachineRef("IPDB", "https://ipdb.example.com"),
            MachineRef("OPDB", "https://opdb.example.com"),
            ManufacturerPage("Stern Pinball", "https://sternpinball.example.com"));

        var svc = new RefusalRecoveryService(repo, loader, NullLogger<RefusalRecoveryService>.Instance);

        var detail = await svc.BuildRecoveryAsync(
            "buy a godzilla",
            RefusalCategory.OutOfScope,
            CancellationToken.None);

        Assert.NotNull(detail);
        Assert.NotNull(detail!.CommunityResources);

        var categories = detail.CommunityResources!
            .Select(r => r.Category)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        Assert.Contains("marketplace", categories);
        Assert.Contains("machine_reference", categories);
    }

    // ──────────────────────────────────────────────────────────────────────
    // 7. OutOfScope marketplace cards count at least 3
    //    (ADR-0026 § 5 plurality pin — load-bearing test per spec)
    // ──────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task BuildRecoveryAsync_OutOfScope_Marketplace_Cards_Count_At_Least_3()
    {
        // This test pins the ADR-0026 § 5 plurality invariant: when the user
        // gets a refusal for OutOfScope, at least 3 marketplace cards must
        // appear. A count of fewer than 3 means we're implicitly recommending
        // one venue over others — the favoritism failure mode.
        //
        // The loader is populated with exactly 3 marketplace entries (the
        // minimum from the seed) so the test verifies the floor, not a
        // coincidental surplus.
        var repo = EmptyRepo();
        var loader = LoaderWithResources(
            Marketplace("Alpha Market", "https://alphamarket.example.com"),
            Marketplace("Beta Market", "https://betamarket.example.com"),
            Marketplace("Gamma Market", "https://gammamarket.example.com"),
            MachineRef("IPDB", "https://ipdb.example.com"),
            MachineRef("OPDB", "https://opdb.example.com"));

        var svc = new RefusalRecoveryService(repo, loader, NullLogger<RefusalRecoveryService>.Instance);

        var detail = await svc.BuildRecoveryAsync(
            "where can I buy a machine",
            RefusalCategory.OutOfScope,
            CancellationToken.None);

        Assert.NotNull(detail);
        Assert.NotNull(detail!.CommunityResources);

        var marketplaceCount = detail.CommunityResources!
            .Count(r => string.Equals(r.Category, "marketplace", StringComparison.OrdinalIgnoreCase));

        Assert.True(
            marketplaceCount >= 3,
            $"Expected at least 3 marketplace community cards for OutOfScope refusals (ADR-0026 § 5 plurality). Got {marketplaceCount}.");
    }

    // ──────────────────────────────────────────────────────────────────────
    // 8. UpstreamThrottled → null CommunityResources (text-only recovery)
    // ──────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task BuildRecoveryAsync_UpstreamThrottled_Returns_Null_CommunityResources()
    {
        // PR-R4: UpstreamThrottled is a transient infra fault — adding community
        // resource cards would clutter the RefusalPanel with irrelevant
        // alternatives when the only correct action is "wait and try again."
        // The detail is non-null (MissingWhat carries the system-state
        // explanation), but CommunityResources and RelatedMachines remain null.
        var repo = Substitute.For<IMachineRepository>();

        var svc = new RefusalRecoveryService(repo, MinimalLoader(), NullLogger<RefusalRecoveryService>.Instance);

        var detail = await svc.BuildRecoveryAsync(
            "godzilla wizard mode",
            RefusalCategory.UpstreamThrottled,
            CancellationToken.None);

        // Non-null — MissingWhat carries useful context for the user.
        Assert.NotNull(detail);

        // Community resources must be null — not appropriate for a transient
        // rate-limit where the right action is retry, not "browse these links."
        Assert.Null(detail!.CommunityResources);
    }

    // ──────────────────────────────────────────────────────────────────────
    // 9. InsufficientGrounding → forums + machine_reference + news_and_culture
    // ──────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task BuildRecoveryAsync_InsufficientGrounding_Returns_Forums_And_MachineReference_Cards()
    {
        // InsufficientGrounding means retrieval returned chunks but scored too
        // low to ground an answer — route the user to forums + canonical refs
        // + news where they can find the answer from community members.
        var repo = EmptyRepo();
        var loader = LoaderWithResources(
            Forum("Pinside", "https://pinside.example.com"),
            Forum("Tilt Forums", "https://tiltforums.example.com"),
            MachineRef("IPDB", "https://ipdb.example.com"),
            MachineRef("OPDB", "https://opdb.example.com"),
            NewsAndCulture("Pinball News", "https://pinballnews.example.com"),
            // marketplace is NOT in the InsufficientGrounding routing
            Marketplace("Alpha Market", "https://alphamarket.example.com"),
            Marketplace("Beta Market", "https://betamarket.example.com"),
            Marketplace("Gamma Market", "https://gammamarket.example.com"));

        var svc = new RefusalRecoveryService(repo, loader, NullLogger<RefusalRecoveryService>.Instance);

        var detail = await svc.BuildRecoveryAsync(
            "what are the rules for godzilla multiball",
            RefusalCategory.InsufficientGrounding,
            CancellationToken.None);

        Assert.NotNull(detail);
        Assert.NotNull(detail!.CommunityResources);

        var categories = detail.CommunityResources!
            .Select(r => r.Category)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        Assert.Contains("forums", categories);
        Assert.Contains("machine_reference", categories);
    }

    // ──────────────────────────────────────────────────────────────────────
    // PR-R4: Per-category MissingWhat + SuggestedRephrase text strategies
    // ──────────────────────────────────────────────────────────────────────

    // 10. OutOfScope → both text fields populated, non-empty
    [Fact]
    public async Task BuildRecoveryAsync_OutOfScope_Populates_MissingWhat_And_SuggestedRephrase()
    {
        // OutOfScope gets both MissingWhat (topic list) and SuggestedRephrase
        // (actionable narrowing hint). Both must be non-null and non-empty so
        // RefusalPanel can render the full guidance panel.
        var svc = new RefusalRecoveryService(EmptyRepo(), MinimalLoader(), NullLogger<RefusalRecoveryService>.Instance);

        var detail = await svc.BuildRecoveryAsync(
            "what is the weather in chicago",
            RefusalCategory.OutOfScope,
            CancellationToken.None);

        Assert.NotNull(detail);
        Assert.NotNull(detail!.MissingWhat);
        Assert.NotEmpty(detail.MissingWhat!);
        Assert.NotNull(detail.SuggestedRephrase);
        Assert.NotEmpty(detail.SuggestedRephrase!);
    }

    // 11. InsufficientGrounding → both text fields populated, non-empty
    [Fact]
    public async Task BuildRecoveryAsync_InsufficientGrounding_Populates_MissingWhat_And_SuggestedRephrase()
    {
        var svc = new RefusalRecoveryService(EmptyRepo(), MinimalLoader(), NullLogger<RefusalRecoveryService>.Instance);

        var detail = await svc.BuildRecoveryAsync(
            "godzilla multiball rules",
            RefusalCategory.InsufficientGrounding,
            CancellationToken.None);

        Assert.NotNull(detail);
        Assert.NotNull(detail!.MissingWhat);
        Assert.NotEmpty(detail.MissingWhat!);
        Assert.NotNull(detail.SuggestedRephrase);
        Assert.NotEmpty(detail.SuggestedRephrase!);
    }

    // 12. LowModelConfidence → both text fields populated, non-empty
    [Fact]
    public async Task BuildRecoveryAsync_LowModelConfidence_Populates_MissingWhat_And_SuggestedRephrase()
    {
        var svc = new RefusalRecoveryService(EmptyRepo(), MinimalLoader(), NullLogger<RefusalRecoveryService>.Instance);

        var detail = await svc.BuildRecoveryAsync(
            "how does the addams family multiball score",
            RefusalCategory.LowModelConfidence,
            CancellationToken.None);

        Assert.NotNull(detail);
        Assert.NotNull(detail!.MissingWhat);
        Assert.NotEmpty(detail.MissingWhat!);
        Assert.NotNull(detail.SuggestedRephrase);
        Assert.NotEmpty(detail.SuggestedRephrase!);
    }

    // 13. NoCitation → both text fields populated, non-empty
    [Fact]
    public async Task BuildRecoveryAsync_NoCitation_Populates_MissingWhat_And_SuggestedRephrase()
    {
        var svc = new RefusalRecoveryService(EmptyRepo(), MinimalLoader(), NullLogger<RefusalRecoveryService>.Instance);

        var detail = await svc.BuildRecoveryAsync(
            "stern godzilla release date",
            RefusalCategory.NoCitation,
            CancellationToken.None);

        Assert.NotNull(detail);
        Assert.NotNull(detail!.MissingWhat);
        Assert.NotEmpty(detail.MissingWhat!);
        Assert.NotNull(detail.SuggestedRephrase);
        Assert.NotEmpty(detail.SuggestedRephrase!);
    }

    // 14. UpstreamThrottled → MissingWhat populated, SuggestedRephrase null
    [Fact]
    public async Task BuildRecoveryAsync_UpstreamThrottled_Populates_MissingWhat_But_Not_SuggestedRephrase()
    {
        // Rephrase would mislead: the issue is a transient rate-limit, not
        // the question. MissingWhat explains the system state; SuggestedRephrase
        // stays null so the UI does not prompt the user to reword a valid query.
        var repo = Substitute.For<IMachineRepository>();
        var svc = new RefusalRecoveryService(repo, MinimalLoader(), NullLogger<RefusalRecoveryService>.Instance);

        var detail = await svc.BuildRecoveryAsync(
            "godzilla rules",
            RefusalCategory.UpstreamThrottled,
            CancellationToken.None);

        Assert.NotNull(detail);
        Assert.NotNull(detail!.MissingWhat);
        Assert.NotEmpty(detail.MissingWhat!);
        Assert.Null(detail.SuggestedRephrase);
    }

    // 15. CostCeilingHit → both null (operational; user shouldn't act)
    [Fact]
    public async Task BuildRecoveryAsync_CostCeilingHit_Returns_Null_Detail()
    {
        // CostCeilingHit is an infrastructure budget limit. There is no
        // user-actionable guidance and no community routing (that would
        // compound the cost problem). Returning null means RefusalPanel
        // renders with no recovery enrichment — just the refusal text.
        var repo = Substitute.For<IMachineRepository>();
        var svc = new RefusalRecoveryService(repo, MinimalLoader(), NullLogger<RefusalRecoveryService>.Instance);

        var detail = await svc.BuildRecoveryAsync(
            "godzilla rules",
            RefusalCategory.CostCeilingHit,
            CancellationToken.None);

        Assert.Null(detail);

        // Repository must not be touched for a cost-ceiling operational block.
        repo.DidNotReceiveWithAnyArgs().QueryByTitleAsync(default!, default);
    }

    // 16. Brevity contract — each text field is ≤ 2 sentences per category
    [Fact]
    public void BuildRecoveryAsync_text_is_under_2_sentences_per_category()
    {
        // Pins the brevity invariant per the R4 spec. Sentences are counted
        // by splitting on '.' and filtering empty segments — a conservative
        // heuristic that catches the most common violations (run-on paragraphs)
        // without false-positives on abbreviations in the test strings.
        //
        // The exact strings are the internal const strings on RefusalRecoveryService.
        // Using them directly means this test catches a silent edit that lengthens
        // the prose beyond the two-sentence contract.
        var textFields = new[]
        {
            RefusalRecoveryService.MissingWhat_OutOfScope,
            RefusalRecoveryService.SuggestedRephrase_OutOfScope,
            RefusalRecoveryService.MissingWhat_InsufficientGrounding,
            RefusalRecoveryService.SuggestedRephrase_InsufficientGrounding,
            RefusalRecoveryService.MissingWhat_LowModelConfidence,
            RefusalRecoveryService.SuggestedRephrase_LowModelConfidence,
            RefusalRecoveryService.MissingWhat_NoCitation,
            RefusalRecoveryService.SuggestedRephrase_NoCitation,
            RefusalRecoveryService.MissingWhat_UpstreamThrottled,
        };

        foreach (var text in textFields)
        {
            // Count non-empty segments after splitting on '.'
            // (e.g., "Sentence one. Sentence two." → 2 segments)
            var sentenceCount = text
                .Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Count(s => s.Length > 0);

            Assert.True(
                sentenceCount <= 2,
                $"Text exceeds 2-sentence brevity limit ({sentenceCount} sentences detected). " +
                $"Text: \"{text}\"");
        }
    }

    // 17. No-blame guard — text must not use blame-suggesting phrases
    [Fact]
    public void BuildRecoveryAsync_text_does_not_blame_the_user()
    {
        // Silent-edit guard: polite-by-construction posture (ADR-0026 § 5 +
        // feedback_community_resource_posture.md) forbids any phrasing that
        // implies the user is at fault. Scan all per-category const strings for
        // known blame-suggesting phrases.
        //
        // This test fails if a future edit introduces blame phrasing — the
        // failure message names the offending phrase so it's immediately
        // actionable without reading the diff.
        var blamePhrases = new[]
        {
            "you should have",
            "your question is",
            "you didn't",
            "incorrectly",
            "you failed",
            "your fault",
            "wrong question",
        };

        var allTexts = new Dictionary<string, string>
        {
            [nameof(RefusalRecoveryService.MissingWhat_OutOfScope)] = RefusalRecoveryService.MissingWhat_OutOfScope,
            [nameof(RefusalRecoveryService.SuggestedRephrase_OutOfScope)] = RefusalRecoveryService.SuggestedRephrase_OutOfScope,
            [nameof(RefusalRecoveryService.MissingWhat_InsufficientGrounding)] = RefusalRecoveryService.MissingWhat_InsufficientGrounding,
            [nameof(RefusalRecoveryService.SuggestedRephrase_InsufficientGrounding)] = RefusalRecoveryService.SuggestedRephrase_InsufficientGrounding,
            [nameof(RefusalRecoveryService.MissingWhat_LowModelConfidence)] = RefusalRecoveryService.MissingWhat_LowModelConfidence,
            [nameof(RefusalRecoveryService.SuggestedRephrase_LowModelConfidence)] = RefusalRecoveryService.SuggestedRephrase_LowModelConfidence,
            [nameof(RefusalRecoveryService.MissingWhat_NoCitation)] = RefusalRecoveryService.MissingWhat_NoCitation,
            [nameof(RefusalRecoveryService.SuggestedRephrase_NoCitation)] = RefusalRecoveryService.SuggestedRephrase_NoCitation,
            [nameof(RefusalRecoveryService.MissingWhat_UpstreamThrottled)] = RefusalRecoveryService.MissingWhat_UpstreamThrottled,
        };

        foreach (var (fieldName, text) in allTexts)
        {
            foreach (var phrase in blamePhrases)
            {
                Assert.False(
                    text.Contains(phrase, StringComparison.OrdinalIgnoreCase),
                    $"Blame-suggesting phrase \"{phrase}\" found in {fieldName}. " +
                    $"Refusal text must be polite and never blame the user (ADR-0026 § 5).");
            }
        }
    }

    // ──────────────────────────────────────────────────────────────────────
    // Helpers
    // ──────────────────────────────────────────────────────────────────────

    private static Machine NewMachine(string id, string title, string manufacturer) => new()
    {
        Id = id,
        PartitionKey = manufacturer,
        ManufacturerDisplayName = manufacturer,
        Title = title,
        Year = 2020,
        OpdbSourceUrl = $"https://opdb.org/machines/{id}",
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

    // Returns a loader stub that serves the minimum required plurality set
    // (3 marketplace + 2 machine_reference) so tests focused on RelatedMachines
    // do not fail on an empty or invalid loader.
    private static ICommunityResourceLoader MinimalLoader()
    {
        return LoaderWithResources(
            Marketplace("Alpha Market", "https://alphamarket.example.com"),
            Marketplace("Beta Market", "https://betamarket.example.com"),
            Marketplace("Gamma Market", "https://gammamarket.example.com"),
            MachineRef("IPDB", "https://ipdb.example.com"),
            MachineRef("OPDB", "https://opdb.example.com"));
    }

    // Returns a loader stub configured to return the given resources.
    private static ICommunityResourceLoader LoaderWithResources(params CommunityResource[] resources)
    {
        var loader = Substitute.For<ICommunityResourceLoader>();

        loader.LoadAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<CommunityResource>>(resources.ToList().AsReadOnly()));

        // Wire LoadByCategoryAsync to filter from the resources list — mirrors
        // the real CommunityResourceLoader.LoadByCategoryAsync behaviour.
        loader.LoadByCategoryAsync(Arg.Any<CommunityResourceCategory>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var cat = callInfo.Arg<CommunityResourceCategory>();
                var categoryString = CommunityResourceLoader.CategoryToString(cat);
                var filtered = resources
                    .Where(r => string.Equals(r.Category, categoryString, StringComparison.OrdinalIgnoreCase))
                    .OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
                    .ToList()
                    .AsReadOnly();
                return Task.FromResult<IReadOnlyList<CommunityResource>>(filtered);
            });

        return loader;
    }

    private static IMachineRepository EmptyRepo()
    {
        var repo = Substitute.For<IMachineRepository>();
        repo.QueryByTitleAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ToAsyncEnumerable<Machine>());
        return repo;
    }

    private static CommunityResource Marketplace(string name, string url) =>
        new(Name: name, Url: url, Category: "marketplace", Description: null);

    private static CommunityResource MachineRef(string name, string url) =>
        new(Name: name, Url: url, Category: "machine_reference", Description: null);

    private static CommunityResource ManufacturerPage(string name, string url) =>
        new(Name: name, Url: url, Category: "manufacturer_pages", Description: null);

    private static CommunityResource Forum(string name, string url) =>
        new(Name: name, Url: url, Category: "forums", Description: null);

    private static CommunityResource NewsAndCulture(string name, string url) =>
        new(Name: name, Url: url, Category: "news_and_culture", Description: null);
}
