// localStorage, wrapped so a private window or a full storage never breaks a page.
export const store = {
  get(name, fallback) { try { const v = localStorage.getItem('oml-dash-' + name); return v === null ? fallback : JSON.parse(v); } catch { return fallback; } },
  set(name, value) { try { localStorage.setItem('oml-dash-' + name, JSON.stringify(value)); } catch { /* private window: not remembered */ } },
};
