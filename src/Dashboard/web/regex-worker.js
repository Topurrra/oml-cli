// The Regex tester runs here, off the page: a pattern that backtracks for ever only stops this worker, which the page
// ends after a time limit. It gets { pattern, flags, text } and answers { matches: [{ index, text, groups }] } or { error }.
self.onmessage = e => {
  const { pattern, flags, text } = e.data;
  let re;
  try { re = new RegExp(pattern, flags.includes('g') ? flags : flags + 'g'); }
  catch (err) { self.postMessage({ error: err.message }); return; }
  const matches = [];
  for (const m of text.matchAll(re)) {
    matches.push({ index: m.index, text: m[0], groups: m.slice(1).map(g => g === undefined ? null : g) });
    if (matches.length >= 1000) break;
  }
  self.postMessage({ matches });
};
