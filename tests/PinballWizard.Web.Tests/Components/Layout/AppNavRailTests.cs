using Bunit;
using Bunit.TestDoubles;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Routing;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using MudBlazor.Services;
using PinballWizard.Web.Components.Layout;
using Xunit;

namespace PinballWizard.Web.Tests.Components.Layout;

public sealed class AppNavRailTests : AsyncBunitContext
{
    private static readonly IReadOnlyList<NavRailItem> SampleItems = new[]
    {
        new NavRailItem("/", "Ask the Wizard", Icons.Material.Filled.AutoAwesome, MatchAll: true),
        new NavRailItem("/about", "What we cover", Icons.Material.Filled.Explore),
    };

    public AppNavRailTests()
    {
        Services.AddMudServices();
        JSInterop.Mode = JSRuntimeMode.Loose;
        _ = Services.GetRequiredService<BunitNavigationManager>();
    }

    [Fact]
    public void RendersOneNavLinkPerItem_WithCorrectHref()
    {
        var cut = Render(builder =>
        {
            builder.OpenComponent<MudPopoverProvider>(0);
            builder.CloseComponent();
            builder.OpenComponent<AppNavRail>(1);
            builder.AddAttribute(2, nameof(AppNavRail.Items), SampleItems);
            builder.AddAttribute(3, nameof(AppNavRail.Open), false);
            builder.CloseComponent();
        }).FindComponent<AppNavRail>();

        Assert.NotNull(cut.Find("a[href='/']"));
        Assert.NotNull(cut.Find("a[href='/about']"));
    }

    [Fact]
    public void InitialState_IsCollapsed_WhenOpenFalse()
    {
        var cut = Render(builder =>
        {
            builder.OpenComponent<MudPopoverProvider>(0);
            builder.CloseComponent();
            builder.OpenComponent<AppNavRail>(1);
            builder.AddAttribute(2, nameof(AppNavRail.Items), SampleItems);
            builder.AddAttribute(3, nameof(AppNavRail.Open), false);
            builder.CloseComponent();
        }).FindComponent<AppNavRail>();

        var toggle = cut.Find("[data-testid='nav-rail-toggle']");
        Assert.Equal("Expand navigation", toggle.GetAttribute("aria-label"));
    }

    [Fact]
    public void InitialState_IsExpanded_WhenOpenTrue()
    {
        var cut = Render(builder =>
        {
            builder.OpenComponent<MudPopoverProvider>(0);
            builder.CloseComponent();
            builder.OpenComponent<AppNavRail>(1);
            builder.AddAttribute(2, nameof(AppNavRail.Items), SampleItems);
            builder.AddAttribute(3, nameof(AppNavRail.Open), true);
            builder.CloseComponent();
        }).FindComponent<AppNavRail>();

        var toggle = cut.Find("[data-testid='nav-rail-toggle']");
        Assert.Equal("Collapse navigation", toggle.GetAttribute("aria-label"));
    }

    [Fact]
    public async Task Toggle_FlipsState_OnClick()
    {
        var cut = Render(builder =>
        {
            builder.OpenComponent<MudPopoverProvider>(0);
            builder.CloseComponent();
            builder.OpenComponent<AppNavRail>(1);
            builder.AddAttribute(2, nameof(AppNavRail.Items), SampleItems);
            builder.AddAttribute(3, nameof(AppNavRail.Open), false);
            builder.CloseComponent();
        }).FindComponent<AppNavRail>();

        await cut.InvokeAsync(() => cut.Find("[data-testid='nav-rail-toggle']").Click());

        Assert.Equal("Collapse navigation", cut.Find("[data-testid='nav-rail-toggle']").GetAttribute("aria-label"));
    }

    [Fact]
    public void ShowToggleFalse_RendersNoToggle_ButStillRendersLinks()
    {
        var cut = Render(builder =>
        {
            builder.OpenComponent<MudPopoverProvider>(0);
            builder.CloseComponent();
            builder.OpenComponent<AppNavRail>(1);
            builder.AddAttribute(2, nameof(AppNavRail.Items), SampleItems);
            builder.AddAttribute(3, nameof(AppNavRail.Open), true);
            builder.AddAttribute(4, nameof(AppNavRail.ShowToggle), false);
            builder.CloseComponent();
        }).FindComponent<AppNavRail>();

        // Static always-expanded mode: no toggle button (no @onclick), but the
        // nav links still render as anchors.
        Assert.Empty(cut.FindAll("[data-testid='nav-rail-toggle']"));
        Assert.NotNull(cut.Find("a[href='/']"));
        Assert.NotNull(cut.Find("a[href='/about']"));
    }

    [Fact]
    public void NavLink_UsesMatchAll_ForMatchAllItems()
    {
        var cut = Render(builder =>
        {
            builder.OpenComponent<MudPopoverProvider>(0);
            builder.CloseComponent();
            builder.OpenComponent<AppNavRail>(1);
            builder.AddAttribute(2, nameof(AppNavRail.Items), SampleItems);
            builder.AddAttribute(3, nameof(AppNavRail.Open), true);
            builder.CloseComponent();
        }).FindComponent<AppNavRail>();

        var navLinks = cut.FindComponents<MudNavLink>();
        var rootLink = navLinks.Single(l => l.Instance.Href == "/");
        var aboutLink = navLinks.Single(l => l.Instance.Href == "/about");

        Assert.Equal(NavLinkMatch.All, rootLink.Instance.Match);
        Assert.Equal(NavLinkMatch.Prefix, aboutLink.Instance.Match);
    }

    [Fact]
    public void Breakpoint_IsForwardedToDrawer()
    {
        var cut = Render(builder =>
        {
            builder.OpenComponent<MudPopoverProvider>(0);
            builder.CloseComponent();
            builder.OpenComponent<AppNavRail>(1);
            builder.AddAttribute(2, nameof(AppNavRail.Items), SampleItems);
            builder.AddAttribute(3, nameof(AppNavRail.Open), false);
            builder.AddAttribute(4, nameof(AppNavRail.Breakpoint), Breakpoint.None);
            builder.CloseComponent();
        }).FindComponent<AppNavRail>();

        var drawer = cut.FindComponent<MudDrawer>();
        Assert.Equal(Breakpoint.None, drawer.Instance.Breakpoint);
    }

    [Fact]
    public async Task HoverToPeek_PointerEnterOpens_PointerLeaveCloses()
    {
        var cut = Render(builder =>
        {
            builder.OpenComponent<MudPopoverProvider>(0);
            builder.CloseComponent();
            builder.OpenComponent<AppNavRail>(1);
            builder.AddAttribute(2, nameof(AppNavRail.Items), SampleItems);
            builder.AddAttribute(3, nameof(AppNavRail.Open), false);
            builder.AddAttribute(4, nameof(AppNavRail.HoverToPeek), true);
            builder.CloseComponent();
        }).FindComponent<AppNavRail>();

        var rail = cut.Find(".app-nav-rail");
        await cut.InvokeAsync(() => rail.PointerEnter());
        Assert.Equal("Pin navigation", cut.Find("[data-testid='nav-rail-toggle']").GetAttribute("aria-label"));

        await cut.InvokeAsync(() => cut.Find(".app-nav-rail").PointerLeave());
        Assert.Equal("Expand navigation", cut.Find("[data-testid='nav-rail-toggle']").GetAttribute("aria-label"));
    }

    [Fact]
    public async Task HoverToPeek_PinnedOpen_PointerEnterIsNoOp()
    {
        // When the rail is already pinned open, pointer-enter must be a no-op.
        // The !_pinned guard in OnPointerEnter prevents _peek from being set,
        // so the toggle label stays "Collapse navigation" throughout.
        var cut = Render(builder =>
        {
            builder.OpenComponent<MudPopoverProvider>(0);
            builder.CloseComponent();
            builder.OpenComponent<AppNavRail>(1);
            builder.AddAttribute(2, nameof(AppNavRail.Items), SampleItems);
            builder.AddAttribute(3, nameof(AppNavRail.Open), true);
            builder.AddAttribute(4, nameof(AppNavRail.HoverToPeek), true);
            builder.CloseComponent();
        }).FindComponent<AppNavRail>();

        Assert.Equal("Collapse navigation", cut.Find("[data-testid='nav-rail-toggle']").GetAttribute("aria-label"));

        await cut.InvokeAsync(() => cut.Find(".app-nav-rail").PointerEnter());

        Assert.Equal("Collapse navigation", cut.Find("[data-testid='nav-rail-toggle']").GetAttribute("aria-label"));
    }

    [Fact]
    public async Task HoverToPeek_Off_PointerEnterDoesNothing()
    {
        var cut = Render(builder =>
        {
            builder.OpenComponent<MudPopoverProvider>(0);
            builder.CloseComponent();
            builder.OpenComponent<AppNavRail>(1);
            builder.AddAttribute(2, nameof(AppNavRail.Items), SampleItems);
            builder.AddAttribute(3, nameof(AppNavRail.Open), false);
            builder.AddAttribute(4, nameof(AppNavRail.HoverToPeek), false);
            builder.CloseComponent();
        }).FindComponent<AppNavRail>();

        await cut.InvokeAsync(() => cut.Find(".app-nav-rail").PointerEnter());
        Assert.Equal("Expand navigation", cut.Find("[data-testid='nav-rail-toggle']").GetAttribute("aria-label"));
    }

    [Fact]
    public async Task HoverToPeek_PinThenLeave_StaysOpen()
    {
        // Sequence: start collapsed, hover (peek-open), click toggle (pin while peeking),
        // then pointer-leave — the rail must STAY open because it is now pinned.
        // Persist is off (default false) so no JS set() calls occur; Loose JSInterop handles
        // any incidental interop from the constructor.
        var cut = Render(builder =>
        {
            builder.OpenComponent<MudPopoverProvider>(0);
            builder.CloseComponent();
            builder.OpenComponent<AppNavRail>(1);
            builder.AddAttribute(2, nameof(AppNavRail.Items), SampleItems);
            builder.AddAttribute(3, nameof(AppNavRail.Open), false);
            builder.AddAttribute(4, nameof(AppNavRail.HoverToPeek), true);
            builder.CloseComponent();
        }).FindComponent<AppNavRail>();

        await cut.InvokeAsync(() => cut.Find(".app-nav-rail").PointerEnter());
        await cut.InvokeAsync(() => cut.Find("[data-testid='nav-rail-toggle']").Click());
        await cut.InvokeAsync(() => cut.Find(".app-nav-rail").PointerLeave());

        Assert.Equal("Collapse navigation", cut.Find("[data-testid='nav-rail-toggle']").GetAttribute("aria-label"));
    }

    [Fact]
    public void Persist_ReadsStoredPinned_OnFirstRender()
    {
        JSInterop.Setup<bool?>("pinwiz.navRail.get", "pinwiz.nav.pinned").SetResult(true);

        var cut = Render(builder =>
        {
            builder.OpenComponent<MudPopoverProvider>(0);
            builder.CloseComponent();
            builder.OpenComponent<AppNavRail>(1);
            builder.AddAttribute(2, nameof(AppNavRail.Items), SampleItems);
            builder.AddAttribute(3, nameof(AppNavRail.Open), false);
            builder.AddAttribute(4, nameof(AppNavRail.Persist), true);
            builder.CloseComponent();
        }).FindComponent<AppNavRail>();

        cut.WaitForAssertion(() =>
            Assert.Equal("Collapse navigation", cut.Find("[data-testid='nav-rail-toggle']").GetAttribute("aria-label")));
    }

    [Fact]
    public async Task Persist_WritesPinned_OnToggle()
    {
        // Constructor already sets JSInterop.Mode = Loose; get() returns null by default.
        // Assert Toggle() fires exactly one set() call with the correct (key, value) args.

        var cut = Render(builder =>
        {
            builder.OpenComponent<MudPopoverProvider>(0);
            builder.CloseComponent();
            builder.OpenComponent<AppNavRail>(1);
            builder.AddAttribute(2, nameof(AppNavRail.Items), SampleItems);
            builder.AddAttribute(3, nameof(AppNavRail.Open), false);
            builder.AddAttribute(4, nameof(AppNavRail.Persist), true);
            builder.CloseComponent();
        }).FindComponent<AppNavRail>();

        await cut.InvokeAsync(() => cut.Find("[data-testid='nav-rail-toggle']").Click());

        var invocation = JSInterop.Invocations.Single(i => i.Identifier == "pinwiz.navRail.set");
        Assert.Equal("pinwiz.nav.pinned", invocation.Arguments[0]);
        Assert.Equal(true, invocation.Arguments[1]);
    }

    [Fact]
    public async Task Persist_False_Toggle_CallsNoJs()
    {
        // Persist is off by default — clicking the toggle must not write to localStorage.
        // JSInterop.Mode is Loose (set in constructor), so any incidental call is allowed;
        // we just assert that NO pinwiz.navRail.* identifier was invoked.
        var cut = Render(builder =>
        {
            builder.OpenComponent<MudPopoverProvider>(0);
            builder.CloseComponent();
            builder.OpenComponent<AppNavRail>(1);
            builder.AddAttribute(2, nameof(AppNavRail.Items), SampleItems);
            builder.AddAttribute(3, nameof(AppNavRail.Open), false);
            builder.CloseComponent();
        }).FindComponent<AppNavRail>();

        await cut.InvokeAsync(() => cut.Find("[data-testid='nav-rail-toggle']").Click());

        Assert.DoesNotContain(JSInterop.Invocations,
            i => i.Identifier.StartsWith("pinwiz.navRail", StringComparison.Ordinal));
    }

    [Fact]
    public void Persist_NullStored_KeepsCollapsedDefault()
    {
        // Private-mode / first-visit: localStorage returns null. The rail must stay
        // collapsed (null never fabricates a pin — Invariant #17 no-masking-fallbacks).
        JSInterop.Setup<bool?>("pinwiz.navRail.get", "pinwiz.nav.pinned").SetResult(null);

        var cut = Render(builder =>
        {
            builder.OpenComponent<MudPopoverProvider>(0);
            builder.CloseComponent();
            builder.OpenComponent<AppNavRail>(1);
            builder.AddAttribute(2, nameof(AppNavRail.Items), SampleItems);
            builder.AddAttribute(3, nameof(AppNavRail.Open), false);
            builder.AddAttribute(4, nameof(AppNavRail.Persist), true);
            builder.CloseComponent();
        }).FindComponent<AppNavRail>();

        cut.WaitForAssertion(() =>
            Assert.Equal("Expand navigation", cut.Find("[data-testid='nav-rail-toggle']").GetAttribute("aria-label")));
    }

    // ── ShowCollapseToggle — CSS/JS collapse button ──────────────────────────

    // Renders the admin rail in static mode (ShowToggle=false) with ShowCollapseToggle set.
    private IRenderedComponent<AppNavRail> RenderAdminRailWithCollapse(bool showCollapseToggle)
    {
        return Render(builder =>
        {
            builder.OpenComponent<MudPopoverProvider>(0);
            builder.CloseComponent();
            builder.OpenComponent<AppNavRail>(1);
            builder.AddAttribute(2, nameof(AppNavRail.Items), SampleItems);
            builder.AddAttribute(3, nameof(AppNavRail.Open), true);
            builder.AddAttribute(4, nameof(AppNavRail.ShowToggle), false);
            builder.AddAttribute(5, nameof(AppNavRail.ShowCollapseToggle), showCollapseToggle);
            builder.CloseComponent();
        }).FindComponent<AppNavRail>();
    }

    [Fact]
    public void ShowCollapseToggle_Default_NoButton()
    {
        // Default ShowCollapseToggle=false: the admin collapse button must NOT render.
        // Guards that the public MainLayout rail (and any ShowToggle=false usage
        // that does not opt in) is completely unaffected by the new parameter.
        var cut = RenderAdminRailWithCollapse(showCollapseToggle: false);
        Assert.Empty(cut.FindAll("[data-testid='admin-nav-collapse-toggle']"));
    }

    [Fact]
    public void ShowCollapseToggle_True_RendersButton()
    {
        // When opted in, exactly one collapse toggle button is rendered in the rail header.
        var cut = RenderAdminRailWithCollapse(showCollapseToggle: true);
        Assert.Single(cut.FindAll("[data-testid='admin-nav-collapse-toggle']"));
    }

    [Fact]
    public void ShowCollapseToggle_True_ButtonIsPlainHtml_NoOnclick()
    {
        // Circuit-safety invariant: the collapse toggle MUST be a plain <button> with
        // NO onclick or blazor:onclick attribute. A Blazor @onclick handler on the admin
        // chrome requires @rendermode on the layout subtree, which breaks admin-page
        // circuit hydration (the documented "hamburger bug" — see AdminLayout.razor header
        // comment and the reference_interactive_island_static_layout_circuit_break memory).
        // This test is the machine-readable guard for that invariant.
        var cut = RenderAdminRailWithCollapse(showCollapseToggle: true);
        var btn = cut.Find("[data-testid='admin-nav-collapse-toggle']");
        Assert.Equal("BUTTON", btn.TagName, StringComparer.OrdinalIgnoreCase);
        // OuterHtml must carry no onclick variant — neither the plain HTML form nor the
        // Blazor event-handler form that the renderer emits when @onclick is present.
        Assert.DoesNotContain("onclick", btn.OuterHtml, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ShowCollapseToggle_True_ButtonHasAriaExpandedAndAriaLabel()
    {
        // Accessibility: aria-expanded reflects the initial expanded state (true), and
        // aria-label provides a descriptive label for assistive technology. Both are
        // required per WCAG 2.1 AA (the JS module keeps them in sync on toggle).
        var cut = RenderAdminRailWithCollapse(showCollapseToggle: true);
        var btn = cut.Find("[data-testid='admin-nav-collapse-toggle']");
        // Pin the exact initial-state contract, not just presence: the SSR markup is the
        // expanded default (a first-time visitor's state), and admin-nav-collapse.js re-syncs
        // both to the persisted state on load. A non-empty check would let the contract drift.
        Assert.Equal("true", btn.GetAttribute("aria-expanded"));
        Assert.Equal("Collapse navigation", btn.GetAttribute("aria-label"));
    }

    [Fact]
    public void ShowCollapseToggle_True_NavLinksStillRender()
    {
        // Sanity: the collapse button coexists with the nav links; it must not
        // displace or suppress any navigation items.
        var cut = RenderAdminRailWithCollapse(showCollapseToggle: true);
        Assert.NotNull(cut.Find("a[href='/']"));
        Assert.NotNull(cut.Find("a[href='/about']"));
    }

    [Fact]
    public void ShowCollapseToggle_True_DrawerHasCollapsibleClass()
    {
        // The 'app-nav-rail--collapsible' class is added to the MudDrawer when
        // ShowCollapseToggle is true. This class scopes the CSS rules that control
        // width and text visibility so they target only the collapsible admin rail
        // and not the public MainLayout rail.
        var cut = RenderAdminRailWithCollapse(showCollapseToggle: true);
        var drawer = cut.FindComponent<MudDrawer>();
        Assert.Contains("app-nav-rail--collapsible", drawer.Instance.Class ?? "", StringComparison.Ordinal);
    }

    [Fact]
    public void ShowCollapseToggle_False_DrawerHasNoCollapsibleClass()
    {
        // Public rail (ShowCollapseToggle=false) must NOT carry the collapsible
        // modifier class — the CSS rules should not apply to it.
        var cut = RenderAdminRailWithCollapse(showCollapseToggle: false);
        var drawer = cut.FindComponent<MudDrawer>();
        Assert.DoesNotContain("app-nav-rail--collapsible", drawer.Instance.Class ?? "", StringComparison.Ordinal);
    }

    // ── Footer slot ───────────────────────────────────────────────────────────

    [Fact]
    public void Footer_IsRendered_WhenProvided()
    {
        var cut = Render(builder =>
        {
            builder.OpenComponent<MudPopoverProvider>(0);
            builder.CloseComponent();
            builder.OpenComponent<AppNavRail>(1);
            builder.AddAttribute(2, nameof(AppNavRail.Items), SampleItems);
            builder.AddAttribute(3, nameof(AppNavRail.Open), true);
            builder.AddAttribute(4, nameof(AppNavRail.ShowToggle), false);
            builder.AddAttribute(5, nameof(AppNavRail.Footer), (RenderFragment)(b =>
            {
                b.OpenElement(0, "span");
                b.AddAttribute(1, "data-testid", "footer-sentinel");
                b.AddContent(2, "Footer content");
                b.CloseElement();
            }));
            builder.CloseComponent();
        }).FindComponent<AppNavRail>();

        cut.Find("[data-testid='nav-rail-footer-slot']");
        cut.Find("[data-testid='footer-sentinel']");
        Assert.Contains("Footer content", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void Footer_IsOmitted_WhenNull()
    {
        // Default Footer is null — no extra DOM emitted, public rail unaffected.
        var cut = Render(builder =>
        {
            builder.OpenComponent<MudPopoverProvider>(0);
            builder.CloseComponent();
            builder.OpenComponent<AppNavRail>(1);
            builder.AddAttribute(2, nameof(AppNavRail.Items), SampleItems);
            builder.AddAttribute(3, nameof(AppNavRail.Open), true);
            builder.AddAttribute(4, nameof(AppNavRail.ShowToggle), false);
            // Footer parameter intentionally NOT set → defaults to null.
            builder.CloseComponent();
        }).FindComponent<AppNavRail>();

        Assert.Empty(cut.FindAll("[data-testid='nav-rail-footer-slot']"));
    }
}
