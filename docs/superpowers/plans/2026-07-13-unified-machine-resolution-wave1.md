# Unified Machine Resolution — Plan 1: Contract + Wave 1

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Land the frozen resolution contract (S0), then build the six independent Wave-1 streams in parallel, so the six consumer migrations (Plan 2) can proceed against a real resolver.

**Architecture:** One normalizer + one variant generator + one evidence-aware resolver in `PinballWizard.Application.Resolution`. Canonical OPDB identity becomes the join key; `Machine.ManufacturerSlugs` is demoted from sole join key to one evidence source. Curated aliases are versioned seed data. Ambiguity is never guessed — it becomes `needs_review`.

**Tech Stack:** .NET 10, xUnit, Cosmos data-plane SDK, MudBlazor (via `Components/Shared/` wrappers), Azure AI Search.

**Spec:** [2026-07-13-unified-machine-resolution-design.md](../specs/2026-07-13-unified-machine-resolution-design.md) · **ADR:** [ADR-0054](../../adr/0054-unified-machine-resolution.md)

## Global Constraints

- **Provenance is sacred.** No path may drop `Source` / `DiscoveryUrl` / `DiscoveryContext` / `GameSlug`. A document attributed to the *wrong* machine is worse than one honestly unattributed.
- **Never guess on ambiguity.** Multiple non-family candidates → `needs_review`, never an auto-pick.
- **Fixtures must be captured from live sources**, never hand-authored (#758). Any fixture directory carries a `CAPTURE.md` recording source URL + capture date.
- **No XML doc comments** on public surface (repo convention). Use `//` comments explaining WHY.
- **Tests assert behavior, not structure.** A test named "deduplicates" must have a fixture where dedup actually fires.
- Commit identity: `Jim Keeley <94459922+jkeeley2073@users.noreply.github.com>`. **No Claude attribution trailer.**
- Never work on `main` — one git worktree per stream (`.worktrees/<branch>`).
- Every task names its **gate**. A task is not done until its gate passes.
- Full CI-equivalent suite before push: `dotnet test PinballWizard.slnx --filter "Category!=Accessibility&Category!=Snapshots&Category!=Circuit&Category!=E2E"`.

## Dependency graph

```text
S0 (contract) ── serial, must merge first
   ├── S1 variants + index + resolver      ─┐
   ├── S2 upsert semantics (#762)           │
   ├── S3 golden-set + parity capture       ├─ parallel, independent worktrees
   ├── S4 AP classification                 │
   ├── S5 alias seed + loader               │
   └── S6 needs_review + admin queue       ─┘
                                             ↓
                                   Plan 2 (Wave 2 migrations)
```

S1 is the critical path. S2–S6 do not depend on S1 and may run fully concurrently.

## File structure

| File | Responsibility | Stream |
| --- | --- | --- |
| `src/PinballWizard.Application/Resolution/MachineTextNormalizer.cs` | The single normalizer | S0 |
| `src/PinballWizard.Application/Resolution/ResolutionContracts.cs` | `EvidenceKind`, `VariantKind`, `MachineVariant`, `ResolutionQuery`, `ResolutionResult`, `IMachineResolver` | S0 |
| `src/PinballWizard.Application/Resolution/MachineAliasSeed.cs` | Seed record shapes | S0 |
| `src/PinballWizard.Application/Resolution/MachineIdentityVariants.cs` | `Machine` + aliases → variants | S1 |
| `src/PinballWizard.Application/Resolution/InMemoryMachineIndex.cs` | Batch index over variants | S1 |
| `src/PinballWizard.Application/Resolution/MachineResolver.cs` | Evidence-aware policy pipeline | S1 |
| `src/PinballWizard.Infrastructure/Persistence/Cosmos/CosmosRawDocumentRepository.cs` | Upsert field-block split | S2 |
| `tools/golden-set/` + `src/PinballWizard.Cli/Program.cs` (`--capture-golden-set`) | Regression snapshots | S3 |
| `src/PinballWizard.Application/Documents/DocumentClassifier.cs` | AP classification rules | S4 |
| `data/seeds/machine_aliases.v1.json` + `.../Resolution/MachineAliasLoader.cs` | Curated aliases | S5 |
| `src/PinballWizard.Core/Models/DocumentRecord.cs` (+ `link_review`), `src/PinballWizard.Web/Components/Pages/Admin/LinkReview.razor` | needs_review + queue | S6 |

---

## S0 — Contract PR (SERIAL — nothing else starts until this merges)

**Branch:** `feat/resolution-contract`
**Gate:** Normalizer golden tests pass; full CI suite green. No consumer touched.
**Why serial:** every other stream codes against these types. Freezing them first is what makes the parallelism safe.

### Task 1: The single normalizer

**Files:**

- Create: `src/PinballWizard.Application/Resolution/MachineTextNormalizer.cs`
- Test: `tests/PinballWizard.Application.Tests/Resolution/MachineTextNormalizerTests.cs`

**Interfaces:**

- Produces: `MachineTextNormalizer.Tokenize(string?) → IReadOnlyList<string>`, `MachineTextNormalizer.Key(string?) → string` (tokens joined by single space). Every later task uses these and **no other normalizer**.

- [ ] **Step 1: Write the failing golden tests**

Each case below is drawn from a real string the existing five normalizers had to handle. `Hotwheels` vs `HotWheels` vs `Hot-Wheels` is the case that motivates the whole exercise — all three appear in AP's real filenames.

```csharp
using PinballWizard.Application.Resolution;

namespace PinballWizard.Application.Tests.Resolution;

public class MachineTextNormalizerTests
{
    [Theory]
    // separators collapse to a single space
    [InlineData("Hot-Wheels", "hot wheels")]
    [InlineData("Hot_Wheels", "hot wheels")]
    [InlineData("Hot--Wheels", "hot wheels")]
    // camelCase splits; an already-joined word does NOT
    [InlineData("HotWheels", "hot wheels")]
    [InlineData("Hotwheels", "hotwheels")]
    // subtitle punctuation
    [InlineData("Houdini: Master of Mystery", "houdini master of mystery")]
    // ampersand folds to "and" — this is the divergence the &/and retry loop existed to bridge
    [InlineData("Bally & Williams", "bally and williams")]
    [InlineData("Bally and Williams", "bally and williams")]
    // apostrophes vanish rather than splitting
    [InlineData("Guns N' Roses", "guns n roses")]
    [InlineData("Barry O's Barbeque Challenge", "barry os barbeque challenge")]
    // slashes are separators
    [InlineData("AC/DC", "ac dc")]
    // diacritics fold
    [InlineData("Café", "cafe")]
    // digit/letter boundaries
    [InlineData("DOC0018-00-REV-A", "doc 0018 00 rev a")]
    // real AP filenames
    [InlineData("GTF-Quick-Reference-Guide", "gtf quick reference guide")]
    [InlineData("API-Houdini-Service-Manual-10-6-21", "api houdini service manual 10 6 21")]
    public void Key_NormalizesToCanonicalForm(string input, string expected)
        => Assert.Equal(expected, MachineTextNormalizer.Key(input));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("---")]
    public void Tokenize_EmptyOrSeparatorOnly_ReturnsEmpty(string? input)
        => Assert.Empty(MachineTextNormalizer.Tokenize(input));

    [Fact]
    public void Tokenize_ReturnsTokens_NotAJoinedString()
        => Assert.Equal(new[] { "hot", "wheels" }, MachineTextNormalizer.Tokenize("Hot-Wheels"));
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/PinballWizard.Application.Tests --filter "FullyQualifiedName~MachineTextNormalizer"`
Expected: FAIL — `MachineTextNormalizer` does not exist (CS0103).

- [ ] **Step 3: Implement**

Order matters: fold diacritics → insert boundaries (needs original casing) → lowercase during emit.

```csharp
using System.Globalization;
using System.Text;

namespace PinballWizard.Application.Resolution;

// The single normalizer for every text→machine match in the system (ADR-0054).
// It replaces five divergent normalizers. The &/and retry loop in MachineGroundingTool
// existed solely to bridge two of them — folding '&' to "and" here deletes that hack.
public static class MachineTextNormalizer
{
    public static string Key(string? text) => string.Join(' ', Tokenize(text));

    public static IReadOnlyList<string> Tokenize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return [];

        var bounded = InsertTokenBoundaries(FoldDiacritics(text));

        var sb = new StringBuilder(bounded.Length + 8);
        foreach (var c in bounded)
        {
            // Apostrophes vanish rather than splitting: "Barry O's" → "barry os", so the
            // token survives as one word instead of degrading into a stray "s".
            if (c is '\'' or '’') continue;
            if (c == '&') { sb.Append(" and "); continue; }
            if (char.IsLetterOrDigit(c)) { sb.Append(char.ToLowerInvariant(c)); continue; }
            sb.Append(' ');
        }

        return sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    private static string FoldDiacritics(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var c in s.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark) sb.Append(c);
        }
        return sb.ToString().Normalize(NormalizationForm.FormC);
    }

    // Splits camelCase and letter/digit runs so "HotWheels" → "Hot Wheels" while the
    // already-joined "Hotwheels" is left alone (both forms occur in real AP filenames).
    private static string InsertTokenBoundaries(string s)
    {
        var sb = new StringBuilder(s.Length + 8);
        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            if (i > 0)
            {
                var p = s[i - 1];
                var boundary =
                    (char.IsLower(p) && char.IsUpper(c)) ||
                    (char.IsLetter(p) && char.IsDigit(c)) ||
                    (char.IsDigit(p) && char.IsLetter(c)) ||
                    (i + 1 < s.Length && char.IsUpper(p) && char.IsUpper(c) && char.IsLower(s[i + 1]));
                if (boundary) sb.Append(' ');
            }
            sb.Append(c);
        }
        return sb.ToString();
    }
}
```

- [ ] **Step 4: Run to verify it passes**

Run: `dotnet test tests/PinballWizard.Application.Tests --filter "FullyQualifiedName~MachineTextNormalizer"`
Expected: PASS — all theory cases green.

- [ ] **Step 5: Commit**

```bash
git add src/PinballWizard.Application/Resolution/MachineTextNormalizer.cs \
        tests/PinballWizard.Application.Tests/Resolution/MachineTextNormalizerTests.cs
git commit -m "feat(resolution) single machine-text normalizer (ADR-0054)"
```

### Task 2: Resolution contracts

**Files:**

- Create: `src/PinballWizard.Application/Resolution/ResolutionContracts.cs`
- Create: `src/PinballWizard.Application/Resolution/MachineAliasSeed.cs`
- Test: `tests/PinballWizard.Application.Tests/Resolution/ResolutionContractsTests.cs`

**Interfaces:**

- Consumes: `MachineTextNormalizer` (Task 1).
- Produces: the exact types every other stream codes against. **Do not rename these later** — S1–S6 and Plan 2 all bind to them.

- [ ] **Step 1: Write the failing test**

The contract's one behavior worth asserting is that a `MachineVariant` is always constructed through the normalizer, so no consumer can smuggle in an un-normalized key.

```csharp
using PinballWizard.Application.Resolution;

namespace PinballWizard.Application.Tests.Resolution;

public class ResolutionContractsTests
{
    [Fact]
    public void MachineVariant_Create_NormalizesTheKey()
    {
        var v = MachineVariant.Create("Hot-Wheels", VariantKind.CuratedAlias,
            machineId: "GRxyz-M1", manufacturerKey: "americanpinball", groupId: "GRxyz");

        Assert.Equal("hot wheels", v.Key);
        Assert.Equal(new[] { "hot", "wheels" }, v.Tokens);
        Assert.Equal(VariantKind.CuratedAlias, v.Kind);
    }

    [Fact]
    public void MachineVariant_Create_SingleTokenVariant_IsFlagged()
    {
        // Single-token variants are eligible for EXACT evidence only — this flag is what
        // the resolver checks instead of excluding them from the index (the 1977 Stern
        // "Pinball" once matched 172 documents).
        var v = MachineVariant.Create("Pinball", VariantKind.FullTitle, "G1-M1", "stern", "G1");
        Assert.True(v.IsSingleToken);

        var multi = MachineVariant.Create("Hot Wheels", VariantKind.FullTitle, "G2-M1", "americanpinball", "G2");
        Assert.False(multi.IsSingleToken);
    }

    [Fact]
    public void MachineVariant_Create_EmptyText_Throws()
        => Assert.ThrowsAny<ArgumentException>(() =>
            MachineVariant.Create("---", VariantKind.FullTitle, "G1-M1", "stern", "G1"));
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/PinballWizard.Application.Tests --filter "FullyQualifiedName~ResolutionContracts"`
Expected: FAIL — types do not exist.

- [ ] **Step 3: Implement the contracts**

```csharp
namespace PinballWizard.Application.Resolution;

// What KIND of text we are matching. The resolver's manufacturer scoping and
// single-token eligibility both key off this (ADR-0054).
public enum EvidenceKind
{
    ProvenanceSlug,  // raw.Game.Slug — the scraper's own claim; high trust
    Filename,        // fuzzy: a filename may mention any machine
    PageText,        // fuzzy: page-1/2 extracted text
    ScrapedTitle,    // a scraped GameRecord title (reconciler)
    FreeText,        // a user/agent query (getMachineByTitle)
}

// Where a matchable variant came from.
public enum VariantKind
{
    FullTitle,
    FranchiseTitle,        // subtitle stripped: "Houdini: Master of Mystery" → "houdini"
    TitleWithEdition,
    ManufacturerPrefixed,
    ScraperSlug,           // Machine.ManufacturerSlugs — now ONE evidence source, not the join key
    CuratedAlias,          // machine_aliases.v1.json
}

public enum ResolutionStage { Exact, FranchisePrefix, Containment, None }

public sealed record MachineVariant
{
    private MachineVariant(string key, IReadOnlyList<string> tokens, VariantKind kind,
        string machineId, string manufacturerKey, string? groupId)
    {
        Key = key; Tokens = tokens; Kind = kind;
        MachineId = machineId; ManufacturerKey = manufacturerKey; GroupId = groupId;
    }

    public string Key { get; }
    public IReadOnlyList<string> Tokens { get; }
    public VariantKind Kind { get; }
    public string MachineId { get; }
    public string ManufacturerKey { get; }
    public string? GroupId { get; }

    public bool IsSingleToken => Tokens.Count == 1;

    // The ONLY way to build a variant — guarantees every key went through the one normalizer.
    public static MachineVariant Create(string text, VariantKind kind,
        string machineId, string manufacturerKey, string? groupId)
    {
        var tokens = MachineTextNormalizer.Tokenize(text);
        if (tokens.Count == 0)
            throw new ArgumentException($"Text '{text}' normalizes to zero tokens.", nameof(text));
        return new MachineVariant(string.Join(' ', tokens), tokens, kind, machineId, manufacturerKey, groupId);
    }
}

public sealed record ResolutionQuery(string Text, EvidenceKind EvidenceKind, string? ManufacturerHint = null);

public sealed record ResolutionEvidence(
    EvidenceKind EvidenceKind, VariantKind VariantKind, string MatchedVariant, ResolutionStage Stage);

public sealed record ResolutionCandidate(
    string MachineId, string MachineTitle, VariantKind VariantKind, string MatchedVariant);

public abstract record ResolutionResult
{
    private ResolutionResult() { }

    public sealed record Resolved(string MachineId, ResolutionEvidence Evidence) : ResolutionResult;

    // One edition family (single distinct GroupId) — all siblings are legitimate targets.
    public sealed record ResolvedFamily(
        string GroupId, IReadOnlyList<string> MachineIds, ResolutionEvidence Evidence) : ResolutionResult;

    // Multiple non-family candidates. The resolver NEVER picks one — this becomes needs_review.
    public sealed record Ambiguous(
        IReadOnlyList<ResolutionCandidate> Candidates, ResolutionEvidence Evidence) : ResolutionResult;

    public sealed record NoMatch : ResolutionResult;
}

public interface IMachineResolver
{
    ResolutionResult Resolve(ResolutionQuery query);
}
```

```csharp
namespace PinballWizard.Application.Resolution;

// Shape of data/seeds/machine_aliases.v1.json. Human-curated entries ONLY — machine-derived
// variants (scraper slugs, OPDB edition/manufacturer tokens) flow automatically and are never seeded.
public sealed record MachineAliasSeedFile(int Version, IReadOnlyList<MachineAliasEntry> Aliases);

public sealed record MachineAliasEntry(
    string Alias,
    string? OpdbGroupId,   // preferred: alias resolves to the whole edition family
    string? MachineId,     // only for edition-specific aliases
    string ManufacturerKey,
    string? Notes,
    string? AddedBy);
```

- [ ] **Step 4: Run to verify it passes**

Run: `dotnet test tests/PinballWizard.Application.Tests --filter "FullyQualifiedName~ResolutionContracts"`
Expected: PASS.

- [ ] **Step 5: Run the full suite and commit**

Run: `dotnet test PinballWizard.slnx --filter "Category!=Accessibility&Category!=Snapshots&Category!=Circuit&Category!=E2E"`
Expected: PASS, 0 failures (no consumer touched yet).

```bash
git add src/PinballWizard.Application/Resolution/ tests/PinballWizard.Application.Tests/Resolution/
git commit -m "feat(resolution) freeze resolution contracts + alias seed schema (ADR-0054)"
```

**S0 exit criteria:** merged to `main`. **Announce the merge — Wave 1 starts only then.**

---

## Wave 1 — six parallel streams (each its own worktree + PR)

Every stream branches from `main` **after S0 merges**. They touch disjoint files and may be executed concurrently.

## S1 — Variants + index + resolver (CRITICAL PATH)

**Branch:** `feat/resolution-core` · **Gate:** resolver policy tests (incl. the single-word guard) + full CI suite.

### Task 3: `MachineIdentityVariants`

**Files:**

- Create: `src/PinballWizard.Application/Resolution/MachineIdentityVariants.cs`
- Test: `tests/PinballWizard.Application.Tests/Resolution/MachineIdentityVariantsTests.cs`

**Interfaces:**

- Consumes: `MachineVariant`, `VariantKind` (S0); `Machine` (`src/PinballWizard.Core/Domain/Machine.cs` — fields `Id`, `PartitionKey`, `Title`, `GroupId`, `Year`, `ManufacturerSlugs`, `EditionTokens`).
- Produces: `MachineIdentityVariants.For(Machine machine, IReadOnlyList<MachineAliasEntry> aliases) → IReadOnlyList<MachineVariant>` and `MachineIdentityVariants.StripTrailingQualifiers(IReadOnlyList<string> tokens) → IReadOnlyList<string>`.

- [ ] **Step 1: Write the failing tests**

```csharp
using PinballWizard.Application.Resolution;
using PinballWizard.Core.Domain;

namespace PinballWizard.Application.Tests.Resolution;

public class MachineIdentityVariantsTests
{
    private static Machine Ap(string id, string title, string group) => new()
    {
        Id = id, Title = title, GroupId = group, PartitionKey = "americanpinball", Year = 2017,
    };

    [Fact]
    public void For_ProducesFranchiseTitle_StrippingSubtitle()
    {
        // The whole AP gap: filenames say "Houdini", the catalog says "Houdini: Master of Mystery".
        var vs = MachineIdentityVariants.For(Ap("GH-M1", "Houdini: Master of Mystery", "GH"), []);

        Assert.Contains(vs, v => v.Kind == VariantKind.FullTitle && v.Key == "houdini master of mystery");
        Assert.Contains(vs, v => v.Kind == VariantKind.FranchiseTitle && v.Key == "houdini");
    }

    [Fact]
    public void For_StripsTrailingQualifiers()
    {
        // Generalizes PR #750 (which fixed this only in the reconciler).
        var vs = MachineIdentityVariants.For(Ap("GM-M1", "Medieval Madness Merlin Edition Pinball", "GM"), []);
        Assert.Contains(vs, v => v.Kind == VariantKind.FranchiseTitle && v.Key == "medieval madness");
    }

    [Fact]
    public void For_IncludesScraperSlugs_AsOneEvidenceSourceAmongSeveral()
    {
        var m = Ap("GH-M1", "Houdini: Master of Mystery", "GH");
        m.ManufacturerSlugs["americanpinball"] = "houdini";

        var vs = MachineIdentityVariants.For(m, []);
        Assert.Contains(vs, v => v.Kind == VariantKind.ScraperSlug && v.Key == "houdini");
    }

    [Fact]
    public void For_IncludesCuratedAliases_ScopedToManufacturerAndGroup()
    {
        var m = Ap("GTFx-M1", "Galactic Tank Force", "GTFx");
        var aliases = new List<MachineAliasEntry>
        {
            new("GTF", "GTFx", null, "americanpinball", "AP filename abbreviation", "jkeeley2073"),
            new("GTF", "OTHER", null, "stern", "must NOT apply — different manufacturer", "x"),
        };

        var vs = MachineIdentityVariants.For(m, aliases);
        Assert.Single(vs, v => v.Kind == VariantKind.CuratedAlias && v.Key == "gtf");
    }

    [Fact]
    public void For_IncludesEditionAndManufacturerForms()
    {
        var m = new Machine
        {
            Id = "GZ-M1", Title = "Godzilla", GroupId = "GZ", PartitionKey = "stern", Year = 2021,
            EditionTokens = ["pro"],
        };

        var vs = MachineIdentityVariants.For(m, []);
        Assert.Contains(vs, v => v.Kind == VariantKind.TitleWithEdition && v.Key == "godzilla pro");
        Assert.Contains(vs, v => v.Kind == VariantKind.ManufacturerPrefixed && v.Key == "stern godzilla");
    }

    [Fact]
    public void For_NeverEmitsAnEmptyVariant()
    {
        var vs = MachineIdentityVariants.For(Ap("G-M1", "Pinball", "G"), []);
        Assert.All(vs, v => Assert.NotEmpty(v.Tokens));
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/PinballWizard.Application.Tests --filter "FullyQualifiedName~MachineIdentityVariants"`
Expected: FAIL — `MachineIdentityVariants` does not exist.

- [ ] **Step 3: Implement**

`TrailingQualifiers` is the vocabulary from `ScraperReconciliationService.DecorationWords` (`src/PinballWizard.Application/Sync/ScraperReconciliationService.cs:418`) — **read that list and copy it verbatim**; do not retype from memory. Ownership moves here.

```csharp
using PinballWizard.Core.Domain;

namespace PinballWizard.Application.Resolution;

// Derives every matchable variant of a machine from its CANONICAL catalog identity.
// This is the heart of ADR-0054: identity (title/manufacturer/group) is the join key;
// ManufacturerSlugs is demoted to one evidence source among several.
// The same generator feeds both the batch index and machine_title_lookups, so the
// two stores cannot diverge again.
public static class MachineIdentityVariants
{
    // Copied verbatim from ScraperReconciliationService.DecorationWords (ownership moves here).
    // Longest-first so compound qualifiers are consumed before their fragments.
    //
    // internal, not private: MachineResolver (Task 4) initialises its own lookup FROM
    // this array. Task 4's snippet references TrailingQualifiers directly, so the list
    // must exist exactly once — do not let the resolver declare a second copy kept in
    // step by a comment. "pinball" here is the guard that stopped the 1977 Stern
    // machine from claiming 172 documents.
    internal static readonly string[] TrailingQualifiers =
    [
        "merlinedition", "vaultedition", "limitededition", "standardedition",
        "remake", "pinball", "gamekit", "deposit", "edition",
    ];

    public static IReadOnlyList<MachineVariant> For(Machine machine, IReadOnlyList<MachineAliasEntry> aliases)
    {
        ArgumentNullException.ThrowIfNull(machine);
        ArgumentNullException.ThrowIfNull(aliases);

        var mfr = machine.PartitionKey;
        var variants = new List<MachineVariant>(8);
        var seen = new HashSet<string>(StringComparer.Ordinal);

        void Add(string? text, VariantKind kind)
        {
            if (string.IsNullOrWhiteSpace(text)) return;
            if (MachineTextNormalizer.Tokenize(text).Count == 0) return;
            var v = MachineVariant.Create(text, kind, machine.Id, mfr, machine.GroupId);
            if (seen.Add($"{v.Key}|{kind}")) variants.Add(v);
        }

        Add(machine.Title, VariantKind.FullTitle);

        var franchise = FranchiseTitle(machine.Title);
        if (!string.IsNullOrWhiteSpace(franchise)) Add(franchise, VariantKind.FranchiseTitle);

        foreach (var token in machine.EditionTokens ?? [])
            Add($"{machine.Title} {token}", VariantKind.TitleWithEdition);

        foreach (var mfrToken in OpdbMachineMapper.GetMatchTokens(mfr))
            Add($"{mfrToken} {machine.Title}", VariantKind.ManufacturerPrefixed);

        foreach (var slug in (machine.ManufacturerSlugs ?? []).Values)
            Add(slug, VariantKind.ScraperSlug);

        foreach (var a in aliases)
        {
            if (!string.Equals(a.ManufacturerKey, mfr, StringComparison.OrdinalIgnoreCase)) continue;
            var appliesToGroup = a.OpdbGroupId is not null
                && string.Equals(a.OpdbGroupId, machine.GroupId, StringComparison.OrdinalIgnoreCase);
            var appliesToMachine = a.MachineId is not null
                && string.Equals(a.MachineId, machine.Id, StringComparison.OrdinalIgnoreCase);
            if (appliesToGroup || appliesToMachine) Add(a.Alias, VariantKind.CuratedAlias);
        }

        return variants;
    }

    // "Houdini: Master of Mystery" → "houdini"; "Medieval Madness Merlin Edition Pinball" → "medieval madness"
    private static string FranchiseTitle(string? title)
    {
        if (string.IsNullOrWhiteSpace(title)) return string.Empty;

        var head = title;
        foreach (var sep in new[] { ": ", " - " })
        {
            var i = head.IndexOf(sep, StringComparison.Ordinal);
            if (i > 0) head = head[..i];
        }

        return string.Join(' ', StripTrailingQualifiers(MachineTextNormalizer.Tokenize(head)));
    }

    // Consumes trailing qualifier tokens right-to-left. Never strips the last remaining token.
    // CORRECTED during S1 implementation — the original snippet checked ONLY
    // single tokens:
    //
    //     foreach (var q in TrailingQualifiers)
    //         if (work.Count > 1 && string.Equals(work[^1], q, ...)) { remove; }
    //
    // Under that version the compound entries in TrailingQualifiers
    // ("merlinedition", "vaultedition", "limitededition", "standardedition")
    // are DEAD: they can never equal a single token, because the tokenizer has
    // already split "Merlin Edition" into ["merlin", "edition"]. They were
    // carried over from ScraperReconciliationService, which matches against a
    // pre-concatenated string where "merlinedition" IS a substring — the
    // tokenized form here needs an adjacent-pair join to see it.
    //
    // Consequence: "Medieval Madness Merlin Edition Pinball" would strip only
    // "pinball" and stop, since "edition" IS a single-token entry but "merlin"
    // is not — leaving "medieval madness merlin" rather than the intended
    // "medieval madness". For_StripsTrailingQualifiers pins the correct result.
    //
    // Compound is checked BEFORE single-token (longest match first, same
    // principle as the reconciler's ordering), and requires >2 tokens so the
    // one-token floor is never breached.
    public static IReadOnlyList<string> StripTrailingQualifiers(IReadOnlyList<string> tokens)
    {
        var work = tokens.ToList();
        var changed = true;
        while (changed && work.Count > 1)
        {
            changed = false;

            if (work.Count > 2)
            {
                var compound = work[^2] + work[^1];
                foreach (var q in TrailingQualifiers)
                {
                    if (string.Equals(compound, q, StringComparison.Ordinal))
                    {
                        work.RemoveAt(work.Count - 1);
                        work.RemoveAt(work.Count - 1);
                        changed = true;
                        break;
                    }
                }
            }

            if (!changed)
            {
                foreach (var q in TrailingQualifiers)
                {
                    if (work.Count > 1 && string.Equals(work[^1], q, StringComparison.Ordinal))
                    {
                        work.RemoveAt(work.Count - 1);
                        changed = true;
                        break;
                    }
                }
            }
        }
        return work;
    }
}
```

Note: `OpdbMachineMapper.GetMatchTokens` lives in Infrastructure. **Before writing this file, check whether Application may reference it** (Clean Architecture: Application must not depend on Infrastructure). If it may not, move `GetMatchTokens` to a small Application-layer `ManufacturerMatchTokens` helper as part of this task and update its callers — do not add an Infrastructure reference to Application.

- [ ] **Step 4: Run to verify it passes**

Run: `dotnet test tests/PinballWizard.Application.Tests --filter "FullyQualifiedName~MachineIdentityVariants"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/PinballWizard.Application/Resolution/MachineIdentityVariants.cs \
        tests/PinballWizard.Application.Tests/Resolution/MachineIdentityVariantsTests.cs
git commit -m "feat(resolution) derive machine variants from canonical catalog identity (ADR-0054)"
```

### Task 4: `InMemoryMachineIndex` + `MachineResolver`

**Files:**

- Create: `src/PinballWizard.Application/Resolution/InMemoryMachineIndex.cs`
- Create: `src/PinballWizard.Application/Resolution/MachineResolver.cs`
- Test: `tests/PinballWizard.Application.Tests/Resolution/MachineResolverTests.cs`

**Interfaces:**

- Consumes: `MachineIdentityVariants` (S1.1); `IMachineResolver`, `ResolutionQuery/Result/Evidence/Candidate`, `EvidenceKind`, `VariantKind`, `ResolutionStage` (S0).
- Produces: `InMemoryMachineIndex.Build(IEnumerable<Machine>, IReadOnlyList<MachineAliasEntry>) → InMemoryMachineIndex`; `MachineResolver(InMemoryMachineIndex, IReadOnlyDictionary<string,Machine>) : IMachineResolver`. Plan 2's `DocumentLinker` migration consumes exactly these.

- [ ] **Step 1: Write the failing tests**

These encode the whole policy. The single-word guard test is the one that protects the 172-document incident.

```csharp
using PinballWizard.Application.Resolution;
using PinballWizard.Core.Domain;

namespace PinballWizard.Application.Tests.Resolution;

public class MachineResolverTests
{
    private static Machine M(string id, string title, string group, string mfr, int year = 2020) =>
        new() { Id = id, Title = title, GroupId = group, PartitionKey = mfr, Year = year };

    private static readonly Machine Houdini   = M("GH-M1", "Houdini: Master of Mystery", "GH", "americanpinball", 2017);
    private static readonly Machine HotWheels = M("GW-M1", "Hot Wheels", "GW", "americanpinball");
    private static readonly Machine GTF       = M("GT-M1", "Galactic Tank Force", "GT", "americanpinball", 2023);
    private static readonly Machine SternPin  = M("GP-M1", "Pinball", "GP", "stern", 1977);
    private static readonly Machine GodzPro   = M("GZ-M1", "Godzilla", "GZ", "stern", 2021);
    private static readonly Machine GodzPrem  = M("GZ-M2", "Godzilla", "GZ", "stern", 2021);

    private static readonly MachineAliasEntry[] Aliases =
    [
        new("GTF", "GT", null, "americanpinball", "AP filename abbreviation", "jkeeley2073"),
    ];

    private static MachineResolver Build(params Machine[] machines)
    {
        var index = InMemoryMachineIndex.Build(machines, Aliases);
        return new MachineResolver(index, machines.ToDictionary(m => m.Id));
    }

    [Fact]
    public void Resolve_FranchiseTitle_BindsFilenameToSubtitledMachine()
    {
        // THE AP CASE: filename says "Houdini", catalog says "Houdini: Master of Mystery".
        var r = Build(Houdini, HotWheels).Resolve(
            new ResolutionQuery("Houdini--Quick-Reference-Guide.pdf", EvidenceKind.Filename, "americanpinball"));

        var resolved = Assert.IsType<ResolutionResult.Resolved>(r);
        Assert.Equal("GH-M1", resolved.MachineId);
    }

    [Fact]
    public void Resolve_CuratedAlias_BindsAbbreviatedFilename()
    {
        var r = Build(GTF, Houdini).Resolve(
            new ResolutionQuery("GTF-Quick-Reference-Guide.pdf", EvidenceKind.Filename, "americanpinball"));

        var resolved = Assert.IsType<ResolutionResult.Resolved>(r);
        Assert.Equal("GT-M1", resolved.MachineId);
        Assert.Equal(VariantKind.CuratedAlias, resolved.Evidence.VariantKind);
    }

    [Fact]
    public void Resolve_GenericDocument_NoMatch()
    {
        // "Shaker.pdf" / "Assembly.pdf" are platform docs — they must NOT be attributed to a machine.
        var r = Build(Houdini, HotWheels, GTF).Resolve(
            new ResolutionQuery("Shaker.pdf", EvidenceKind.Filename, "americanpinball"));

        Assert.IsType<ResolutionResult.NoMatch>(r);
    }

    [Fact]
    public void Resolve_SingleTokenVariant_NotEligibleForContainmentEvidence()
    {
        // The 1977 Stern "Pinball" once matched 172 documents. A containment-kind query
        // mentioning the word "pinball" must NOT bind to it.
        var r = Build(SternPin, GodzPro).Resolve(
            new ResolutionQuery("Stern-Pinball-Service-Bulletin.pdf", EvidenceKind.Filename, "stern"));

        Assert.IsType<ResolutionResult.NoMatch>(r);
    }

    [Fact]
    public void Resolve_SingleTokenVariant_IsEligibleForExactEvidence()
    {
        // ...but an exact provenance slug of "pinball" is strong evidence and MAY bind.
        var r = Build(SternPin, GodzPro).Resolve(
            new ResolutionQuery("pinball", EvidenceKind.ProvenanceSlug, "stern"));

        var resolved = Assert.IsType<ResolutionResult.Resolved>(r);
        Assert.Equal("GP-M1", resolved.MachineId);
        Assert.Equal(ResolutionStage.Exact, resolved.Evidence.Stage);
    }

    [Fact]
    public void Resolve_SameGroupSiblings_ResolvesAsFamily()
    {
        var r = Build(GodzPro, GodzPrem).Resolve(
            new ResolutionQuery("Godzilla-Manual.pdf", EvidenceKind.Filename, "stern"));

        var fam = Assert.IsType<ResolutionResult.ResolvedFamily>(r);
        Assert.Equal("GZ", fam.GroupId);
        Assert.Equal(2, fam.MachineIds.Count);
    }

    [Fact]
    public void Resolve_NonFamilyMultiMatch_IsAmbiguous_AndNeverGuesses()
    {
        var a = M("GA-M1", "Rampage", "GA", "americanpinball", 2019);
        var b = M("GB-M1", "Rampage", "GB", "americanpinball", 2024); // different group = different game
        var r = Build(a, b).Resolve(
            new ResolutionQuery("Rampage-Manual-10-19-2021.pdf", EvidenceKind.Filename, "americanpinball"));

        var amb = Assert.IsType<ResolutionResult.Ambiguous>(r);
        Assert.Equal(2, amb.Candidates.Count);
    }

    [Fact]
    public void Resolve_FuzzyEvidence_HardFiltersByManufacturer()
    {
        var sternHoudini = M("GX-M1", "Houdini", "GX", "stern");
        var r = Build(Houdini, sternHoudini).Resolve(
            new ResolutionQuery("Houdini--Quick-Reference-Guide.pdf", EvidenceKind.Filename, "americanpinball"));

        var resolved = Assert.IsType<ResolutionResult.Resolved>(r);
        Assert.Equal("GH-M1", resolved.MachineId); // AP, not Stern
    }

    [Fact]
    public void Resolve_LongestVariantWins()
    {
        var tank = M("GK-M1", "Tank", "GK", "americanpinball");
        var r = Build(GTF, tank).Resolve(
            new ResolutionQuery("Galactic-Tank-Force-Game-Manual.pdf", EvidenceKind.Filename, "americanpinball"));

        var resolved = Assert.IsType<ResolutionResult.Resolved>(r);
        Assert.Equal("GT-M1", resolved.MachineId);
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/PinballWizard.Application.Tests --filter "FullyQualifiedName~MachineResolverTests"`
Expected: FAIL — `InMemoryMachineIndex` / `MachineResolver` do not exist.

- [ ] **Step 3: Implement the index**

```csharp
using PinballWizard.Core.Domain;

namespace PinballWizard.Application.Resolution;

// Batch-side index over machine variants. Built once per run by streaming the catalog.
// Interactive consumers use machine_title_lookups + AI Search instead — but BOTH are fed
// by MachineIdentityVariants, so they cannot diverge (ADR-0054).
public sealed class InMemoryMachineIndex
{
    private readonly Dictionary<string, List<MachineVariant>> _byKey;
    private readonly List<MachineVariant> _all;

    private InMemoryMachineIndex(Dictionary<string, List<MachineVariant>> byKey, List<MachineVariant> all)
    {
        _byKey = byKey;
        _all = all;
    }

    public int VariantCount => _all.Count;

    public static InMemoryMachineIndex Build(IEnumerable<Machine> machines, IReadOnlyList<MachineAliasEntry> aliases)
    {
        ArgumentNullException.ThrowIfNull(machines);
        ArgumentNullException.ThrowIfNull(aliases);

        var byKey = new Dictionary<string, List<MachineVariant>>(StringComparer.Ordinal);
        var all = new List<MachineVariant>();

        foreach (var m in machines)
        {
            foreach (var v in MachineIdentityVariants.For(m, aliases))
            {
                all.Add(v);
                if (!byKey.TryGetValue(v.Key, out var list))
                {
                    list = [];
                    byKey[v.Key] = list;
                }
                list.Add(v);
            }
        }

        // Longest variant first so containment matching prefers "galactic tank force" over "tank".
        all.Sort((a, b) => b.Tokens.Count.CompareTo(a.Tokens.Count));
        return new InMemoryMachineIndex(byKey, all);
    }

    public IReadOnlyList<MachineVariant> Exact(string key) =>
        _byKey.TryGetValue(key, out var list) ? list : [];

    // Ordered longest-first. Callers stop at the first token-count tier that yields a match.
    public IReadOnlyList<MachineVariant> AllLongestFirst() => _all;
}
```

- [ ] **Step 4: Implement the resolver**

```csharp
using PinballWizard.Core.Domain;

namespace PinballWizard.Application.Resolution;

// Evidence-aware, confidence-tiered resolution (ADR-0054).
// It NEVER guesses: multiple non-family candidates yield Ambiguous, which the caller
// turns into needs_review. A wrongly-attributed document is worse than an unattributed one.
public sealed class MachineResolver : IMachineResolver
{
    private readonly InMemoryMachineIndex _index;
    private readonly IReadOnlyDictionary<string, Machine> _machines;

    public MachineResolver(InMemoryMachineIndex index, IReadOnlyDictionary<string, Machine> machines)
    {
        ArgumentNullException.ThrowIfNull(index);
        ArgumentNullException.ThrowIfNull(machines);
        _index = index;
        _machines = machines;
    }

    // Fuzzy evidence may mention any machine, so manufacturer scoping is a HARD filter.
    // Provenance evidence is the scraper's own claim, so scoping is a soft preference
    // (preserves DocumentLinker's deliberate NarrowToSourceManufacturer vs PreferByManufacturer split).
    private static bool IsFuzzy(EvidenceKind k) => k is EvidenceKind.Filename or EvidenceKind.PageText;

    public ResolutionResult Resolve(ResolutionQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);

        var tokens = MachineTextNormalizer.Tokenize(query.Text);
        if (tokens.Count == 0) return new ResolutionResult.NoMatch();
        var key = string.Join(' ', tokens);

        // Stage 1 — exact. The ONLY stage single-token variants are eligible for.
        var exact = Eligible(_index.Exact(key), query, ResolutionStage.Exact);
        if (exact.Count > 0) return Decide(exact, query, ResolutionStage.Exact);

        // Stage 2 — franchise prefix + trailing-qualifier consumption (generalizes PR #750).
        var stripped = MachineIdentityVariants.StripTrailingQualifiers(tokens);
        if (stripped.Count != tokens.Count)
        {
            var prefix = Eligible(_index.Exact(string.Join(' ', stripped)), query, ResolutionStage.FranchisePrefix);
            if (prefix.Count > 0) return Decide(prefix, query, ResolutionStage.FranchisePrefix);
        }

        // Stage 3 — token word-boundary containment, longest variant wins.
        var containment = new List<MachineVariant>();
        var bestLength = 0;
        foreach (var v in _index.AllLongestFirst())
        {
            if (v.Tokens.Count < bestLength) break; // sorted longest-first: no better match remains
            if (!IsEligible(v, query, ResolutionStage.Containment)) continue;
            if (!ContainsSequence(tokens, v.Tokens)) continue;

            if (v.Tokens.Count > bestLength)
            {
                bestLength = v.Tokens.Count;
                containment.Clear();
            }
            containment.Add(v);
        }

        var scoped = Scope(containment, query);
        return scoped.Count > 0
            ? Decide(scoped, query, ResolutionStage.Containment)
            : new ResolutionResult.NoMatch();
    }

    private List<MachineVariant> Eligible(IReadOnlyList<MachineVariant> candidates, ResolutionQuery q, ResolutionStage stage)
        => Scope(candidates.Where(v => IsEligible(v, q, stage)).ToList(), q);

    // The single-word guard, as a policy rule rather than a hole in the index.
    //
    // CORRECTED during S1 implementation. This block originally read:
    //
    //     => !v.IsSingleToken || stage == ResolutionStage.Exact;
    //
    // i.e. "a single-token variant is eligible for EXACT evidence only". That
    // blanket rule contradicts four of this task's own tests below, all of
    // which need a SINGLE-token variant to match at Containment:
    // "houdini" (FranchiseTitle), "gtf" (CuratedAlias), "godzilla" and
    // "rampage" (FullTitle). Under the blanket rule each returns NoMatch
    // instead of the expected Resolved / ResolvedFamily / Ambiguous.
    //
    // The rule below is derived from the tests, which are the ground truth,
    // and satisfies all six: it blocks only the variants that actually caused
    // the over-matching this guard exists for — manufacturer-prefixed forms
    // ("stern pinball" would otherwise match every Stern-branded document) and
    // single-token trailing qualifiers ("pinball", the 1977 Stern machine that
    // once matched 172 documents). "houdini" and "godzilla" are not trailing
    // qualifiers and stay eligible. Exact evidence bypasses both checks, so
    // an exact provenance slug of "pinball" still binds.
    private static bool IsEligible(MachineVariant v, ResolutionQuery q, ResolutionStage stage)
    {
        if (stage == ResolutionStage.Exact) return true;
        if (stage == ResolutionStage.Containment && v.Kind == VariantKind.ManufacturerPrefixed) return false;
        if (v.IsSingleToken && TrailingQualifiers.Contains(v.Tokens[0])) return false;
        return true;
    }

    private List<MachineVariant> Scope(List<MachineVariant> candidates, ResolutionQuery q)
    {
        if (candidates.Count == 0 || q.ManufacturerHint is null) return candidates;

        var matching = candidates
            .Where(v => string.Equals(v.ManufacturerKey, q.ManufacturerHint, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (IsFuzzy(q.EvidenceKind)) return matching;                 // hard filter
        return matching.Count > 0 ? matching : candidates;            // soft preference
    }

    private ResolutionResult Decide(List<MachineVariant> candidates, ResolutionQuery q, ResolutionStage stage)
    {
        var first = candidates[0];
        var evidence = new ResolutionEvidence(q.EvidenceKind, first.Kind, first.Key, stage);

        var machineIds = candidates.Select(v => v.MachineId).Distinct(StringComparer.Ordinal).ToList();
        if (machineIds.Count == 1) return new ResolutionResult.Resolved(machineIds[0], evidence);

        var groups = candidates.Select(v => v.GroupId).Distinct(StringComparer.Ordinal).ToList();
        if (groups.Count == 1 && groups[0] is { } groupId)
            return new ResolutionResult.ResolvedFamily(groupId, machineIds, evidence);

        var cands = machineIds
            .Select(id =>
            {
                var v = candidates.First(c => c.MachineId == id);
                var title = _machines.TryGetValue(id, out var m) ? m.Title : id;
                return new ResolutionCandidate(id, title, v.Kind, v.Key);
            })
            .ToList();

        return new ResolutionResult.Ambiguous(cands, evidence);
    }

    private static bool ContainsSequence(IReadOnlyList<string> haystack, IReadOnlyList<string> needle)
    {
        if (needle.Count == 0 || needle.Count > haystack.Count) return false;
        for (var i = 0; i + needle.Count <= haystack.Count; i++)
        {
            var ok = true;
            for (var j = 0; j < needle.Count; j++)
            {
                if (!string.Equals(haystack[i + j], needle[j], StringComparison.Ordinal)) { ok = false; break; }
            }
            if (ok) return true;
        }
        return false;
    }
}
```

- [ ] **Step 5: Run to verify it passes**

Run: `dotnet test tests/PinballWizard.Application.Tests --filter "FullyQualifiedName~MachineResolverTests"`
Expected: PASS — all 9 tests, including both single-word-guard cases.

- [ ] **Step 6: Full suite + commit**

Run: `dotnet test PinballWizard.slnx --filter "Category!=Accessibility&Category!=Snapshots&Category!=Circuit&Category!=E2E"`

```bash
git add src/PinballWizard.Application/Resolution/ tests/PinballWizard.Application.Tests/Resolution/
git commit -m "feat(resolution) evidence-aware machine resolver + in-memory variant index (ADR-0054)"
```

**S1 gate:** all resolver policy tests green (esp. both single-word-guard cases + Ambiguous-never-guesses) + full CI suite. No consumer wired yet — `DocumentLinker` still uses its old path until Plan 2.

## S2 — Upsert semantics (#762)

**Branch:** `fix/upsert-scraper-owned-fields` · **Gate:** upsert tests (linker state preserved / scraper fields refreshed / re-link-on-change / ETag conflict) + full CI suite. **Independent of S1.**

### Task 5: Upsert semantics (#762)

**Files:**

- Modify: `src/PinballWizard.Infrastructure/Persistence/Cosmos/CosmosRawDocumentRepository.cs` (the `existing is not null` branch)
- Test: `tests/PinballWizard.Infrastructure.Tests/Persistence/CosmosRawDocumentRepositoryUpsertTests.cs`

**Interfaces:**

- Produces: unchanged public signature; changed *semantics*. Plan 2's Wave-3 live run depends on this — without it a scraper fix cannot reach the live corpus.

- [ ] **Step 1: Read the current merge branch first**

Run: `rg -n -A40 "public.*UpsertRawAsync" src/PinballWizard.Infrastructure/Persistence/Cosmos/CosmosRawDocumentRepository.cs`
Record exactly which fields are copied today. **Do not proceed from memory** — this is the field list you are about to split.

- [ ] **Step 2: Write the failing tests**

```csharp
// Preserve linker/admin state; refresh scraper-owned fields (#762).
[Fact]
public async Task UpsertRaw_ExistingDoc_RefreshesScraperOwnedFields()
{
    // existing: source_type=ServiceBulletinPage, game.slug=null, doc_type=Other, link_status=linked
    // incoming re-scrape: source_type=AmericanPinballBulletinPage, game.slug="houdini", doc_type=Manual
    // EXPECT: all three refreshed.
}

[Fact]
public async Task UpsertRaw_ExistingDoc_PreservesLinkerOwnedState()
{
    // machine_id, run_id, timeline.first_discovered_at, file.local_path, http.etag preserved.
}

[Fact]
public async Task UpsertRaw_ManuallyLinkedDoc_KeepsMachineAndStaysLinked()
{
    // An admin override always wins — a re-scrape must never re-link a ManuallyLinked doc.
}

[Fact]
public async Task UpsertRaw_ChangedSlugOrDocType_FlipsToPending()
{
    // A changed game.slug or classification.document_type invalidates the old binding,
    // so link_status → pending for re-link.
}

[Fact]
public async Task UpsertRaw_UnchangedScraperFields_DoesNotFlipLinkStatus()
{
    // Idempotence: a no-op re-scrape must not churn linked docs back to pending.
}
```

Fill each body against the existing test-fixture pattern in `tests/PinballWizard.Infrastructure.Tests/Persistence/` (read a sibling Cosmos repository test for the emulator/fake setup — do not invent a harness).

- [ ] **Step 3: Run to verify they fail**

Run: `dotnet test tests/PinballWizard.Infrastructure.Tests --filter "FullyQualifiedName~CosmosRawDocumentRepositoryUpsert"`
Expected: FAIL — current code preserves everything.

- [ ] **Step 4: Implement the field-block split**

In the `existing is not null` branch, replace "preserve all, update timeline+xrefs" with two explicit blocks:

- **Preserved (linker/admin-owned):** `MachineId`, `LinkStatus`, `LinkReview`, `ManuallyLinked`, `PlatformGeneric`, `RunId` (write-once), `Timeline.FirstDiscoveredAt`, `File.LocalPath`/blob state, `Http.ETag`/`LastModified`.
- **Refreshed (scraper-owned):** `Source.*`, `Game.*`, `Classification.*`, `Timeline.LastCheckedAt`, cross-references (merged, dedup by `AlsoFoundAt`).

Then:

```csharp
var slugChanged = !string.Equals(existing.Game?.Slug, record.Game?.Slug, StringComparison.OrdinalIgnoreCase);
var typeChanged = existing.Classification?.DocumentType != record.Classification?.DocumentType;

// An admin override always wins; otherwise changed scraper evidence invalidates the binding.
if ((slugChanged || typeChanged) && !existing.ManuallyLinked)
{
    existing.LinkStatus = LinkStatus.Pending;
    existing.MachineId = null;
}
```

Use `ItemRequestOptions { IfMatchEtag = existing.ETag }` on the replace — the scraper and linker can write the same document concurrently (ADR-0025 lost-update protection).

- [ ] **Step 5: Run to verify they pass, then full suite**

Run: `dotnet test tests/PinballWizard.Infrastructure.Tests --filter "FullyQualifiedName~CosmosRawDocumentRepositoryUpsert"`
Then: `dotnet test PinballWizard.slnx --filter "Category!=Accessibility&Category!=Snapshots&Category!=Circuit&Category!=E2E"`

- [ ] **Step 6: Commit**

```bash
git add src/PinballWizard.Infrastructure/Persistence/Cosmos/CosmosRawDocumentRepository.cs \
        tests/PinballWizard.Infrastructure.Tests/Persistence/CosmosRawDocumentRepositoryUpsertTests.cs
git commit -m "fix(persistence) refresh scraper-owned fields on re-scrape; preserve linker state (#762)"
```

## S3 — Golden-set + parity capture (the gate for Plan 2)

**Branch:** `feat/golden-link-set` · **Gate:** the captured snapshot replays green against the CURRENT linker (proving the harness is correct before it judges the new one). **Independent of S1.**

### Task 6: Golden-set + parity capture

**Files:**

- Create: `src/PinballWizard.Cli/Commands/CaptureGoldenSetCommand.cs`
- Modify: `src/PinballWizard.Cli/Program.cs` (add `--capture-golden-set`, read-only)
- Create: `tests/PinballWizard.Application.Tests/Linking/GoldenLinkSetReplayTests.cs`
- Create: `tests/PinballWizard.Application.Tests/Fixtures/Linking/golden-link-set.captured.json` + `CAPTURE.md`

- [ ] **Step 1: Add the read-only capture verb**

`--capture-golden-set` streams `scraped_documents_raw`, emits every document whose `link_status == linked` as `{ documentId, fileUrl, sourceType, gameSlug, documentType, manufacturerKey, expectedMachineId }`, and writes the fixture + a `CAPTURE.md` (source = live Cosmos, capture date, document count). **Read-only — no writes.**

- [ ] **Step 2: Write the replay test**

```csharp
// The regression gate for Plan 2. Replays every currently-linked document through the linker
// and asserts the binding is reproduced.
//   linked → different machine  = 🔴 BLOCKING (mis-attribution)
//   linked → needs_review       = reviewable (report, do not fail)
//   not_in_catalog → linked     = a WIN (report)
[Fact]
public async Task GoldenLinkSet_Replays_WithNoMisattribution()
{
    var golden = LoadCapturedGoldenSet();
    var mismatches = new List<string>();
    foreach (var entry in golden)
    {
        var actual = await ResolveThroughLinker(entry);
        if (actual.MachineId is not null && actual.MachineId != entry.ExpectedMachineId)
            mismatches.Add($"{entry.DocumentId}: expected {entry.ExpectedMachineId}, got {actual.MachineId}");
    }
    Assert.Empty(mismatches);
}
```

- [ ] **Step 3: Capture from live (OPERATOR-GATED — ask before running)**

```bash
export AZURE_TOKEN_CREDENTIALS=dev
export Cosmos__AccountEndpoint="https://pinwiz-cosmos-dev-buutj.documents.azure.com:443/"
dotnet run --project src/PinballWizard.Cli -c Release -- --capture-golden-set
```

Expected: ~373 linked documents captured (the live count after the 2026-07-13 relink).

- [ ] **Step 4: Prove the harness by replaying against the CURRENT linker**

Run: `dotnet test tests/PinballWizard.Application.Tests --filter "FullyQualifiedName~GoldenLinkSetReplay"`
Expected: PASS. If it fails now, the harness is wrong — fix it before it is ever used to judge the new resolver.

- [ ] **Step 5: Add the reconciler parity snapshot**

Same shape: capture per-manufacturer `ManufacturerSlugs` state + match-outcome counts (slug/title/group/unmatched/ambiguous) into `tests/.../Fixtures/Sync/reconciler-parity.captured.json` + `CAPTURE.md`, and a replay test asserting no regression.

- [ ] **Step 6: Commit**

```bash
git add src/PinballWizard.Cli/ tests/PinballWizard.Application.Tests/Linking/ tests/PinballWizard.Application.Tests/Fixtures/
git commit -m "test(linking) capture golden link set + reconciler parity snapshot as the Wave-2 regression gate (ADR-0054)"
```

## S4 — AP classification

**Branch:** `fix/ap-document-classification` · **Gate:** classification tests driven by the **captured** AP URL list; full CI suite. **Independent of S1.**

### Task 7: AP classification + TEST-05 fixture rule

**Files:**

- Modify: `src/PinballWizard.Application/Documents/DocumentClassifier.cs` (locate via `rg -n "ClassifyDocumentType" src/`)
- Test: `tests/PinballWizard.Application.Tests/Documents/ApDocumentClassificationTests.cs`
- Use: `tests/PinballWizard.Infrastructure.Tests/Fixtures/Ap/bulletin-urls.captured.txt` (**already captured** on branch `fix/ap-bulletins-real-patterns`; move it to this branch — do NOT re-author it)

- [ ] **Step 1: Write the failing tests, driven by the captured list**

Today every AP document classifies as `Other`, which RAG ingestion skips — so AP can never be indexed even once linked.

```csharp
[Theory]
[InlineData("Houdini--Quick-Reference-Guide.pdf", DocumentType.Manual)]
[InlineData("API-Houdini-Service-Manual-10-6-21.pdf", DocumentType.Manual)]
[InlineData("Galactic-Tank-Force-Game-Manual-(Version-1.0_October-2023).pdf", DocumentType.Manual)]
[InlineData("Okto-english-manual-10-5-21.pdf", DocumentType.Manual)]
[InlineData("Hot-Wheels-Manual-10-14-2021.pdf", DocumentType.Manual)]
[InlineData("Houdini-Skill-Shot-Fix.pdf", DocumentType.ServiceBulletin)]
[InlineData("Hotwheels-GI-EPIC-3-Wire-update.pdf", DocumentType.ServiceBulletin)]
[InlineData("Houdini--Coil-Performance-Improvement-Kit.pdf", DocumentType.ServiceBulletin)]
public void Classify_ApSupportDocument_IsIndexable(string filename, DocumentType expected)
{
    var actual = DocumentClassifier.ClassifyDocumentType(
        fileUrl: $"http://s4.american-pinball.com/img/support/2021-11/{filename}",
        linkText: null,
        discoveryContext: "American Pinball Support Page");

    Assert.Equal(expected, actual);
}

[Fact]
public void EveryCapturedApUrl_ClassifiesToAnIndexableType_OrIsAKnownGenericDoc()
{
    // Reads the CAPTURED url list — if AP changes its site, this test tells us.
    var urls = File.ReadAllLines(CapturedApUrlList);
    Assert.NotEmpty(urls);
    foreach (var url in urls)
    {
        var t = DocumentClassifier.ClassifyDocumentType(url, null, "American Pinball Support Page");
        Assert.True(t != DocumentType.Other || IsKnownGenericDoc(url),
            $"{url} classified Other and is not a known generic/platform doc — RAG would skip it.");
    }
}
```

`IsKnownGenericDoc` matches the genuinely game-agnostic captured files (`Shaker.pdf`, `Assembly.pdf`, `Power-Distribution.pdf`, `SCOOP-ADJUSTMENT.pdf`, `Knocker-Installation.pdf`, `Speaker-Grill-Installation.pdf`, `USB-drive-formatting-procedure.pdf`, …) — enumerate them from the captured list, don't guess.

- [ ] **Step 2–4: RED → implement classification rules → GREEN**

Rules (derived from the captured filenames only): `quick reference guide` | `service manual` | `game manual` | `manual` → `Manual`; `fix` | `update` | `install` | `kit` | `improvement` → `ServiceBulletin`; otherwise `Other`.

- [ ] **Step 5: Promote the captured-fixture rule to a standard (the #758 guard)**

This is the single change most likely to prevent a repeat of #752. Add a rule to
`.claude/standards/testing/STANDARD.md`:

```text
**RULE TEST-05** (captured-scraper-fixtures)
Scraper/parsing fixtures MUST be captured from the live source, never hand-authored.
Any fixture directory for a scraped source carries CAPTURE.md recording the source URL
and capture date. A test asserting against an invented URL/DOM shape is not a test — it
restates the implementation's assumption and cannot falsify it.
CHECK:  every tests/**/Fixtures/<source>/ directory contains CAPTURE.md
REF:    #758 · docs/learning-from-failure.md
```

Then add a mechanical test asserting every `tests/**/Fixtures/<source>/` directory contains a
`CAPTURE.md`.

- [ ] **Step 6: Commit**

```bash
git add src/PinballWizard.Application/Documents/DocumentClassifier.cs \
        tests/PinballWizard.Application.Tests/Documents/ApDocumentClassificationTests.cs \
        tests/PinballWizard.Infrastructure.Tests/Fixtures/Ap/ \
        .claude/standards/testing/STANDARD.md
git commit -m "fix(documents) classify AP support docs as Manual/ServiceBulletin so RAG can index them (#745)

Also promotes the captured-fixture rule to TEST-05 — scraper fixtures must come from
the live source, never hand-authored. This is the #758 guard."
```

## S5 — Alias seed + loader

**Branch:** `feat/machine-alias-seed` · **Gate:** contract tests (every alias resolves to a real group/machine; no duplicate `(alias, manufacturerKey)`); full CI suite. **Independent of S1.**

### Task 8: Alias seed + loader

**Files:**

- Create: `data/seeds/machine_aliases.v1.json`
- Create: `src/PinballWizard.Application/Resolution/MachineAliasLoader.cs`
- Test: `tests/PinballWizard.Application.Tests/Resolution/MachineAliasLoaderTests.cs`

**Interfaces:**

- Consumes: `MachineAliasSeedFile`, `MachineAliasEntry` (S0); `SeedPathResolver` (`src/PinballWizard.Application/SeedData/SeedPathResolver.cs:22`).
- Produces: `MachineAliasLoader.LoadAsync(CancellationToken) → IReadOnlyList<MachineAliasEntry>` — consumed by S1's index build and (Plan 2) `OpdbSyncService`.

- [ ] **Step 1: Look up the REAL OPDB group ids — do not guess them**

```bash
# AP machines and their group ids, from the live catalog:
#   Houdini: Master of Mystery / Oktoberfest / Hot Wheels /
#   Legends of Valhalla / Galactic Tank Force / Barry O's Barbeque Challenge
```

Query the `machines` container (partition `americanpinball`) and record each `GroupId`. **Every `opdbGroupId` in the seed MUST come from this lookup** — this is the #758 rule applied to seed data.

Seed content (aliases justified by the captured AP filenames):

| alias | machine | justified by |
| --- | --- | --- |
| `GTF` | Galactic Tank Force | `GTF-Quick-Reference-Guide.pdf` |
| `Okto` | Oktoberfest | `Okto-english-manual-10-5-21.pdf` |
| `HW` | Hot Wheels | `HW-car-attachment-instructions[6658].pdf` |
| `HWL` | Hot Wheels | `HWL--shaker-install.pdf` |
| `LOV` | Legends of Valhalla | `DBA-for-LOV.pdf` |

`Rampage` is **not** aliased — it has AP manuals but no OPDB machine, so it correctly stays `not_in_catalog`.

- [ ] **Step 2: Write the failing contract tests**

```csharp
[Fact]
public async Task Load_EveryAlias_ResolvesToARealGroupOrMachine()
{
    // A dangling alias silently mis-attributes nothing — but it also silently does nothing.
    // Fail CI rather than ship a lie.
}

[Fact]
public async Task Load_NoDuplicateAliasPerManufacturer() { }

[Fact]
public async Task Load_EveryEntry_HasManufacturerKey()
{
    // An unscoped alias could collide across manufacturers ("hw" is not universal).
}

[Fact]
public async Task Load_CorruptSeed_ThrowsAtStartup()
{
    // Fail-fast, like CommunityResourceLoader — a corrupt alias file must not silently
    // degrade attribution.
}
```

- [ ] **Step 3–4: RED → implement the loader (lazy singleton, fail-fast validation, `SeedPathResolver`) → GREEN**

Mirror `src/PinballWizard.Application/Ai/Refusal/CommunityResourceLoader.cs:23` — read it first and follow its shape.

- [ ] **Step 5: Commit**

```bash
git add data/seeds/machine_aliases.v1.json \
        src/PinballWizard.Application/Resolution/MachineAliasLoader.cs \
        tests/PinballWizard.Application.Tests/Resolution/MachineAliasLoaderTests.cs
git commit -m "feat(resolution) curated machine-alias seed + fail-fast loader (ADR-0054)"
```

## S6 — `needs_review` status + admin queue

**Branch:** `feat/link-review-queue` · **Gate:** bUnit component test + `CrossPartitionQueryAllowListTests` updated; full CI suite. **Independent of S1** (persists the status; the resolver starts producing it in Plan 2).

### Task 9: needs_review status + admin queue

**Files:**

- Modify: `src/PinballWizard.Core/Models/DocumentRecord.cs` (add `LinkStatus.NeedsReview`, `LinkReview` block)
- Create: `src/PinballWizard.Web/Components/Pages/Admin/LinkReview.razor`
- Modify: `tests/PinballWizard.Infrastructure.Tests/Persistence/CrossPartitionQueryAllowListTests.cs` (allow-list the admin scan per ADR-0036)
- Test: `tests/PinballWizard.Web.Tests/Admin/LinkReviewTests.cs`

- [ ] **Step 1: Extend the model**

```csharp
public enum LinkStatus { Pending, Linked, NotInCatalog, PlatformGeneric, Failed, NeedsReview }

public sealed class LinkReviewInfo
{
    public List<LinkReviewCandidate> Candidates { get; set; } = [];
    public DateTime CreatedAt { get; set; }
}

public sealed class LinkReviewCandidate
{
    public string MachineId { get; set; } = string.Empty;
    public string MachineTitle { get; set; } = string.Empty;
    public string EvidenceKind { get; set; } = string.Empty;
    public string MatchedVariant { get; set; } = string.Empty;
}
```

- [ ] **Step 2: Write the failing bUnit test**

Use `AppDataGrid` + `AppPageHeader` + `AppEmptyState` per ADR-0046 (never raw `MudTable`). Add `MudPopoverProvider` as a sibling in the test host (MudBlazor 9 requirement).

```csharp
[Fact]
public void LinkReview_RendersCandidates_AndResolvingWritesAnOverride()
{
    // Given a needs_review doc with 2 candidates, the grid shows both with their evidence;
    // clicking "Assign" writes a link_overrides row and flips the doc to pending.
}

[Fact]
public void LinkReview_NoPendingReviews_ShowsEmptyState() { }
```

- [ ] **Step 3–4: RED → implement page + repository query + override write → GREEN**

Public surfaces must treat `NeedsReview` exactly like `NotInCatalog` (invisible) — grep every `LinkStatus.Linked` read path and confirm none newly surfaces `NeedsReview`.

- [ ] **Step 5: Meter it**

Add `pinwiz.linking.needs_review_total` (tags: `manufacturer`, `evidence_kind`) to `PinballWizardTelemetry`. Ambiguity must be **visible**, not merely stored.

- [ ] **Step 6: Commit**

```bash
git add src/PinballWizard.Core/Models/DocumentRecord.cs \
        src/PinballWizard.Web/Components/Pages/Admin/LinkReview.razor \
        src/PinballWizard.Application/Observability/PinballWizardTelemetry.cs \
        tests/PinballWizard.Web.Tests/Admin/LinkReviewTests.cs \
        tests/PinballWizard.Infrastructure.Tests/Persistence/CrossPartitionQueryAllowListTests.cs
git commit -m "feat(admin) needs_review link status + review queue writing Tier-0 overrides (ADR-0054)"
```

---

## Wave 1 exit criteria

All six PRs merged, each behind its own gate. Then **Plan 2** (six consumer migrations) is written against the *real* `IMachineResolver` — not against a guess about it.

## What is NOT in this plan (deliberately)

- **Wave 2** (DocumentLinker, Reconciler, GroundingTool+OpdbSync, TiltForums, Kineticist, PB Freshdesk migrations) — each needs the real resolver in hand. Planning them now would mean writing code against an imagined API. That is the exact failure mode ADR-0054 exists to eliminate.
- **Wave 3** (live re-scrape → reclassify → relink → download → backfill → **corpus-coverage probe**) — an operator-gated runbook, not a code plan. Every step requires explicit approval per the confirm-before-live-ingestion rule.
- **#760** (RAG ingestion politeness fallback) — separate fix, separate PR.

## Definition of done for the whole program

The **corpus-coverage probe (#748)** reports **zero source gaps** (closing `ap`, auto-closing #749), and the **golden link set replays with no `linked → different machine` regressions**.

Not "the tests pass." #752's tests passed.
