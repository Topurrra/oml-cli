# The picker

The control that flips variants in the running page. It is injected with `oml browser try <url> --js picker.js` in a headed session, so nothing is saved. Keep it as below; `state-machine`'s `switcher.md` is its twin on a test screen, with the same look.

## Outside the design system

Never style the picker with the app's theme tokens, fonts or colors. One dark surface, the system font, no theme variables, fixed in the corner of the window.

## Behavior

- A list of variant buttons under one heading (the axis). The active one has `aria-pressed="true"`.
- Arrow keys step through the variants; `H` hides and shows the picker for screenshots. Keys are ignored with a modifier and inside inputs.
- Switching a variant replaces the variant style sheet and, when the variant has HTML, the content of the target widget. The scroll position stays.

## picker.js

Fill `VARIANTS` from the variant files (CSS text and, where the structure changes, the HTML for the widget), `TARGET` with the widget name and `AXIS` with the axis. Keep the strings plain: no comments inside the CSS.

```js
(function () {
  var AXIS = "Density";
  var TARGET = "KpiCard";
  var VARIANTS = [
    { name: "Quiet", css: ".kpi-card { padding: var(--space-m); }", html: null },
    { name: "Dense", css: ".kpi-card { padding: var(--space-s); }", html: null }
  ];
  var target = document.querySelector('[id$="-' + TARGET + '"], [id="' + TARGET + '"]');
  var original = target ? target.innerHTML : null;
  var sheet = document.createElement("style");
  document.head.appendChild(sheet);
  var box = document.createElement("div");
  box.setAttribute("role", "group");
  box.setAttribute("aria-label", "Variants");
  box.style.cssText = "position:fixed;right:16px;bottom:16px;z-index:2147483647;display:flex;flex-direction:column;gap:2px;" +
    "padding:6px;border-radius:14px;background:rgba(20,20,20,.92);box-shadow:inset 0 0 0 1px rgba(255,255,255,.1);" +
    "font:13px/1 system-ui,-apple-system,'Segoe UI',sans-serif";
  var head = document.createElement("span");
  head.textContent = AXIS;
  head.style.cssText = "padding:4px 10px 6px;font-size:11px;font-weight:500;color:rgba(255,255,255,.45)";
  box.appendChild(head);
  var buttons = VARIANTS.map(function (v, i) {
    var b = document.createElement("button");
    b.type = "button";
    b.textContent = v.name;
    b.style.cssText = "all:unset;padding:8px 10px;border-radius:8px;cursor:pointer;color:rgba(255,255,255,.6)";
    b.addEventListener("click", function () { pick(i); });
    box.appendChild(b);
    return b;
  });
  var current = 0;
  function pick(i) {
    current = (i + VARIANTS.length) % VARIANTS.length;
    var v = VARIANTS[current];
    sheet.textContent = v.css;
    if (target) target.innerHTML = v.html !== null ? v.html : original;
    buttons.forEach(function (b, n) {
      var on = n === current;
      b.setAttribute("aria-pressed", on ? "true" : "false");
      b.style.background = on ? "rgba(255,255,255,.14)" : "transparent";
      b.style.color = on ? "#fff" : "rgba(255,255,255,.6)";
    });
  }
  document.addEventListener("keydown", function (e) {
    if (e.defaultPrevented || e.ctrlKey || e.metaKey || e.altKey) return;
    if (/^(INPUT|TEXTAREA|SELECT)$/.test(e.target.tagName) || e.target.isContentEditable) return;
    if (e.key === "ArrowDown" || e.key === "ArrowRight") pick(current + 1);
    else if (e.key === "ArrowUp" || e.key === "ArrowLeft") pick(current - 1);
    else if (e.key === "h" || e.key === "H") box.style.display = box.style.display === "none" ? "flex" : "none";
    else return;
    e.preventDefault();
  });
  document.body.appendChild(box);
  pick(0);
})();
```

The platform sets widget ids, and a widget's name is the last part of its id (`b2-b3-KpiCard` is `KpiCard`). When a block renders it more than once, the script takes the first one. A data refresh can re-render the widget and drop the variant's HTML; select the variant again, or reload and inject again.
