# Admin test coverage (axe + in-process real-circuit) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Close #423 — give the now-interactive admin pages real-browser coverage: WCAG axe scans (Half A, SSR) and a deterministic in-process **real Blazor circuit** that proves the interactive controls respond (Half B), with zero Azure/Entra/Cloudflare dependencies.

**Architecture:** Both halves render `/admin/*` pages (which carry `[Authorize(Policy="AdminOnly")]`) by reproducing `Program.cs`'s **no-tenant** posture — a permissive `AdminOnly` policy (`RequireAssertion(_ => true)`) and no OIDC — plus a shared `AddAdminTestDoubles` fixture that stubs every service the admin pages inject. Half A extends the existing minimal SSR Playwright host (axe on SSR HTML). Half B runs the **real Web app** with `AzureAd:TenantId` unset (real `MapStaticAssets` → live circuit) on a Kestrel loopback port, driven by Playwright.

**Tech Stack:** .NET 10 (SDK 10.0.200), Blazor Web App (InteractiveServer + WASM auto), MudBlazor 8.5.0 (strict, ADR-0008), bUnit/xUnit/NSubstitute, Microsoft.Playwright + Deque.AxeCore.Playwright (both already referenced in `PinballWizard.Web.Tests.csproj`).

## Global Constraints

- **No external deps:** no Azure, Entra, Cloudflare, or standing admin account. Everything runs from a clean checkout. (The `AzureAd:TenantId`-unset path gives permissive `AdminOnly` + no OIDC — `src/PinballWizard.Web/Program.cs:90-146`.)
- **MudBlazor strict (ADR-0008);** no hardcoded hex colors.
- **Personal identity only:** commits use `94459922+jkeeley2073@users.noreply.github.com` (verify `git config user.email`; set locally if needed).
- **Do NOT touch** `.claude-gates/*` or `git stash` entries.
- **Tests assert behavior,** not structure. bUnit can't catch the render-mode bug (it always renders interactive) — these Playwright tests are the real-circuit proof; axe runs on SSR HTML.
- **No real PII** in fixtures — synthetic data only.
- **Branch:** `feat/admin-test-coverage` (already checked out).
- **Seed identifiers (used verbatim across tasks):** manufacturer `stern`; machines `mch_godzilla_pro` (GroupId `godzilla`, "Godzilla", edition "Pro", 2 docs, HasManual) and `mch_godzilla_le` (GroupId `godzilla`, "Godzilla", edition "LE", 0 docs → edition-gap). Triage doc id `doc_triage_1`. Override pattern `https://sternpinball.com/x|Manual`. Setting key `ai.confidence_threshold`.

---

## File Structure

**New (tests):**
- `tests/PinballWizard.Web.Tests/A11y/AdminTestDoubles.cs` — shared `AddAdminTestDoubles(IServiceCollection)` extension + seed fixture (NSubstitute). Used by both halves.
- `tests/PinballWizard.Web.Tests/A11y/AdminAccessibilityTests.cs` — Half A axe Theory over admin routes.
- `tests/PinballWizard.Web.Tests/Circuit/InteractiveAdminWebApplicationFactory.cs` — Half B real-circuit host (real `Program.cs`, no tenant, stub overrides, Kestrel loopback).
- `tests/PinballWizard.Web.Tests/Circuit/AdminCircuitSkeletonTests.cs` — Half B skeleton (de-risk gate).
- `tests/PinballWizard.Web.Tests/Circuit/AdminInteractiveTests.cs` — Half B per-page interactive tests.

**Modified:**
- `tests/PinballWizard.Web.Tests/A11y/PlaywrightWebApplicationFactory.cs` — add an `adminMode` flag that registers the permissive `AdminOnly` policy + `AddAdminTestDoubles` + the `EmbeddedResourceAgentPromptProvider` singleton.
- `.github/workflows/ci.yml` — ensure the UI-tests job builds the Web project (Half B needs its static-asset manifest) and runs the new test classes.

**Parallelism note:** Task 1 (fixture) is the foundation. Tasks 2→3 (Half A) and Task 4 (Half B skeleton) both depend on Task 1 but are independent of each other. Tasks 5 (Half B per-page) depends on Task 4's skeleton succeeding. Task 6 (CI) is last. **Task 4 is a de-risk GATE** — if the skeleton can't establish a live circuit at acceptable cost, stop and escalate before Task 5 (fallback in the spec §7).

---

### Task 1: `AddAdminTestDoubles` — shared stub fixture

**Files:**
- Create: `tests/PinballWizard.Web.Tests/A11y/AdminTestDoubles.cs`
- Test: same file's behavior is exercised transitively by Tasks 3/4/5; this task adds a focused guard test in `tests/PinballWizard.Web.Tests/A11y/AdminTestDoublesTests.cs`.

**Interfaces:**
- Produces: `public static IServiceCollection AddAdminTestDoubles(this IServiceCollection services)` — registers NSubstitute stubs for every admin-page service, returning the seed fixture. Consumed by Tasks 2 and 4.

- [ ] **Step 1: Write the guard test (`AdminTestDoublesTests.cs`)**

```csharp
using Microsoft.Extensions.DependencyInjection;
using PinballWizard.Application.Catalog;
using PinballWizard.Application.Persistence;
using Xunit;

namespace PinballWizard.Web.Tests.A11y;

// Pins that AddAdminTestDoubles registers resolvable stubs returning the seed
// fixture the admin pages render against. If a new admin-page dependency is
// added without a double, the page won't render in the axe/circuit hosts —
// this catches the missing registration directly.
public sealed class AdminTestDoublesTests
{
    [Fact]
    public async Task AddAdminTestDoubles_RegistersResolvableStats_WithSeedFamily()
    {
        var sp = new ServiceCollection().AddAdminTestDoubles().BuildServiceProvider();

        var stats = sp.GetRequiredService<ICatalogStatsReadRepository>();
        var mfrs = new List<ManufacturerCatalogStats>();
        await foreach (var m in stats.StreamAllManufacturersAsync(CancellationToken.None))
            mfrs.Add(m);

        Assert.Single(mfrs);
        Assert.Equal("stern", mfrs[0].Manufacturer);
        // Godzilla family: one machine with docs, one with zero (edition gap).
        Assert.Contains(mfrs[0].Machines, m => m.MachineId == "mch_godzilla_pro" && m.DocCount == 2);
        Assert.Contains(mfrs[0].Machines, m => m.MachineId == "mch_godzilla_le" && m.DocCount == 0);
    }

    [Fact]
    public void AddAdminTestDoubles_RegistersAllAdminPageDependencies()
    {
        var sp = new ServiceCollection().AddAdminTestDoubles().BuildServiceProvider();

        // Every service injected by an /admin/* page must resolve.
        Assert.NotNull(sp.GetService<ICatalogStatsReadRepository>());
        Assert.NotNull(sp.GetService<IMachineRepository>());
        Assert.NotNull(sp.GetService<IMachineDocumentReadRepository>());
        Assert.NotNull(sp.GetService<IRawDocumentRepository>());
        Assert.NotNull(sp.GetService<PinballWizard.Application.Linking.IDocumentLinker>());
        Assert.NotNull(sp.GetService<ILinkOverrideRepository>());
        Assert.NotNull(sp.GetService<IAdminSettingsRepository>());
        Assert.NotNull(sp.GetService<IAgentPromptOverrideRepository>());
        Assert.NotNull(sp.GetService<PinballWizard.Application.Ai.EmbeddedResourceAgentPromptProvider>());
        Assert.NotNull(sp.GetService<Microsoft.Extensions.Options.IOptions<PinballWizard.Core.Configuration.AiFoundryOptions>>());
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/PinballWizard.Web.Tests --filter "FullyQualifiedName~AdminTestDoublesTests"`
Expected: FAIL — `AddAdminTestDoubles` does not exist (compile error).

- [ ] **Step 3: Implement `AdminTestDoubles.cs`**

```csharp
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NSubstitute;
using PinballWizard.Application.Ai;
using PinballWizard.Application.Catalog;
using PinballWizard.Application.Linking;
using PinballWizard.Application.Persistence;
using PinballWizard.Core.Configuration;
using PinballWizard.Core.Domain;
using PinballWizard.Core.Models;

namespace PinballWizard.Web.Tests.A11y;

// Shared in-memory test doubles for the /admin/* pages, used by both the SSR
// axe host (Half A) and the real-circuit host (Half B). Synthetic seed data —
// a single manufacturer ("stern") with a two-edition Godzilla family where the
// LE has zero docs, so health chips, the edition-gap callout, triage rows,
// an override, and a settings row all render. NSubstitute matches the existing
// repo-stub pattern (AdminMachinesTests). No real PII, no Cosmos, no Foundry.
internal static class AdminTestDoubles
{
    public const string Manufacturer = "stern";
    public const string ProId = "mch_godzilla_pro";
    public const string LeId = "mch_godzilla_le";
    public const string GroupId = "godzilla";

    private static readonly DateTimeOffset AsOf =
        new(2026, 6, 1, 12, 0, 0, TimeSpan.Zero);

    public static IServiceCollection AddAdminTestDoubles(this IServiceCollection services)
    {
        services.AddSingleton(CatalogStats());
        services.AddSingleton(Machines());
        services.AddSingleton(MachineDocs());
        services.AddSingleton(RawDocs());
        services.AddSingleton(Linker());
        services.AddSingleton(Overrides());
        services.AddSingleton(Settings());
        services.AddSingleton(Prompts());

        // Concrete singleton — parameterless, loads embedded prompt .md resources.
        services.AddSingleton<EmbeddedResourceAgentPromptProvider>();

        // AdminSettings injects IOptions<AiFoundryOptions>; defaults are usable.
        services.AddSingleton<IOptions<AiFoundryOptions>>(Options.Create(new AiFoundryOptions()));

        return services;
    }

    // ── ICatalogStatsReadRepository ──────────────────────────────────────────
    private static readonly MachineDocStats ProStat = new(
        MachineId: ProId, Title: "Godzilla", EditionLabel: "Pro", GroupId: GroupId,
        Year: 2021, IsOpdbOnly: false, DocCount: 2,
        DocTypeCounts: new Dictionary<string, int> { ["Manual"] = 1 }, HasManual: true);

    private static readonly MachineDocStats LeStat = new(
        MachineId: LeId, Title: "Godzilla", EditionLabel: "LE", GroupId: GroupId,
        Year: 2021, IsOpdbOnly: false, DocCount: 0,
        DocTypeCounts: new Dictionary<string, int>(), HasManual: false);

    private static readonly ManufacturerCatalogStats SternStats =
        new(Manufacturer, AsOf, [ProStat, LeStat]);

    private static ICatalogStatsReadRepository CatalogStats()
    {
        var repo = Substitute.For<ICatalogStatsReadRepository>();
        repo.StreamAllManufacturersAsync(Arg.Any<CancellationToken>())
            .Returns(_ => Stream(SternStats));
        repo.GetByManufacturerAsync(Manufacturer, Arg.Any<CancellationToken>())
            .Returns(SternStats);
        return repo;
    }

    // ── IMachineRepository ───────────────────────────────────────────────────
    private static Machine MachineRecord(string id, string edition) => new()
    {
        Id = id,
        PartitionKey = Manufacturer,
        ManufacturerDisplayName = "Stern Pinball",
        Title = "Godzilla",
        GroupId = GroupId,
        Year = 2021,
        EditionLabel = edition,
        EditionTokens = [edition],
        Designers = [],
        Themes = [],
        Editions = [],
        ManufacturerSlugs = new Dictionary<string, string>(),
        OpdbSourceUrl = "https://opdb.org/machines/" + id,
        FirstSeenAt = AsOf,
        LastSeenAt = AsOf,
    };

    private static IMachineRepository Machines()
    {
        var repo = Substitute.For<IMachineRepository>();
        var pro = MachineRecord(ProId, "Pro");
        var le = MachineRecord(LeId, "LE");
        repo.GetByOpdbIdAsync(ProId, Manufacturer, Arg.Any<CancellationToken>()).Returns(pro);
        repo.GetByOpdbIdAsync(LeId, Manufacturer, Arg.Any<CancellationToken>()).Returns(le);
        repo.GetSiblingsByGroupIdAsync(GroupId, Arg.Any<CancellationToken>())
            .Returns(_ => Stream(pro, le));
        return repo;
    }

    // ── IMachineDocumentReadRepository ───────────────────────────────────────
    private static IMachineDocumentReadRepository MachineDocs()
    {
        var repo = Substitute.For<IMachineDocumentReadRepository>();
        var links = new[]
        {
            new MachineDocumentLink(
                DocumentId: "doc_man_1", DocumentType: "Manual",
                DocumentUrl: "https://sternpinball.com/godzilla-manual.pdf",
                LinkText: "Godzilla Manual", Edition: "Pro", EditionScope: "SingleEdition",
                LinkStatus: "Linked", ResolutionStrategy: "title_match",
                LastDownloadedUtc: AsOf, SizeBytes: 2_400_000, PageCount: 48),
            new MachineDocumentLink(
                DocumentId: "doc_rules_1", DocumentType: "Other",
                DocumentUrl: "https://sternpinball.com/godzilla-rules.pdf",
                LinkText: "Rules", Edition: null, EditionScope: "FranchiseWide",
                LinkStatus: "Linked", ResolutionStrategy: "title_match",
                LastDownloadedUtc: AsOf, SizeBytes: 800_000, PageCount: 12),
        };
        repo.StreamByMachineIdAsync(ProId, Arg.Any<CancellationToken>())
            .Returns(_ => Stream(links));
        repo.StreamByMachineIdAsync(LeId, Arg.Any<CancellationToken>())
            .Returns(_ => Stream<MachineDocumentLink>());
        return repo;
    }

    // ── IRawDocumentRepository ───────────────────────────────────────────────
    private static RawDocumentRecord TriageDoc() => new()
    {
        DocumentId = "doc_triage_1",
        DocumentUrl = "https://sternpinball.com/unknown.pdf",
        DocumentType = DocumentType.Manual,
        Source = new SourceInfo
        {
            DiscoveryUrl = "https://sternpinball.com/support/",
            LinkText = "Mystery doc",
            ScrapedAt = AsOf.UtcDateTime,
        },
        Timeline = new TimelineInfo { FirstDiscoveredAt = AsOf, LastCheckedAt = AsOf },
        LinkStatus = LinkStatus.Failed,
        LinkFailureReason = "No matching machine",
    };

    private static IRawDocumentRepository RawDocs()
    {
        var repo = Substitute.For<IRawDocumentRepository>();
        repo.StreamByStatusAsync(Arg.Any<IReadOnlyCollection<LinkStatus>>(), Arg.Any<CancellationToken>())
            .Returns(_ => Stream(TriageDoc()));
        repo.GetAsync("doc_triage_1", Arg.Any<CancellationToken>()).Returns(TriageDoc());
        repo.UpdateLinkStatusAsync(
            Arg.Any<string>(), Arg.Any<LinkStatus>(), Arg.Any<string?>(),
            Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        return repo;
    }

    // ── IDocumentLinker (Relink returns Linked so the row resolves) ──────────
    private static IDocumentLinker Linker()
    {
        var linker = Substitute.For<IDocumentLinker>();
        linker.InitializeAsync(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        linker.LinkAsync(Arg.Any<RawDocumentRecord>(), Arg.Any<CancellationToken>())
            .Returns(new LinkingResult(
                DocumentId: "doc_triage_1",
                FinalStatus: LinkStatus.Linked,
                ResolutionStrategy: "admin_relink",
                LinkedMachineIds: [ProId]));
        return linker;
    }

    // ── ILinkOverrideRepository ──────────────────────────────────────────────
    private static ILinkOverrideRepository Overrides()
    {
        var repo = Substitute.For<ILinkOverrideRepository>();
        var seed = new LinkOverrideRecord
        {
            SourcePattern = "https://sternpinball.com/x|Manual",
            MachineIds = [ProId],
            CreatedBy = "admin (local-dev)",
            CreatedAt = AsOf,
            Notes = "seed override",
        };
        repo.LoadAllAsync(Arg.Any<CancellationToken>())
            .Returns(new Dictionary<string, LinkOverrideRecord> { [seed.SourcePattern] = seed });
        repo.UpsertAsync(Arg.Any<LinkOverrideRecord>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        repo.DeleteAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        return repo;
    }

    // ── IAdminSettingsRepository ─────────────────────────────────────────────
    private static IAdminSettingsRepository Settings()
    {
        var repo = Substitute.For<IAdminSettingsRepository>();
        var rows = new List<AdminSettingRecord>
        {
            new("ai.confidence_threshold", "0.70", AsOf, "admin (local-dev)"),
        };
        repo.GetAllAsync(Arg.Any<CancellationToken>())
            .Returns((IReadOnlyList<AdminSettingRecord>)rows);
        repo.SetAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        repo.DeleteAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        return repo;
    }

    // ── IAgentPromptOverrideRepository (no overrides → embedded default) ─────
    private static IAgentPromptOverrideRepository Prompts()
    {
        var repo = Substitute.For<IAgentPromptOverrideRepository>();
        repo.GetActiveAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((AgentPromptOverride?)null);
        repo.GetVersionsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((IReadOnlyList<AgentPromptOverride>)[]);
        return repo;
    }

    // ── async-enumerable helper ──────────────────────────────────────────────
    private static async IAsyncEnumerable<T> Stream<T>(params T[] items)
    {
        await Task.CompletedTask;
        foreach (var i in items) yield return i;
    }
}
```

- [ ] **Step 4: Run to verify it passes**

Run: `dotnet test tests/PinballWizard.Web.Tests --filter "FullyQualifiedName~AdminTestDoublesTests"`
Expected: PASS (2/2). If a constructor/property name mismatches the real type, fix against the named type (the signatures here are from the codebase as of 2026-06-17; the compiler is the source of truth).

- [ ] **Step 5: Commit**

```bash
git add tests/PinballWizard.Web.Tests/A11y/AdminTestDoubles.cs tests/PinballWizard.Web.Tests/A11y/AdminTestDoublesTests.cs
git commit -m "test(web): AddAdminTestDoubles shared admin-page stub fixture

Synthetic in-memory doubles for every service the /admin/* pages inject
(Godzilla two-edition family, a triage doc, an override, a setting). Shared by
the admin axe host and the real-circuit host. NSubstitute, no Cosmos/Foundry."
```

---

### Task 2: Admin mode on the SSR Playwright host

**Files:**
- Modify: `tests/PinballWizard.Web.Tests/A11y/PlaywrightWebApplicationFactory.cs`

**Interfaces:**
- Consumes: `AddAdminTestDoubles` (Task 1).
- Produces: `PlaywrightWebApplicationFactory(bool adminMode = false)` — when `adminMode` is true, registers the permissive `AdminOnly` policy + `AddAdminTestDoubles`, so `/admin/*` pages render. Default `false` keeps the existing public anonymous behavior. Consumed by Task 3.

The existing factory's `AddAuthorization()` registers no `AdminOnly` policy; admin pages reference it by name and throw at render without it. Admin mode adds the permissive policy exactly as `Program.cs`'s no-tenant branch does, plus the admin services.

- [ ] **Step 1: Write the failing test (temporary, in `AdminAccessibilityTests.cs` — promoted in Task 3)**

Create `tests/PinballWizard.Web.Tests/A11y/AdminAccessibilityTests.cs` with just the host-reachability assertion for now:

```csharp
using Microsoft.Playwright;
using Xunit;

namespace PinballWizard.Web.Tests.A11y;

[Trait("Category", "Accessibility")]
public sealed class AdminAccessibilityTests(AdminPlaywrightFactory factory)
    : IClassFixture<AdminAccessibilityTests.AdminPlaywrightFactory>
{
    // Distinct fixture type so xUnit builds an admin-mode host separate from the
    // public anonymous one.
    public sealed class AdminPlaywrightFactory() : PlaywrightWebApplicationFactory(adminMode: true);

    [Fact]
    public async Task AdminDashboard_RendersUnder200_NotAChallengeRedirect()
    {
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
        var page = await browser.NewPageAsync();

        var response = await page.GotoAsync(
            $"{factory.ServerAddress}/admin",
            new() { WaitUntil = WaitUntilState.DOMContentLoaded });

        Assert.NotNull(response);
        Assert.Equal(200, response!.Status); // permissive AdminOnly → renders, no 302/401
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/PinballWizard.Web.Tests --filter "FullyQualifiedName~AdminAccessibilityTests.AdminDashboard_RendersUnder200_NotAChallengeRedirect"`
Expected: FAIL — `PlaywrightWebApplicationFactory` has no `adminMode` ctor parameter (compile error), and even once it compiles the admin page would throw (no `AdminOnly` policy / no admin services).

- [ ] **Step 3: Add admin mode to the factory.** In `PlaywrightWebApplicationFactory.cs`: change the class to take the flag and branch the auth/service registration.

Replace the class declaration line `public sealed class PlaywrightWebApplicationFactory : IAsyncLifetime` with:

```csharp
public class PlaywrightWebApplicationFactory(bool adminMode = false) : IAsyncLifetime
```

(non-sealed so the Task-3 `AdminPlaywrightFactory` can derive it.)

Then, in `InitializeAsync`, replace the auth/authorization block

```csharp
        builder.Services
            .AddAuthentication(defaultScheme: "Test")
            .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>("Test", _ => { });
        builder.Services.AddAuthorization();
```

with:

```csharp
        builder.Services
            .AddAuthentication(defaultScheme: "Test")
            .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>("Test", _ => { });

        // Admin mode reproduces Program.cs's no-tenant posture: the permissive
        // AdminOnly policy (RequireAssertion(_ => true)) so /admin/* pages render
        // for the anonymous TestAuthHandler identity. Public mode keeps the bare
        // AddAuthorization() (no AdminOnly policy) — unchanged for the public axe suite.
        if (adminMode)
        {
            builder.Services.AddAuthorization(o =>
                o.AddPolicy("AdminOnly", p => p.RequireAssertion(_ => true)));
            builder.Services.AddAdminTestDoubles();
        }
        else
        {
            builder.Services.AddAuthorization();
        }
```

(`AddAdminTestDoubles` also registers `EmbeddedResourceAgentPromptProvider` + `IOptions<AiFoundryOptions>`, which AdminSettings needs.)

- [ ] **Step 4: Run to verify it passes**

Run: `dotnet test tests/PinballWizard.Web.Tests --filter "FullyQualifiedName~AdminAccessibilityTests.AdminDashboard_RendersUnder200_NotAChallengeRedirect"`
Expected: PASS — `/admin` returns 200.

- [ ] **Step 5: Commit**

```bash
git add tests/PinballWizard.Web.Tests/A11y/PlaywrightWebApplicationFactory.cs tests/PinballWizard.Web.Tests/A11y/AdminAccessibilityTests.cs
git commit -m "test(web): admin-mode SSR Playwright host (permissive AdminOnly + admin doubles)

Adds an adminMode flag to PlaywrightWebApplicationFactory that registers the
no-tenant permissive AdminOnly policy + AddAdminTestDoubles so /admin/* pages
render in the SSR host. Public anonymous axe suite unchanged."
```

---

### Task 3: Half A — admin axe scans

**Files:**
- Modify: `tests/PinballWizard.Web.Tests/A11y/AdminAccessibilityTests.cs`

**Interfaces:**
- Consumes: `AdminPlaywrightFactory` (Task 2).

- [ ] **Step 1: Replace the placeholder test with the axe Theory.** Replace the body of `AdminAccessibilityTests` (keep the `AdminPlaywrightFactory` nested class) with:

```csharp
using Deque.AxeCore.Commons;
using Deque.AxeCore.Playwright;
using Microsoft.Playwright;
using Xunit;

namespace PinballWizard.Web.Tests.A11y;

// WCAG 2.1 AA axe scan for every routable /admin/* page (SSR HTML), mirroring
// the public AccessibilityTests. The render-modes work (ADR-0034) made these
// pages interactive; the design spec requires they stay axe-clean. Admin pages
// render here via the no-tenant permissive AdminOnly policy + AddAdminTestDoubles
// (AdminPlaywrightFactory). Axe runs on DOMContentLoaded (SSR HTML, pre-JS) —
// the same layer the public suite validates.
[Trait("Category", "Accessibility")]
public sealed class AdminAccessibilityTests(AdminAccessibilityTests.AdminPlaywrightFactory factory)
    : IClassFixture<AdminAccessibilityTests.AdminPlaywrightFactory>
{
    public sealed class AdminPlaywrightFactory() : PlaywrightWebApplicationFactory(adminMode: true);

    private static readonly AxeRunOptions Wcag21Aa = new()
    {
        RunOnly = new RunOnlyOptions
        {
            Type = "tag",
            Values = ["wcag2a", "wcag2aa", "wcag21a", "wcag21aa"],
        },
        ResultTypes = [ResultType.Violations],
    };

    [Theory]
    [InlineData("/admin", "dashboard")]
    [InlineData("/admin/sources", "sources")]
    [InlineData("/admin/machines", "machine catalog")]
    [InlineData("/admin/machines/mch_godzilla_pro?mfr=stern", "machine detail")]
    [InlineData("/admin/document-triage", "document triage")]
    [InlineData("/admin/link-overrides", "link overrides")]
    [InlineData("/admin/settings", "settings")]
    public async Task AdminPage_HasNoAxeViolations(string path, string description)
    {
        _ = description;

        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(
            new BrowserTypeLaunchOptions { Headless = true });

        var page = await browser.NewPageAsync();
        await page.GotoAsync(
            $"{factory.ServerAddress}{path}",
            new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });

        AxeResult results = await page.RunAxe(Wcag21Aa);

        var detail = string.Join("\n", results.Violations.Select(v =>
            $"  [{v.Id}] {v.Description}\n" +
            string.Join("", v.Nodes.Take(3).Select(n =>
                $"    Target: {n.Target}\n    HTML:   {n.Html}\n"))));

        Assert.True(
            results.Violations.Length == 0,
            $"axe found {results.Violations.Length} WCAG 2.1 AA violation(s) on {path}:\n{detail}");
    }
}
```

- [ ] **Step 2: Run the admin axe suite**

Run: `dotnet test tests/PinballWizard.Web.Tests --filter "FullyQualifiedName~AdminAccessibilityTests"`
Expected: PASS for all 7 routes. If axe reports a real violation on an admin page, that is a genuine a11y finding — fix the page's markup (e.g. add an `aria-label` to an icon-only control) in this task; do not suppress the rule.

- [ ] **Step 3: Commit**

```bash
git add tests/PinballWizard.Web.Tests/A11y/AdminAccessibilityTests.cs
git commit -m "test(web): WCAG 2.1 AA axe scans for every admin page (Half A, #423)

SSR axe over all seven routable /admin/* routes via the admin-mode host,
mirroring the public AccessibilityTests. Closes the a11y half of #423."
```

---

### Task 4: Half B skeleton — real interactive circuit (DE-RISK GATE)

**Files:**
- Create: `tests/PinballWizard.Web.Tests/Circuit/InteractiveAdminWebApplicationFactory.cs`
- Create: `tests/PinballWizard.Web.Tests/Circuit/AdminCircuitSkeletonTests.cs`

**Interfaces:**
- Consumes: `AddAdminTestDoubles` (Task 1); the real `PinballWizard.Web` app (`Program`/`App`).
- Produces: `InteractiveAdminWebApplicationFactory` exposing `string ServerAddress` (a real Kestrel loopback URL serving the real app with no tenant + admin doubles).

**This task is a gate.** Its goal is to prove a real Blazor circuit runs in the harness. If the recommended approach and the documented fallback both fail to establish a live circuit at acceptable cost, **STOP and report BLOCKED** — do not proceed to Task 5. (Spec §7: the fallback of last resort is a documented manual smoke step.)

- [ ] **Step 1: Write the skeleton circuit test (`AdminCircuitSkeletonTests.cs`)**

```csharp
using Microsoft.Playwright;
using Xunit;

namespace PinballWizard.Web.Tests.Circuit;

// DE-RISK GATE for Half B (#423): proves a REAL Blazor Server circuit runs in
// the in-process harness — the one thing bUnit (always-interactive) and the
// build-time RenderModeConventionTests cannot show. Loads /admin/machines and
// clicks a group-by axis button: on a live circuit the active button flips and
// the grid regroups WITHOUT navigation (pure in-circuit client state). If this
// can't be made to pass, Half B's per-page tests do not get built (see plan
// Task 4 gate + spec §7 fallback).
[Trait("Category", "Circuit")]
public sealed class AdminCircuitSkeletonTests(InteractiveAdminWebApplicationFactory factory)
    : IClassFixture<InteractiveAdminWebApplicationFactory>
{
    [Fact]
    public async Task AdminMachines_GroupByAxisClick_RegroupsInCircuit_NoNavigation()
    {
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
        var page = await browser.NewPageAsync();

        await page.GotoAsync(
            $"{factory.ServerAddress}/admin/machines",
            new() { WaitUntil = WaitUntilState.DOMContentLoaded });

        var selector = page.Locator("[data-testid='groupby-selector']");
        await selector.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 60_000 });

        // Find the "Health" axis button. On a static (dead) render this click does
        // nothing; on a live circuit it becomes mud-button-filled-primary.
        var healthButton = selector.GetByRole(AriaRole.Button, new() { Name = "Health" });

        // Circuit may lag the prerender — retry the click + the state assertion
        // (the WizardE2ETests pattern).
        var active = false;
        for (var attempt = 0; attempt < 20 && !active; attempt++)
        {
            try
            {
                await healthButton.ClickAsync(new() { Timeout = 5_000 });
                await page.Locator("[data-testid='groupby-selector'] button.mud-button-filled-primary")
                    .Filter(new() { HasText = "Health" })
                    .WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 3_000 });
                active = true;
            }
            catch (Exception ex) when (ex is TimeoutException or PlaywrightException)
            {
                await page.WaitForTimeoutAsync(2_000);
            }
        }

        Assert.True(active, "Group-by 'Health' button never became active — admin circuit not interactive.");
        // No navigation: still on /admin/machines.
        Assert.Contains("/admin/machines", page.Url, StringComparison.Ordinal);
    }
}
```

- [ ] **Step 2: Run to verify it fails (no factory yet)**

Run: `dotnet test tests/PinballWizard.Web.Tests --filter "FullyQualifiedName~AdminCircuitSkeletonTests"`
Expected: FAIL — `InteractiveAdminWebApplicationFactory` does not exist (compile error).

- [ ] **Step 3: Implement the factory (recommended approach first).** Create `InteractiveAdminWebApplicationFactory.cs`. The host runs the **real** `PinballWizard.Web` app via `WebApplicationFactory<App>` with `AzureAd:TenantId` unset (no OIDC, permissive `AdminOnly`), overrides the admin backends with `AddAdminTestDoubles`, and binds a **real Kestrel loopback port** (a plain `WebApplicationFactory` uses an in-memory `TestServer` a browser can't reach). Use the real app's `App` component as the entry-point marker.

```csharp
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using PinballWizard.Web.Tests.A11y; // AddAdminTestDoubles
using Xunit;

namespace PinballWizard.Web.Tests.Circuit;

// Runs the REAL PinballWizard.Web app (real Program.cs → real MapStaticAssets →
// live Blazor circuit) with AzureAd:TenantId unset (no OIDC; permissive
// AdminOnly), the admin backends replaced by AddAdminTestDoubles, on a real
// Kestrel loopback port so Playwright can drive it. See spec §5.1.
public sealed class InteractiveAdminWebApplicationFactory
    : WebApplicationFactory<PinballWizard.Web.Components.App>, IAsyncLifetime
{
    private IHost? _kestrelHost;
    public string ServerAddress { get; private set; } = string.Empty;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // Force the no-tenant branch (no OIDC, permissive AdminOnly) and a non-
        // Production environment so dev-only paths are active.
        builder.UseEnvironment("Development");
        builder.UseSetting("AzureAd:TenantId", string.Empty);

        // Replace the admin pages' backends (no Cosmos/Foundry in tests).
        builder.ConfigureTestServices(services => services.AddAdminTestDoubles());
    }

    // WebApplicationFactory's default server is the in-memory TestServer, which a
    // real browser cannot connect to. Start a parallel Kestrel host bound to a
    // loopback port (the PlaywrightWebApplicationFactory pattern), reusing this
    // factory's configured services via CreateHost override.
    async Task IAsyncLifetime.InitializeAsync()
    {
        // Touch Services once so WebApplicationFactory builds its host and applies
        // ConfigureWebHost/ConfigureTestServices, then start a Kestrel-bound host.
        _ = Services;

        var builder = CreateHostBuilderForKestrel();
        _kestrelHost = builder.Build();
        await _kestrelHost.StartAsync();

        var server = _kestrelHost.Services
            .GetRequiredService<Microsoft.AspNetCore.Hosting.Server.IServer>();
        ServerAddress = server.Features
            .Get<Microsoft.AspNetCore.Hosting.Server.Features.IServerAddressesFeature>()!
            .Addresses.First();
    }

    async Task IAsyncLifetime.DisposeAsync()
    {
        if (_kestrelHost is not null)
        {
            await _kestrelHost.StopAsync();
            _kestrelHost.Dispose();
        }
        await base.DisposeAsync();
    }

    // Build a real-Kestrel host that mirrors the factory's configuration. The
    // skeleton task validates this resolves the Web project's static-asset
    // manifest; if it cannot, switch to the out-of-process fallback below.
    private IHostBuilder CreateHostBuilderForKestrel() =>
        Host.CreateDefaultBuilder()
            .ConfigureWebHostDefaults(web =>
            {
                web.UseEnvironment("Development");
                web.UseSetting("AzureAd:TenantId", string.Empty);
                web.UseStaticWebAssets(); // discover PinballWizard.Web.staticwebassets manifest
                web.UseStartup<PinballWizard.Web.Tests.Circuit.AdminCircuitStartupShim>();
                web.UseKestrel(o => o.Listen(System.Net.IPAddress.Loopback, 0));
            });
}
```

> **Implementer note (the spike):** the exact mechanism for "real app + real static
> assets + Kestrel + test-service overrides" is the de-risk. The block above sketches
> the intended shape (`UseStaticWebAssets` + a startup shim that calls the same pipeline
> as `Program.cs` and applies `AddAdminTestDoubles`). If wiring `App` through
> `WebApplicationFactory<App>` while also binding Kestrel proves awkward (the two host
> models don't compose cleanly), **prefer the simpler proven shape**: copy
> `PlaywrightWebApplicationFactory`'s self-built `WebApplication` on Kestrel, but this
> time (a) build the component pipeline exactly as `Program.cs` does **including
> `app.MapStaticAssets()`**, (b) set the host's `WebRootPath`/content root to the built
> `PinballWizard.Web` output so the static-asset manifest resolves, (c) register the
> permissive `AdminOnly` policy + `AddAdminTestDoubles`. That self-built variant avoids
> the `WebApplicationFactory`/Kestrel composition entirely and is the recommended path if
> the above doesn't come together within ~1–2 hours. If neither resolves the manifest,
> use the **out-of-process spawn fallback** (spec §5.1) — `dotnet run` the real Web
> project with `AzureAd__TenantId=""` and the admin backends stubbed via an env/config
> switch, point Playwright at its URL — and report DONE_WITH_CONCERNS noting the pivot.
> If none of these establish a live circuit, report **BLOCKED** (do not proceed to Task 5).

- [ ] **Step 4: Run the skeleton until the circuit proof passes**

Run: `dotnet build src/PinballWizard.Web && dotnet test tests/PinballWizard.Web.Tests --filter "FullyQualifiedName~AdminCircuitSkeletonTests"`
Expected: PASS — the Health axis button becomes active and the URL stays `/admin/machines` (live circuit). (Build the Web project first so its static-asset manifest exists.)

- [ ] **Step 5: Commit**

```bash
git add tests/PinballWizard.Web.Tests/Circuit/InteractiveAdminWebApplicationFactory.cs tests/PinballWizard.Web.Tests/Circuit/AdminCircuitSkeletonTests.cs
git commit -m "test(web): Half B skeleton — real admin Blazor circuit in-process (#423)

Runs the real Web app with AzureAd:TenantId unset (no OIDC, permissive
AdminOnly) + AddAdminTestDoubles on a Kestrel loopback port; Playwright proves
the group-by axis click regroups in a live circuit without navigation. The
de-risk gate for Half B's per-page interactive tests."
```

---

### Task 5: Half B — per-page interactive tests

**Files:**
- Create: `tests/PinballWizard.Web.Tests/Circuit/AdminInteractiveTests.cs`

**Interfaces:**
- Consumes: `InteractiveAdminWebApplicationFactory` (Task 4 — must be green first).

One test per interactive admin page, each exercising its interactivity primitive on the real circuit. All use the same circuit-lag retry helper.

- [ ] **Step 1: Write the per-page tests**

```csharp
using Microsoft.Playwright;
using Xunit;

namespace PinballWizard.Web.Tests.Circuit;

// Per-page real-circuit proofs (#423): each formerly-dead control class
// (OnClick, @bind, dialog, grid sort) exercised on a live admin circuit. Uses
// the InteractiveAdminWebApplicationFactory (real app, no tenant, admin doubles).
[Trait("Category", "Circuit")]
public sealed class AdminInteractiveTests(InteractiveAdminWebApplicationFactory factory)
    : IClassFixture<InteractiveAdminWebApplicationFactory>
{
    private async Task<IBrowser> LaunchAsync()
    {
        var pw = await Playwright.CreateAsync();
        return await pw.Chromium.LaunchAsync(new() { Headless = true });
    }

    // Retry an action until its post-condition holds — the circuit can lag the
    // prerender (WizardE2ETests pattern). Throws if it never succeeds.
    private static async Task UntilAsync(Func<Task> action, IPage page, string failure)
    {
        for (var attempt = 0; attempt < 20; attempt++)
        {
            try { await action(); return; }
            catch (Exception ex) when (ex is TimeoutException or PlaywrightException)
            {
                await page.WaitForTimeoutAsync(2_000);
            }
        }
        throw new Xunit.Sdk.XunitException(failure);
    }

    // ── @bind primitive: AdminSettings numeric field updates bound state ─────
    [Fact]
    public async Task AdminSettings_EditingCeiling_UpdatesBoundValue_AndDirtyHint()
    {
        await using var browser = await LaunchAsync();
        var page = await browser.NewPageAsync();
        await page.GotoAsync($"{factory.ServerAddress}/admin/settings",
            new() { WaitUntil = WaitUntilState.DOMContentLoaded });

        var ceiling = page.Locator("[data-testid='ceiling-input'] input");
        await ceiling.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 60_000 });

        await UntilAsync(async () =>
        {
            await ceiling.FillAsync(""); // clear
            await ceiling.PressSequentiallyAsync("42");
            await ceiling.BlurAsync();
            // @bind round-trips through the circuit → the dirty hint appears.
            await page.Locator("[data-testid='dirty-hint']")
                .WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 3_000 });
        }, page, "Settings @bind never updated dirty state — circuit not interactive.");

        var hint = await page.Locator("[data-testid='dirty-hint']").InnerTextAsync();
        Assert.Contains("unsaved", hint, StringComparison.OrdinalIgnoreCase);
    }

    // ── dialog primitive: AdminLinkOverrides "New Override" opens MudDialog ──
    [Fact]
    public async Task AdminLinkOverrides_NewOverride_OpensDialog()
    {
        await using var browser = await LaunchAsync();
        var page = await browser.NewPageAsync();
        await page.GotoAsync($"{factory.ServerAddress}/admin/link-overrides",
            new() { WaitUntil = WaitUntilState.DOMContentLoaded });

        var newButton = page.GetByRole(AriaRole.Button, new() { Name = "New Override" });
        await newButton.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 60_000 });

        await UntilAsync(async () =>
        {
            await newButton.ClickAsync(new() { Timeout = 5_000 });
            // The MudDialog title appears only when the circuit handles the click.
            await page.GetByText("New Link Override")
                .WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 3_000 });
        }, page, "New Override dialog never opened — circuit not interactive.");
    }

    // ── OnClick primitive: AdminDocumentTriage Re-link resolves the row ──────
    [Fact]
    public async Task AdminDocumentTriage_Relink_ResolvesRow()
    {
        await using var browser = await LaunchAsync();
        var page = await browser.NewPageAsync();
        await page.GotoAsync($"{factory.ServerAddress}/admin/document-triage",
            new() { WaitUntil = WaitUntilState.DOMContentLoaded });

        var relink = page.GetByRole(AriaRole.Button, new() { Name = "Re-link" }).First;
        await relink.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 60_000 });

        await UntilAsync(async () =>
        {
            await relink.ClickAsync(new() { Timeout = 5_000 });
            // Stub linker returns Linked → the row is removed → the empty-state shows.
            await page.Locator("[data-testid='admin-document-triage-empty']")
                .WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 5_000 });
        }, page, "Re-link never resolved the row — circuit not interactive.");
    }

    // ── grid-sort primitive: AdminMachineDetail docs grid sorts on header click
    [Fact]
    public async Task AdminMachineDetail_DocsGrid_SortsOnHeaderClick()
    {
        await using var browser = await LaunchAsync();
        var page = await browser.NewPageAsync();
        await page.GotoAsync($"{factory.ServerAddress}/admin/machines/mch_godzilla_pro?mfr=stern",
            new() { WaitUntil = WaitUntilState.DOMContentLoaded });

        var grid = page.Locator("[data-testid='detail-docs-grid']");
        await grid.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 60_000 });

        // Click the "Type" column header; on a live circuit MudDataGrid applies a
        // sort indicator (aria-sort) to the header cell.
        var typeHeader = grid.GetByText("Type", new() { Exact = true });
        await UntilAsync(async () =>
        {
            await typeHeader.ClickAsync(new() { Timeout = 5_000 });
            // A sorted column header carries a sort direction icon class.
            await grid.Locator(".mud-table-sort-label-active, [aria-sort]")
                .First.WaitForAsync(new() { State = WaitForSelectorState.Attached, Timeout = 3_000 });
        }, page, "Docs grid never applied a sort — circuit not interactive.");
    }

    // ── OnClick primitive (Machines): covered by AdminCircuitSkeletonTests ───
}
```

- [ ] **Step 2: Run the per-page suite**

Run: `dotnet build src/PinballWizard.Web && dotnet test tests/PinballWizard.Web.Tests --filter "FullyQualifiedName~AdminInteractiveTests"`
Expected: PASS (4/4). If a selector doesn't resolve (a `data-testid` differs from the page), correct the selector against the actual page markup — do not weaken the assertion. If a primitive genuinely doesn't respond on a live circuit, that is a real render-mode bug — surface it (it is exactly what this suite exists to catch).

- [ ] **Step 3: Commit**

```bash
git add tests/PinballWizard.Web.Tests/Circuit/AdminInteractiveTests.cs
git commit -m "test(web): per-page admin real-circuit interactive proofs (Half B, #423)

Exercises each formerly-dead control class on a live admin circuit: @bind
(Settings), dialog (LinkOverrides), OnClick (Triage), grid sort (MachineDetail).
The OnClick(Machines) primitive is covered by the skeleton test. Closes the
real-circuit half of #423."
```

---

### Task 6: CI wiring

**Files:**
- Modify: `.github/workflows/ci.yml` (the "UI tests (axe-core + responsive snapshots, Playwright)" job)

**Interfaces:**
- Consumes: all prior tasks' test classes.

The new `Category=Accessibility` (admin axe) and `Category=Circuit` tests are deterministic and Azure-free, so they run in the existing UI-tests job. Half B needs the Web project's static-asset manifest, so the job must build `src/PinballWizard.Web` before running.

- [ ] **Step 1: Inspect the current UI-tests job**

Run: `grep -n "UI tests\|Playwright\|dotnet test\|Accessibility\|publish\|dotnet build" .github/workflows/ci.yml`
Expected: locate the job that runs the Playwright/axe tests and how it builds.

- [ ] **Step 2: Ensure the Web project is built and the new categories run.** In the UI-tests job, before the `dotnet test` step that runs the Playwright suites, ensure a build of the Web project exists (the static-asset manifest is produced by building `src/PinballWizard.Web`). If the job already does a full `dotnet build PinballWizard.slnx`, no new build step is needed — the manifest is in the Web project's output. If the test step uses a `--filter`, confirm it includes `Category=Accessibility` and add `Category=Circuit` (or removes the filter so all non-E2E tests run). Concretely, the test step's filter should select the UI suites without excluding the new ones, e.g.:

```yaml
        run: |
          dotnet test tests/PinballWizard.Web.Tests/PinballWizard.Web.Tests.csproj \
            --no-build --configuration Release \
            --filter "Category=Accessibility|Category=Circuit|Category=Snapshot" \
            --logger "trx;LogFileName=ui-tests.trx"
```

(Match the existing job's category names; the load-bearing change is that `Category=Circuit` is included and the Web project is built so the manifest exists. `Category=E2E` stays excluded — those need a live stack.)

- [ ] **Step 3: Validate the workflow file parses**

Run: `python -c "import yaml,sys; yaml.safe_load(open('.github/workflows/ci.yml')); print('ok')"`
Expected: `ok`.

- [ ] **Step 4: Commit**

```bash
git add .github/workflows/ci.yml
git commit -m "ci: run admin axe + circuit suites in the UI-tests job (#423)

Includes Category=Circuit (and admin Category=Accessibility) in the Playwright
UI-tests job; ensures src/PinballWizard.Web is built so the static-asset
manifest the circuit host needs exists. E2E stays excluded (needs a live stack)."
```

---

## Self-Review (against the spec)

- **Spec §2 / Half A (admin axe):** Tasks 2 (admin-mode host) + 3 (axe Theory over all 7 routes). ✅
- **Spec §2 / Half B (real circuit):** Task 4 (skeleton gate) + Task 5 (per-page primitives). ✅
- **Spec §3.1 permissive AdminOnly (no test-auth handler):** Task 2 (SSR) + Task 4 (`AzureAd:TenantId` unset → no-tenant branch). ✅
- **Spec §3.2 AddAdminTestDoubles shared fixture:** Task 1, reused by Tasks 2 and 4. ✅
- **Spec §5.1 skeleton-first de-risk + fallbacks:** Task 4 is an explicit gate with the recommended/self-built/out-of-process/BLOCKED ladder. ✅
- **Spec §5.2 broad per-page coverage (each primitive):** Task 5 covers @bind, dialog, OnClick, grid-sort; OnClick(Machines) in the skeleton. ✅
- **Spec §6 CI (both in CI, Web build step):** Task 6. ✅
- **Spec §8 non-goals:** no Cloudflare/Entra/Azure/standing-admin; no post-render axe; no page changes (except a genuine a11y fix if axe finds one, per Task 3 Step 2). ✅
- **Placeholder scan:** the one deliberate open item is Task 4's mechanism (a genuine spike with a documented decision ladder, not a hand-wave) — every other step has concrete code/commands.
- **Type consistency:** `AddAdminTestDoubles`, `InteractiveAdminWebApplicationFactory.ServerAddress`, the seed ids (`mch_godzilla_pro`/`mch_godzilla_le`/`stern`), and `data-testid` values (`groupby-selector`, `ceiling-input`, `dirty-hint`, `admin-document-triage-empty`, `detail-docs-grid`) are used consistently and match the live page markup read during design.
