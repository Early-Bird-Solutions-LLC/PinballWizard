// Admin nav collapse — vanilla JS module for the collapsible admin sidebar (spec §5.1).
//
// Wires the plain <button data-testid="admin-nav-collapse-toggle"> rendered by
// AppNavRail (ShowCollapseToggle="true"). This is intentionally NOT a Blazor
// interactive island — the button has no @onclick — so the admin layout keeps its
// static circuit profile (the "hamburger bug" guard; see AdminLayout.razor and
// ADR-0034). The button's click is handled here instead.
//
// This module NEVER touches MudBlazor-owned DOM (FE-10 / NoJsMutationOfBlazorOwnedDomTests).
// It only toggles the `pw-admin-nav--collapsed` class on <html>; the rail width and
// the content reflow are driven entirely by CSS (app.css overrides the
// --mud-drawer-width-left variable MudBlazor already consumes, scoped to that class).
// An earlier attempt set `.mud-drawer`/`.mud-main-content` inline styles from here and
// silently broke the admin circuit — the reason FE-10 exists.
//
// The before-paint class application (no flash on reload) is done by the IIFE in
// App.razor, which reads the same localStorage key. This module handles the toggle.
//
// Idempotent via the `data-pw-collapse-wired` attribute.
//
// Storage key : 'pinwiz.admin.nav.collapsed' (string "true" | "false")
// CSS class   : 'pw-admin-nav--collapsed' on <html>

(function () {
    var KEY = 'pinwiz.admin.nav.collapsed';
    var CLASS = 'pw-admin-nav--collapsed';

    function applyAria(btn, collapsed) {
        var label = collapsed ? 'Expand navigation' : 'Collapse navigation';
        btn.setAttribute('aria-expanded', String(!collapsed));
        btn.setAttribute('aria-label', label);
        btn.setAttribute('title', label);
    }

    function init() {
        var btn = document.querySelector('[data-testid="admin-nav-collapse-toggle"]');
        if (!btn || btn.dataset.pwCollapseWired) return;
        btn.dataset.pwCollapseWired = 'true';

        // Sync the button's aria with the state the App.razor IIFE already applied
        // to <html> from localStorage.
        applyAria(btn, document.documentElement.classList.contains(CLASS));

        btn.addEventListener('click', function () {
            var nowCollapsed = !document.documentElement.classList.contains(CLASS);
            document.documentElement.classList.toggle(CLASS, nowCollapsed);
            applyAria(btn, nowCollapsed);
            try {
                localStorage.setItem(KEY, nowCollapsed ? 'true' : 'false');
            } catch (_) { /* privacy mode — state still toggles visually, just not remembered */ }
        });
    }

    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', init);
    } else {
        init();
    }
})();
