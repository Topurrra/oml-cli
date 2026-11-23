# Page scripts

Scripts for `explain-interface`. Each is a function that returns its data. Run one on the page of an `oml browser` session:

1. `oml browser start` (once), then `oml browser page <url>` to open the page.
2. Save the function as `<name>.js`. Make the params file in PowerShell:
   `@{ expression = "(" + (Get-Content -Raw <name>.js) + ")()"; returnByValue = $true } | ConvertTo-Json | Set-Content <name>.json`
3. `oml browser raw Runtime.evaluate <name>.json`. A result over 20,000 characters goes to a file; the output says where.

For one element, `oml browser look <url> <target>` is cheaper than any script here.

## Which platform

```js
() => ({
  outsystemsReactive: !!document.getElementById('transitionContainer'),
  outsystemsTraditional: !!document.querySelector('input[name="__OSVSTATE"]'),
  outsystemsUI: !!document.querySelector('[class*="osui-"]'),
  blocks: [...new Set([...document.querySelectorAll('[data-block]')].map(e => e.getAttribute('data-block')))].slice(0, 30),
  bodyClasses: document.body.className,
  next: !!window.__NEXT_DATA__,
  tailwind: !!document.querySelector('[class*="bg-linear-to"], [class*="bg-gradient-to"]'),
  generator: document.querySelector('meta[name="generator"]')?.content ?? null,
})
```

A hit is evidence for an inference, not proof; a check that did not fire is not evidence of absence. `bodyClasses` shows the device classes OutSystems UI sets (`desktop`, `tablet`, `phone`).

## The layer search

```js
() => {
  const dead = v => !v || v === 'none' || v === 'normal' || v === '1' || v === 'blur(0px)';
  const props = ['backgroundImage', 'filter', 'backdropFilter', 'mixBlendMode', 'maskImage', 'boxShadow', 'opacity'];
  const hits = [];
  for (const el of document.querySelectorAll('*')) {
    for (const pseudo of [null, '::before', '::after']) {
      const s = getComputedStyle(el, pseudo);
      const found = {};
      for (const p of props) if (!dead(s[p])) found[p] = String(s[p]).slice(0, 200);
      const keys = Object.keys(found);
      if (!keys.length || (keys.length === 1 && found.opacity)) continue;
      const r = el.getBoundingClientRect();
      hits.push({ tag: el.tagName.toLowerCase(), pseudo: pseudo ?? 'element',
        cls: (el.getAttribute('class') ?? '').slice(0, 90), z: getComputedStyle(el).zIndex,
        box: `${Math.round(r.width)}x${Math.round(r.height)} @ x${Math.round(r.left)} y${Math.round(r.top)}`, found });
    }
  }
  return { viewport: innerWidth, total: hits.length, hits: hits.slice(0, 60) };
}
```

Read the stack, not one row:

- A `box` wider than the viewport is oversized on purpose.
- Rows come back in DOM order: a later row roughly paints over an earlier one, unless `z` says otherwise.
- `backdropFilter` or `mixBlendMode` on a row means the layer under it is part of the answer.

An empty result over the region means the effect is not CSS: look for a `canvas`, `svg` filter or video.

## What paints at one point

```js
() => {
  const x = 720, y = 300;
  return document.elementsFromPoint(x, y).slice(0, 8).map(el => {
    const s = getComputedStyle(el);
    return { tag: el.tagName.toLowerCase(), cls: (el.getAttribute('class') ?? '').slice(0, 70),
      bg: s.backgroundImage.slice(0, 80), filter: s.filter, backdrop: s.backdropFilter, blend: s.mixBlendMode };
  });
}
```

Front to back. It skips elements with `pointer-events: none`, which decorative layers often have, and returns a pseudo-element as its host.

## Is it animated?

```js
() => document.getAnimations().slice(0, 20).map(a => {
  const t = a.effect?.getTiming?.() ?? {};
  return { target: a.effect?.target?.tagName?.toLowerCase(),
    cls: (a.effect?.target?.getAttribute?.('class') ?? '').slice(0, 60),
    name: a.animationName ?? a.transitionProperty ?? '(js)',
    duration: t.duration, delay: t.delay, easing: t.easing,
    keyframes: (a.effect?.getKeyframes?.() ?? []).map(({ offset, easing, opacity, transform, filter }) => ({ offset, easing, opacity, transform, filter })) };
})
```

A CSS animation carries its curve on each keyframe; its effect-level `easing` reads `linear`. Different `delay` values on siblings show a stagger. Nothing returned: reload and run it during the reveal.

## The whole system

Run these in order; tokens tell you most of the answer.

**Tokens** (custom properties on the root, also inside `@media` and `@layer`):

```js
() => {
  const tokens = {}, unreadable = [];
  const onRoot = sel => { try { return document.documentElement.matches(sel); } catch { return false; } };
  const applies = r => r.media ? matchMedia(r.media.mediaText).matches : r.conditionText ? CSS.supports(r.conditionText) : true;
  const walk = list => { for (const r of list ?? []) {
    if (!applies(r)) continue;
    if (r.selectorText && onRoot(r.selectorText)) for (const p of r.style) if (p.startsWith('--')) tokens[p] = r.style.getPropertyValue(p).trim();
    if (r.cssRules) walk(r.cssRules);
  } };
  for (const sheet of document.styleSheets) { let rules; try { rules = sheet.cssRules; } catch { unreadable.push(sheet.href); continue; } walk(rules); }
  return { count: Object.keys(tokens).length, tokens, unreadable };
}
```

Group by prefix: OutSystems UI themes use `--color-*`, `--space-*`, `--border-radius-*`, `--shadow-*` and `--font-size-*`. A sheet in `unreadable` is cross-origin: name it as unread.

**Type scale** (leaf text only):

```js
() => {
  const seen = new Map();
  for (const el of document.querySelectorAll('*')) {
    if (el.children.length || !el.textContent?.trim()) continue;
    const s = getComputedStyle(el);
    const key = `${parseFloat(s.fontSize)}px w${s.fontWeight} lh ${s.lineHeight} ls ${s.letterSpacing} ${s.fontFamily.split(',')[0]}`;
    seen.set(key, (seen.get(key) ?? 0) + 1);
  }
  return [...seen].sort((a, b) => b[1] - a[1]).slice(0, 25);
}
```

**Spacing, radii, shadows, motion** (counts per value):

```js
() => {
  const count = (fn) => { const m = new Map(); for (const el of document.querySelectorAll('*')) for (const v of fn(getComputedStyle(el))) if (v) m.set(v, (m.get(v) ?? 0) + 1); return [...m].sort((a, b) => b[1] - a[1]).slice(0, 15); };
  return {
    spacing: count(s => ['paddingTop', 'paddingLeft', 'marginTop', 'gap'].map(p => parseFloat(s[p]) > 0 ? s[p] : null)),
    radius: count(s => [s.borderRadius !== '0px' ? s.borderRadius : null]),
    shadow: count(s => [s.boxShadow !== 'none' ? s.boxShadow.slice(0, 80) : null]),
    motion: count(s => [s.transitionDuration !== '0s' ? `${s.transitionProperty} ${s.transitionDuration} ${s.transitionTimingFunction}` : null]),
  };
}
```

Find the base unit that divides most spacing values, and compare the gap between groups with the gap inside one (`better-layout` expects twice or more). A few values reused everywhere is derived evidence of a scale.

**A second state.** Everything above reads one width and one state. Before you explain: run the type and spacing scripts again at a phone width (a session started with `oml browser start --width 390`), read the dark theme with `oml browser look <url> <target> --theme dark`, and read the focus style with `--state focus-visible`.
