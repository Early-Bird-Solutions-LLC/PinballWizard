# Inline Citation Markers Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Render truthful numbered inline citation markers in Wizard answers that scroll to and pulse the matching (numbered) citation card, closing the navigation loop with the PR #463 left flipper.

**Architecture:** The model tags grounded sentences with `[[cite:k]]` (k = ordinal of a `searchCorpus` source). A server-side reconciliation step in `AiRouter` rebuilds the `k`-ordering from the `AgentResponse` tool traces, maps `k → Citation → card ordinal N`, rewrites the body to `[[cite:N]]` (dropping + metering unmatched), and the CSP-safe `MarkdownTokenizer` renders `[[cite:N]]` as a `<CitationMarker>` component. Markers resolve at `Final`; raw tokens are suppressed mid-stream.

**Tech Stack:** C# / .NET 10, Blazor (`RenderFragment` tokenizer, bUnit), Microsoft Agent Framework (Foundry), xUnit, OpenTelemetry meters.

**Spec:** [`docs/superpowers/specs/2026-06-20-inline-citation-markers-design.md`](../specs/2026-06-20-inline-citation-markers-design.md).

## Global Constraints

- **Branch:** `feat/inline-citation-markers` (stacked on the PR #463 citation-flippers work — `CitationCard.InAnswerAnchor` exists). Never commit on `main`.
- **Identity:** commits author as `Jim Keeley <94459922+jkeeley2073@users.noreply.github.com>`; conventional format; NO Claude attribution trailer.
- **CSP-safe rendering (locked):** the answer body renders through `MarkdownTokenizer` which HTML-encodes all text via `builder.AddContent` — **never** `MarkupString`, never injected HTML. Markers are real components emitted via `builder.OpenComponent`/`OpenElement`.
- **Truthful-only (OBS-01):** a `[[cite:k]]` that does not reconcile to a real citation is **dropped + metered**, never rendered as a fake/blank marker.
- **Numbering:** `k` = `searchCorpus` source ordinal (model-echoed); `N` = citation card display ordinal (RelevanceScore-desc render order, unchanged). The frontend only ever sees `N`.
- **Reconciliation keys on `SourceUrl`** (stable on both the `searchCorpus` and `getMachineByTitle` paths; `DocumentChunkId` used when populated).
- **Markers resolve at `Final`**; raw `[[cite:k]]` never reaches the user mid-stream or final.
- **Governing rule:** `FE-09`. Markers/cards live in the four locked delight surfaces (FE-03 OK).
- **Token syntax:** `[[cite:<digits>]]` — double-bracket so it cannot collide with the safe-markdown subset (which only handles `*bold*`/`*italic*`/lists, never `[`).

---

## File Structure

**Create:**
- `src/PinballWizard.Web/Components/Citations/CitationMarker.razor` (+ `.razor.css`) — the inline pinball-insert marker component.
- `src/PinballWizard.Application/Ai/Citations/InlineCitationReconciler.cs` — pure `k→N` rewrite logic (testable without Foundry).
- `tests/PinballWizard.Application.Tests/Ai/Citations/InlineCitationReconcilerTests.cs`
- `tests/PinballWizard.Web.Tests/Components/Citations/CitationMarkerTests.cs`
- `tests/PinballWizard.Web.Tests/Components/Wizard/MarkdownTokenizerCitationTests.cs`

**Modify:**
- `src/PinballWizard.Web/Components/Wizard/MarkdownTokenizer.cs` — recognize `[[cite:N]]` inline → render `CitationMarker`.
- `src/PinballWizard.Application/Observability/PinballWizardTelemetry.cs` — 3 inline-marker meters.
- `src/PinballWizard.Infrastructure/Ai/Citations/ToolTraceCitationExtractor.cs` — also expose the ordered `searchCorpus`-hit list (the `k` index).
- `src/PinballWizard.Application/Ai/AiRouter.cs` — call the reconciler post-extraction (≈ line 1000); suppress raw tokens in the `TextDelta` path (≈ line 747).
- `src/PinballWizard.Web/Components/Citations/CitationStrip.razor`, `CitationGroup.razor`, `CitationCard.razor` — thread the card ordinal `N` (visible number) + set `InAnswerAnchor = "marker-N-1"`.
- `src/PinballWizard.Application/Ai/Agents/Wizard.md`, `Repair.md`, `Rules.md`, `Valuation.md` — numbered-source instruction + `[[cite:k]]` emission.
- `docs/adr/0026-*.md`, `docs/adr/0022-*.md` — append follow-up entries.

---

## Task 1: Inline-marker telemetry meters

**Files:**
- Modify: `src/PinballWizard.Application/Observability/PinballWizardTelemetry.cs` (add to the AI region near line 82–134)

**Interfaces:**
- Produces: `PinballWizardTelemetry.AiInlineMarkerRendered`, `AiInlineMarkerDropped`, `AiInlineMarkerTotal` (`Counter<long>`).

- [ ] **Step 1: Add the three counters**

Add next to the existing `AiCitationsExtracted` counter (same `Meter`, same style):

```csharp
public static readonly Counter<long> AiInlineMarkerTotal = Meter.CreateCounter<long>(
    "pinwiz.ai.inline_marker_total",
    unit: "{marker}",
    description: "Inline [[cite:k]] tokens the model emitted in an answer, before reconciliation.");

public static readonly Counter<long> AiInlineMarkerRendered = Meter.CreateCounter<long>(
    "pinwiz.ai.inline_marker_rendered_total",
    unit: "{marker}",
    description: "Inline citation markers that reconciled to a real citation and were rewritten to [[cite:N]].");

public static readonly Counter<long> AiInlineMarkerDropped = Meter.CreateCounter<long>(
    "pinwiz.ai.inline_marker_dropped_total",
    unit: "{marker}",
    description: "Inline [[cite:k]] tokens dropped because no structural citation matched (tagged with reason). OBS-01: degrade visibly.");
```

- [ ] **Step 2: Build to verify it compiles**

Run: `dotnet build src/PinballWizard.Application/PinballWizard.Application.csproj --nologo -warnaserror`
Expected: 0 errors / 0 warnings.

- [ ] **Step 3: Commit**

```bash
git add src/PinballWizard.Application/Observability/PinballWizardTelemetry.cs
git commit -m "feat(ai) inline citation marker meters (rendered/dropped/total)"
```

---

## Task 2: `MarkdownTokenizer` renders `[[cite:N]]` markers

**Files:**
- Modify: `src/PinballWizard.Web/Components/Wizard/MarkdownTokenizer.cs` (the `RenderInlineSpans` plain-text loop, ≈ lines 237–293)
- Test: `tests/PinballWizard.Web.Tests/Components/Wizard/MarkdownTokenizerCitationTests.cs`

**Interfaces:**
- Consumes: nothing.
- Produces: the tokenizer recognizes `[[cite:<digits>]]` inside a text run and emits a `CitationMarker` component with `Number` set; malformed/unknown `[[…]]` renders as literal text. (Generic enough that a later `[[portal:…]]` registers the same way.)

- [ ] **Step 1: Write the failing test**

```csharp
using Bunit;
using PinballWizard.Web.Components.Wizard;
using Xunit;

namespace PinballWizard.Web.Tests.Components.Wizard;

public sealed class MarkdownTokenizerCitationTests : TestContext
{
    private IRenderedFragment RenderText(string text)
        => Render(builder => builder.AddContent(0, MarkdownTokenizer.Render(text)));

    [Fact]
    public void CiteToken_RendersCitationMarker_WithNumber()
    {
        var cut = RenderText("The flippers persist after the switch test passes [[cite:2]].");
        var marker = cut.Find("[data-testid='citation-marker']");
        Assert.Equal("2", marker.GetAttribute("data-citation-number"));
        // Body text keeps the surrounding prose, minus the raw token.
        Assert.DoesNotContain("[[cite:2]]", cut.Markup);
    }

    [Fact]
    public void MalformedCiteToken_RendersAsLiteralText()
    {
        var cut = RenderText("Edge case [[cite:]] and [[unknown:3]] stay literal.");
        Assert.Contains("[[cite:]]", cut.Markup);
        Assert.Contains("[[unknown:3]]", cut.Markup);
        Assert.Empty(cut.FindAll("[data-testid='citation-marker']"));
    }
}
```

- [ ] **Step 2: Run it — fails (Render doesn't know the token)**

Run: `dotnet test tests/PinballWizard.Web.Tests --filter "FullyQualifiedName~MarkdownTokenizerCitationTests" --nologo`
Expected: FAIL (markers not found / raw token present).

- [ ] **Step 3: Add inline-token scanning to `RenderInlineSpans`**

In `MarkdownTokenizer.cs`, the plain-text run loop currently breaks only on `*`. Add `[` to the break set and, when two consecutive `[` are seen, try to match a closed inline token `[[<kind>:<payload>]]`. Insert this helper and wire it into the loop (replace the run-buffer break condition near line 278):

```csharp
// Inline-insert tokens: [[<kind>:<payload>]]. Closed registry — currently only
// "cite" with an integer payload. Unknown kind / malformed payload falls through
// to literal text (fail-safe, CSP-safe). Designed to extend (e.g. "portal").
private static bool TryMatchInlineToken(
    string text, int pos, out int consumed, out string kind, out string payload)
{
    consumed = 0; kind = ""; payload = "";
    if (pos + 4 >= text.Length || text[pos] != '[' || text[pos + 1] != '[') return false;
    var close = text.IndexOf("]]", pos + 2, StringComparison.Ordinal);
    if (close < 0) return false;
    var inner = text.Substring(pos + 2, close - (pos + 2)); // e.g. "cite:2"
    var colon = inner.IndexOf(':');
    if (colon <= 0) return false;
    kind = inner[..colon];
    payload = inner[(colon + 1)..];
    if (payload.Length == 0) return false;
    consumed = (close + 2) - pos; // include closing ]]
    return true;
}
```

In the plain-text accumulation loop, before the `*` handling, add:

```csharp
if (text[pos] == '[' && pos + 1 < text.Length && text[pos + 1] == '[')
{
    if (TryMatchInlineToken(text, pos, out var consumed, out var kind, out var payload)
        && kind == "cite" && int.TryParse(payload, out var citeNumber))
    {
        FlushRun(builder, ref run, ref seq);          // emit buffered plain text first
        var occ = occurrences.TryGetValue(citeNumber, out var prev) ? prev + 1 : 1;
        occurrences[citeNumber] = occ;                // per-render occurrence counter
        builder.OpenComponent<CitationMarker>(seq++);
        builder.AddComponentParameter(seq++, nameof(CitationMarker.Number), citeNumber);
        builder.AddComponentParameter(seq++, nameof(CitationMarker.Occurrence), occ);
        builder.CloseComponent();
        pos += consumed;
        continue;
    }
    // Not a recognized token — fall through; the '[' is appended as literal text below.
}
```

(`FlushRun` = the existing run-buffer flush; if it isn't a named method, inline the existing buffer-emit code. `seq`/`run` are the loop's existing sequence + buffer locals — match their real names.) Ensure the loop still appends a lone `[` as literal text when `TryMatchInlineToken` fails, so malformed tokens stay literal.

**Thread a per-render occurrence counter.** Markers for the same `N` across the whole answer must get distinct `Occurrence` values (1, 2, …) so their DOM ids (`marker-N-occ`) stay unique. `RenderInlineSpans` is called per line, so the counter cannot be a local there — create `var occurrences = new Dictionary<int,int>();` once in `Render` (or `BuildTree`) and thread it through `BuildTree → RenderInline → RenderInlineSpans` as a parameter to the marker-emit code above. (Internal method signatures gain a `Dictionary<int,int> occurrences` param — a mechanical change confined to this file.)

- [ ] **Step 4: Run the test — passes**

Run: `dotnet test tests/PinballWizard.Web.Tests --filter "FullyQualifiedName~MarkdownTokenizerCitationTests" --nologo`
Expected: PASS (2 tests). (`CitationMarker` is created in Task 3; until then this references a missing type — implement Task 3 first if the build fails, or stub `CitationMarker` as an empty component then flesh it out in Task 3. Recommended order: do Task 3 before Task 2's Step 4.)

- [ ] **Step 5: Commit**

```bash
git add src/PinballWizard.Web/Components/Wizard/MarkdownTokenizer.cs tests/PinballWizard.Web.Tests/Components/Wizard/MarkdownTokenizerCitationTests.cs
git commit -m "feat(web) MarkdownTokenizer renders [[cite:N]] as CitationMarker (CSP-safe, extensible)"
```

---

## Task 3: `<CitationMarker>` component

**Files:**
- Create: `src/PinballWizard.Web/Components/Citations/CitationMarker.razor`, `CitationMarker.razor.css`
- Test: `tests/PinballWizard.Web.Tests/Components/Citations/CitationMarkerTests.cs`

**Interfaces:**
- Consumes: nothing.
- Produces: `CitationMarker` with `[Parameter] public int Number`, optional `[Parameter] public string? Tooltip`. Renders an anchor to `#citation-{Number}`, carries `data-testid="citation-marker"` + `data-citation-number="{Number}"`, and its own DOM id `marker-{Number}-{Occurrence}` (Occurrence via a cascading counter — see Step 3).

- [ ] **Step 1: Write the failing test**

```csharp
using Bunit;
using PinballWizard.Web.Components.Citations;
using Xunit;

namespace PinballWizard.Web.Tests.Components.Citations;

public sealed class CitationMarkerTests : TestContext
{
    [Fact]
    public void Renders_number_and_anchor_to_matching_card()
    {
        var cut = RenderComponent<CitationMarker>(p => p.Add(x => x.Number, 3));
        var el = cut.Find("[data-testid='citation-marker']");
        Assert.Equal("3", el.GetAttribute("data-citation-number"));
        Assert.Equal("#citation-3", el.GetAttribute("href"));
        Assert.Contains("3", el.TextContent);
    }

    [Fact]
    public void Tooltip_when_provided_is_the_aria_label_and_title()
    {
        var cut = RenderComponent<CitationMarker>(p => p
            .Add(x => x.Number, 1)
            .Add(x => x.Tooltip, "[MANUAL] Stern Godzilla Manual"));
        var el = cut.Find("[data-testid='citation-marker']");
        Assert.Equal("[MANUAL] Stern Godzilla Manual", el.GetAttribute("title"));
        Assert.Equal("[MANUAL] Stern Godzilla Manual", el.GetAttribute("aria-label"));
    }
}
```

- [ ] **Step 2: Run — fails (no component)**

Run: `dotnet test tests/PinballWizard.Web.Tests --filter "FullyQualifiedName~CitationMarkerTests" --nologo`
Expected: FAIL (type not found).

- [ ] **Step 3: Create `CitationMarker.razor`**

```razor
@* CitationMarker — inline pinball-insert citation marker (design gap #3).
 * Option C (locked, modern-lcd.md): small numbered insert, accent-grounded glow,
 * hover tooltip "[SOURCE TYPE] Source name", click scrolls to + pulses the card.
 * Native <a href="#citation-N"> — CSP-safe, no circuit-dependent handler (works in
 * any render mode). One of the four locked Citation delight surfaces (FE-03/FE-09).
 *
 * DOM id is marker-{Number}-{Occurrence} so multiple markers for the same source
 * (same N, several sentences) keep unique ids; the card's left flipper targets
 * marker-N-1 (first use). Tooltip is derived from a cascaded ordinal-ordered
 * citation list so the marker and card share numbering (no positional drift).
 *@
@using PinballWizard.Application.Ai
<a class="citation-marker"
   id="@($"marker-{Number}-{Occurrence}")"
   href="@($"#citation-{Number}")"
   data-testid="citation-marker"
   data-citation-number="@Number"
   title="@Tooltip"
   aria-label="@Tooltip">@Number</a>

@code {
    [Parameter, EditorRequired] public int Number { get; set; }

    // 1-based occurrence among markers that share this Number (set by the tokenizer).
    [Parameter] public int Occurrence { get; set; } = 1;

    // Ordinal-ordered citation list (same order the cards are numbered), cascaded by
    // WizardAnswerStream. Used only to derive the hover tooltip.
    [CascadingParameter] public IReadOnlyList<Citation>? OrderedCitations { get; set; }

    // "[SOURCE TYPE] Source name". Null when no citation context — marker still
    // renders its number + anchor (graceful).
    private string? Tooltip
    {
        get
        {
            if (OrderedCitations is null || Number < 1 || Number > OrderedCitations.Count)
                return null;
            var c = OrderedCitations[Number - 1];
            return $"[{c.SourceType.ToString().ToUpperInvariant()}] {c.Title}";
        }
    }
}
```

`CitationMarker.razor.css` (scoped; accent-grounded glow, insert shape, reduced-motion-safe — no animation on the marker itself; the *card* pulses):

```css
.citation-marker {
    display: inline-flex;
    align-items: center;
    justify-content: center;
    min-width: 1.15em;
    height: 1.15em;
    padding: 0 0.3em;
    margin: 0 0.12em;
    border-radius: 2px; /* machined-edge insert, per modern-lcd.md */
    font-family: var(--pw-font-mono, monospace);
    font-size: 0.72em;
    line-height: 1;
    color: var(--pw-accent-grounded, #34d96a);
    border: 1px solid color-mix(in srgb, var(--pw-accent-grounded, #34d96a) 55%, transparent);
    background: color-mix(in srgb, var(--pw-accent-grounded, #34d96a) 12%, transparent);
    box-shadow: 0 0 6px color-mix(in srgb, var(--pw-accent-grounded, #34d96a) 35%, transparent);
    text-decoration: none;
    vertical-align: baseline;
    transition: box-shadow var(--pw-motion-fast, 120ms) ease;
}
.citation-marker:hover {
    box-shadow: 0 0 10px color-mix(in srgb, var(--pw-accent-grounded, #34d96a) 60%, transparent);
}
```

- [ ] **Step 4: Run — passes**

Run: `dotnet test tests/PinballWizard.Web.Tests --filter "FullyQualifiedName~CitationMarkerTests" --nologo`
Expected: PASS (2 tests).

- [ ] **Step 5: Commit**

```bash
git add src/PinballWizard.Web/Components/Citations/CitationMarker.razor src/PinballWizard.Web/Components/Citations/CitationMarker.razor.css tests/PinballWizard.Web.Tests/Components/Citations/CitationMarkerTests.cs
git commit -m "feat(web) CitationMarker inline pinball-insert (number + tooltip + #citation-N anchor)"
```

---

## Task 4: Number the citation cards + wire the left-flipper round-trip

**Files:**
- Modify: `src/PinballWizard.Web/Components/Citations/CitationStrip.razor`, `CitationGroup.razor`, `CitationCard.razor`
- Test: `tests/PinballWizard.Web.Tests/Components/Citations/CitationStripTests.cs`, `CitationCardTests.cs`

**Interfaces:**
- Consumes: `CitationCard.InAnswerAnchor` (from PR #463).
- Produces: each `CitationCard` carries `[Parameter] public int Ordinal` (1-based render order), renders DOM id `citation-{Ordinal}` + a visible number, and sets `InAnswerAnchor = "marker-{Ordinal}-1"` so the existing left flipper targets the first inline marker. The ordinal is assigned in `CitationStrip` (it already orders groups) and threaded through `CitationGroup`.

- [ ] **Step 1: Write the failing test (CitationStrip assigns sequential ordinals across groups)**

```csharp
[Fact]
public void Cards_get_sequential_ordinals_and_anchor_ids_across_groups()
{
    var citations = new List<Citation>
    {
        new("A", "https://a.com/1", RelevanceScore: 0.9),
        new("B", "https://b.com/1", RelevanceScore: 0.8),
        new("C", "https://a.com/2", RelevanceScore: 0.7),
    };
    var cut = RenderComponent<CitationStrip>(p => p.Add(x => x.Citations, citations));
    var cards = cut.FindAll("[data-testid='citation-card']");
    // Three cards, each with a unique citation-{N} id, N = 1..3 in render order.
    var ids = cards.Select(c => c.Id).ToList();
    Assert.Equal(new[] { "citation-1", "citation-2", "citation-3" }, ids);
}
```

(Confirm the real attribute the test should read — `id` on the card root. Adjust the assertion if `CitationCard`'s root is a `MudPaper` whose `id` is set via `@attributes`/`id=`.)

- [ ] **Step 2: Run — fails (no ordinals/ids today)**

Run: `dotnet test tests/PinballWizard.Web.Tests --filter "FullyQualifiedName~CitationStripTests.Cards_get_sequential_ordinals" --nologo`
Expected: FAIL.

- [ ] **Step 3: Assign ordinals in `CitationStrip.razor`**

`CitationStrip` already computes `groups` (relevance-ordered). Flatten to a single render order and pass a running ordinal into each `CitationGroup`, which forwards it per card. In `CitationStrip.razor`, replace the group loop so a shared counter increments across groups:

```razor
@{ var ordinal = 0; }
<div class="citation-strip-layout">
    @foreach (var group in groups)
    {
        <div class="citation-strip-group">
            <CitationGroup Host="@group.Host" Citations="@group.Citations" StartOrdinal="@ordinal" />
        </div>
        @{ ordinal += group.Citations.Count; }
    }
</div>
```

In `CitationGroup.razor`, add `[Parameter] public int StartOrdinal { get; set; }` and pass each card its ordinal:

```razor
@for (var i = 0; i < sorted.Count; i++)
{
    <CitationCard Citation="@sorted[i]" Ordinal="@(StartOrdinal + i + 1)" />
}
```

In `CitationCard.razor`, add the parameter + the id + visible number + the InAnswerAnchor wiring. Set the root element id to `citation-{Ordinal}`, render the number as a small badge in the source-type row, and set `InAnswerAnchor` only when the card actually has an inline marker. Since the card cannot itself know whether a marker exists, default `InAnswerAnchor = "marker-{Ordinal}-1"` and let the marker's absence simply mean the anchor target doesn't exist (the flipper still scrolls harmlessly). To avoid a dead flipper when there is NO marker at all, gate it on a new `[Parameter] public bool HasInlineMarker` passed from the strip (computed in Task 6 once the body is known) — for now default `HasInlineMarker=false` so the left flipper stays hidden until Task 6 wires it:

```razor
@code {
    [Parameter, EditorRequired] public Citation Citation { get; set; } = null!;
    [Parameter] public int Ordinal { get; set; }
    [Parameter] public bool HasInlineMarker { get; set; }
    private string? InAnswerAnchor => HasInlineMarker ? $"marker-{Ordinal}-1" : null;
}
```

Set `id="@($"citation-{Ordinal}")"` on the card root (`MudPaper` supports `id` via attribute) and render `<span class="citation-ordinal">@Ordinal</span>` in the header row.

- [ ] **Step 4: Run the citation tests — pass**

Run: `dotnet test tests/PinballWizard.Web.Tests --filter "FullyQualifiedName~Citation" --nologo`
Expected: PASS (existing + new; update any test that asserted the old header structure).

- [ ] **Step 5: Commit**

```bash
git add src/PinballWizard.Web/Components/Citations/ tests/PinballWizard.Web.Tests/Components/Citations/
git commit -m "feat(web) number citation cards (citation-N id + visible ordinal); wire left-flipper anchor gate"
```

---

## Task 5: `ToolTraceCitationExtractor` exposes the ordered `k`-index

**Files:**
- Modify: `src/PinballWizard.Infrastructure/Ai/Citations/ToolTraceCitationExtractor.cs`
- Test: `tests/PinballWizard.Infrastructure.Tests/Ai/Citations/ToolTraceCitationExtractorTests.cs`

**Interfaces:**
- Produces: alongside `Extract(response) → IReadOnlyList<Citation>`, a new method (or out-param) `ExtractWithSourceIndex(response) → (IReadOnlyList<Citation> Citations, IReadOnlyList<string> SourceIndex)` where `SourceIndex[k-1]` is the `SourceUrl` of the k-th `searchCorpus` hit **in tool-trace order** (the same order the prompt numbers sources). This is the `k → SourceUrl` table the reconciler needs.

- [ ] **Step 1: Write the failing test**

```csharp
[Fact]
public void ExtractWithSourceIndex_orders_searchCorpus_hits_by_tool_trace_appearance()
{
    // Arrange a fake AgentResponse with two searchCorpus tool results, the first
    // returning hits for urlA then urlB, the second returning urlC. (Reuse the test
    // builders already used by the existing extractor tests.)
    var response = FakeResponse.With(
        SearchCorpusResult(hits: ["https://a/1", "https://b/1"]),
        SearchCorpusResult(hits: ["https://c/1"]));

    var (citations, sourceIndex) = new ToolTraceCitationExtractor(/* deps */).ExtractWithSourceIndex(response);

    Assert.Equal(new[] { "https://a/1", "https://b/1", "https://c/1" }, sourceIndex);
}
```

(Use the existing extractor-test fixtures/builders for `AgentResponse`/`SearchCorpusResult` — mirror `ToolTraceCitationExtractorTests` setup.)

- [ ] **Step 2: Run — fails (method absent)**

Run: `dotnet test tests/PinballWizard.Infrastructure.Tests --filter "FullyQualifiedName~ExtractWithSourceIndex" --nologo`
Expected: FAIL.

- [ ] **Step 3: Implement `ExtractWithSourceIndex`**

Refactor the existing `Extract` so its `searchCorpus`-hit enumeration also records each hit's `DocumentUrl` (the `SourceUrl`) into an ordered list as it walks the tool results. Return both. `Extract` keeps its signature by delegating:

```csharp
public IReadOnlyList<Citation> Extract(AgentResponse? response)
    => ExtractWithSourceIndex(response).Citations;

public (IReadOnlyList<Citation> Citations, IReadOnlyList<string> SourceIndex)
    ExtractWithSourceIndex(AgentResponse? response)
{
    var citations = new List<Citation>();
    var sourceIndex = new List<string>();
    // ... existing walk over response tool results ...
    // For each searchCorpus hit, in order:
    //     sourceIndex.Add(hit.DocumentUrl);
    //     citations.Add(MapHit(hit));   // existing mapping (DocumentChunkId: hit.DocumentId, SourceUrl: hit.DocumentUrl)
    // getMachineByTitle / OPDB-regex citations are appended to `citations` but NOT to sourceIndex
    //     (they are not numbered sources the model cites with [[cite:k]]).
    return (citations, sourceIndex);
}
```

- [ ] **Step 4: Run — passes; existing extractor tests still green**

Run: `dotnet test tests/PinballWizard.Infrastructure.Tests --filter "FullyQualifiedName~ToolTraceCitationExtractor" --nologo`
Expected: PASS (new + existing).

- [ ] **Step 5: Commit**

```bash
git add src/PinballWizard.Infrastructure/Ai/Citations/ToolTraceCitationExtractor.cs tests/PinballWizard.Infrastructure.Tests/Ai/Citations/ToolTraceCitationExtractorTests.cs
git commit -m "feat(ai) ToolTraceCitationExtractor exposes ordered searchCorpus source index (k->SourceUrl)"
```

---

## Task 6: `InlineCitationReconciler` (pure `k→N` rewrite)

**Files:**
- Create: `src/PinballWizard.Application/Ai/Citations/InlineCitationReconciler.cs`
- Test: `tests/PinballWizard.Application.Tests/Ai/Citations/InlineCitationReconcilerTests.cs`

**Interfaces:**
- Consumes: the `SourceIndex` from Task 5; `citations` (final, render order = the order they will be numbered); the answer text.
- Produces:
```csharp
public sealed record ReconcileResult(
    string RewrittenText,          // [[cite:k]] -> [[cite:N]]; unmatched dropped
    IReadOnlySet<int> MarkedOrdinals, // card ordinals (N) that have >=1 inline marker
    int TotalTokens, int RenderedTokens, int DroppedTokens);

public static ReconcileResult Reconcile(
    string answerText,
    IReadOnlyList<Citation> citations,   // index i -> card ordinal N = i+1
    IReadOnlyList<string> sourceIndex);  // sourceIndex[k-1] = SourceUrl of source k
```

- [ ] **Step 1: Write the failing tests**

```csharp
using PinballWizard.Application.Ai;
using PinballWizard.Application.Ai.Citations;
using Xunit;

namespace PinballWizard.Application.Tests.Ai.Citations;

public sealed class InlineCitationReconcilerTests
{
    private static Citation Cite(string url) => new("t", url);

    [Fact]
    public void Maps_k_to_card_ordinal_by_sourceurl()
    {
        // sources: k1=urlA, k2=urlB. citations render order: [urlB, urlA] => urlB=N1, urlA=N2.
        var citations = new[] { Cite("https://b/1"), Cite("https://a/1") };
        var sourceIndex = new[] { "https://a/1", "https://b/1" };
        var r = InlineCitationReconciler.Reconcile("X [[cite:1]] and Y [[cite:2]].", citations, sourceIndex);
        Assert.Equal("X [[cite:2]] and Y [[cite:1]].", r.RewrittenText); // k1(urlA)->N2, k2(urlB)->N1
        Assert.Equal(new HashSet<int> { 1, 2 }, r.MarkedOrdinals);
        Assert.Equal(2, r.RenderedTokens);
        Assert.Equal(0, r.DroppedTokens);
    }

    [Fact]
    public void Drops_unmatched_token_and_counts_it()
    {
        var citations = new[] { Cite("https://a/1") };          // only urlA -> N1
        var sourceIndex = new[] { "https://a/1", "https://z/9" }; // k2=urlZ has no citation
        var r = InlineCitationReconciler.Reconcile("A [[cite:1]] Z [[cite:2]].", citations, sourceIndex);
        Assert.Equal("A [[cite:1]] Z .", r.RewrittenText);       // k2 dropped (token removed)
        Assert.Equal(new HashSet<int> { 1 }, r.MarkedOrdinals);
        Assert.Equal(2, r.TotalTokens);
        Assert.Equal(1, r.RenderedTokens);
        Assert.Equal(1, r.DroppedTokens);
    }

    [Fact]
    public void Out_of_range_or_garbage_k_is_dropped()
    {
        var r = InlineCitationReconciler.Reconcile("[[cite:9]] [[cite:x]]", new[] { Cite("https://a/1") }, new[] { "https://a/1" });
        Assert.Equal(" [[cite:x]]", r.RewrittenText); // k=9 out of range -> dropped; [[cite:x]] not a valid token -> left literal
        Assert.Equal(1, r.TotalTokens);               // only [[cite:9]] counts as a cite token; [[cite:x]] is non-numeric
        Assert.Equal(1, r.DroppedTokens);
    }
}
```

- [ ] **Step 2: Run — fails (no reconciler)**

Run: `dotnet test tests/PinballWizard.Application.Tests --filter "FullyQualifiedName~InlineCitationReconcilerTests" --nologo`
Expected: FAIL.

- [ ] **Step 3: Implement the reconciler**

```csharp
using System.Text;
using System.Text.RegularExpressions;

namespace PinballWizard.Application.Ai.Citations;

/// Rewrites model-emitted [[cite:k]] markers (k = searchCorpus source ordinal) into
/// [[cite:N]] (N = citation card ordinal). Unmatched markers are dropped (OBS-01:
/// never render a fake marker). Pure + deterministic — no Foundry, fully unit-tested.
public static class InlineCitationReconciler
{
    public sealed record ReconcileResult(
        string RewrittenText,
        IReadOnlySet<int> MarkedOrdinals,
        int TotalTokens, int RenderedTokens, int DroppedTokens);

    // Numeric payload only; non-numeric [[cite:x]] is not a cite token (left literal).
    private static readonly Regex CiteToken = new(@"\[\[cite:(\d+)\]\]", RegexOptions.Compiled);

    private static string Normalize(string url) => url.Trim().TrimEnd('/').ToLowerInvariant();

    public static ReconcileResult Reconcile(
        string answerText,
        IReadOnlyList<Citation> citations,
        IReadOnlyList<string> sourceIndex)
    {
        // SourceUrl -> card ordinal N (1-based render order). First wins on dup URLs.
        var urlToOrdinal = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < citations.Count; i++)
            urlToOrdinal.TryAdd(Normalize(citations[i].SourceUrl), i + 1);

        var marked = new HashSet<int>();
        int total = 0, rendered = 0, dropped = 0;

        var rewritten = CiteToken.Replace(answerText, m =>
        {
            total++;
            var k = int.Parse(m.Groups[1].Value);
            if (k >= 1 && k <= sourceIndex.Count
                && urlToOrdinal.TryGetValue(Normalize(sourceIndex[k - 1]), out var n))
            {
                rendered++; marked.Add(n);
                return $"[[cite:{n}]]";
            }
            dropped++;
            return string.Empty; // drop the token (truthful-only)
        });

        return new ReconcileResult(rewritten, marked, total, rendered, dropped);
    }
}
```

- [ ] **Step 4: Run — passes**

Run: `dotnet test tests/PinballWizard.Application.Tests --filter "FullyQualifiedName~InlineCitationReconcilerTests" --nologo`
Expected: PASS (3 tests).

- [ ] **Step 5: Commit**

```bash
git add src/PinballWizard.Application/Ai/Citations/InlineCitationReconciler.cs tests/PinballWizard.Application.Tests/Ai/Citations/InlineCitationReconcilerTests.cs
git commit -m "feat(ai) InlineCitationReconciler: k->N rewrite, drop+count unmatched"
```

---

## Task 7: Wire reconciliation + token suppression into `AiRouter`

**Files:**
- Modify: `src/PinballWizard.Application/Ai/AiRouter.cs` (post-extraction ≈ line 1000; `TextDelta` path ≈ line 747)
- Test: `tests/PinballWizard.Application.Tests/Ai/` (extend the existing AiRouter test that exercises `ApplyPostAgentGuardrailsAsync` with a fake response)

**Interfaces:**
- Consumes: `ExtractWithSourceIndex` (Task 5), `InlineCitationReconciler.Reconcile` (Task 6), the meters (Task 1).
- Produces: `WizardAnswer.Text` carries `[[cite:N]]` markers (reconciled); `TextDelta` chunks never carry raw `[[cite:k]]`.

- [ ] **Step 1: Write the failing test (reconciliation applied post-extraction)**

Extend the AiRouter test that builds a fake `AgentResponse`. Construct a response whose answer text contains `[[cite:1]]` and whose `searchCorpus` source index + extracted citation share a SourceUrl. Assert the returned `WizardAnswer.Text` contains `[[cite:1]]` rewritten to the matching ordinal and contains no raw un-reconciled token. (Mirror the existing `ApplyPostAgentGuardrailsAsync` test harness; if none exists, add a focused one using the project's existing `IAiRouter` test doubles.)

```csharp
[Fact]
public async Task PostGuardrails_rewrites_inline_markers_to_card_ordinals()
{
    // response: answer "Flippers persist [[cite:1]]." ; searchCorpus source k1 = urlA ;
    // extracted citation has SourceUrl urlA (render ordinal 1).
    var answer = await InvokePostGuardrails(/* fake response */);
    Assert.Contains("[[cite:1]]", answer.Text);
    Assert.DoesNotContain("[[cite:99]]", answer.Text);
}
```

- [ ] **Step 2: Run — fails**

Run: `dotnet test tests/PinballWizard.Application.Tests --filter "FullyQualifiedName~PostGuardrails_rewrites_inline_markers" --nologo`
Expected: FAIL.

- [ ] **Step 3: Insert reconciliation after citation extraction (≈ line 1000)**

Replace the existing `var citations = _toolTraceExtractor.Extract(response);` with the index-aware call, then reconcile `responseText` before confidence is computed:

```csharp
var (citations, sourceIndex) = _toolTraceExtractor.ExtractWithSourceIndex(response);
PinballWizardTelemetry.AiCitationsExtracted.Add(
    citations.Count, new KeyValuePair<string, object?>("source", _toolTraceExtractor.SourceTag));

// (multi-turn inheritance block stays here, unchanged — it mutates `citations`.)

var reconciled = InlineCitationReconciler.Reconcile(responseText, citations, sourceIndex);
responseText = reconciled.RewrittenText;
PinballWizardTelemetry.AiInlineMarkerTotal.Add(reconciled.TotalTokens);
PinballWizardTelemetry.AiInlineMarkerRendered.Add(reconciled.RenderedTokens);
if (reconciled.DroppedTokens > 0)
    PinballWizardTelemetry.AiInlineMarkerDropped.Add(
        reconciled.DroppedTokens, new KeyValuePair<string, object?>("reason", "no_matching_citation"));
```

`responseText` (set at line 924 via `StripInlineMarkdownLinks`) is then used in the success `WizardAnswer` constructor (line 1148) unchanged. Confidence (line 1046) now runs on the reconciled text — acceptable (markers are sparse; coverage signal is left as-is per spec §8). Note: `reconciled.MarkedOrdinals` is not threaded to the frontend in this plan; the frontend infers "has marker" by scanning the body — see Task 8 Step 3.

- [ ] **Step 4: Suppress raw tokens in the streaming `TextDelta` path (≈ line 747)**

Where `streamChunks.Add(new AnswerChunk.TextDelta(update.Text));` is emitted, strip cite tokens from the *delta* text only (the full unstripped text is reconstructed post-stream from `accumulatedMessages`):

```csharp
if (!string.IsNullOrEmpty(update.Text))
{
    var visible = InlineCitationReconciler.StripCiteTokens(update.Text); // see below
    if (!string.IsNullOrEmpty(visible))
        streamChunks.Add(new AnswerChunk.TextDelta(visible));
}
```

Add a small helper to the reconciler (and a unit test in Task 6's file):

```csharp
public static string StripCiteTokens(string text) => CiteToken.Replace(text, string.Empty);
```

> Caveat to verify during implementation: a `[[cite:k]]` token may be split across two streaming `update.Text` deltas. If the project's streaming coalesces deltas before emit (check the `fragment` accumulator near line 747), strip post-coalesce. If deltas are emitted raw per-update, add a 12-char carry-over buffer so a token split across deltas is still suppressed. Pick whichever matches the actual coalescing and note it in the report.

- [ ] **Step 5: Run the AiRouter + reconciler tests — pass; build clean**

Run: `dotnet test tests/PinballWizard.Application.Tests --filter "FullyQualifiedName~AiRouter|FullyQualifiedName~InlineCitationReconciler" --nologo`
Run: `dotnet build src/PinballWizard.Application/PinballWizard.Application.csproj --nologo -warnaserror`
Expected: PASS; 0 warnings.

- [ ] **Step 6: Commit**

```bash
git add src/PinballWizard.Application/Ai/AiRouter.cs src/PinballWizard.Application/Ai/Citations/InlineCitationReconciler.cs tests/PinballWizard.Application.Tests/
git commit -m "feat(ai) AiRouter reconciles inline markers post-extraction + suppresses raw tokens mid-stream"
```

---

## Task 8: Light the left flipper when a card has a marker (frontend join)

**Files:**
- Modify: `src/PinballWizard.Web/Components/Citations/CitationStrip.razor` (compute `HasInlineMarker` per ordinal from the body), `WizardAnswerStream.razor` (pass the answer body to `CitationStrip` so it can detect markers)
- Test: `tests/PinballWizard.Web.Tests/Components/Citations/CitationStripTests.cs`

**Interfaces:**
- Consumes: `CitationCard.HasInlineMarker` (Task 4); the answer body text (to detect which ordinals have markers).
- Produces: a card whose ordinal appears as `[[cite:N]]` in the body gets `HasInlineMarker=true` → its left flipper shows; otherwise hidden.

- [ ] **Step 1: Write the failing test**

```csharp
[Fact]
public void Left_flipper_shows_only_for_cards_referenced_in_the_body()
{
    var citations = new[] { new Citation("A", "https://a/1", RelevanceScore: 0.9),
                            new Citation("B", "https://b/1", RelevanceScore: 0.8) };
    // Body cites only ordinal 1.
    var cut = RenderComponent<CitationStrip>(p => p
        .Add(x => x.Citations, citations)
        .Add(x => x.AnswerBody, "Grounded claim [[cite:1]]."));
    var inAnswer = cut.FindAll("[data-testid='citation-flipper-in-answer']");
    Assert.Single(inAnswer); // only card 1 lights its left flipper
}
```

- [ ] **Step 2: Run — fails (no `AnswerBody` param / no marker detection)**

Run: `dotnet test tests/PinballWizard.Web.Tests --filter "FullyQualifiedName~Left_flipper_shows_only_for_cards" --nologo`
Expected: FAIL.

- [ ] **Step 3: Add `AnswerBody` to `CitationStrip` + detect marked ordinals**

```razor
@code {
    [Parameter, EditorRequired] public IReadOnlyList<Citation> Citations { get; set; } = [];
    [Parameter] public string? AnswerBody { get; set; }

    private static readonly System.Text.RegularExpressions.Regex CiteN =
        new(@"\[\[cite:(\d+)\]\]", System.Text.RegularExpressions.RegexOptions.Compiled);

    private HashSet<int> MarkedOrdinals()
        => AnswerBody is null ? []
           : CiteN.Matches(AnswerBody).Select(m => int.Parse(m.Groups[1].Value)).ToHashSet();
}
```

Pass `HasInlineMarker="@marked.Contains(ordinal)"` down through `CitationGroup` to `CitationCard` (add a `HasInlineMarker` lookup the group forwards, or pass the `MarkedOrdinals` set into `CitationGroup` and let it compute per card).

**Shared ordering so `N` is consistent for cards AND marker tooltips.** Introduce one helper, `CitationOrdering.InRenderOrder(IReadOnlyList<Citation>) → IReadOnlyList<Citation>` (in `src/PinballWizard.Web/Components/Citations/CitationOrdering.cs`), that produces the exact flattened render order `CitationStrip` already uses (group by host via `CitationStrip.BuildGroups`, groups by max RelevanceScore desc, within-group by score desc, then flatten). Refactor `CitationStrip` to assign card ordinals by indexing this list (so `N` = `InRenderOrder` index + 1) instead of an ad-hoc running counter. Then `CitationMarker`'s cascaded tooltip lookup (`OrderedCitations[Number-1]`, Task 3) lines up exactly with the card numbering.

In `WizardAnswerStream.razor`: cascade the ordered list around the body so markers can read it, and pass the body to the strip:

```razor
<CascadingValue Value="@PinballWizard.Web.Components.Citations.CitationOrdering.InRenderOrder(answer.Citations)" Name="OrderedCitations">
    <MarkdownContent Text="@answer.Text" />
</CascadingValue>
<CitationStrip Citations="@answer.Citations" AnswerBody="@answer.Text" />
```

(`CitationMarker.OrderedCitations` uses an unnamed `[CascadingParameter]`; if a `Name` is needed to disambiguate from other cascading values, add `Name="OrderedCitations"` to the parameter attribute to match.)

- [ ] **Step 4: Run citation + wizard tests — pass**

Run: `dotnet test tests/PinballWizard.Web.Tests --filter "FullyQualifiedName~Citation|FullyQualifiedName~WizardAnswerStream" --nologo`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/PinballWizard.Web/Components/
git commit -m "feat(web) light citation left-flipper only for body-referenced cards (close nav loop)"
```

---

## Task 9: Pulse the card on marker click

**Files:**
- Modify: `src/PinballWizard.Web/wwwroot/app.js` (add a hashchange pulse), `src/PinballWizard.Web/Components/Citations/CitationCard.razor.css` (pulse keyframe)
- Test: manual + `CitationCard.razor.css` reduced-motion guard (assert via existing render test that the pulse class is reduced-motion-gated — CSS-only, no bUnit assertion needed beyond presence).

**Interfaces:**
- Consumes: the `#citation-N` anchors (Task 4) and `#marker-N-1` anchors (Task 3/4).
- Produces: clicking a marker (`#citation-N`) briefly pulses the target card border; reduced-motion users get no animation (the scroll still happens).

- [ ] **Step 1: Add a hashchange pulse to `app.js`**

```javascript
// ── Citation marker pulse ───────────────────────────────────────────────────
// When the URL hash points at a citation card (#citation-N) or a marker
// (#marker-N-x), add a one-shot pulse class to the target so the user sees where
// they landed. CSS gates the animation behind prefers-reduced-motion.
window.pinwiz = window.pinwiz || {};
window.pinwiz._pulseHashTarget = function () {
    var id = (location.hash || '').slice(1);
    if (!id) return;
    var el = document.getElementById(id);
    if (!el) return;
    el.classList.remove('pw-pulse');
    void el.offsetWidth;            // restart the animation
    el.classList.add('pw-pulse');
};
window.addEventListener('hashchange', window.pinwiz._pulseHashTarget);
```

- [ ] **Step 2: Add the pulse keyframe (reduced-motion-safe) to `CitationCard.razor.css`**

```css
:global(.pw-pulse) {
    animation: pw-card-pulse 900ms ease-out 1;
}
@keyframes pw-card-pulse {
    0%   { box-shadow: 0 0 0 0 color-mix(in srgb, var(--pw-accent-grounded, #34d96a) 55%, transparent); }
    100% { box-shadow: 0 0 0 6px transparent; }
}
@media (prefers-reduced-motion: reduce) {
    :global(.pw-pulse) { animation: none; }
}
```

- [ ] **Step 3: Build + run the Web test suite**

Run: `dotnet build src/PinballWizard.Web/PinballWizard.Web.csproj --nologo -warnaserror`
Run: `dotnet test tests/PinballWizard.Web.Tests --filter "FullyQualifiedName~Citation" --nologo`
Expected: 0 warnings; PASS.

- [ ] **Step 4: Commit**

```bash
git add src/PinballWizard.Web/wwwroot/app.js src/PinballWizard.Web/Components/Citations/CitationCard.razor.css
git commit -m "feat(web) pulse the citation card/marker on hash navigation (reduced-motion safe)"
```

---

## Task 10: Prompt changes — numbered sources + `[[cite:k]]` emission

**Files:**
- Modify: `src/PinballWizard.Application/Ai/Agents/Wizard.md` (Step 5 corpus-content formatting + Step 7), `Repair.md` (Step 3), `Rules.md` (Step 3), `Valuation.md` (Step 4)

**Interfaces:**
- Produces: sub-agents emit `[[cite:k]]` at grounded sentences; the Wizard numbers the sources it passes in the order it received them from `searchCorpus` (matching the server's `SourceIndex` order from Task 5).

- [ ] **Step 1: `Wizard.md` — number the corpus-content block**

In Step 5 (the corpus-content block format, ≈ lines 55–68), instruct the Wizard to present sources to the sub-agent as a **numbered list in the order `searchCorpus` returned them**, e.g.:

```
Number the corpus sources you pass to the sub-agent sequentially in the exact order
searchCorpus returned them — "Source 1", "Source 2", … — and keep that numbering
stable. Each numbered source shows its document_url, section heading, and page range.
```

In Step 7 add: *"Sub-agent prose may contain `[[cite:k]]` markers — pass them through verbatim. Never renumber or strip them."*

- [ ] **Step 2: Each sub-agent (`Repair.md` Step 3, `Rules.md` Step 3, `Valuation.md` Step 4) — emit markers**

Append to the citation instruction in each:

```
When a sentence is grounded in a numbered source from the corpus content you were
given, end that sentence with [[cite:k]] where k is that source's number (e.g.
"…persists after the switch test passes [[cite:2]]."). Cite the source you actually
used; never invent a number. A sentence may carry more than one marker if it draws
on more than one source. Sentences you did not ground from a source need no marker.
These markers are the only citation syntax you add — keep prose otherwise clean.
```

- [ ] **Step 3: Bump the prompt version + verify the embedded-resource CHECK (RAG-05)**

Per the `rag-agent` standard RAG-05, a prompt change must bump `EmbeddedResourceAgentPromptProvider.CurrentPromptVersion` in the same commit. Update it:

Run: `rg -n "CurrentPromptVersion" src/PinballWizard.Application/Ai/EmbeddedResourceAgentPromptProvider.cs`
Then increment the version constant (follow its existing `vN.YYYY.MM` format).

- [ ] **Step 4: Build (embeds the prompts) + run the prompt-provider tests**

Run: `dotnet build src/PinballWizard.Application/PinballWizard.Application.csproj --nologo -warnaserror`
Run: `dotnet test tests/PinballWizard.Application.Tests --filter "FullyQualifiedName~PromptProvider|FullyQualifiedName~AgentPrompt" --nologo`
Expected: 0 warnings; PASS.

- [ ] **Step 5: Commit**

```bash
git add src/PinballWizard.Application/Ai/Agents/ src/PinballWizard.Application/Ai/EmbeddedResourceAgentPromptProvider.cs
git commit -m "feat(ai) prompts: numbered corpus sources + [[cite:k]] emission; bump prompt version (RAG-05)"
```

---

## Task 11: ADR follow-ups + full verification + eval re-baseline

**Files:**
- Modify: `docs/adr/0026-user-delight-frontend-and-streaming.md`, `docs/adr/0022-*.md` (append-only follow-up entries)

- [ ] **Step 1: Append ADR follow-up entries**

To `docs/adr/0026` (§8 citation surface): a dated follow-up describing the inline-marker layer, the `[[cite:N]]` body contract, the marker↔card numbering, and that markers resolve at `Final`. To `docs/adr/0022` (citation extraction): the `[[cite:k]]` inline-token contract + the `ExtractWithSourceIndex` `k→SourceUrl` index + the reconciliation drop-on-no-match rule. Both append-only (do not edit existing text).

- [ ] **Step 2: Full build + full Web/Application/Infrastructure test pass**

Run: `dotnet build PinballWizard.slnx --nologo -warnaserror`
Run: `dotnet test tests/PinballWizard.Web.Tests tests/PinballWizard.Application.Tests tests/PinballWizard.Infrastructure.Tests --filter "FullyQualifiedName~Citation|FullyQualifiedName~Marker|FullyQualifiedName~AiRouter|FullyQualifiedName~Tokenizer" --nologo`
Expected: 0 warnings; all PASS.

- [ ] **Step 3: Standards self-audit**

Run `/standards-audit` and `/local-review` on the branch. FE-09 (cards uncollapsed — unchanged), RAG-05 (prompt version bumped — Task 10), OBS-01 (dropped markers metered, not faked) all apply. Record the verdicts in the PR description.

- [ ] **Step 4: Eval re-baseline (operational — requires Foundry/Azure; may be human-run)**

The prompt change requires re-baselining evals (guardrails goal #5; 5% citation-precision regression gate). Run the harness against the live model and compare to the rolling baseline:

```bash
dotnet run --project src/PinballWizard.Cli -- --eval
```
Compare `data/eval/results/wizard.{timestamp}.json` citation-precision to the prior baseline. **Gate:** no >5% citation-precision regression. If the run cannot execute here (no Azure creds / cost), flag it explicitly in the PR as a required human step before merge — do NOT mark the feature done without it.

- [ ] **Step 5: Commit + open PR**

```bash
git add docs/adr/
git commit -m "docs(adr) ADR-0026/0022 follow-ups for inline citation markers"
```
Then open the PR (base `main` if #463 has merged, else stacked on `feat/citation-flippers-uncollapse`), add the `claude-code` label, and put the full URL in the response. PR body records: `/standards-audit` + `/local-review` verdicts and the eval re-baseline result (or that it's a pending human step).

---

## Deferred (per spec §9)

- **Inline entity portals** (machine/manufacturer → outbound links). The tokenizer inline-insert mechanism (Task 2) is built to register a `portal` kind with no rework.
- **`citation_coverage`** tightening to use explicit markers (spec §8).
- **Live mid-stream markers** (spec §6 — markers resolve at `Final`).
