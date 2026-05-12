using Bunit;
using Bunit.TestDoubles;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using MudBlazor.Services;
using PinballWizard.Application.Landing;
using PinballWizard.Web.Components.Landing;
using Xunit;

namespace PinballWizard.Web.Tests.Components.Landing;

// bUnit behavioral tests for SeedQuestionGrid.
//
// Tests assert behavior (clicking a card navigates, fallback renders when
// Questions is null) — not HTML structure or CSS class names.
// Each test creates its own BunitContext; services are registered before any
// component is rendered.
public sealed class SeedQuestionGridTests
{
    private static SeedQuestion[] BuildQuestions(int count = 4)
    {
        return Enumerable.Range(1, count)
            .Select(i => new SeedQuestion(
                Slug: $"question-{i}",
                Question: $"Question text {i}?",
                TargetSubAgent: i switch { 1 => "Wizard", 2 => "Valuation", 3 => "Rules", _ => "Repair" },
                Description: $"Description for question {i}."))
            .ToArray();
    }

    // ──────────────────────────────────────────────────────────────────────
    // 1. Renders 4 cards given 4 seed questions
    // ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void SeedQuestionGrid_RendersFourCards_WhenFourQuestionsProvided()
    {
        using var ctx = new BunitContext();
        ctx.Services.AddMudServices();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;

        var questions = BuildQuestions(4);

        var cut = ctx.Render<SeedQuestionGrid>(p => p
            .Add(g => g.Questions, questions));

        // Each card gets a data-testid="seed-card-{slug}". Use exact attribute
        // value matching to avoid matching nested elements with similar names.
        var cards = cut.FindAll("[data-testid^='seed-card-question-']");
        Assert.Equal(4, cards.Count);
    }

    // ──────────────────────────────────────────────────────────────────────
    // 2. Renders skeletons when Questions is null (loading state)
    // ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void SeedQuestionGrid_RendersSkeletons_WhenQuestionsIsNull()
    {
        using var ctx = new BunitContext();
        ctx.Services.AddMudServices();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;

        // Null = loading state — the parent hasn't received the API response yet.
        var cut = ctx.Render<SeedQuestionGrid>(p => p
            .Add(g => g.Questions, (IReadOnlyList<SeedQuestion>?)null));

        // Skeleton placeholders should be present.
        var skeletons = cut.FindAll("[data-testid='seed-question-skeleton']");
        Assert.Equal(4, skeletons.Count);

        // No real seed cards should be rendered while in loading state.
        var cards = cut.FindAll("[data-testid^='seed-card-question-']");
        Assert.Empty(cards);
    }

    // ──────────────────────────────────────────────────────────────────────
    // 3. Clicking a card navigates to /wizard/q/{slug}
    //    Behavior: verifies the actual NavigationManager URI changes
    // ──────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task SeedQuestionGrid_OnCardClick_NavigatesToWizardSlugRoute()
    {
        using var ctx = new BunitContext();
        ctx.Services.AddMudServices();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;

        // bUnit provides a BunitNavigationManager automatically.
        var navMan = ctx.Services.GetRequiredService<BunitNavigationManager>();

        var questions = BuildQuestions(4);

        var cut = ctx.Render<SeedQuestionGrid>(p => p
            .Add(g => g.Questions, questions));

        // Click the first card (slug = "question-1").
        var card = cut.Find("[data-testid='seed-card-question-1']");
        await cut.InvokeAsync(() => card.Click());

        // Assert: NavigationManager navigated to /wizard/q/{slug}.
        Assert.EndsWith("/wizard/q/question-1", navMan.Uri, StringComparison.Ordinal);
    }

    // ──────────────────────────────────────────────────────────────────────
    // 4. QuestionSelected callback is raised with the card's question text
    //    Behavior: parent can pre-fill the hero input on card click
    // ──────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task SeedQuestionGrid_OnCardClick_RaisesQuestionSelectedWithQuestionText()
    {
        using var ctx = new BunitContext();
        ctx.Services.AddMudServices();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;

        string? selected = null;

        var questions = BuildQuestions(4);

        // EventCallback.Factory.Create requires a non-null receiver — use the
        // test class itself as the receiver (any object works as receiver).
        var cut = ctx.Render<SeedQuestionGrid>(p => p
            .Add(g => g.Questions, questions)
            .Add(g => g.QuestionSelected, EventCallback.Factory.Create<string>(
                this, q => selected = q)));

        // Click the second card (slug = "question-2", question = "Question text 2?").
        var card = cut.Find("[data-testid='seed-card-question-2']");
        await cut.InvokeAsync(() => card.Click());

        Assert.Equal("Question text 2?", selected);
    }

    // ──────────────────────────────────────────────────────────────────────
    // 5. Each card renders sub-agent name (structural sanity on question data)
    // ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void SeedQuestionGrid_EachCard_RendersSubAgentName()
    {
        using var ctx = new BunitContext();
        ctx.Services.AddMudServices();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;

        var questions = BuildQuestions(4);

        var cut = ctx.Render<SeedQuestionGrid>(p => p
            .Add(g => g.Questions, questions));

        // Sub-agent names are shown in each card (data-testid=seed-card-description).
        var descriptions = cut.FindAll("[data-testid='seed-card-description']");
        Assert.Equal(4, descriptions.Count);
        Assert.Contains(descriptions, d => d.TextContent.Contains("Wizard", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(descriptions, d => d.TextContent.Contains("Repair", StringComparison.OrdinalIgnoreCase));
    }
}
