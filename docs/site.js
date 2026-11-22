// oml site: theme toggle, copy buttons, task filter. No dependencies, no tracking.
(() => {
  const root = document.documentElement;
  const status = document.getElementById('copy-status');

  // Theme. The inline script in <head> sets data-theme before first paint.
  const toggle = document.getElementById('theme-toggle');
  const savedTheme = () => {
    try { return localStorage.getItem('oml-theme'); } catch { return null; }
  };
  const updateToggle = () => {
    const next = root.dataset.theme === 'dark' ? 'light' : 'dark';
    toggle.setAttribute('aria-label', `Switch to ${next} theme`);
    toggle.title = `Switch to ${next} theme`;
  };
  toggle.addEventListener('click', () => {
    root.dataset.theme = root.dataset.theme === 'dark' ? 'light' : 'dark';
    try { localStorage.setItem('oml-theme', root.dataset.theme); } catch {}
    updateToggle();
  });
  matchMedia('(prefers-color-scheme: dark)').addEventListener('change', event => {
    const saved = savedTheme();
    if (saved !== 'dark' && saved !== 'light') {
      root.dataset.theme = event.matches ? 'dark' : 'light';
      updateToggle();
    }
  });
  updateToggle();

  // Copy buttons: copy the text of the <code> in the same .code block.
  const copyText = async text => {
    if (navigator.clipboard && window.isSecureContext) {
      try {
        await navigator.clipboard.writeText(text);
        return;
      } catch {
        // Permission denied or no focus: try the older way below.
      }
    }
    const field = document.createElement('textarea');
    field.value = text;
    field.setAttribute('readonly', '');
    field.className = 'sr-only';
    document.body.append(field);
    field.select();
    const ok = document.execCommand('copy');
    field.remove();
    if (!ok) throw new Error('Copy unavailable');
  };
  document.querySelectorAll('.copy').forEach(button => {
    let timer;
    button.addEventListener('click', async () => {
      const code = button.closest('.code').querySelector('pre code');
      try {
        await copyText(code.textContent.replace(/\n+$/, ''));
        button.textContent = 'Copied';
        button.classList.add('done');
        status.textContent = 'Copied to the clipboard.';
      } catch {
        button.textContent = 'Select it';
        status.textContent = 'Copy is not available here. Select the text and copy it yourself.';
        const range = document.createRange();
        range.selectNodeContents(code);
        const selection = getSelection();
        selection.removeAllRanges();
        selection.addRange(range);
      }
      button.focus();
      clearTimeout(timer);
      timer = setTimeout(() => {
        button.textContent = 'Copy';
        button.classList.remove('done');
      }, 2000);
    });
  });

  // "I want to..." filter: every word must appear in the row.
  const search = document.getElementById('job-search');
  const rows = [...document.querySelectorAll('#job-table tbody tr')];
  const count = document.getElementById('job-count');
  const empty = document.getElementById('job-empty');
  const table = document.getElementById('job-table');
  const filter = () => {
    const words = search.value.trim().toLowerCase().split(/\s+/).filter(Boolean);
    let shown = 0;
    rows.forEach(row => {
      const text = row.textContent.toLowerCase();
      row.hidden = !words.every(word => text.includes(word));
      if (!row.hidden) shown++;
    });
    count.textContent = words.length
      ? `${shown} of ${rows.length} tasks`
      : `${rows.length} tasks`;
    empty.hidden = shown !== 0;
    table.hidden = shown === 0;
  };
  search.addEventListener('input', filter);
  search.addEventListener('keydown', event => {
    if (event.key === 'Escape' && search.value) {
      search.value = '';
      filter();
    }
  });
  filter();
})();
