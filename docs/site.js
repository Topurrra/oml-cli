(() => {
  const root = document.documentElement;
  const toggle = document.getElementById('theme-toggle');
  function updateLabel() {
    toggle.setAttribute('aria-label', `Switch to ${root.dataset.theme === 'dark' ? 'light' : 'dark'} theme`);
  }
  toggle.addEventListener('click', () => {
    root.dataset.theme = root.dataset.theme === 'dark' ? 'light' : 'dark';
    try { localStorage.setItem('oml-theme', root.dataset.theme); } catch {}
    updateLabel();
  });
  updateLabel();
  const preference = matchMedia('(prefers-color-scheme: dark)');
  preference.addEventListener('change', event => {
    let saved;
    try { saved = localStorage.getItem('oml-theme'); } catch {}
    if (saved !== 'dark' && saved !== 'light') {
      root.dataset.theme = event.matches ? 'dark' : 'light';
      updateLabel();
    }
  });
  const search = document.getElementById('command-search');
  const rows = [...document.querySelectorAll('#command-table tbody tr')];
  function filter() {
    const query = search.value.trim().toLowerCase();
    let count = 0;
    rows.forEach(row => { row.hidden = !row.textContent.toLowerCase().includes(query); if (!row.hidden) count++; });
    document.getElementById('command-count').textContent = `${count} of ${rows.length} commands`;
    document.getElementById('no-commands').hidden = count !== 0;
  }
  search.addEventListener('input', filter);
  filter();
  document.querySelectorAll('.copy').forEach(button => {
    button.addEventListener('click', async () => {
      const text = button.closest('.code-block').querySelector('code').textContent;
      try {
        if (navigator.clipboard && window.isSecureContext) await navigator.clipboard.writeText(text);
        else {
          const field = document.createElement('textarea');
          field.value = text; field.className = 'sr-only'; document.body.append(field); field.select();
          const copied = document.execCommand('copy'); field.remove(); button.focus();
          if (!copied) throw new Error('Copy unavailable');
        }
        button.textContent = 'Copied';
        document.getElementById('copy-status').textContent = 'Commands copied.';
      } catch {
        button.textContent = 'Select text';
        document.getElementById('copy-status').textContent = 'Copy unavailable. Select the code and copy it manually.';
      }
      setTimeout(() => { button.textContent = 'Copy'; }, 2000);
    });
  });
})();
