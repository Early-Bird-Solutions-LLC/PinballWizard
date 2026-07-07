# Tilt Forums unscoped-resolution relevance floor — fix for #711

> **For agentic workers:** REQUIRED SUB-SKILL: superpowers:subagent-driven-development. Steps use `- [ ]`.

**Goal:** Stop the unscoped Tilt Forums resolver (Phase 2, #693) from mis-grounding non-per-game subcategory topics to unrelated machines, by requiring a token-subset match confirmation before accepting a machine-index hit, and by excluding the Discourse category "about" topic at discovery.

**Architecture:** Add a normalize+subset confirmation gate inside `TiltForumsGameMatcher.ResolveViaMachineIndexAsync` (applies to the machine-index fuzzy path, scoped and unscoped). Add a title-pattern exclusion in `TiltForumsRulesheetsClient.DiscoverSubcategoryRulesheetsAsync`.

**Tech Stack:** C# / .NET 10, xUnit + NSubstitute.

## Global Constraints

- Personal identity commits; NO Claude attribution trailer.
- Invariant #17: a topic that fails confirmation → NoMatch (skip + log), never mis-grounded.
- Do NOT change the exact-match path or the master-list scoped behavior beyond adding the confirmation gate (all Phase-1 fuzzy resolves must still pass — verified: Pokemon/Jurassic Park/James Bond/Willy Wonka all satisfy the rule).
- Full CI-equivalent suite before push.
- Run from the worktree root: `C:\earlybird\PinballWizard\.worktrees\tf-misground-fix`.

## The confirmation rule (authoritative)

`ConfirmMatch(queryTitle, machineTitle) → bool`:
1. Normalize each to a token set: lowercase; strip diacritics (Unicode decompose, drop combining marks — so `Pokémon`→`pokemon`); split on any non-alphanumeric; drop tokens of length < 2 and a small stopword set (`the, of, and, a, an, for, to, in, on, with, at, `— articles/preps only; keep numerics like `007`).
2. Let `shared = query ∩ machine`. Reject if `shared` is empty.
3. Reject unless the SHORTER set ⊆ the LONGER set (every token of the smaller title appears in the larger). This is the "one title is a clean extension of the other" test.
4. Reject unless ≥1 token in `shared` is **distinctive** — NOT in the generic set `{pinball, game, games}`. (Catches `Junkyard Pinball`→"Pinball" where the only shared token is generic.)
5. Otherwise accept.

Validated against all confirmed #711 cases (accept: Pokemon, Jurassic Park (Stern), James Bond, Rules document for Alien, Willy Wonka and…; reject: Junkyard→Pinball, Points for Extra Ball→Extra Inning, Action Button Master List→Triple Action, List of games…→Beach Games, About the…category→Avengers, RoadShow 2.0→Eros One).

---

### Task 1: Match-confirmation gate in the fuzzy resolver

**Files:**
- Modify: `src/PinballWizard.Infrastructure/Scraping/TiltForums/TiltForumsGameMatcher.cs`
- Test: `tests/PinballWizard.Infrastructure.Tests/Scraping/TiltForums/TiltForumsGameMatcherTests.cs`

**Interfaces:**
- Produces: private `static bool ConfirmTitleMatch(string queryTitle, string machineTitle)` implementing the rule above, and private `static IReadOnlyList<string> NormalizeTitleTokens(string)` (lowercase + diacritic-fold + split + stopword/short drop). Applied in `ResolveViaMachineIndexAsync` immediately after the top-hit `Machine` point-read: if `!ConfirmTitleMatch(gameTitle, topMachine.Title)` → return null (→ caller emits NoMatch). Everything else in the collapse rule unchanged.

- [ ] **Step 1: Write the failing tests.** Add a `[Theory]` on the confirmation via the public resolve path — exact-miss + a fake `IMachineSearchIndex` returning a single hit whose machine (via `GetByOpdbIdAsync`) has the given title; assert Resolved vs NoMatch:

```csharp
[Theory]
// accepts — genuine title relationships
[InlineData("Pokemon", "Pokémon", true)]
[InlineData("Jurassic Park (Stern)", "Jurassic Park", true)]
[InlineData("James Bond", "James Bond 007", true)]
[InlineData("Rules document for Alien", "Alien", true)]
[InlineData("Willy Wonka and the Chocolate Factory", "Willy Wonka & The Chocolate Factory", true)]
// rejects — the #711 mis-grounds
[InlineData("Junkyard Pinball", "Pinball", false)]
[InlineData("Points for Extra Ball", "Extra Inning", false)]
[InlineData("Action Button Master List", "Triple Action / Star Action", false)]
[InlineData("List of games with their current code number", "Beach Games", false)]
[InlineData("About the Wiki Rulesheets category", "The Avengers", false)]
[InlineData("RoadShow 2.0 - Where's my Dozer At?", "Eros One / Flame of Athens", false)]
public async Task ResolveAsync_FuzzyMatch_ConfirmsTitleOverlap(string query, string machineTitle, bool shouldResolve)
{
    var machine = MakeMachine("Gxxx-1", "stern", "Stern Pinball", machineTitle, "Gxxx", 2020);
    var repo = Substitute.For<IMachineRepository>();
    repo.QueryByTitleAsync(query, Arg.Any<CancellationToken>()).Returns(ToAsyncEnumerable(Array.Empty<Machine>()));
    repo.GetByOpdbIdAsync("Gxxx-1", "stern", Arg.Any<CancellationToken>()).Returns(machine);
    repo.GetSiblingsByGroupIdAsync("Gxxx", Arg.Any<CancellationToken>()).Returns(ToAsyncEnumerable([machine]));
    var index = Substitute.For<IMachineSearchIndex>();
    index.SearchAsync(query, Arg.Any<int>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
         .Returns(new[] { new MachineSearchHit("Gxxx-1", machineTitle, "Stern Pinball", "stern", "Gxxx", 2020, 10.0) });

    var result = await TiltForumsGameMatcher.ResolveAsync(repo, index, query, manufacturerHeaderText: null, CancellationToken.None);

    if (shouldResolve)
        Assert.Contains(result.Status, new[] { TiltForumsGameMatchStatus.Resolved, TiltForumsGameMatchStatus.ResolvedEditionFamily });
    else
        Assert.Equal(TiltForumsGameMatchStatus.NoMatchInManufacturerPartition, result.Status);
}
```

- [ ] **Step 2: Run to verify fail** — `dotnet test tests/PinballWizard.Infrastructure.Tests --filter "FullyQualifiedName~TiltForumsGameMatcherTests.ResolveAsync_FuzzyMatch_ConfirmsTitleOverlap" -v minimal` (the reject cases currently resolve → fail).

- [ ] **Step 3: Implement** the two private helpers and the gate call. `NormalizeTitleTokens`:

```csharp
private static readonly HashSet<string> TitleStopWords = new(StringComparer.Ordinal)
{ "the","of","and","a","an","for","to","in","on","with","at" };
private static readonly HashSet<string> GenericTitleTokens = new(StringComparer.Ordinal)
{ "pinball","game","games" };

private static IReadOnlyList<string> NormalizeTitleTokens(string title)
{
    // Diacritic-fold: decompose then drop combining marks (Pokémon → pokemon).
    var decomposed = title.ToLowerInvariant().Normalize(System.Text.NormalizationForm.FormD);
    var sb = new System.Text.StringBuilder(decomposed.Length);
    foreach (var ch in decomposed)
        if (System.Globalization.CharUnicodeInfo.GetUnicodeCategory(ch) != System.Globalization.UnicodeCategory.NonSpacingMark)
            sb.Append(ch);
    var folded = sb.ToString().Normalize(System.Text.NormalizationForm.FormC);

    var tokens = new List<string>();
    foreach (var raw in folded.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
    {
        var t = new string(raw.Where(char.IsLetterOrDigit).ToArray());
        if (t.Length < 2 || TitleStopWords.Contains(t)) continue;
        tokens.Add(t);
    }
    return tokens;
}

// Confirms a fuzzy machine-index hit genuinely corresponds to the query title,
// guarding the unscoped path against single-weak-token mis-grounds (#711).
private static bool ConfirmTitleMatch(string queryTitle, string machineTitle)
{
    var q = NormalizeTitleTokens(queryTitle);
    var m = NormalizeTitleTokens(machineTitle);
    if (q.Count == 0 || m.Count == 0) return false;

    var qSet = new HashSet<string>(q, StringComparer.Ordinal);
    var mSet = new HashSet<string>(m, StringComparer.Ordinal);
    var shared = qSet.Where(mSet.Contains).ToList();
    if (shared.Count == 0) return false;

    // Shorter title's tokens must all appear in the longer (clean extension).
    var (smaller, larger) = qSet.Count <= mSet.Count ? (qSet, mSet) : (mSet, qSet);
    if (!smaller.IsSubsetOf(larger)) return false;

    // At least one shared token must be distinctive (not generic-pinball filler).
    return shared.Any(t => !GenericTitleTokens.Contains(t));
}
```

Gate call in `ResolveViaMachineIndexAsync`, right after `topMachine` is fetched and the stale-null check:

```csharp
if (!ConfirmTitleMatch(gameTitle, topMachine.Title))
{
    logger?.LogInformation(
        "TiltForumsGameMatcher: fuzzy hit '{MachineTitle}' ({OpdbId}) rejected for query '{Query}' — insufficient title overlap; treating as no match.",
        topMachine.Title, topMachine.Id, gameTitle);
    return null;
}
```

- [ ] **Step 4: Run to verify pass** — the theory (11 cases) + all existing matcher tests green.

- [ ] **Step 5: Commit** — `fix(tiltforums) confirm title overlap before accepting a fuzzy machine-index hit (#711)`.

---

### Task 2: Exclude the Discourse category "about" topic at discovery

**Files:**
- Modify: `src/PinballWizard.Infrastructure/Scraping/TiltForums/TiltForumsRulesheetsClient.cs`
- Test: `tests/PinballWizard.Infrastructure.Tests/Scraping/TiltForums/TiltForumsRulesheetsClientTests.cs`

**Interfaces:**
- Produces: `DiscoverSubcategoryRulesheetsAsync` skips any topic whose (raw, pre-normalize) link text matches the Discourse category-definition pattern — case-insensitive `^About the .* category$`. (The Task-1 floor already rejects it by machine mismatch; this avoids the pointless polite fetch + keeps the count honest.)

- [ ] **Step 1: Write the failing test** — a small helper `internal static bool IsCategoryAboutTopic(string linkText)` and a theory:

```csharp
[Theory]
[InlineData("About the Wiki Rulesheets category", true)]
[InlineData("About the Rulesheet Wikis category", true)]
[InlineData("Stranger Things Rulesheet", false)]
[InlineData("Godzilla", false)]
public void IsCategoryAboutTopic_MatchesDiscourseAboutTopics(string t, bool expected)
    => Assert.Equal(expected, TiltForumsRulesheetsClient.IsCategoryAboutTopic(t));
```

- [ ] **Step 2: Run to verify fail** (method absent).

- [ ] **Step 3: Implement** `internal static bool IsCategoryAboutTopic(string linkText) => Regex.IsMatch(linkText.Trim(), @"^About the .+ category$", RegexOptions.IgnoreCase);` and skip such topics in the `DiscoverSubcategoryRulesheetsAsync` loop (before adding to `byUrl`), with a `Logger.LogDebug` noting the skip.

- [ ] **Step 4: Run to verify pass** + client tests green.

- [ ] **Step 5: Commit** — `fix(tiltforums) skip Discourse category 'about' topic in subcategory discovery (#711)`.

---

### Task 3: Full gate + live re-verify (operational)

- [ ] Full CI-equivalent suite green; zero-warning build.
- [ ] Live re-run `--sync-tiltforums-rulesheets` (live-load runbook env). Expected: the #711 meta topics now log as unmatched/skipped (not indexed); `subcategory_indexed` drops to the legit game count; NO grounding to Triple Action / Extra Inning / Beach Games / "Pinball" / Eros One / The Avengers. Read-only probe confirms those machines have 0 Rulesheet chunks.
- [ ] Hand back for `/local-review` + PR-AUDIT gate; do not self-open the PR from a plan step.

---

## Self-Review

- Spec coverage: relevance floor → Task 1 (11-case theory covers every confirmed accept/reject); category-about exclusion → Task 2; live re-verify → Task 3.
- Placeholders: none.
- Type consistency: `ConfirmTitleMatch`/`NormalizeTitleTokens` private to the matcher; `IsCategoryAboutTopic` internal on the client (test project sees internals). `MachineSearchHit` ctor arg order matches Phase 1: `(OpdbId, Title, ManufacturerDisplayName, ManufacturerKey, GroupId, Year, Score)`.
- Risk: the floor also applies to the scoped (master-list) fuzzy path; all Phase-1 fuzzy resolves satisfy the rule (verified on paper), so no master-list regression — Task 1's accept-cases include the master-list examples.
