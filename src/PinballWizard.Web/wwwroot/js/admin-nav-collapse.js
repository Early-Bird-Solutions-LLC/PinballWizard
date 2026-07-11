// Admin nav collapse — vanilla JS module for the collapsible admin sidebar.
//
// Wires the plain <button data-testid="admin-nav-collapse-toggle"> in AppNavRail
// (rendered when ShowCollapseToggle="true"). This is intentionally NOT a Blazor
// interactive island — the button has no @onclick, so this script handles the
// click externally, preserving the admin layout's static circuit profile
// (the "hamburger bug" guard; see AdminLayout.razor header comment and ADR-0034).
//
// Initial state before first paint is handled by the IIFE in App.razor (reads
// localStorage and applies the class + dimensions synchronously in the HTML
// stream). This module handles the toggle behaviour and re-applies dimensions
// at DOMContentLoaded in case MudBlazor's layout JS has settled by then.
//
// Idempotent: the 'data-pw-collapse-wired' attribute prevents double-init
// if this script ever runs more than once.
//
// Storage key : 'pinwiz.admin.nav.collapsed' (string "true" | "false")
// CSS class   : 'pw-admin-nav--collapsed' on <html> (controls text visibility)
// Expanded    : 260px (spec §5.1)  |  Collapsed: 64px icon-only

(function () {
    var KEY = 'pinwiz.admin.nav.collapsed';
    var CLASS = 'pw-admin-nav--collapsed';
    var EXPANDED = '260px';
    var MINI = '64px';

    function getDrawer() { return document.querySelector('.mud-drawer.app-nav-rail'); }
    function getMain() { return document.querySelector('.mud-main-content'); }
    function getBtn() { return document.querySelector('[data-testid="admin-nav-collapse-toggle"]'); }

    function applyDimensions(collapsed) {
        var drawer = getDrawer();
        var main = getMain();
        if (drawer) drawer.style.width = collapsed ? MINI : EXPANDED;
        if (main) main.style.paddingLeft = collapsed ? MINI : EXPANDED;
    }

    function applyAria(btn, collapsed) {
        if (!btn) return;
        var label = collapsed ? 'Expand navigation' : 'Collapse navigation';
        btn.setAttribute('aria-expanded', String(!collapsed));
        btn.setAttribute('aria-label', label);
        btn.setAttribute('title', label);
    }

    function init() {
        var btn = getBtn();
        if (!btn || btn.dataset.pwCollapseWired) return;
        btn.dataset.pwCollapseWired = 'true';

        // Sync dimensions + aria with the state the IIFE applied from localStorage.
        // This also enforces the 260px spec width for the expanded state, which the
        // IIFE already sets — this call is a belt-and-suspenders re-apply after any
        // MudBlazor layout JS has run.
        var collapsed = document.documentElement.classList.contains(CLASS);
        applyDimensions(collapsed);
        applyAria(btn, collapsed);

        btn.addEventListener('click', function () {
            var nowCollapsed = !document.documentElement.classList.contains(CLASS);
            document.documentElement.classList.toggle(CLASS, nowCollapsed);
            applyDimensions(nowCollapsed);
            applyAria(btn, nowCollapsed);
            try {
                localStorage.setItem(KEY, nowCollapsed ? 'true' : 'false');
            } catch (_) { /* privacy mode — state still toggles visually, just not remembered */ }
        });
    }

    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', init);
    } else {
        // Already past DOMContentLoaded (e.g. script deferred or loaded late)
        init();
    }
})();
