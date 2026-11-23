# Motion recipes

CSS for the theme or the block's style sheet. Scope every rule to your own class. A state class is switched by the widget's Style Classes expression: `"my-panel" + If(IsOpen, " is-open", "")` in Reactive; in Traditional Web, Style Classes is plain text, so switch the class from the screen's JavaScript or with an Ajax Refresh.

## Press

```css
.my-actions .btn {
  transition-property: scale;
  transition-duration: 150ms;
  transition-timing-function: ease-out;
}
.my-actions .btn:not(:disabled):active {
  scale: 0.96;
}
```

## Open and close a panel

A transition, so a second click halfway reverses it:

```css
.my-panel {
  opacity: 0;
  transition: opacity 150ms ease-out;
}
.my-panel.is-open {
  opacity: 1;
}
@media (prefers-reduced-motion: no-preference) {
  .my-panel {
    translate: 0 12px;
    transition-property: opacity, translate;
    transition-duration: 150ms;
  }
  .my-panel.is-open {
    translate: 0 0;
    transition-duration: 250ms;
  }
}
```

The open state's duration applies on the way in, the closed state's on the way out.

## Staged entrance

The hidden state lives in the `from` keyframe, so an item whose animation never runs stays visible. Under reduced motion only the fade runs.

```css
.my-intro > * {
  animation: my-fade-in 300ms ease-out both;
}
@media (prefers-reduced-motion: no-preference) {
  .my-intro > * {
    animation-name: my-fade-in-up;
  }
}
.my-intro > :nth-child(2) { animation-delay: 100ms; }
.my-intro > :nth-child(3) { animation-delay: 200ms; }

@keyframes my-fade-in {
  from { opacity: 0; }
}
@keyframes my-fade-in-up {
  from {
    opacity: 0;
    transform: translateY(12px);
    filter: blur(4px);
  }
}
```

## Icon cross-fade

Both Icons stay rendered in one grid cell, so the larger one sets the size and both directions animate. The wrapper's Style Classes: `"my-copy" + If(IsCopied, " is-copied", "")`. Give each Icon `aria-hidden="true"`; the Button's `aria-label` names the action.

```css
.my-copy__icons {
  display: inline-grid;
  place-items: center;
}
.my-copy__icons > * {
  grid-area: 1 / 1;
  transition-property: opacity, scale, filter;
  transition-duration: 300ms;
  transition-timing-function: cubic-bezier(0.2, 0, 0, 1);
}
.my-copy .my-copy__done,
.my-copy.is-copied .my-copy__idle {
  opacity: 0;
  scale: 0.25;
  filter: blur(4px);
}
.my-copy.is-copied .my-copy__done {
  opacity: 1;
  scale: 1;
  filter: blur(0px);
}
@media (prefers-reduced-motion: reduce) {
  .my-copy__icons > * {
    scale: 1 !important;
    filter: none !important;
  }
}
```

The `!important` is the one allowed case: a fixed state that must win over every scale rule.

## Measured height

For a block whose content changes size. Start it in the block's `OnReady` with a JavaScript node, keep the observer, and disconnect it in `OnDestroy`. Query inside the block's root, never the page.

```js
var root = document.getElementById($parameters.RootId);
var outer = root.querySelector(".my-expand");
var inner = root.querySelector(".my-expand__inner");
var observer = new ResizeObserver(function () {
  outer.style.height = inner.offsetHeight + "px";
});
observer.observe(inner);
$parameters.Observer = observer;
```

```css
.my-expand {
  overflow: hidden;
  transition: height 300ms cubic-bezier(0.2, 0, 0, 1);
}
@media (prefers-reduced-motion: reduce) {
  .my-expand { transition: none; }
}
```

`offsetHeight` reads the layout size; `getBoundingClientRect` reads the painted size, which is wrong while a parent scales in. Keep the observer in an Object local (an output of the JavaScript node) so `OnDestroy` can call `observer.disconnect()`.

## Reduced motion

The fade runs for everyone; movement, scale and blur join it only without the preference:

```css
.my-toast {
  transition: opacity 150ms ease-out;
}
@media (prefers-reduced-motion: no-preference) {
  .my-toast {
    transition-property: opacity, translate, scale, filter;
    transition-duration: 300ms;
  }
}
```

Under reduced motion: no parallax, no autoplay, no smooth scrolling (jump instead). Spinners, progress bars and instant color changes stay.

## Theme switch

When the user flips the theme, turn transitions off for one frame so every color does not animate at once. Run this in the client action that switches the theme, around the class change:

```js
var style = document.createElement("style");
style.textContent = "*, *::before, *::after { transition: none !important; }";
document.head.appendChild(style);
// switch the theme class here
void document.body.offsetHeight;
requestAnimationFrame(function () {
  requestAnimationFrame(function () { style.remove(); });
});
```

The forced read (`offsetHeight`) commits the new colors while the override is in place; the second frame removes it after that paint.
