# Admin Sources — Status Clarity Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make `/admin/sources` explain *why* a source is off — replacing a binary Enabled/Disabled chip with a status that distinguishes Active / No source / Deferred / Disabled, grouped by manufacturer, with the reason shown inline.

**Architecture:** The reason data already exists in the seed JSON but is dropped at seed time. We persist four new fields (`SourceGroup`, `DiscoveryStatus`, `DiscoveryNotes`, `DiscoveryDate`) from seed → domain entity → Cosmos, then surface them in the grid: group rows by manufacturer, derive a four-state status chip from `(Enabled, DiscoveryStatus)`, and render the notes + assessed date inline for any non-Active row.

**Tech Stack:** .NET 10, Blazor (`@rendermode InteractiveServer`), MudBlazor (`MudDataGrid` grouping), xUnit + NSubstitute + bUnit, System.Text.Json.

## Global Constraints

- **Personal identity only:** commits author as `Jim Keeley <94459922+jkeeley2073@users.noreply.github.com>`. No Claude attribution trailer.
- **Branch:** `feat/admin-sources-status-clarity`, based off `main`. The page is already `@rendermode InteractiveServer` (merged in #629, commit `1bbec63`) — do not revert it; this plan consumes it.
- **No `///` XML doc comments** on new members (repo preference `feedback_no_xml_docs`) — use plain `//` comments even though the existing file has legacy XML docs.
- **Colour is never the sole carrier of meaning** (WCAG 2.1 AA): every status renders an icon + text label, not just a colour.
- **Fallbacks degrade visibly** (Invariant #17): do not remove the existing load-failure `AppErrorAlert` path.
- **Read-safety:** the domain entity's `SourceGroup` is non-`required` (default `""`) so reads of not-yet-reseeded Cosmos docs never throw; only the seed DTO makes it `required`.
- **CI-equivalent suite before push:** `dotnet test PinballWizard.slnx --filter "Category!=Accessibility&Category!=Snapshots&Category!=Circuit&Category!=E2E"`.

---

### Task 1: Seed data — add fields, fix em-dashes, strip suffixes

**Files:**
- Modify: `data/seeds/ingestion_sources.v1.json` (full rewrite)
- Test: `tests/PinballWizard.Application.Tests/Sync/IngestionSourceSeederTests.cs` (add one raw-JSON test)

**Interfaces:**
- Consumes: nothing.
- Produces: a manifest where every entry has `sourceGroup` (non-empty) and `discoveryStatus` (`Active`/`NoSource`/`Deferred`); the four bulletin sub-feeds carry `discoveryNotes` + `discoveryDate`; display names carry no `(NoSource)`/`(Deferred)` suffix and no `â€”` mojibake. These property names are what Task 2's DTO binds.

Do JSON first: the new properties are tolerated as unknown fields by today's DTO, so this task is green on its own, and Task 2 can make `SourceGroup` `required` knowing the manifest already supplies it.

- [ ] **Step 1: Write the failing test**

Add to `IngestionSourceSeederTests` (raw-JSON, no DTO dependency so it passes before Task 2):

```csharp
[Fact]
public void ProductionManifest_EveryEntryHasSourceGroupAndDiscoveryStatus()
{
    var repoRoot = FindRepoRoot();
    var manifestPath = Path.Combine(repoRoot, "data", "seeds", "ingestion_sources.v1.json");
    using var doc = JsonDocument.Parse(File.ReadAllText(manifestPath));

    foreach (var entry in doc.RootElement.EnumerateArray())
    {
        var id = entry.GetProperty("id").GetString();

        Assert.True(entry.TryGetProperty("sourceGroup", out var group)
            && !string.IsNullOrWhiteSpace(group.GetString()),
            $"Entry '{id}' is missing a non-empty sourceGroup.");

        Assert.True(entry.TryGetProperty("discoveryStatus", out var status)
            && status.GetString() is "Active" or "NoSource" or "Deferred",
            $"Entry '{id}' has an invalid or missing discoveryStatus.");

        // No display-name mojibake or leftover status suffixes.
        var name = entry.GetProperty("displayName").GetString()!;
        Assert.DoesNotContain("â€", name, StringComparison.Ordinal); // corrupted em-dash bytes
        Assert.DoesNotContain("(NoSource)", name, StringComparison.Ordinal);
        Assert.DoesNotContain("(Deferred)", name, StringComparison.Ordinal);
    }

    // The four disabled sub-feeds must carry an explanation.
    var disabledWithReason = doc.RootElement.EnumerateArray()
        .Where(e => e.GetProperty("discoveryStatus").GetString() is "NoSource" or "Deferred")
        .ToList();
    Assert.Equal(4, disabledWithReason.Count);
    Assert.All(disabledWithReason, e =>
        Assert.False(string.IsNullOrWhiteSpace(e.GetProperty("discoveryNotes").GetString())));
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/PinballWizard.Application.Tests --filter "FullyQualifiedName~ProductionManifest_EveryEntryHasSourceGroupAndDiscoveryStatus"`
Expected: FAIL (current manifest has no `sourceGroup`, has `â€”` mojibake and `(NoSource)` suffixes).

- [ ] **Step 3: Rewrite the manifest**

Replace the entire contents of `data/seeds/ingestion_sources.v1.json` with (literal `—` em-dashes, cleaned notes, added fields):

```json
[
  {
    "id": "stern",
    "displayName": "Stern Pinball",
    "scraperImplKey": "stern",
    "baseUrl": "https://sternpinball.com/",
    "enabled": true,
    "cadence": "daily",
    "politenessOverrides": null,
    "sourceGroup": "Stern Pinball",
    "discoveryStatus": "Active"
  },
  {
    "id": "jjp",
    "displayName": "Jersey Jack Pinball",
    "scraperImplKey": "jjp",
    "baseUrl": "https://www.jerseyjackpinball.com/",
    "enabled": true,
    "cadence": "daily",
    "politenessOverrides": null,
    "sourceGroup": "Jersey Jack Pinball",
    "discoveryStatus": "Active"
  },
  {
    "id": "jjp_support",
    "displayName": "Per-Edition Support Docs",
    "scraperImplKey": "jjp_support",
    "baseUrl": "https://www.jerseyjackpinball.com/",
    "enabled": true,
    "cadence": "weekly",
    "politenessOverrides": null,
    "sourceGroup": "Jersey Jack Pinball",
    "discoveryStatus": "Active"
  },
  {
    "id": "ap",
    "displayName": "American Pinball",
    "scraperImplKey": "ap",
    "baseUrl": "https://www.american-pinball.com/",
    "enabled": true,
    "cadence": "daily",
    "politenessOverrides": null,
    "sourceGroup": "American Pinball",
    "discoveryStatus": "Active"
  },
  {
    "id": "spooky",
    "displayName": "Spooky Pinball",
    "scraperImplKey": "spooky",
    "baseUrl": "https://www.spookypinball.com/",
    "enabled": true,
    "cadence": "weekly",
    "politenessOverrides": null,
    "sourceGroup": "Spooky Pinball",
    "discoveryStatus": "Active"
  },
  {
    "id": "spooky_support",
    "displayName": "Support",
    "scraperImplKey": "spooky_support",
    "baseUrl": "https://www.spookypinball.com/",
    "enabled": true,
    "cadence": "weekly",
    "politenessOverrides": null,
    "sourceGroup": "Spooky Pinball",
    "discoveryStatus": "Active"
  },
  {
    "id": "pinballbrothers",
    "displayName": "Pinball Brothers",
    "scraperImplKey": "pinballbrothers",
    "baseUrl": "https://pinballbrothers.com/",
    "enabled": true,
    "cadence": "weekly",
    "politenessOverrides": null,
    "sourceGroup": "Pinball Brothers",
    "discoveryStatus": "Active"
  },
  {
    "id": "pb_docs",
    "displayName": "Per-Game Documents",
    "scraperImplKey": "pb_docs",
    "baseUrl": "https://pinballbrothers.com/",
    "enabled": true,
    "cadence": "weekly",
    "politenessOverrides": null,
    "sourceGroup": "Pinball Brothers",
    "discoveryStatus": "Active",
    "discoveryNotes": "Pinball Brothers game pages embed per-game PDF rulesheets in nectar_btn shortcode url= attributes within content.rendered. ABBA Pinball has ABBA_Quick_Rule_Sheet.pdf (confirmed rulesheet; link text 'Rulesheet', URL path /games/abba/documents/). robots.txt: Disallow /wp-admin/ only; no Crawl-delay; no restrictions on /games/ or /wp-json/. PbGamePageDocumentScraper extracts from WP REST pages?_fields=…,content. Predator/Queen/Alien pages have no documents as of 2026-06-25.",
    "discoveryDate": "2026-06-25"
  },
  {
    "id": "barrelsoffun",
    "displayName": "Barrels of Fun",
    "scraperImplKey": "barrelsoffun",
    "baseUrl": "https://shop.kollectfun.com/",
    "enabled": true,
    "cadence": "monthly",
    "politenessOverrides": null,
    "sourceGroup": "Barrels of Fun",
    "discoveryStatus": "Active"
  },
  {
    "id": "multimorphic",
    "displayName": "Multimorphic",
    "scraperImplKey": "multimorphic",
    "baseUrl": "https://www.multimorphic.com/",
    "enabled": true,
    "cadence": "weekly",
    "politenessOverrides": null,
    "sourceGroup": "Multimorphic",
    "discoveryStatus": "Active"
  },
  {
    "id": "cgc",
    "displayName": "Chicago Gaming Company",
    "scraperImplKey": "cgc",
    "baseUrl": "https://chicago-gaming.com/",
    "enabled": true,
    "cadence": "weekly",
    "politenessOverrides": null,
    "sourceGroup": "Chicago Gaming Company",
    "discoveryStatus": "Active"
  },
  {
    "id": "opdb",
    "displayName": "Open Pinball Database (OPDB)",
    "scraperImplKey": "opdb",
    "baseUrl": "https://opdb.org/api/",
    "enabled": true,
    "cadence": "daily",
    "politenessOverrides": {
      "requestDelayMs": 10000
    },
    "sourceGroup": "Open Pinball Database (OPDB)",
    "discoveryStatus": "Active"
  },
  {
    "id": "pinballmap",
    "displayName": "Pinball Map (pinballmap.com)",
    "scraperImplKey": "pinballmap",
    "baseUrl": "https://pinballmap.com/api/v1/",
    "enabled": true,
    "cadence": "daily",
    "politenessOverrides": {
      "requestDelayMs": 5000
    },
    "sourceGroup": "Pinball Map",
    "discoveryStatus": "Active"
  },
  {
    "id": "jjp_bulletins",
    "displayName": "Service Bulletins",
    "scraperImplKey": "jjp_bulletins",
    "baseUrl": "https://www.jerseyjackpinball.com/",
    "enabled": false,
    "cadence": "none",
    "politenessOverrides": null,
    "sourceGroup": "Jersey Jack Pinball",
    "discoveryStatus": "NoSource",
    "discoveryNotes": "No service bulletin section on jerseyjackpinball.com. Per-game support pages at /pages/support/{slug} contain manuals, code updates, ISOs, and rules — but no bulletin-class documents. robots.txt (Shopify) has no path restrictions on support content.",
    "discoveryDate": "2026-05-26"
  },
  {
    "id": "ap_bulletins",
    "displayName": "Service Bulletins",
    "scraperImplKey": "ap_bulletins",
    "baseUrl": "https://www.american-pinball.com/",
    "enabled": true,
    "cadence": "weekly",
    "politenessOverrides": null,
    "sourceGroup": "American Pinball",
    "discoveryStatus": "Active",
    "discoveryNotes": "Service bulletins published at american-pinball.com/support/ — single static HTML page with anchor-tab layout per game (Houdini, Oktoberfest, Hot Wheels, Galactic Tank Force, Legends of Valhalla, Barry O's BBQ Challenge). Bulletin PDFs hosted on CDN subdomain s4.american-pinball.com under /img/support/YYYY-M/{filename}.pdf. No robots.txt restrictions found. ApBulletinScraper wired.",
    "discoveryDate": "2026-05-26"
  },
  {
    "id": "spooky_bulletins",
    "displayName": "Service Bulletins",
    "scraperImplKey": "spooky_bulletins",
    "baseUrl": "https://www.spookypinball.com/",
    "enabled": false,
    "cadence": "none",
    "politenessOverrides": null,
    "sourceGroup": "Spooky Pinball",
    "discoveryStatus": "NoSource",
    "discoveryNotes": "No service bulletin document type on spookypinball.com. /game-support/ hub has per-game pages with manuals, switch/coil charts, rules PDFs, and code update packages — but no distinct bulletin/advisory category. robots.txt specifies 10-second crawl-delay; no path restrictions on support content.",
    "discoveryDate": "2026-05-26"
  },
  {
    "id": "cgc_bulletins",
    "displayName": "Service Bulletins",
    "scraperImplKey": "cgc_bulletins",
    "baseUrl": "https://chicago-gaming.com/",
    "enabled": false,
    "cadence": "none",
    "politenessOverrides": null,
    "sourceGroup": "Chicago Gaming Company",
    "discoveryStatus": "NoSource",
    "discoveryNotes": "chicago-gaming.com/product/bulletins/ exists but contains only arcade product bulletins (Arcade Legends, Golden Tee). Pinball titles (Attack From Mars, Medieval Madness, Monster Bash, Pulp Fiction, Cactus Canyon) are not covered. Pinball support routed via Freshdesk; /coinop/{slug}/update pages list code updates only.",
    "discoveryDate": "2026-05-26"
  },
  {
    "id": "pb_bulletins",
    "displayName": "Service Bulletins",
    "scraperImplKey": "pb_bulletins",
    "baseUrl": "https://pinballbrothers.freshdesk.com/",
    "enabled": false,
    "cadence": "none",
    "politenessOverrides": null,
    "sourceGroup": "Pinball Brothers",
    "discoveryStatus": "Deferred",
    "discoveryNotes": "Service bulletins exist at pinballbrothers.freshdesk.com/support/solutions (General > Service Bulletins folder, 4 confirmed notices). Freshdesk REST API available at {subdomain}.freshdesk.com/api/v2/solutions/folders/{id}/articles returns JSON but requires API key even for public portals. Deferred pending API key acquisition.",
    "discoveryDate": "2026-05-26"
  },
  {
    "id": "kineticist_tutorials",
    "displayName": "Pinball Tutorials",
    "scraperImplKey": "kineticist_tutorials",
    "baseUrl": "https://www.kineticist.com/",
    "enabled": true,
    "cadence": "weekly",
    "politenessOverrides": null,
    "sourceGroup": "Kineticist",
    "discoveryStatus": "Active",
    "discoveryNotes": "Kineticist founder Colin Alsheimer granted explicit written permission (ADR-0043 / PR #520) to index published gameplay tutorials. Appending .md to any article URL returns clean Markdown with title, author, date, category, and canonical URL inline. Category listing at /news/category/pinball-tutorial. robots.txt ai-train=yes, ai-input=yes; /news/ allowed for all crawlers including ClaudeBot. ~50 tutorials across 2 pages as of 2026-06-25. Ingested via --sync-kineticist-tutorials CLI verb (synthesis path, not change-feed).",
    "discoveryDate": "2026-06-25"
  },
  {
    "id": "twip",
    "displayName": "This Week in Pinball (TWIP)",
    "scraperImplKey": "twip",
    "baseUrl": "https://twip.kineticist.com",
    "enabled": true,
    "cadence": "weekly",
    "politenessOverrides": {
      "requestDelayMs": 2000
    },
    "sourceGroup": "This Week in Pinball",
    "discoveryStatus": "Active",
    "discoveryNotes": "Colin Alsheimer / Kineticist granted explicit permission to index TWIP newsletter content per ADR-0043 (June 2026). robots.txt (verified 2026-06-26) allows all crawlers on /p/* paths. No API key required — content is publicly accessible.",
    "discoveryDate": "2026-06-26"
  }
]
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/PinballWizard.Application.Tests --filter "FullyQualifiedName~ProductionManifest"`
Expected: PASS (both the new test and the existing `ProductionManifest_DeserializesCleanlyAndContainsExpectedEntries` — still 20 entries, unknown props tolerated).

- [ ] **Step 5: Commit**

```bash
git add data/seeds/ingestion_sources.v1.json tests/PinballWizard.Application.Tests/Sync/IngestionSourceSeederTests.cs
git commit -m "fix(seed) add sourceGroup + discovery status to ingestion sources; fix em-dash mojibake, strip name suffixes"
```

---

### Task 2: Persist discovery + group fields through domain entity, DTO, and seeder

**Files:**
- Modify: `src/PinballWizard.Core/Domain/IngestionSource.cs` (add 4 fields)
- Modify: `src/PinballWizard.Application/Sync/IngestionSourceSeed.cs` (add 4 fields)
- Modify: `src/PinballWizard.Application/Sync/IngestionSourceSeeder.cs` (map fields on insert + update)
- Test: `tests/PinballWizard.Application.Tests/Sync/IngestionSourceSeederTests.cs`

**Interfaces:**
- Consumes: the manifest properties from Task 1.
- Produces: `IngestionSource.SourceGroup` (`string`), `IngestionSource.DiscoveryStatus` (`string?`), `IngestionSource.DiscoveryNotes` (`string?`), `IngestionSource.DiscoveryDate` (`DateOnly?`) — the projection Task 4 reads. `IngestionSourceSeed.SourceGroup` is `required string`; the others are `init` nullables.

- [ ] **Step 1: Write the failing test**

Add two tests to `IngestionSourceSeederTests`, and update the `Seed()` helper (shown in Step 3) so existing call sites keep compiling:

```csharp
[Fact]
public async Task SeedAsync_FirstRun_PersistsDiscoveryAndGroupFields()
{
    _repo.GetByIdAsync(Arg.Any<string>(), "config", Arg.Any<CancellationToken>())
        .Returns((IngestionSource?)null);

    IngestionSource? upserted = null;
    _repo.UpsertAsync(Arg.Any<IngestionSource>(), Arg.Any<CancellationToken>())
        .Returns(call => { upserted = call.Arg<IngestionSource>(); return Task.FromResult(upserted); });

    var manifestPath = WriteManifest(
        Seed("jjp_bulletins", "Service Bulletins", "jjp_bulletins",
            "https://www.jerseyjackpinball.com/", false, "none",
            sourceGroup: "Jersey Jack Pinball",
            discoveryStatus: "NoSource",
            discoveryNotes: "No bulletin section exists.",
            discoveryDate: new DateOnly(2026, 5, 26)));

    await _seeder.SeedAsync(manifestPath, CancellationToken.None);

    Assert.NotNull(upserted);
    Assert.Equal("Jersey Jack Pinball", upserted!.SourceGroup);
    Assert.Equal("NoSource", upserted.DiscoveryStatus);
    Assert.Equal("No bulletin section exists.", upserted.DiscoveryNotes);
    Assert.Equal(new DateOnly(2026, 5, 26), upserted.DiscoveryDate);
}

[Fact]
public async Task SeedAsync_ReRun_UpdatesDiscoveryFieldsWhilePreservingRuntimeCounters()
{
    var existing = new IngestionSource
    {
        Id = "pb_bulletins",
        PartitionKey = "config",
        DisplayName = "old",
        ScraperImplKey = "pb_bulletins",
        BaseUrl = "https://old/",
        Enabled = false,
        Cadence = "none",
        SourceGroup = "old-group",
        DiscoveryStatus = "NoSource",
        DiscoveryNotes = "old note",
        TotalDocumentsDiscovered = 99,
    };
    _repo.GetByIdAsync("pb_bulletins", "config", Arg.Any<CancellationToken>()).Returns(existing);

    IngestionSource? upserted = null;
    _repo.UpsertAsync(Arg.Any<IngestionSource>(), Arg.Any<CancellationToken>())
        .Returns(call => { upserted = call.Arg<IngestionSource>(); return Task.FromResult(upserted); });

    var manifestPath = WriteManifest(
        Seed("pb_bulletins", "Service Bulletins", "pb_bulletins",
            "https://pinballbrothers.freshdesk.com/", false, "none",
            sourceGroup: "Pinball Brothers",
            discoveryStatus: "Deferred",
            discoveryNotes: "Needs API key.",
            discoveryDate: new DateOnly(2026, 5, 26)));

    await _seeder.SeedAsync(manifestPath, CancellationToken.None);

    Assert.NotNull(upserted);
    // Discovery/group config re-applied from the seed…
    Assert.Equal("Pinball Brothers", upserted!.SourceGroup);
    Assert.Equal("Deferred", upserted.DiscoveryStatus);
    Assert.Equal("Needs API key.", upserted.DiscoveryNotes);
    // …runtime counter preserved.
    Assert.Equal(99, upserted.TotalDocumentsDiscovered);
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/PinballWizard.Application.Tests --filter "FullyQualifiedName~PersistsDiscoveryAndGroupFields|FullyQualifiedName~UpdatesDiscoveryFields"`
Expected: FAIL to compile (`SourceGroup`/`DiscoveryStatus`/… not defined on the entity, `Seed()` has no such params).

- [ ] **Step 3: Implement**

In `src/PinballWizard.Core/Domain/IngestionSource.cs`, add after the `ETag` property (before the closing brace of the class), using plain `//` comments:

```csharp
    // Manufacturer grouping key shared by a primary source and its sub-feeds
    // (e.g. "Jersey Jack Pinball"). Populated from the seed; non-required so a
    // read of a not-yet-reseeded Cosmos doc never throws.
    [JsonPropertyName("sourceGroup")]
    public string SourceGroup { get; set; } = "";

    // Discovery assessment: "Active" / "NoSource" / "Deferred". Null ⇒ Active.
    [JsonPropertyName("discoveryStatus")]
    public string? DiscoveryStatus { get; set; }

    // Human explanation for a non-Active discovery status (shown in the admin UI).
    [JsonPropertyName("discoveryNotes")]
    public string? DiscoveryNotes { get; set; }

    // Date the discovery assessment was made.
    [JsonPropertyName("discoveryDate")]
    public DateOnly? DiscoveryDate { get; set; }
```

In `src/PinballWizard.Application/Sync/IngestionSourceSeed.cs`, add before the closing brace:

```csharp
    [JsonPropertyName("sourceGroup")]
    public required string SourceGroup { get; init; }

    [JsonPropertyName("discoveryStatus")]
    public string? DiscoveryStatus { get; init; }

    [JsonPropertyName("discoveryNotes")]
    public string? DiscoveryNotes { get; init; }

    [JsonPropertyName("discoveryDate")]
    public DateOnly? DiscoveryDate { get; init; }
```

In `src/PinballWizard.Application/Sync/IngestionSourceSeeder.cs`, add to the **insert** object initializer (after `PolitenessOverrides = seed.PolitenessOverrides,`):

```csharp
                    SourceGroup = seed.SourceGroup,
                    DiscoveryStatus = seed.DiscoveryStatus,
                    DiscoveryNotes = seed.DiscoveryNotes,
                    DiscoveryDate = seed.DiscoveryDate,
```

And to the **update** block (after `existing.PolitenessOverrides = seed.PolitenessOverrides;`):

```csharp
                existing.SourceGroup = seed.SourceGroup;
                existing.DiscoveryStatus = seed.DiscoveryStatus;
                existing.DiscoveryNotes = seed.DiscoveryNotes;
                existing.DiscoveryDate = seed.DiscoveryDate;
```

In `IngestionSourceSeederTests`, replace the `Seed(...)` helper with:

```csharp
private static IngestionSourceSeed Seed(
    string id, string displayName, string scraperImplKey,
    string baseUrl, bool enabled, string cadence,
    string? sourceGroup = null,
    string? discoveryStatus = null,
    string? discoveryNotes = null,
    DateOnly? discoveryDate = null)
{
    return new IngestionSourceSeed
    {
        Id = id,
        DisplayName = displayName,
        ScraperImplKey = scraperImplKey,
        BaseUrl = baseUrl,
        Enabled = enabled,
        Cadence = cadence,
        PolitenessOverrides = null,
        SourceGroup = sourceGroup ?? displayName, // default keeps existing call sites valid
        DiscoveryStatus = discoveryStatus,
        DiscoveryNotes = discoveryNotes,
        DiscoveryDate = discoveryDate,
    };
}
```

If any other file constructs `IngestionSourceSeed` directly, the compiler will flag the now-`required` `SourceGroup`; add `SourceGroup = <appropriate group>` at those sites.

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/PinballWizard.Application.Tests --filter "FullyQualifiedName~IngestionSourceSeederTests"`
Expected: PASS (all seeder tests, including the existing idempotency + production-manifest tests).

- [ ] **Step 5: Commit**

```bash
git add src/PinballWizard.Core/Domain/IngestionSource.cs src/PinballWizard.Application/Sync/IngestionSourceSeed.cs src/PinballWizard.Application/Sync/IngestionSourceSeeder.cs tests/PinballWizard.Application.Tests/Sync/IngestionSourceSeederTests.cs
git commit -m "feat(sync) persist sourceGroup + discovery status/notes/date through the ingestion-source seeder"
```

---

### Task 3: Status derivation helper

**Files:**
- Create: `src/PinballWizard.Web/Components/Pages/Admin/SourceStatusView.cs`
- Test: `tests/PinballWizard.Web.Tests/Components/Admin/SourceStatusViewTests.cs`

**Interfaces:**
- Consumes: `(bool enabled, string? discoveryStatus)`.
- Produces: `SourceStatusView.Derive(bool, string?) -> SourceStatusView` where `SourceStatusView` is a record `(SourceStatus Status, string Label, Color Color, string Icon)` and `SourceStatus` is `enum { Active, NoSource, Deferred, Disabled }`. Task 4 calls `SourceStatusView.Derive(...)`.

- [ ] **Step 1: Write the failing test**

Create `tests/PinballWizard.Web.Tests/Components/Admin/SourceStatusViewTests.cs`:

```csharp
using MudBlazor;
using PinballWizard.Web.Components.Pages.Admin;
using Xunit;

namespace PinballWizard.Web.Tests.Components.Admin;

public sealed class SourceStatusViewTests
{
    [Theory]
    [InlineData(true, null, SourceStatus.Active, "Active")]
    [InlineData(true, "Active", SourceStatus.Active, "Active")]
    [InlineData(false, "NoSource", SourceStatus.NoSource, "No source")]
    [InlineData(false, "Deferred", SourceStatus.Deferred, "Deferred")]
    [InlineData(false, null, SourceStatus.Disabled, "Disabled")]
    [InlineData(false, "Active", SourceStatus.Disabled, "Disabled")]
    public void Derive_MapsStatusAndLabel(
        bool enabled, string? discoveryStatus, SourceStatus expectedStatus, string expectedLabel)
    {
        var view = SourceStatusView.Derive(enabled, discoveryStatus);

        Assert.Equal(expectedStatus, view.Status);
        Assert.Equal(expectedLabel, view.Label);
        Assert.False(string.IsNullOrWhiteSpace(view.Icon)); // icon always present (colour not sole carrier)
    }

    [Fact]
    public void Derive_Active_UsesSuccessColour()
    {
        Assert.Equal(Color.Success, SourceStatusView.Derive(true, null).Color);
    }

    [Fact]
    public void Derive_Deferred_UsesWarningColour()
    {
        Assert.Equal(Color.Warning, SourceStatusView.Derive(false, "Deferred").Color);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/PinballWizard.Web.Tests --filter "FullyQualifiedName~SourceStatusViewTests"`
Expected: FAIL to compile (`SourceStatusView` does not exist).

- [ ] **Step 3: Implement**

Create `src/PinballWizard.Web/Components/Pages/Admin/SourceStatusView.cs`:

```csharp
using MudBlazor;

namespace PinballWizard.Web.Components.Pages.Admin;

// Four-state ingestion-source status shown on /admin/sources. Distinguishes a
// deliberate "no such content exists" (NoSource) and "blocked, exists elsewhere"
// (Deferred) from a plain manual off-switch (Disabled) — so a disabled row reads
// as a documented decision, not a failure.
public enum SourceStatus { Active, NoSource, Deferred, Disabled }

// Presentation projection of a status: label + colour + icon. Icon is always set
// so colour is never the sole carrier of meaning (WCAG 2.1 AA).
public sealed record SourceStatusView(SourceStatus Status, string Label, Color Color, string Icon)
{
    public static SourceStatusView Derive(bool enabled, string? discoveryStatus)
    {
        // Enabled sources are Active regardless of any recorded discovery note.
        if (enabled)
        {
            return new SourceStatusView(
                SourceStatus.Active, "Active", Color.Success, Icons.Material.Filled.CheckCircle);
        }

        return discoveryStatus switch
        {
            "NoSource" => new SourceStatusView(
                SourceStatus.NoSource, "No source", Color.Default, Icons.Material.Filled.RemoveCircleOutline),
            "Deferred" => new SourceStatusView(
                SourceStatus.Deferred, "Deferred", Color.Warning, Icons.Material.Filled.PauseCircleOutline),
            _ => new SourceStatusView(
                SourceStatus.Disabled, "Disabled", Color.Default, Icons.Material.Filled.Block),
        };
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/PinballWizard.Web.Tests --filter "FullyQualifiedName~SourceStatusViewTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/PinballWizard.Web/Components/Pages/Admin/SourceStatusView.cs tests/PinballWizard.Web.Tests/Components/Admin/SourceStatusViewTests.cs
git commit -m "feat(web) add SourceStatusView — four-state ingestion-source status derivation"
```

---

### Task 4: AdminSources grid — group by manufacturer, status chip, inline reason

**Files:**
- Modify: `src/PinballWizard.Web/Components/Pages/Admin/AdminSources.razor`
- Test: `tests/PinballWizard.Web.Tests/Components/Admin/AdminSourcesTests.cs`

**Interfaces:**
- Consumes: `SourceStatusView.Derive` (Task 3); `IngestionSource.SourceGroup/DiscoveryStatus/DiscoveryNotes/DiscoveryDate` (Task 2).
- Produces: the redesigned grid (terminal deliverable).

- [ ] **Step 1: Write the failing tests**

In `AdminSourcesTests.cs`, update `MakeSource` to carry group + discovery data, update the vocabulary assertions, and add grouping/reason tests. Replace `MakeSource` with:

```csharp
private static IngestionSource MakeSource(
    string id, bool enabled,
    string? sourceGroup = null,
    string? discoveryStatus = null,
    string? discoveryNotes = null,
    DateOnly? discoveryDate = null) => new()
{
    Id = id,
    DisplayName = $"{id} Pinball",
    ScraperImplKey = id,
    BaseUrl = $"https://{id}.example.com",
    Enabled = enabled,
    Cadence = "weekly",
    SourceGroup = sourceGroup ?? $"{id} Group",
    DiscoveryStatus = discoveryStatus,
    DiscoveryNotes = discoveryNotes,
    DiscoveryDate = discoveryDate,
    TotalDocumentsDiscovered = 7,
    TotalRunFailures = 0,
};
```

Replace the existing `WithSources_RendersRows` test (the chip vocabulary changed — an enabled row no longer says "Enabled") and add the reason/grouping tests:

```csharp
[Fact]
public void WithSources_RendersStatusVocabulary()
{
    RegisterSources(ct => Stream([MakeSource("stern", true), MakeSource("jjp", false)], ct));
    _ = Services.GetRequiredService<BunitNavigationManager>();

    var cut = RenderWithPopover<AdminSources>();

    cut.WaitForAssertion(() =>
    {
        Assert.Contains("stern Pinball", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("Active", cut.Markup, StringComparison.Ordinal);   // enabled row
        Assert.Contains("Disabled", cut.Markup, StringComparison.Ordinal); // disabled, no discovery reason
    });
}

[Fact]
public void NoSourceRow_RendersNoSourceChipAndInlineReason()
{
    RegisterSources(ct => Stream([
        MakeSource("jjp_bulletins", false,
            sourceGroup: "Jersey Jack Pinball",
            discoveryStatus: "NoSource",
            discoveryNotes: "No bulletin section exists here.",
            discoveryDate: new DateOnly(2026, 5, 26))
    ], ct));
    _ = Services.GetRequiredService<BunitNavigationManager>();

    var cut = RenderWithPopover<AdminSources>();

    cut.WaitForAssertion(() =>
    {
        Assert.Contains("No source", cut.Markup, StringComparison.Ordinal);
        var reason = cut.Find("[data-testid='source-reason']");
        Assert.Contains("No bulletin section exists here.", reason.TextContent, StringComparison.Ordinal);
        Assert.Contains("2026-05-26", reason.TextContent, StringComparison.Ordinal);
    });
}

[Fact]
public void ActiveRow_RendersNoReasonCaption()
{
    RegisterSources(ct => Stream([
        MakeSource("stern", true, sourceGroup: "Stern Pinball",
            discoveryStatus: "Active", discoveryNotes: "Should not be shown for active.")
    ], ct));
    _ = Services.GetRequiredService<BunitNavigationManager>();

    var cut = RenderWithPopover<AdminSources>();

    cut.WaitForAssertion(() =>
        Assert.Empty(cut.FindAll("[data-testid='source-reason']")));
}

[Fact]
public void SubFeeds_GroupUnderTheirManufacturer()
{
    RegisterSources(ct => Stream([
        MakeSource("jjp", true, sourceGroup: "Jersey Jack Pinball"),
        MakeSource("jjp_bulletins", false, sourceGroup: "Jersey Jack Pinball",
            discoveryStatus: "NoSource", discoveryNotes: "n/a", discoveryDate: new DateOnly(2026, 5, 26)),
    ], ct));
    _ = Services.GetRequiredService<BunitNavigationManager>();

    var cut = RenderWithPopover<AdminSources>();

    cut.WaitForAssertion(() =>
        // One group header for the shared manufacturer, rendered once.
        Assert.Single(cut.FindAll("[data-testid='source-group-header']")));
}
```

> Keep the existing `EmptyList_...`, `LoadFailure_...`, `Breadcrumb_...`, `SourceName_LinksToDetailPage`, and `SourceUrl_RendersAsLink` tests unchanged — they still hold.

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/PinballWizard.Web.Tests --filter "FullyQualifiedName~AdminSourcesTests"`
Expected: FAIL (no `SourceGroup` grouping, no `source-reason`/`source-group-header` markup, MakeSource signature changed).

- [ ] **Step 3: Implement the grid changes**

In `AdminSources.razor`, ensure `@using MudBlazor` is present near the top (add if missing).

Replace the `<AppDataGrid …>` opening tag and its `<Columns>` block with a groupable grid, a hidden grouping column carrying a labelled group header, and a reworked Status column:

```razor
    <AppDataGrid T="IngestionSourceRow"
                 Items="@_sources"
                 Groupable="@true"
                 GroupExpanded="@true"
                 data-testid="admin-sources-grid">

        <Columns>
            <PropertyColumn Property="x => x.SourceGroup" Title="Manufacturer" Grouping="true" Hidden="true">
                <GroupTemplate>
                    <MudText Typo="Typo.body2"
                             data-testid="source-group-header"
                             Style="color: var(--mud-palette-text-primary); font-weight: 600">
                        @context.Grouping.Key
                    </MudText>
                </GroupTemplate>
            </PropertyColumn>
            <TemplateColumn Title="Name">
                <CellTemplate>
                    <MudLink Href="@($"/admin/sources/{context.Item.Id}")">
                        @context.Item.Name
                    </MudLink>
                </CellTemplate>
            </TemplateColumn>
            <TemplateColumn Title="Source URL">
                <CellTemplate>
                    <MudLink Href="@context.Item.SourceUrl" Target="_blank">@context.Item.SourceUrl</MudLink>
                </CellTemplate>
            </TemplateColumn>
            <TemplateColumn Title="Status">
                <CellTemplate>
                    @{
                        var view = SourceStatusView.Derive(context.Item.Enabled, context.Item.DiscoveryStatus);
                    }
                    <AppStatusChip Color="@view.Color" Icon="@view.Icon">@view.Label</AppStatusChip>
                    @if (view.Status != SourceStatus.Active
                         && !string.IsNullOrWhiteSpace(context.Item.DiscoveryNotes))
                    {
                        <MudText Typo="Typo.caption"
                                 Color="Color.Secondary"
                                 Class="d-block mt-1"
                                 data-testid="source-reason">
                            @context.Item.DiscoveryNotes@(context.Item.DiscoveryDate is { } d
                                ? $" (assessed {d:yyyy-MM-dd})" : "")
                        </MudText>
                    }
                </CellTemplate>
            </TemplateColumn>
            <PropertyColumn Property="x => x.Cadence" Title="Cadence" />
            <PropertyColumn Property="x => x.LastRun" Title="Last Run" />
            <PropertyColumn Property="x => x.LastSuccess" Title="Last Success" />
            <PropertyColumn Property="x => x.DocsDiscovered" Title="Docs Discovered" />
            <PropertyColumn Property="x => x.RunFailures" Title="Run Failures" />
        </Columns>
```

> If MudDataGrid does not group when the grouping column is `Hidden="true"`, remove `Hidden="true"` (the manufacturer then also shows as a column — acceptable). Verify via the `SubFeeds_GroupUnderTheirManufacturer` test.

Extend the `IngestionSourceRow` record with the new fields:

```csharp
    private sealed record IngestionSourceRow(
        string Id,
        string Name,
        string SourceUrl,
        bool Enabled,
        string Cadence,
        string LastRun,
        string LastSuccess,
        long DocsDiscovered,
        long RunFailures,
        string SourceGroup,
        string? DiscoveryStatus,
        string? DiscoveryNotes,
        DateOnly? DiscoveryDate);
```

And populate them in the `OnInitializedAsync` projection. Change the tail of the `new IngestionSourceRow(...)` call from `RunFailures: s.TotalRunFailures));` to:

```csharp
                    RunFailures:     s.TotalRunFailures,
                    SourceGroup:     s.SourceGroup,
                    DiscoveryStatus: s.DiscoveryStatus,
                    DiscoveryNotes:  s.DiscoveryNotes,
                    DiscoveryDate:   s.DiscoveryDate));
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/PinballWizard.Web.Tests --filter "FullyQualifiedName~AdminSourcesTests"`
Expected: PASS (all tests — the unchanged link/empty/failure/breadcrumb tests plus the four new/updated ones).

- [ ] **Step 5: Run the full web + application suites**

Run: `dotnet test tests/PinballWizard.Web.Tests tests/PinballWizard.Application.Tests --filter "Category!=Accessibility&Category!=Snapshots&Category!=Circuit&Category!=E2E"`
Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add src/PinballWizard.Web/Components/Pages/Admin/AdminSources.razor tests/PinballWizard.Web.Tests/Components/Admin/AdminSourcesTests.cs
git commit -m "feat(web) /admin/sources: group by manufacturer, four-state status chip, inline discovery reason"
```

---

## Self-Review

**Spec coverage:**
- Data model (spec §1): Task 2 (entity + DTO + seeder) ✓
- Status vocabulary (spec §2): Task 3 (derivation) + Task 4 (chip render) ✓
- Layout / grouping + inline reason (spec §3): Task 4 ✓
- Seed cleanup: em-dashes, suffixes, added fields (spec §4): Task 1 ✓
- Testing: seeder round-trip (Task 2), status derivation (Task 3), render + grouping (Task 4) (spec §5) ✓
- Out of scope (spec §6): no editing, no detail-page change, render-mode already in main (#629) — honored ✓

**Type consistency:** `SourceStatusView.Derive(bool, string?)` and enum `SourceStatus` are defined in Task 3 and consumed verbatim in Task 4. `IngestionSource.SourceGroup/DiscoveryStatus/DiscoveryNotes/DiscoveryDate` defined in Task 2, read in Task 4's projection. `data-testid` values (`source-reason`, `source-group-header`) match between Task 4 markup and tests.

**Placeholder scan:** none — every step has concrete code and commands.

**Ordering:** Task 1 (JSON) precedes Task 2 (which makes `SourceGroup` `required`), so the manifest already supplies the field when the DTO starts binding it — each task ends green independently.
