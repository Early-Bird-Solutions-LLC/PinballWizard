// PinballWizard — client-side preference helpers.
// Called by UserPreferencesService via JS interop. All functions are
// idempotent and swallow localStorage errors so Privacy Mode doesn't throw.

window.pinwiz = window.pinwiz || {};

// ── Theme ─────────────────────────────────────────────────────────────────
// Applies a sibling-theme CSS class to <html>. Modern LCD is the classless base;
// every other theme (including Paper — the default for new visitors, see getTheme
// and the App.razor inline applier) gets a `theme-<name>` class, so themes switch
// live, not only on reload.
// Uses classList.add/remove so existing classes (e.g. mud-theme-*) are preserved.
// Internal: set the sibling-theme class on <html> (Modern LCD = classless base).
// Shared by setTheme (user picks a theme) and applyStoredHtmlState (re-apply after
// an enhanced navigation) so the class list and the modern-lcd rule live in one place.
window.pinwiz._applyThemeClass = function (name) {
    var html = document.documentElement;
    ['daytime-route', 'backbox', 'cabinet', 'dmd-classic', 'paper'].forEach(function (t) {
        html.classList.remove('theme-' + t);
    });
    if (name && name !== 'modern-lcd') {
        html.classList.add('theme-' + name);
    }
};
window.pinwiz.setTheme = function (name) {
    window.pinwiz._applyThemeClass(name);
    try { localStorage.setItem('pinwiz.theme', name); } catch (_) { }
};
window.pinwiz.getTheme = function () {
    // Paper is the default for a visitor with no saved preference (theme #343).
    // A visitor who explicitly picked Modern LCD has 'modern-lcd' stored, which
    // setTheme/the applier treat as the classless base.
    try { return localStorage.getItem('pinwiz.theme') || 'paper'; } catch (_) { return 'paper'; }
};

// Re-applies the <html>-level preference state (theme class + data-motion) that the
// App.razor first-paint inline script sets from localStorage. Blazor enhanced
// navigation replaces <html> with the server-rendered response — which carries none
// of these client-applied attributes — so they are stripped on every in-app
// navigation, dropping the page to the Modern LCD base until the next full reload.
// The wiring block below registers this on Blazor's `enhancedload` event so the
// visitor's chosen theme (and motion preference) survive navigation. Idempotent;
// safe to call repeatedly.
window.pinwiz.applyStoredHtmlState = function () {
    try {
        window.pinwiz._applyThemeClass(window.pinwiz.getTheme());
        var m = localStorage.getItem('pinwiz.motion');
        if (m) document.documentElement.dataset.motion = m;
    } catch (_) { }
};

// Wire applyStoredHtmlState to Blazor's `enhancedload` event so the theme (and
// motion) survive in-app navigation. Enhanced navigation replaces <html> with the
// server response, which carries none of these client-applied attributes; without
// this re-apply the page reverts to the Modern LCD base on every navigation until
// the next full reload (regression guard: PublicRouteThemeNavigationE2ETests).
//
// Registered here in the external app.js — allowed by the CSP 'self' script-src —
// rather than an inline <script> in App.razor, which would need a per-edit SHA-256
// hash in the Cloudflare edge policy and is blocked by the enforced CSP without one
// (see CspPolicySyncTests). app.js loads before blazor.web.js, so `Blazor` is not
// yet defined at top level; defer wiring to DOMContentLoaded/load (both fire after
// blazor.web.js has executed and defined the global). Enhanced navigations only
// occur on user interaction, long after load, so no event is missed.
(function () {
    var wired = false;
    function wireEnhancedNavThemeReapply() {
        if (wired || !window.Blazor || typeof window.Blazor.addEventListener !== 'function') {
            return;
        }
        window.Blazor.addEventListener('enhancedload', window.pinwiz.applyStoredHtmlState);
        wired = true;
    }
    wireEnhancedNavThemeReapply(); // in case Blazor is already available
    document.addEventListener('DOMContentLoaded', wireEnhancedNavThemeReapply);
    window.addEventListener('load', wireEnhancedNavThemeReapply);
})();

// ── Motion ────────────────────────────────────────────────────────────────
// Sets data-motion on <html>. CSS rules key off this to override
// prefers-reduced-motion. Values: "match" | "on" | "off". Default: "match".
window.pinwiz.setMotion = function (pref) {
    document.documentElement.dataset.motion = pref;
    try { localStorage.setItem('pinwiz.motion', pref); } catch (_) { }
};
window.pinwiz.getMotion = function () {
    try { return localStorage.getItem('pinwiz.motion') || 'match'; } catch (_) { return 'match'; }
};

// ── Sound ─────────────────────────────────────────────────────────────────
// Muted by default per ADR-0026. No DOM mutation needed — SoundController
// reads pinwiz.sound from localStorage on its own initialization.
window.pinwiz.setSound = function (value) {
    try { localStorage.setItem('pinwiz.sound', value); } catch (_) { }
};
window.pinwiz.getSound = function () {
    try { return localStorage.getItem('pinwiz.sound') || 'muted'; } catch (_) { return 'muted'; }
};

// ── Page Size ─────────────────────────────────────────────────────────────
// Default: 10 per user request.
window.pinwiz.setPageSize = function (value) {
    try { localStorage.setItem('pinwiz.pageSize', value); } catch (_) { }
};
window.pinwiz.getPageSize = function () {
    try { return localStorage.getItem('pinwiz.pageSize') || '10'; } catch (_) { return '10'; }
};

// ── Timezone ──────────────────────────────────────────────────────────────
// Returns the browser's IANA timezone ID (e.g. "America/New_York").
// Called by AdminJobDetail via JS interop to display local timestamps.
// Using a named function avoids the 'unsafe-eval' CSP dependency that
// window.eval() would require.
window.pinwiz.getTimezone = function () {
    try { return Intl.DateTimeFormat().resolvedOptions().timeZone; } catch (_) { return ''; }
};

// ── Citation marker pulse ───────────────────────────────────────────────────
// When the URL hash points at a citation card (#citation-N) or a marker
// (#marker-N-x), add a one-shot pulse class to the target so the user sees where
// they landed. CSS gates the animation behind prefers-reduced-motion.
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

// ── Nav rail preference (collapsed/expanded persistence) ────────────────────
// localStorage wrapped in try/catch so private-mode / disabled storage degrades
// to a no-op (the rail still toggles; the preference just isn't remembered).
window.pinwiz.navRail = {
    get: function (key) {
        try {
            var v = localStorage.getItem(key);
            return v === null ? null : v === "true";
        } catch (_) { return null; }
    },
    set: function (key, value) {
        try { localStorage.setItem(key, value ? "true" : "false"); } catch (_) { /* no-op */ }
    }
};
