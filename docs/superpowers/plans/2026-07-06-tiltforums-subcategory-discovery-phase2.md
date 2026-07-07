# Tilt Forums Subcategory Discovery — Phase 2 (#693) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.
>
> **Prerequisite:** PR #703 (Phase 1, forgiving resolver) MUST be merged first — this plan reuses `TiltForumsGameMatcher`'s machine-index resolution UNSCOPED. Branch this off the post-merge `origin/main`.

**Goal:** Ingest the ~83 Tilt Forums rulesheets that exist only in the "Wiki Rulesheets" subcategory (not on the master list) — including Stranger Things — by unioning subcategory topics into the ingestion set and resolving them without a manufacturer hint.

**Architecture:** Subcategory discovery starts returning `(topicUrl, gameTitle)` pairs (the link text, de-suffixed). `TiltForumsGameMatcher` gains an unscoped resolution mode (null manufacturer hint) that reuses the Phase 1 machine-index collapse rule cross-partition, deriving the manufacturer from the resolved machine. The `--sync-tiltforums-rulesheets` verb ingests master-list listings first (manufacturer-scoped, unchanged), then the subcategory-only topics (unscoped).

**Tech Stack:** C# / .NET 10, AngleSharp (HTML parse), xUnit + NSubstitute.

## Global Constraints

- Personal identity commits only: `Jim Keeley <94459922+jkeeley2073@users.noreply.github.com>`; **no** Claude attribution trailer.
- Invariant #17 — a topic that cannot be confidently resolved (no match, or ambiguous across manufacturers) is logged + skipped, never mis-grounded.
- Manufacturer scoping for MASTER-LIST listings is unchanged (Phase 1 behavior preserved) — only subcategory-only topics use the unscoped path.
- Polite-by-construction: all fetches stay on `PoliteScraperBase` (`GetStringPolitelyAsync`) — no new bare HTTP.
- Full CI-equivalent suite before push: `dotnet test PinballWizard.slnx --filter "Category!=Accessibility&Category!=Snapshots&Category!=Circuit&Category!=E2E"`.

---

## File Structure

- `src/PinballWizard.Infrastructure/Scraping/TiltForums/TiltForumsRulesheetListing.cs` — allow a null/absent `ManufacturerHeaderText` (subcategory topics have none).
- `src/PinballWizard.Infrastructure/Scraping/TiltForums/TiltForumsRulesheetsClient.cs` — subcategory discovery returns titled listings; add a game-title de-suffix helper.
- `src/PinballWizard.Infrastructure/Scraping/TiltForums/TiltForumsGameMatcher.cs` — unscoped resolution when the manufacturer hint is absent.
- `src/PinballWizard.Cli/Program.cs` — union subcategory-only topics into ingestion; new `subcategory_indexed` counter.
- Tests: `TiltForumsRulesheetsClientTests.cs`, `TiltForumsGameMatcherTests.cs`.

---

### Task 1: Subcategory discovery yields titled listings

**Files:**
- Modify: `TiltForumsRulesheetListing.cs`
- Modify: `TiltForumsRulesheetsClient.cs`
- Test: `tests/PinballWizard.Infrastructure.Tests/Scraping/TiltForums/TiltForumsRulesheetsClientTests.cs`

**Interfaces:**
- Produces: `Task<IReadOnlyList<TiltForumsRulesheetListing>> TiltForumsRulesheetsClient.DiscoverSubcategoryRulesheetsAsync(CancellationToken)` — each listing has `TopicUrl` set, `GameTitle` = de-suffixed link text, `ManufacturerHeaderText` = `null`. Replaces the URL-only `DiscoverSubcategoryTopicUrlsAsync` (callers updated).
- `TiltForumsRulesheetListing.ManufacturerHeaderText` becomes `string?` (nullable).
- Internal helper: `static string NormalizeSubcategoryTitle(string linkText)` — strips a trailing " Rulesheet" / " Wiki" / " Rulesheet Wiki" token(s), case-insensitive, so "Stranger Things Rulesheet" → "Stranger Things".

- [ ] **Step 1: Write the failing tests**

Add to `TiltForumsRulesheetsClientTests` (mirror the existing master-list discovery test's fixture-HTML approach):

```csharp
[Theory]
[InlineData("Stranger Things Rulesheet", "Stranger Things")]
[InlineData("Godzilla Rulesheet Wiki", "Godzilla")]
[InlineData("Elvira's House of Horrors", "Elvira's House of Horrors")] // no suffix → unchanged
public void NormalizeSubcategoryTitle_StripsRulesheetWikiSuffix(string input, string expected)
    => Assert.Equal(expected, TiltForumsRulesheetsClient.NormalizeSubcategoryTitle(input));
```

- [ ] **Step 2: Run to verify fail** — `dotnet test tests/PinballWizard.Infrastructure.Tests --filter "FullyQualifiedName~TiltForumsRulesheetsClientTests.NormalizeSubcategoryTitle" -v minimal` → FAIL (method absent).

- [ ] **Step 3: Implement**

In `TiltForumsRulesheetListing.cs`, change:

```csharp
/// <summary>Manufacturer section header from the master list, or null for subcategory-only topics.</summary>
public string? ManufacturerHeaderText { get; init; }
```

In `TiltForumsRulesheetsClient.cs`, add the de-suffix helper and rewrite subcategory discovery to capture link text:

```csharp
// Subcategory topic titles carry a trailing "Rulesheet"/"Wiki" word the
// clean master-list game titles lack. Strip it so title-resolution sees
// the bare game name.
internal static string NormalizeSubcategoryTitle(string linkText)
{
    var t = linkText.Trim();
    // Repeatedly strip a trailing " Wiki" or " Rulesheet" token (handles
    // "Rulesheet Wiki"). Case-insensitive; whole-word only.
    while (true)
    {
        var trimmed = Regex.Replace(t, @"\s+(Rulesheet|Wiki)$", "", RegexOptions.IgnoreCase);
        if (trimmed == t) break;
        t = trimmed.Trim();
    }
    return t;
}

public async Task<IReadOnlyList<TiltForumsRulesheetListing>> DiscoverSubcategoryRulesheetsAsync(CancellationToken cancellationToken)
{
    var byUrl = new Dictionary<string, TiltForumsRulesheetListing>(StringComparer.OrdinalIgnoreCase);
    var page = 0;
    while (true)
    {
        var pageUrl = page == 0
            ? new Uri($"{BaseUrl}{SubcategoryPath}")
            : new Uri($"{BaseUrl}{SubcategoryPath}?page={page}");
        string html;
        try { html = await GetStringPolitelyAsync(_http, pageUrl, cancellationToken).ConfigureAwait(false); }
        catch (HttpRequestException ex)
        {
            if (ex.StatusCode == HttpStatusCode.NotFound)
                Logger.LogDebug("TiltForumsRulesheetsClient: subcategory page {Page} 404; pagination exhausted.", page);
            else
                Logger.LogWarning(ex, "TiltForumsRulesheetsClient: subcategory page {Page} fetch failed ({StatusCode}); stopping with {Collected} collected.", page, ex.StatusCode, byUrl.Count);
            break;
        }

        using var ctx = BrowsingContext.New(Configuration.Default);
        var parser = ctx.GetService<IHtmlParser>()!;
        using var document = await parser.ParseDocumentAsync(html, cancellationToken).ConfigureAwait(false);

        var newCount = 0;
        foreach (var link in document.QuerySelectorAll("a.raw-topic-link[href]"))
        {
            var href = link.GetAttribute("href");
            var text = link.TextContent.Trim();
            if (string.IsNullOrWhiteSpace(href) || string.IsNullOrWhiteSpace(text)) continue;
            if (!byUrl.ContainsKey(href))
            {
                byUrl[href] = new TiltForumsRulesheetListing
                {
                    GameTitle = NormalizeSubcategoryTitle(text),
                    ManufacturerHeaderText = null,
                    TopicUrl = href,
                };
                newCount++;
            }
        }
        Logger.LogDebug("TiltForumsRulesheetsClient: subcategory page {Page} yielded {New} new topic(s) (total {Total}).", page, newCount, byUrl.Count);
        if (newCount == 0) break;
        page++;
    }
    Logger.LogInformation("TiltForumsRulesheetsClient: subcategory listing yielded {Count} topic(s).", byUrl.Count);
    return [.. byUrl.Values];
}
```

Delete the old `DiscoverSubcategoryTopicUrlsAsync` (Task 3 updates its only caller in `Program.cs`).

- [ ] **Step 4: Run to verify pass** — the theory test green.

- [ ] **Step 5: Commit** — `feat(tiltforums) subcategory discovery yields titled listings (#693)`.

---

### Task 2: Unscoped resolution in TiltForumsGameMatcher

**Files:**
- Modify: `TiltForumsGameMatcher.cs`
- Test: `TiltForumsGameMatcherTests.cs`

**Interfaces:**
- Consumes: Phase 1 `ResolveViaMachineIndexAsync` (now called with `manufacturerKey: null` for the unscoped path); `IMachineRepository.QueryByTitleAsync`; `EditionFamily.IsEditionFamily`.
- Produces: `ResolveAsync` accepts a null/whitespace `manufacturerHeaderText`. When null → **unscoped**: the exact path keeps ALL cross-partition title matches (no partition filter); the fuzzy path calls the index with `manufacturerKey: null`. Same `Resolved / ResolvedEditionFamily / MultipleMatches (→ ambiguous) / NoMatch` outcomes. The resolved `TiltForumsMachineMatch` carries the machine's own `ManufacturerDisplayName` (already the case).

- [ ] **Step 1: Write the failing tests** (add to `TiltForumsGameMatcherTests`)

```csharp
[Fact]
public async Task ResolveAsync_NullManufacturer_SingleCrossPartitionMatch_Resolves()
{
    // Subcategory topic, no manufacturer hint. "Stranger Things" exists only
    // in the Stern partition → resolves unscoped; manufacturer derived from machine.
    var st = MakeMachine("Gzy89-M0oPy", "stern", "Stern Pinball", "Stranger Things", "Gzy89", 2019);
    var repo = Substitute.For<IMachineRepository>();
    repo.QueryByTitleAsync("Stranger Things", Arg.Any<CancellationToken>())
        .Returns(ToAsyncEnumerable([st]));
    repo.GetSiblingsByGroupIdAsync("Gzy89", Arg.Any<CancellationToken>())
        .Returns(ToAsyncEnumerable([st]));

    var result = await TiltForumsGameMatcher.ResolveAsync(
        repo, machineSearchIndex: null, "Stranger Things", manufacturerHeaderText: null, CancellationToken.None);

    Assert.Equal(TiltForumsGameMatchStatus.Resolved, result.Status);
    Assert.Equal("Stern Pinball", result.Machines[0].ManufacturerDisplayName);
}

[Fact]
public async Task ResolveAsync_NullManufacturer_MultiManufacturerCollision_IsAmbiguous()
{
    // "Star Wars" exists for Bally AND Stern (different partitions). Unscoped
    // with no hint → genuinely ambiguous → skip, never guess.
    var bally = MakeMachine("Gb-1", "bally", "Bally", "Star Wars", "Gb", 1992);
    var stern = MakeMachine("Gs-1", "stern", "Stern Pinball", "Star Wars", "Gs", 2017);
    var repo = Substitute.For<IMachineRepository>();
    repo.QueryByTitleAsync("Star Wars", Arg.Any<CancellationToken>())
        .Returns(ToAsyncEnumerable([bally, stern]));

    var result = await TiltForumsGameMatcher.ResolveAsync(
        repo, machineSearchIndex: null, "Star Wars", manufacturerHeaderText: null, CancellationToken.None);

    Assert.Equal(TiltForumsGameMatchStatus.MultipleMatchesInManufacturerPartition, result.Status);
    Assert.Empty(result.Machines);
}
```

- [ ] **Step 2: Run to verify fail** — compile error (`manufacturerHeaderText` currently `ArgumentException.ThrowIfNullOrWhiteSpace`).

- [ ] **Step 3: Implement** — make the manufacturer hint optional and branch scoping on it:

```csharp
public static async Task<TiltForumsGameMatchResult> ResolveAsync(
    IMachineRepository machineRepository,
    IMachineSearchIndex? machineSearchIndex,
    string gameTitle,
    string? manufacturerHeaderText,
    CancellationToken cancellationToken,
    ILogger? logger = null)
{
    ArgumentNullException.ThrowIfNull(machineRepository);
    ArgumentException.ThrowIfNullOrWhiteSpace(gameTitle);

    // null/whitespace manufacturer hint = subcategory topic → unscoped resolution.
    var manufacturerKey = string.IsNullOrWhiteSpace(manufacturerHeaderText)
        ? null
        : OpdbMachineMapper.NormalizeManufacturerKey(manufacturerHeaderText);

    var matches = new List<Machine>();
    await foreach (var machine in machineRepository.QueryByTitleAsync(gameTitle, cancellationToken))
    {
        // Scoped: keep only the hinted partition. Unscoped (key null): keep all.
        if (manufacturerKey is null
            || string.Equals(machine.PartitionKey, manufacturerKey, StringComparison.OrdinalIgnoreCase))
        {
            matches.Add(machine);
        }
    }

    if (matches.Count == 1)
        return new TiltForumsGameMatchResult(TiltForumsGameMatchStatus.Resolved, [ToMatch(matches[0])]);

    if (matches.Count > 1)
    {
        if (EditionFamily.IsEditionFamily(matches))
        {
            var siblings = await CollectSiblingsAsync(machineRepository, matches[0].GroupId!, cancellationToken);
            return new TiltForumsGameMatchResult(TiltForumsGameMatchStatus.ResolvedEditionFamily, siblings);
        }
        return new TiltForumsGameMatchResult(TiltForumsGameMatchStatus.MultipleMatchesInManufacturerPartition, []);
    }

    // Exact miss — forgiving index path (scoped or unscoped per manufacturerKey).
    if (machineSearchIndex is not null)
    {
        var fuzzy = await ResolveViaMachineIndexAsync(
            machineRepository, machineSearchIndex, gameTitle, manufacturerKey, cancellationToken, logger);
        if (fuzzy is not null) return fuzzy;
    }

    return new TiltForumsGameMatchResult(TiltForumsGameMatchStatus.NoMatchInManufacturerPartition, []);
}
```

In `ResolveViaMachineIndexAsync`, change its `manufacturerKey` parameter to `string?` and pass it straight to `SearchAsync` (null ⇒ unscoped, already supported by Phase 1's `IMachineSearchIndex` signature). No other change — the collapse rule is identical. Note: `IsEditionFamily(matches)` where matches span multiple partitions returns false (different GroupIds), so a cross-manufacturer collision correctly falls to `MultipleMatches` — verified by the second test.

- [ ] **Step 4: Run to verify pass** — matcher suite green (Phase 1 tests unchanged; 2 new pass).

- [ ] **Step 5: Commit** — `feat(tiltforums) unscoped resolution for hint-less subcategory topics (#693)`.

---

### Task 3: Ingest subcategory-only topics in the sync verb

**Files:**
- Modify: `src/PinballWizard.Cli/Program.cs` (the `--sync-tiltforums-rulesheets` handler)

**Interfaces:**
- Consumes: `DiscoverSubcategoryRulesheetsAsync` (Task 1); `ResolveAsync(..., manufacturerHeaderText: null, ...)` (Task 2).

- [ ] **Step 1: Replace the gap-report block with gap-ingestion**

The handler currently calls `DiscoverSubcategoryTopicUrlsAsync`, computes `tiltForumsGaps` (subcategory URLs not in the master list), and only logs them. Replace with: discover subcategory listings, compute the set whose `TopicUrl` is not already covered by a master-list listing, and feed those through the SAME per-listing loop body (resolve → fetch → synthesize → index) that master-list listings use. Master-list listings run first (manufacturer-scoped, unchanged).

Concretely: build `var allListings = masterListings.Concat(subcategoryOnlyListings).ToList();` where `subcategoryOnlyListings` excludes any `TopicUrl` already in `masterListings`, and iterate `allListings` in the existing `foreach`. Since `ResolveAsync` now accepts a null `ManufacturerHeaderText`, the loop body needs no per-item branching — a master-list listing passes its manufacturer, a subcategory listing passes null.

- [ ] **Step 2: Add a subcategory counter to the run summary**

```csharp
var tiltForumsSubcategoryIndexed = 0;
```

Increment it in the resolved+indexed branch when `string.IsNullOrWhiteSpace(listing.ManufacturerHeaderText)` (i.e. a subcategory-only topic indexed). Add `subcategory_indexed={tiltForumsSubcategoryIndexed}` to the completion log line (alongside the Phase-1 `fuzzy_resolved` and #701 `raw_doc_write_failed`).

- [ ] **Step 3: Build** — `dotnet build src/PinballWizard.Cli --nologo -warnaserror` → succeeds.

- [ ] **Step 4: Full suite** — `dotnet test PinballWizard.slnx --filter "Category!=Accessibility&Category!=Snapshots&Category!=Circuit&Category!=E2E"` → green.

- [ ] **Step 5: Commit** — `feat(cli) ingest subcategory-only tiltforums rulesheets (#693)`.

---

### Task 4: Live verification (operational, before PR)

- [ ] Re-run `--sync-tiltforums-rulesheets` against live (live-load runbook env). Expected: `subcategory_indexed` > 0; the run's "not in the master list" gap report shrinks toward zero; Stranger Things logs `Indexed … -> machine Gzy89-…`.
- [ ] Read-only probe: `pinwiz-rag-v1` for `machine_title` `Stranger Things` filtered `document_type eq 'Rulesheet'` → count > 0.
- [ ] Re-ask the Wizard "tournament strategy for Stranger Things" → expect a `tiltforums.com` citation.
- [ ] Hand back for the `/local-review` + PR-AUDIT gate; do not self-open the PR.

---

## Self-Review

- **Spec coverage:** subcategory union (spec Phase 2) → Tasks 1+3; unscoped manufacturer-derivation → Task 2; ambiguous-across-manufacturers skip (invariant #17) → Task 2 test 2; Stranger Things acceptance → Task 4.
- **Placeholder scan:** none.
- **Type consistency:** `DiscoverSubcategoryRulesheetsAsync` returns `IReadOnlyList<TiltForumsRulesheetListing>`; `ManufacturerHeaderText` is `string?`; `ResolveAsync`'s hint is `string?`; used consistently across Tasks 1-3.
- **Risk noted:** widening `QueryByTitleAsync` results to all partitions on the unscoped path increases multi-match/ambiguous cases; that is the intended safe outcome (skip-and-log), not a regression. Titles resolving to a single catalog entry (the common case, incl. Stranger Things) are unaffected.
