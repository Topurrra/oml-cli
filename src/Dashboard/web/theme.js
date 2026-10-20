// Runs before the first paint: the color theme (saved choice, else the system setting) and a record of script errors.
// ?theme=light|dark in the address wins for this visit only (used by the screenshot test).
(() => {
  const root = document.documentElement;
  let theme = new URLSearchParams(location.search).get('theme');
  if (theme !== 'light' && theme !== 'dark') {
    try { theme = localStorage.getItem('oml-theme'); } catch { theme = null; }
  }
  if (theme !== 'light' && theme !== 'dark') theme = matchMedia('(prefers-color-scheme: dark)').matches ? 'dark' : 'light';
  root.dataset.theme = theme;
  window.omlErrors = [];
  const note = message => { window.omlErrors.push(String(message)); root.dataset.jsErrors = String(window.omlErrors.length); };
  root.dataset.jsErrors = '0';
  addEventListener('error', e => note(e.message || 'Script error'));
  addEventListener('unhandledrejection', e => note((e.reason && e.reason.message) || e.reason || 'Unhandled promise rejection'));
})();
