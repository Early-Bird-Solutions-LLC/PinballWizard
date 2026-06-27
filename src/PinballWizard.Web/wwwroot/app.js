// PinballWizard — client-side preference helpers.
// Called by UserPreferencesService via JS interop. All functions are
// idempotent and swallow localStorage errors so Privacy Mode doesn't throw.

window.pinwiz = window.pinwiz || {};

// ── Theme ─────────────────────────────────────────────────────────────────
// Applies a sibling-theme CSS class to <html>. Modern LCD = no class (default).
// Class name is `theme-<name>` for every non-default theme — matching the early
// inline applier in App.razor — so all sibling themes switch live, not only on reload.
// Uses classList.add/remove so existing classes (e.g. mud-theme-*) are preserved.
window.pinwiz.setTheme = function (name) {
    var html = document.documentElement;
    ['daytime-route', 'backbox', 'cabinet', 'dmd-classic'].forEach(function (t) {
        html.classList.remove('theme-' + t);
    });
    if (name && name !== 'modern-lcd') {
        html.classList.add('theme-' + name);
    }
    try { localStorage.setItem('pinwiz.theme', name); } catch (_) { }
};
window.pinwiz.getTheme = function () {
    try { return localStorage.getItem('pinwiz.theme') || 'modern-lcd'; } catch (_) { return 'modern-lcd'; }
};

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
