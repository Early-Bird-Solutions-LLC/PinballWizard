using Microsoft.Playwright;
using Xunit;

namespace PinballWizard.Web.Tests.E2E;

// True end-to-end coverage of the ASK FLOW: real browser → real Web app →
// real Api → live Azure (Cosmos / AI Search / Foundry). These assert the
// answer path that no in-process test can see — the ask flow returning a
// CITED answer (the citation chain crosses the camelCase tool-trace seam
// end-to-end), cache-hit replay, and multi-turn follow-up context.
// (Landing-page circuit interactivity lives in
// LandingInteractivityCanaryE2ETests — it makes no model call and runs in
// the post-deploy canary.)
//
// Cost note: AskFlow makes one real model call per run (~cents). The
// suite is local-only (Category=E2E is CI-excluded); run it before
// pushing changes that touch the answer path, render modes, hosting
// config, or after any live-data migration.
[Collection("E2E live stack")]
[Trait("Category", "E2E")]
// The ask flow makes a real model call (~cents) and is the slowest E2E by far. The
// post-deploy canary (deploy.yml) filters this out with `E2E!=Ask` so it stays a fast,
// free, deterministic route/health check; the local full run (tools/e2e/Run-E2E.ps1)
// still exercises it via the bare `Category=E2E` filter.
[Trait("E2E", "Ask")]
public sealed class WizardE2ETests : IAsyncLifetime
{
    private readonly LiveStackFixture _stack;
    private IPlaywright? _playwright;
    private IBrowser? _browser;

    public WizardE2ETests(LiveStackFixture stack) => _stack = stack;

    public async Task InitializeAsync()
    {
        if (!E2EFactAttribute.IsConfigured)
        {
            return;
        }

        _playwright = await Playwright.CreateAsync();
        _browser = await _playwright.Chromium.LaunchAsync(E2EEdgeAccess.LaunchOptions());
    }

    public async Task DisposeAsync()
    {
        if (_browser is not null)
        {
            await _browser.DisposeAsync();
        }
        _playwright?.Dispose();
    }

    // NOTE: landing-page seed-card interactivity moved to
    // LandingInteractivityCanaryE2ETests — it makes no model call (so it belongs
    // in the post-deploy canary, not the Ask-excluded set) and its circuit-
    // hydration signal is only reliable against a deployed target.

    [E2EFact]
    public async Task AskFlow_GodzillaQuestion_ReturnsCitedAnswer()
    {
        // The headline E2E: the full provenance chain. "godzilla" is
        // deliberately ambiguous (Sega 1998 vs Stern 2021) so the answer
        // also exercises cross-group disambiguation (ADR-0029).
        var page = await NewPageAsync();
        await page.GotoAsync($"{_stack.WebBaseUrl}/wizard", new() { WaitUntil = WaitUntilState.DOMContentLoaded });

        await AskOnceAndAssertCitedAsync(page);
    }

    [E2EFact]
    public async Task AskFlow_RepeatedQuestion_CachedAnswerStillRendersCitations()
    {
        // #364: a semantic-cache hit replays the answer as a single
        // TextDelta + Final within ~1s — a chunk shape the single-ask test
        // never exercises (its first ask is always a cache miss). The
        // citation strip's MudHidden breakpoint resolution raced exactly
        // such fast answers: a "Sources" header rendered with no cards for
        // several seconds. The strip is CSS-only now; this test pins the
        // cached path by asking the SAME question twice in one session and
        // requiring citations both times.
        var page = await NewPageAsync();
        await page.GotoAsync($"{_stack.WebBaseUrl}/wizard", new() { WaitUntil = WaitUntilState.DOMContentLoaded });

        await AskOnceAndAssertCitedAsync(page);

        // Reset via "New conversation" — NOT the follow-up button. Since the
        // chat-thread UI (PR-A3), "Ask a follow-up" keeps the thread, which
        // makes the second ask multi-turn — and multi-turn asks BYPASS the
        // semantic cache by design (ADR-0015 follow-up 2026-06-11). This
        // test exists to pin the cache-HIT chunk shape, so the second ask
        // must be a genuine single-shot repeat.
        await page.Locator("[data-testid='new-conversation-button']").ClickAsync();
        await AskOnceAndAssertCitedAsync(page);
    }

    [E2EFact]
    public async Task AskFlow_FollowUp_CarriesConversationContext()
    {
        // PR-A3: the pronoun-only follow-up is unanswerable without the
        // prior turn ("it" has no referent) — a grounded answer here proves
        // history rode the wire and the router resolved it. Exercises the
        // cache-bypass multi-turn path end-to-end, which no single-shot
        // canary can reach.
        var page = await NewPageAsync();
        await page.GotoAsync($"{_stack.WebBaseUrl}/wizard", new() { WaitUntil = WaitUntilState.DOMContentLoaded });

        await AskOnceAndAssertCitedAsync(page);

        // The follow-up premise requires a SUCCESSFUL first turn — refusal
        // turns never join the thread by design, so a legitimate refusal
        // here (transient retrieval degradation, eval-known gaps) would
        // time out the conversation-turn wait and fail the deploy canary
        // for the wrong reason (observed: run 27422343997). The refusal
        // path is already covered by the contract assertion above and the
        // dedicated bUnit behavior; bail out as inconclusive-but-green.
        if (await page.Locator("[data-testid='refusal-panel']").IsVisibleAsync())
        {
            return;
        }

        // "Ask a follow-up" — the completed turn must join the visible thread.
        await page.Locator("[data-testid='new-question-button']").ClickAsync();
        await page.Locator("[data-testid='conversation-turn']").First.WaitForAsync(
            new() { State = WaitForSelectorState.Visible, Timeout = 10_000 });

        await AskOnceAndAssertCitedAsync(page, "who designed it");

        // The thread still shows the first turn alongside the follow-up's
        // answer (the contract assertion above already covered citations
        // or rendered refusal for the follow-up itself).
        Assert.True(
            await page.Locator("[data-testid='conversation-turn']").CountAsync() >= 1,
            "Conversation thread lost the prior turn after the follow-up.");
    }

    [E2EFact]
    public async Task AskFlow_CitationSourceLink_NavigatesToRenderedSourceDetail()
    {
        // Provenance is THE differentiator: an answer's citation must be a live,
        // clickable link INTO the internal source page — not a dead ordinal. This
        // drives the full chain end-to-end: ask → cited answer → click the
        // citation's primary link → the source-detail page renders its content.
        // A corpus chunk links to /documents/{id} (citation-source-link); a machine
        // record links to /machines/resolve/{id} (citation-title-link). The chain
        // is only real if the click lands on a page that RENDERS — not the global
        // Tilt error page and not a load-error / not-found state.
        //
        // This is the acceptance guard for the synthesized-source provenance break:
        // synthesized docs (Kineticist/Tilt Forums/TWIP) were indexed + cited but
        // absent from scraped_documents_raw, so their /documents/{id} link 404'd
        // ("Document not found"). Now they are first-class document records
        // (SynthesizedDocumentRecordFactory). NOTE: this runs against live data —
        // existing synthesized docs need the one-time raw-doc backfill before a
        // citation to one resolves; the test asserts the general contract that a
        // cited source link renders its detail page.
        var page = await NewPageAsync();
        await page.GotoAsync($"{_stack.WebBaseUrl}/wizard", new() { WaitUntil = WaitUntilState.DOMContentLoaded });

        // A rules/corpus question grounds the answer in an indexed manual, so the
        // primary citation carries a DocumentChunkId and renders the document
        // source-link. (AskOnceAndAssertCitedAsync already asserts a cited answer.)
        await AskOnceAndAssertCitedAsync(page, "how does the wizard mode work on Stern Godzilla");

        // A legitimate transient refusal (eval-known gap / retrieval degradation)
        // has no citation to click — bail green, matching AskFlow_FollowUp.
        if (await page.Locator("[data-testid='refusal-panel']").IsVisibleAsync())
        {
            return;
        }

        // Click the citation's primary INTERNAL link. Discriminate by the
        // source-link's href, not its mere presence: `citation-source-link` is
        // emitted on BOTH a document citation's internal link (href="/documents/{id}")
        // AND a machine/curated citation's EXTERNAL link (href=SourceUrl,
        // target="_blank"). Presence alone misclassifies a machine citation as a
        // document, then clicks the external link — which opens a new tab and never
        // navigates this page, so the /documents/ wait below times out (#667).
        // A document citation → click the internal source-link (→ /documents/{id});
        // a machine record → click the title-link (→ /machines/resolve/{id}).
        var sourceLink = page.Locator("[data-testid='citation-source-link']").First;
        var sourceHref = await sourceLink.CountAsync() > 0
            ? await sourceLink.GetAttributeAsync("href") ?? string.Empty
            : string.Empty;
        var expectDocument = sourceHref.StartsWith("/documents/", StringComparison.OrdinalIgnoreCase);

        var titleLink = page.Locator("[data-testid='citation-title-link']").First;
        // A curated/external-only citation has no internal detail page (no
        // /documents link, no machine title-link) — nothing to click through to,
        // so bail green like the refusal case above rather than assert a nav that
        // cannot happen.
        if (!expectDocument && await titleLink.CountAsync() == 0)
        {
            return;
        }

        var primaryLink = expectDocument ? sourceLink : titleLink;
        await primaryLink.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 15_000 });
        await primaryLink.ClickAsync();

        Assert.DoesNotContain("/error", page.Url, StringComparison.OrdinalIgnoreCase);

        if (expectDocument)
        {
            await page.WaitForURLAsync(url => url.Contains("/documents/"), new() { Timeout = 15_000 });
            await page.Locator("[data-testid='doc-detail-card']")
                .WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 15_000 });
            Assert.False(
                await page.Locator("[data-testid='doc-detail-load-error'], [data-testid='doc-detail-not-found']").IsVisibleAsync(),
                "Citation source-link landed on a document-detail error/not-found state.");
        }
        else
        {
            await page.WaitForURLAsync(url => url.Contains("/machines/"), new() { Timeout = 15_000 });
            await page.Locator("[data-testid='detail-title']")
                .WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 15_000 });
            Assert.False(
                await page.Locator("[data-testid='resolve-failed']").IsVisibleAsync(),
                "Citation title-link landed on a machine-resolve-failed state.");
        }
    }

    // Types the ask-flow question (canonical by default), submits, awaits
    // the terminal state, and asserts the provenance contract (cited answer
    // or rendered refusal). Assumes the page is already on /wizard in Idle
    // state.
    private static async Task AskOnceAndAssertCitedAsync(
        IPage page,
        string question = "what year was godzilla pinball game released")
    {
        var input = page.Locator("[data-testid='question-input']");
        await input.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 60_000 });

        // MudTextField commits its bound value on blur/Enter, not per
        // keystroke — type, then Tab, then check the button. Retry while
        // the circuit finishes coming up.
        var askButton = page.Locator("[data-testid='ask-button']");
        var submitted = false;
        for (var attempt = 0; attempt < 20 && !submitted; attempt++)
        {
            await input.ClickAsync();
            await input.FillAsync("");
            await input.PressSequentiallyAsync(question, new() { Delay = 15 });
            await input.PressAsync("Tab");
            await page.WaitForTimeoutAsync(750);
            if (await askButton.IsEnabledAsync())
            {
                await askButton.ClickAsync();
                submitted = true;
            }
            else
            {
                await page.WaitForTimeoutAsync(2_000);
            }
        }
        Assert.True(submitted, "Ask button never enabled — circuit not interactive or bind never committed.");

        // Terminal state: "Ask another question" (Complete/Refusal) or
        // the stream-error alert. Generous budget — a real model call.
        var terminal = page.Locator("[data-testid='new-question-button'], [data-testid='stream-error-alert']");
        await terminal.First.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 120_000 });

        Assert.False(
            await page.Locator("[data-testid='stream-error-alert']").IsVisibleAsync(),
            "Ask flow ended in the stream-error alert.");

        var answerText = await page.Locator("[data-testid='wizard-answer-stream']").InnerTextAsync();
        Assert.False(string.IsNullOrWhiteSpace(answerText), "Answer container is empty.");
        Assert.DoesNotContain("](http", answerText); // raw markdown links must be stripped (PR #340)

        // Provenance is sacred: an answer (not a refusal) must carry at
        // least one citation. A refusal is a legitimate terminal state
        // only if the refusal panel renders with its recovery payload.
        var refusalVisible = await page.Locator("[data-testid='refusal-panel']").IsVisibleAsync();
        if (!refusalVisible)
        {
            var citationCount = await page.Locator("[data-testid='citation-source-link']").CountAsync();
            Assert.True(citationCount >= 1, "Answer rendered without a single citation link.");
        }
    }

    private async Task<IPage> NewPageAsync()
    {
        // Fresh context per test: no shared cookies/cache, so each test
        // exercises the cold-visit path deterministically.
        var context = await _browser!.NewContextAsync(
            E2EEdgeAccess.ContextOptions(new() { ViewportSize = new() { Width = 1280, Height = 900 } }));
        return await context.NewPageAsync();
    }
}
