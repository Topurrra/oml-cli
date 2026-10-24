// Page-lifecycle cleanup: a page registers what to undo when it is left (timers, revokes, observers, dialogs).
// The router runs runCleanups() before each new page.
const cleanups = [];

export function onLeave(fn) {
  cleanups.push(fn);
}

export function runCleanups() {
  for (const fn of [...cleanups]) { try { fn(); } catch { /* nothing to undo */ } }
  cleanups.length = 0;
}
