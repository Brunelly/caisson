// FOUC guard (Story #119 NFR1): sets data-theme on <html> before Angular bootstraps, mirroring only the
// read-and-apply half of ThemeService.resolveInitial()/resolveSystemPreference(). ThemeService re-derives
// and re-applies the same theme on init (idempotent). Served as an external, same-origin script (loaded
// render-blocking from <head>) so it satisfies a strict `script-src 'self'` CSP without an inline script
// or a hash — no `<base>`-relative inline execution. Wrapped in try/catch so a locked-down browser
// (storage disabled, no matchMedia) never blocks first paint; falls through to the static data-theme="dark".
(function () {
  try {
    var STORAGE_KEY = 'caisson.theme';
    var THEMES = ['dark', 'light', 'hc-dark'];
    var theme = null;
    try {
      var stored = localStorage.getItem(STORAGE_KEY);
      if (THEMES.indexOf(stored) !== -1) {
        theme = stored;
      }
    } catch (e) {
      // Storage unavailable — fall through to system preference.
    }
    if (!theme) {
      theme =
        typeof matchMedia === 'function' &&
        matchMedia('(prefers-color-scheme: light)').matches
          ? 'light'
          : 'dark';
    }
    document.documentElement.setAttribute('data-theme', theme);
  } catch (e) {
    // Never block first paint — the dark default on <html> still applies.
  }
})();
