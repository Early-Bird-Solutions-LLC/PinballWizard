// PinballWizard — client-side preference helpers.
// Called by UserPreferencesService via JS interop. All functions are
// idempotent and swallow localStorage errors so Privacy Mode doesn't throw.

window.pinwiz = window.pinwiz || {};

// ── Theme ─────────────────────────────────────────────────────────────────
// Applies a sibling-theme CSS class to <html>. Modern LCD = no class (default).
// Uses classList.add/remove so existing classes (e.g. mud-theme-*) are preserved.
window.pinwiz.setTheme = function (name) {
    document.documentElement.classList.remove('theme-daytime-route');
    if (name === 'daytime-route') {
        document.documentElement.classList.add('theme-daytime-route');
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
