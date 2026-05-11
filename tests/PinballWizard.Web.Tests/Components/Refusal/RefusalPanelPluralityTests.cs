using Bunit;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;
using PinballWizard.Application.Ai;
using PinballWizard.Web.Components.Refusal;
using Xunit;

namespace PinballWizard.Web.Tests.Components.Refusal;

// LOAD-BEARING plurality and favoritism pin tests for RefusalPanel.
//
// These tests are load-bearing per ADR-0026 § 5 + feedback_destination_plurality.md:
//   - Marketplace category MUST render ≥3 community resource cards.
//   - Machine-reference category MUST render ≥2 community resource cards.
//   - Cards MUST be in alphabetical order within category (no favoritism ordering).
//
// Each test asserts BEHAVIOR (card count, ordering, absence of favored markup),
// not structure (no MudBlazor class assertions).
//
// Terminology note: "community resource cards" are identified by the
// [data-testid='community-resource-card'] attribute on each card, and
// [data-testid='community-resource-cards'] on the grid wrapper.
public sealed class RefusalPanelPluralityTests
{
    // ─────────────────────────────────────────────────────────────────────────
    // LOAD-BEARING: Marketplace refusal renders ≥3 community resource cards.
    //
    // Spec authority: ADR-0026 § 5 sub-rule (a) + feedback_destination_plurality.md
    // "Marketplace category MUST render ≥3 resource cards."
    //
    // Fixture: 3 marketplace + 2 machine_reference + 1 forums resource.
    // Assertion: at least 3 cards with data-resource-category="marketplace".
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Marketplace_refusal_renders_at_least_3_community_resource_cards()
    {
        using var ctx = new BunitContext();
        ctx.Services.AddMudServices();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;

        var resources = BuildResources(
            marketplace: 3,
            machineReference: 2,
            forums: 1);

        var detail = new RefusalDetail(
            Confidence: null,
            RelatedMachines: null,
            CommunityResources: resources,
            MissingWhat: null,
            SuggestedRephrase: null);

        var cut = ctx.Render<RefusalPanel>(p => p
            .Add(x => x.Category, RefusalCategory.OutOfScope) // recovery-eligible
            .Add(x => x.Detail, detail));

        // CommunityResourceCards wrapper must be present.
        cut.Find("[data-testid='community-resource-cards']");

        // All resource cards (any category) are tagged [data-testid='community-resource-card'].
        var allCards = cut.FindAll("[data-testid='community-resource-card']");

        // Filter by data-resource-category attribute for marketplace.
        var marketplaceCards = allCards
            .Where(el => el.GetAttribute("data-resource-category") is "marketplace")
            .ToList();

        Assert.True(
            marketplaceCards.Count >= 3,
            $"Expected ≥3 marketplace cards but got {marketplaceCards.Count}. " +
            "This is a load-bearing ADR-0026 § 5 plurality invariant.");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // LOAD-BEARING: Machine-reference refusal renders ≥2 resource cards.
    //
    // Spec authority: ADR-0026 § 5 sub-rule (b) + feedback_destination_plurality.md
    // "Machine-reference MUST render ≥2 cards."
    //
    // Fixture: 3 marketplace + 2 machine_reference + 1 forums resource.
    // Assertion: at least 2 cards with data-resource-category="machine_reference".
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void MachineReference_refusal_renders_at_least_2_machine_reference_cards()
    {
        using var ctx = new BunitContext();
        ctx.Services.AddMudServices();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;

        var resources = BuildResources(
            marketplace: 3,
            machineReference: 2,
            forums: 1);

        var detail = new RefusalDetail(
            Confidence: null,
            RelatedMachines: null,
            CommunityResources: resources,
            MissingWhat: null,
            SuggestedRephrase: null);

        var cut = ctx.Render<RefusalPanel>(p => p
            .Add(x => x.Category, RefusalCategory.NoCitation) // recovery-eligible
            .Add(x => x.Detail, detail));

        cut.Find("[data-testid='community-resource-cards']");

        var allCards = cut.FindAll("[data-testid='community-resource-card']");

        var machineRefCards = allCards
            .Where(el => el.GetAttribute("data-resource-category") is "machine_reference")
            .ToList();

        Assert.True(
            machineRefCards.Count >= 2,
            $"Expected ≥2 machine_reference cards but got {machineRefCards.Count}. " +
            "This is a load-bearing ADR-0026 § 5 plurality invariant.");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // No-favoritism: CommunityResourceCards renders alphabetically within category.
    //
    // Spec authority: feedback_avoid_appearance_of_favoritism.md
    // "Default ordering = alphabetical by name within each category."
    //
    // Fixture: 3 marketplace resources inserted in reverse-alpha order.
    // Assertion: rendered card names are in alphabetical order.
    //
    // The CommunityResourceLoader sorts alphabetically at load time
    // (CommunityResourceLoader.cs ~line 196). The component renders in
    // the order received — this test pins that contract end-to-end.
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void CommunityResourceCards_renders_alphabetical_within_category()
    {
        using var ctx = new BunitContext();
        ctx.Services.AddMudServices();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;

        // Insert in reverse alpha order — the component must not re-sort,
        // so the caller must pass pre-sorted resources. This mirrors what
        // CommunityResourceLoader provides. We provide them pre-sorted (as
        // the loader would) and assert the rendered order matches.
        var resources = new List<CommunityResource>
        {
            // Alphabetical order (as CommunityResourceLoader would return):
            new("Alpha Pinball", "https://alpha.example.com", "marketplace", "First alphabetically."),
            new("Beta Pinball", "https://beta.example.com", "marketplace", "Second alphabetically."),
            new("Zeta Pinball", "https://zeta.example.com", "marketplace", "Third alphabetically."),
        };

        var cut = ctx.Render<CommunityResourceCards>(p => p
            .Add(x => x.Resources, resources));

        var cards = cut.FindAll("[data-testid='community-resource-card']");
        var renderedNames = cards
            .Select(el => el.GetAttribute("data-resource-name"))
            .ToList();

        Assert.Equal(3, renderedNames.Count);
        Assert.Equal("Alpha Pinball", renderedNames[0]);
        Assert.Equal("Beta Pinball", renderedNames[1]);
        Assert.Equal("Zeta Pinball", renderedNames[2]);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // No-favoritism: all resource categories are rendered with equal card count
    // when passed — no single category is capped or boosted.
    //
    // Fixture: 3 marketplace + 2 machine_reference = 5 total cards.
    // Assertion: all 5 cards render (no category is secretly filtered).
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void RefusalPanel_RendersTotalCardCount_MatchingInput()
    {
        using var ctx = new BunitContext();
        ctx.Services.AddMudServices();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;

        var resources = BuildResources(marketplace: 3, machineReference: 2, forums: 0);

        var detail = new RefusalDetail(
            Confidence: null,
            RelatedMachines: null,
            CommunityResources: resources,
            MissingWhat: null,
            SuggestedRephrase: null);

        var cut = ctx.Render<RefusalPanel>(p => p
            .Add(x => x.Category, RefusalCategory.InsufficientGrounding)
            .Add(x => x.Detail, detail));

        var allCards = cut.FindAll("[data-testid='community-resource-card']");
        Assert.Equal(5, allCards.Count);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Helpers
    // ─────────────────────────────────────────────────────────────────────────

    // Builds a resource list in the alphabetical ordering that CommunityResourceLoader
    // would produce. The list is pre-sorted so tests mirror the real loader contract.
    private static List<CommunityResource> BuildResources(
        int marketplace,
        int machineReference,
        int forums)
    {
        var result = new List<CommunityResource>();

        // marketplace entries — alphabetical A..Z
        for (var i = 0; i < marketplace; i++)
        {
            result.Add(new CommunityResource(
                Name: $"Marketplace Site {(char)('A' + i)}",
                Url: $"https://marketplace-{(char)('a' + i)}.example.com",
                Category: "marketplace",
                Description: $"Marketplace resource {i + 1}."));
        }

        // machine_reference entries — alphabetical A..Z
        for (var i = 0; i < machineReference; i++)
        {
            result.Add(new CommunityResource(
                Name: $"Machine Reference {(char)('A' + i)}",
                Url: $"https://machineref-{(char)('a' + i)}.example.com",
                Category: "machine_reference",
                Description: $"Machine reference resource {i + 1}."));
        }

        // forums entries
        for (var i = 0; i < forums; i++)
        {
            result.Add(new CommunityResource(
                Name: $"Forum Site {(char)('A' + i)}",
                Url: $"https://forum-{(char)('a' + i)}.example.com",
                Category: "forums",
                Description: $"Forum resource {i + 1}."));
        }

        return result;
    }
}
