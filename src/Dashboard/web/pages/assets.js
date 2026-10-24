import { h, add, icon } from '../core/dom.js';
import { store } from '../core/store.js';
import { api } from '../core/api.js';
import { task, cli, copyButton, bytes, fileName, download, downloadLink, dropZone, readFiles, blobUrl } from '../core/ui.js';
import { viewer } from '../core/viewer.js';
import { onLeave } from '../core/lifecycle.js';

export default {
  id: 'assets', group: 'Assets', title: 'Fonts and images', icon: 'assets',
  description: 'Make fonts and images smaller for the web. Files are converted on this computer and deleted after you get the result.',
  render(main) {
    let mode = store.get('assets-mode', 'images');
    const tabs = h('div', { class: 'segmented', role: 'group', 'aria-label': 'Kind of file' });
    const body = h('div', { class: 'stack' });
    for (const [value, label] of [['images', 'Images'], ['fonts', 'Fonts']]) {
      const b = h('button', { type: 'button', 'aria-pressed': String(value === mode) }, label);
      b.addEventListener('click', () => { mode = value; store.set('assets-mode', value); tabs.querySelectorAll('button').forEach(x => x.setAttribute('aria-pressed', String(x === b))); draw(); });
      add(tabs, b);
    }
    add(main, h('div', { class: 'field' }, tabs), body);

    function draw() {
      const out = h('div', { class: 'stack' });
      if (mode === 'fonts') {
        body.replaceChildren(h('section', { class: 'card' }, h('h2', {}, 'Fonts to WOFF2'),
          h('p', { class: 'muted small' }, 'TTF, OTF and WOFF fonts become WOFF2, the smallest web font format. All glyphs and features stay.'),
          dropZone('.ttf,.otf,.woff,.woff2', 'Drop fonts (.ttf, .otf, .woff)', files => convert('convert/fonts', files, {}, out)), out));
      } else drawImages(out);
    }

    // The image form shows only the options of the chosen format; each maps to one `oml image` option.
    function drawImages(out) {
      const more = store.get('img-more', {});
      const field = (id, text, input, hint) => h('div', { class: 'field' }, h('label', { for: id }, text), input, hint ? h('p', { class: 'hint' }, hint) : null);
      const number = (id, min, max, placeholder, value, step) => h('input', { type: 'number', id, min: String(min), max: String(max), step: step || '1', placeholder, value: value === undefined ? '' : value });
      const box = (id, value) => h('input', { type: 'checkbox', id, checked: !!value });
      const check = (input, text, hint) => h('div', { class: 'field' }, h('label', { class: 'check', for: input.id }, input, h('span', {}, text, hint ? h('span', { class: 'hint block' }, hint) : null)));

      const to = h('select', { id: 'img-to' }, [['', 'Same as input'], ['webp', 'WebP'], ['png', 'PNG'], ['jpeg', 'JPEG'],
        ['svg', 'SVG (trace or optimize)'], ['ico', 'ICO (icon, several sizes)']].map(([v, t]) => h('option', { value: v }, t)));
      to.value = store.get('img-to', 'webp');
      const formatHint = h('p', { class: 'hint' });
      const maxWidth = number('img-width', 1, 100000, 'No limit', store.get('img-width', ''));
      const maxHeight = number('img-height', 1, 100000, 'No limit', more.maxHeight);
      const quality = h('input', { type: 'range', id: 'img-quality', min: '1', max: '100', value: store.get('img-quality', 85) });
      const qualityText = h('span', { class: 'mono small' }, quality.value);
      quality.addEventListener('input', () => { qualityText.textContent = quality.value; });
      const sizeFields = [field('img-width', 'Max width (px)', maxWidth), field('img-height', 'Max height (px)', maxHeight)];
      const qualityField = h('div', { class: 'field' }, h('label', { for: 'img-quality' }, 'Quality ', qualityText), quality, h('p', { class: 'hint' }, 'JPEG and WebP only. 85 is a good start; lower is smaller.'));

      const mode = h('select', { id: 'img-mode' }, [['trace', 'Trace: vector paths (logos, icons)'], ['embed', 'Embed: the picture inside an SVG (photos)']].map(([v, t]) => h('option', { value: v }, t)));
      mode.value = more.mode === 'embed' ? 'embed' : 'trace';
      const colors = number('img-colors', 1, 32, 'Auto (up to 16)', more.colors);
      const tolerance = number('img-tolerance', 0.1, 5, '0.3', more.tolerance, '0.1');
      const minArea = number('img-min-area', 0, 1000000, 'Auto', more.minArea);
      const alpha = number('img-alpha', 1, 255, '128', more.alphaThreshold);
      const transparentBg = box('img-transparent', more.transparentBg);
      const traceOnly = h('div', {},
        h('div', { class: 'grid-3' },
          field('img-colors', 'Colours', colors, 'Most colours in the result.'),
          field('img-tolerance', 'Smoothness (px)', tolerance, 'How far a curve may be from the edge: 0.1 to 5. Higher is smoother and smaller.'),
          field('img-min-area', 'Remove specks under (px²)', minArea, 'Shapes and holes smaller than this go.'),
          field('img-alpha', 'Alpha threshold', alpha, 'From this alpha (1 to 255) a pixel counts as solid.')),
        check(transparentBg, 'Transparent background', 'Leave out the colour around the border, such as a white background.'));
      const traceGroup = h('fieldset', { class: 'options' }, h('legend', {}, 'For PNG and JPEG files'),
        field('img-mode', 'Mode', mode), traceOnly);

      const precision = number('img-precision', 0, 10, 'Auto', more.precision);
      const removeIds = box('img-remove-ids', more.removeIds), aggressive = box('img-aggressive', more.aggressive), stripTitle = box('img-strip-title', more.stripTitle);
      const optimizeGroup = h('fieldset', { class: 'options' }, h('legend', {}, 'For SVG files (optimize)'),
        h('div', { class: 'grid-3' }, field('img-precision', 'Decimals', precision, '0 to 10. Auto: from the image size; more are used when the result would look different.')),
        check(removeIds, 'Remove unused ids', 'Only when no other file or page points into this SVG.'),
        check(aggressive, 'Smallest result', 'Only for an SVG used as an image or a CSS background, not one inlined in a page.'),
        check(stripTitle, 'Remove the title and description', 'Screen readers read them: keep them on a meaningful image.'));

      const SIZES = [16, 24, 32, 48, 64, 128, 256];
      const picked = new Set(Array.isArray(more.sizes) && more.sizes.length ? more.sizes : [16, 32, 48]);
      const sizeBoxes = SIZES.map(n => box('img-size-' + n, picked.has(n)));
      const preset = (text, list) => h('button', { class: 'button small', type: 'button', onclick: () => sizeBoxes.forEach((b, i) => { b.checked = list.includes(SIZES[i]); }) }, text);
      const icoGroup = h('fieldset', { class: 'options' }, h('legend', {}, 'Icon sizes (px)'),
        h('div', { class: 'row' }, sizeBoxes.map((b, i) => h('label', { class: 'check inline', for: b.id }, b, String(SIZES[i])))),
        h('div', { class: 'row' }, preset('Favicon (16, 32, 48)', [16, 32, 48]), preset('App icon (all sizes)', SIZES)),
        h('p', { class: 'hint' }, 'Each size is drawn from the source. A non-square image is centred on a transparent square.'));

      const hints = {
        '': 'Each file keeps its format. GIF, BMP and ICO files need a format: choose one first.',
        webp: 'WebP: the smallest file for photos and pictures. Every current browser shows it.',
        png: 'PNG: lossless, keeps transparency.',
        jpeg: 'JPEG: for photos. Transparent parts become white.',
        svg: 'SVG: a PNG or JPEG logo or icon is traced to vector paths; an SVG file is optimized. Each file uses the options for its type.',
        ico: 'ICO: one file with several icon sizes, for a favicon or an app icon. It is often larger than the source.',
      };
      const show = () => {
        const v = to.value;
        formatHint.textContent = hints[v];
        sizeFields.forEach(f => { f.hidden = v === 'ico'; });
        qualityField.hidden = !['', 'webp', 'jpeg'].includes(v);
        traceGroup.hidden = optimizeGroup.hidden = v !== 'svg';
        traceOnly.hidden = mode.value === 'embed';
        icoGroup.hidden = v !== 'ico';
      };
      to.addEventListener('change', show); mode.addEventListener('change', show);
      show();

      const options = files => {
        const svgs = files.filter(f => /\.svg$/i.test(f.name)).length, rasters = files.length - svgs;
        const v = to.value, n = el => el.value === '' ? undefined : Number(el.value), on = el => el.checked || undefined;
        const sizes = SIZES.filter((s, i) => sizeBoxes[i].checked);
        store.set('img-to', v); store.set('img-width', maxWidth.value); store.set('img-quality', Number(quality.value));
        store.set('img-more', { maxHeight: maxHeight.value, mode: mode.value, colors: colors.value, tolerance: tolerance.value, minArea: minArea.value, alphaThreshold: alpha.value,
          transparentBg: transparentBg.checked, precision: precision.value, removeIds: removeIds.checked, aggressive: aggressive.checked, stripTitle: stripTitle.checked, sizes });
        const o = { to: v || undefined };
        if (v !== 'ico') Object.assign(o, { maxWidth: n(maxWidth), maxHeight: n(maxHeight) });
        if (!qualityField.hidden) o.quality = Number(quality.value);
        if (v === 'ico') o.sizes = sizes;
        if (v === 'svg') {
          if (rasters && mode.value === 'embed') o.mode = 'embed';
          else if (rasters) Object.assign(o, { colors: n(colors), tolerance: n(tolerance), minArea: n(minArea), alphaThreshold: n(alpha), transparentBg: on(transparentBg) });
          o.precision = n(precision);
          if (svgs) Object.assign(o, { removeIds: on(removeIds), aggressive: on(aggressive), stripTitle: on(stripTitle) });
        }
        return o;
      };
      const refuse = (text, focus) => { out.replaceChildren(h('div', { class: 'message error', role: 'alert' }, h('strong', {}, 'Not sent. '), text)); if (focus) focus.focus(); };
      const send = files => {
        const needFormat = to.value === '' ? files.filter(f => /\.(ico|gif|bmp)$/i.test(f.name)) : [];
        if (needFormat.length) return refuse(`${needFormat.map(f => f.name).join(', ')}: ICO, GIF and BMP files need a format. Choose one under Format, then drop the files again.`, to);
        if (to.value === 'ico' && !sizeBoxes.some(b => b.checked)) return refuse('Choose at least one icon size.', sizeBoxes[0]);
        const bad = [...body.querySelectorAll('input[type="number"]')].find(i => !i.closest('[hidden]') && !i.checkValidity());
        if (bad) { bad.reportValidity(); return refuse(`${bad.labels[0] ? bad.labels[0].textContent : 'A field'}: ${bad.validationMessage}`, bad); }
        convert('convert/images', files, options(files), out);
      };
      body.replaceChildren(h('section', { class: 'card' }, h('h2', {}, 'Images'),
        h('p', { class: 'muted small' }, 'PNG, JPEG, WebP, GIF, BMP, ICO and SVG. Images are never scaled up, and metadata is removed. ' +
          'A converted file can be larger than the original (ICO and embedded SVG often are): check the Saved column.'),
        h('div', { class: 'grid-3' }, h('div', { class: 'field' }, h('label', { for: 'img-to' }, 'Format'), to, formatHint), sizeFields, qualityField),
        traceGroup, optimizeGroup, icoGroup,
        dropZone('.png,.jpg,.jpeg,.gif,.bmp,.webp,.ico,.svg', 'Drop images', send), out));
    }

    async function convert(path, files, options, out) {
      const data = await task(null, out, `Converting ${files.length} file${files.length > 1 ? 's' : ''}…`, async () => {
        const uploads = await readFiles(files);
        return api(path, { files: uploads, ...options });
      });
      if (!data) return;
      const rows = data.files || [];
      const done = rows.filter(r => r.data);
      const totalIn = done.reduce((s, r) => s + (r.inputBytes || 0), 0), totalOut = done.reduce((s, r) => s + (r.outputBytes || 0), 0);
      const fonts = path === 'convert/fonts';
      const comparable = r => r.outputFormat !== 'ico';
      const size = (dims, n) => [dims ? `${dims.width} × ${dims.height} px` : '', bytes(n)].filter(Boolean).join(', ');
      add(out,
        done.length > 1 ? h('div', { class: 'row' }, h('span', { class: 'badge ok' }, `${done.length} files: ${bytes(totalIn)} → ${bytes(totalOut)}` + (done.every(comparable) ? `, ${saved(totalIn, totalOut)}` : '')),
          data.zipDownload ? downloadLink(data.zipDownload, 'Download all (zip)', fonts ? 'fonts-woff2.zip' : 'images-optimized.zip') : null) : null,
        h('div', { class: 'table-wrap' }, h('table', {}, h('thead', {}, h('tr', {}, [fonts ? '' : 'Result', 'File', 'Before', 'After', 'Saved', ''].map((t, i) => h('th', { class: i >= 2 && i <= 4 ? 'num' : null }, t)))),
          h('tbody', {}, rows.map(r => {
            let thumb = null;
            if (!fonts && r.data) {
              const type = { webp: 'image/webp', png: 'image/png', jpeg: 'image/jpeg', svg: 'image/svg+xml', ico: 'image/x-icon' }[r.outputFormat] || 'application/octet-stream';
              const url = blobUrl(r.data, type); onLeave(() => URL.revokeObjectURL(url));
              const original = files.find(f => f.name === r.name);
              const before = original ? URL.createObjectURL(original) : null;
              if (before) onLeave(() => URL.revokeObjectURL(before));
              const title = 'Result: ' + r.output;
              thumb = h('button', { class: 'thumb-button', type: 'button', 'aria-label': `View ${r.output}` + (before ? ', before and after' : ''),
                onclick: () => viewer.open({ src: url, title, label: `After: ${r.output}, ${size(r.after, r.outputBytes)}`, before: before && { src: before, label: `Before: ${r.name}, ${size(r.before, r.inputBytes)}` } }) },
                h('img', { class: 'thumb', src: url, alt: title }));
            }
            const d = r.details || {};
            const facts = [
              d.sizes ? `Sizes ${d.sizes.join(', ')} px` : null,
              d.mode === 'trace' ? `Traced: ${d.colors} colour${d.colors === 1 ? '' : 's'}, ${d.paths} path${d.paths === 1 ? '' : 's'}` : d.mode === 'embed' ? 'Picture embedded in an SVG' : null,
              d.removedBackground ? `Background ${d.removedBackground} left out` : null,
              d.kept === 'original' ? 'Kept the original' + (d.reason ? ': ' + d.reason : '') : null,
              d.note || null,
            ].filter(Boolean);
            return h('tr', {},
              h('td', {}, thumb),
              h('td', {}, h('strong', {}, r.output || r.name), r.output ? h('div', { class: 'tiny muted' }, 'from ' + r.name, r.after ? ` · ${r.after.width}×${r.after.height}` : '') : null,
                facts.map(f => h('div', { class: 'tiny muted' }, f)),
                r.error ? h('div', { class: 'small', role: 'alert' }, h('span', { class: 'saved-bad' }, 'Not converted: '), r.error) : null,
                r.note ? h('div', { class: 'small muted' }, r.note === 'already woff2' ? 'Already WOFF2: nothing to do.' : r.note) : null),
              h('td', { class: 'num' }, bytes(r.inputBytes)), h('td', { class: 'num' }, bytes(r.outputBytes)),
              h('td', { class: 'num' }, !r.data ? '' : !comparable(r) ? h('span', { class: 'muted', title: 'An ICO holds several sizes, so it is not compared with the source.' }, 'n/a')
                : h('span', { class: r.outputBytes <= r.inputBytes ? 'saved-good' : 'saved-bad' }, saved(r.inputBytes, r.outputBytes))),
              h('td', { class: 'num' }, r.data ? h('button', { class: 'button small', type: 'button', 'aria-label': 'Download ' + r.output, onclick: () => download(r.output, r.data, 'application/octet-stream', true) }, icon('download'), 'Download') : null));
          })))),
        fonts && done.length ? fontFace(done) : null,
        cli(data.cli));
    }
    const saved = (a, b) => a ? `${Math.round((1 - b / a) * 100)}%` : '';
    function fontFace(done) {
      const css = done.map(r => {
        const base = r.output.replace(/\.woff2$/i, '');
        const weight = /black|heavy/i.test(base) ? 900 : /extrabold|ultrabold/i.test(base) ? 800 : /semibold|demibold/i.test(base) ? 600 : /bold/i.test(base) ? 700 : /medium/i.test(base) ? 500 : /extralight|ultralight/i.test(base) ? 200 : /light/i.test(base) ? 300 : /thin/i.test(base) ? 100 : 400;
        const family = base.replace(/[-_ ]?(thin|extralight|ultralight|light|regular|medium|semibold|demibold|bold|extrabold|ultrabold|black|heavy|italic)+$/gi, '') || base;
        return `@font-face {\n  font-family: "${family}";\n  src: url("${r.output}") format("woff2");\n  font-weight: ${weight};\n  font-style: ${/italic/i.test(base) ? 'italic' : 'normal'};\n  font-display: swap;\n}`;
      }).join('\n\n');
      return h('div', { class: 'stack' }, h('div', { class: 'card-head' }, h('h3', {}, 'CSS for the theme'), copyButton(css, 'Copy CSS', 'CSS')),
        h('p', { class: 'hint' }, 'Upload the WOFF2 files as resources of the module, then check the file names in url(…).'), h('pre', { class: 'code-view' }, h('code', { text: css })));
    }
    draw();
  },
};
