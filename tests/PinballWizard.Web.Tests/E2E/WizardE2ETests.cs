using Microsoft.Playwright;
using Xunit;

namespace PinballWizard.Web.Tests.E2E;

// True end-to-end coverage: real browser → real Web app → real Api →
// live Azure (Cosmos / AI Search / Foundry). These assert the behaviors
// that broke on 2026-06-10 and that no in-process test could see:
// the Blazor circuit becoming interactive, seed-question cards being
// clickable, and the ask flow returning a CITED answer (the citation
// chain crosses the camelCase tool-trace seam end-to-end).
//
// Cost note: AskFlow makes one real model call per run (~cents). The
// suite is local-only (Category=E2E is CI-excluded); run it before
// pushing changes that touch the answer path, render modes, hosting
// config, or after any live-data migration.
[Collection("E2E live stack")]
[Trait("Category", "E2E")]
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
        _browser = await _playwright.Chromium.LaunchAsync(new() { Headless = true });
    }

    public async Task DisposeAsync()
    {
        if (_browser is not null)
        {
            await _browser.DisposeAsync();
        }
        _playwright?.Dispose();
    }

    [E2EFact]
    public async Task Landing_SeedQuestionCard_IsClickable_AndNavigatesToWizard()
    {
        // The 2026-06-10 regression class: the page prerenders but the
        // circuit never becomes interactive, leaving the @onclick seed
        // cards inert ("seed questions aren't links"). Clicking one and
        // observing navigation proves the circuit is alive end-to-end.
        var page = await NewPageAsync();
        await page.GotoAsync($"{_stack.WebBaseUrl}/", new() { WaitUntil = WaitUntilState.DOMContentLoaded });

        var card = page.Locator("[data-testid^='seed-card-']").First;
        await card.WaitForAsync(new() { State = WaitForSelectorState.Attached, Timeout = 60_000 });

        // The circuit may lag the prerender, and the grid re-renders as it
        // transitions skeleton → fallback → live manifest, detaching the
        // node mid-click. Retry both the click (detach race throws) and
        // the navigation wait (an inert prerendered card swallows clicks).
        var navigated = false;
        for (var attempt = 0; attempt < 20 && !navigated; attempt++)
        {
            try
            {
                await card.ClickAsync(new() { Timeout = 5_000 });
                await page.WaitForURLAsync(url => url.Contains("/wizard"), new() { Timeout = 3_000 });
                navigated = true;
            }
            catch (Exception ex) when (ex is TimeoutException or PlaywrightException)
            {
                // Circuit not live yet or the card re-rendered mid-click —
                // give it a beat and retry against the re-resolved locator.
                await page.WaitForTimeoutAsync(2_000);
            }
        }

        Assert.True(navigated, "Seed-question card click never navigated to /wizard — circuit not interactive.");

        // Navigation alone is not the contract — the hand-off must carry
        // the question into the wizard and AUTO-SUBMIT it. The original
        // version of this test stopped at the URL check and missed the
        // /wizard?q= query parameter being silently dropped (the page only
        // read the /wizard/q/{slug} route): users landed on a bare idle
        // page. The submitted-question header proves the full hand-off.
        var submittedQuestion = page.Locator("[data-testid='submitted-question']");
        await submittedQuestion.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 30_000 });
        var text = await submittedQuestion.InnerTextAsync();
        Assert.False(string.IsNullOrWhiteSpace(text), "Submitted-question header rendered empty after seed-card hand-off.");
    }

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

        // Reset to Idle and re-ask — guaranteed cache hit on a healthy
        // cache (and still a valid cited-answer assertion if eviction or
        // multi-replica routing makes it a miss).
        await page.Locator("[data-testid='new-question-button']").ClickAsync();
        await AskOnceAndAssertCitedAsync(page);
    }

    // Types the canonical ask-flow question, submits, awaits the terminal
    // state, and asserts the provenance contract (cited answer or rendered
    // refusal). Assumes the page is already on /wizard in Idle state.
    private static async Task AskOnceAndAssertCitedAsync(IPage page)
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
            await input.PressSequentiallyAsync("what year was godzilla pinball game released", new() { Delay = 15 });
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
        var context = await _browser!.NewContextAsync(new() { ViewportSize = new() { Width = 1280, Height = 900 } });
        return await context.NewPageAsync();
    }
}
