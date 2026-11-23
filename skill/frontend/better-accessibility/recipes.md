# Accessibility recipes

CSS for the theme or the block's style sheet, and the attribute tables. Scope every rule to your own class; never style `body`, `html`, `*` or a bare `button`.

## Focus rings

Keep the browser ring and give it room:

```css
.my-card a:focus-visible,
.my-card button:focus-visible {
  outline-offset: 2px;
}
```

A custom ring, when the design needs one, uses a theme color you checked at 3:1 against every color it crosses:

```css
.my-toolbar button:focus-visible {
  outline: 2px solid var(--color-primary);
  outline-offset: 2px;
}
```

An outline with no color draws `currentColor`, which may fail. A wrapper that should light up while its input has focus (a search box with an icon) uses `:focus-within`. In `forced-colors: active` keep the default colors, or name a system color such as `Highlight`.

## Focus under a sticky header

When the app has a fixed header (`HasFixedHeader` on the layout), give the scroller room so a focused element never lands under it. The header height is the value `oml browser look <url> <HeaderWidget>` gives in `box`:

```css
html {
  scroll-padding-block-start: 64px;
}
```

## Visually hidden text

Text for screen readers only, such as "Opens in a new tab":

```css
.sr-only {
  position: absolute;
  width: 1px;
  height: 1px;
  padding: 0;
  margin: -1px;
  overflow: hidden;
  clip: rect(0 0 0 0);
  clip-path: inset(50%);
  white-space: nowrap;
  border: 0;
}
```

Use `1px`, not `0`: some screen readers skip zero-size elements. Never `display: none` or `visibility: hidden`, which remove the text for screen readers too.

## Larger hit area

A small visible control keeps its look and gets a 44 px target from a pseudo-element on the Button or Link, never on the Input:

```css
.my-icon-button {
  position: relative;
}
.my-icon-button::after {
  content: "";
  position: absolute;
  top: 50%;
  left: 50%;
  transform: translate(-50%, -50%);
  width: 44px;
  height: 44px;
}
```

When two extended areas would overlap, shrink both until they do not. A control that can afford the size takes it directly: `min-width: 44px; min-height: 44px; display: inline-grid; place-items: center;`.

## Decorative layers

A glow, gradient or overlay on top of controls takes every click it covers. Give it `pointer-events: none`, and `aria-hidden="true"` when it is a widget:

```css
.my-hero__glow {
  position: absolute;
  inset: 0;
  pointer-events: none;
}
```

A popup backdrop that closes on click is a control: keep its pointer events.

## Status region

A Container that is always rendered, with attribute `role` = `"status"` (or `"alert"` for urgent errors) and the class `sr-only` when it is for screen readers only. Change its Expression's value to announce. An If around it removes it from the page.

## Forms

`autocomplete` attribute values for fields about the user:

| Field | `autocomplete` |
| --- | --- |
| Name | `name`, or `given-name` and `family-name` |
| Email | `email` |
| Phone | `tel` |
| Address | `street-address`, `address-line1`, `postal-code`, `country` |
| Login | `username`, `current-password` |
| New password | `new-password` |
| One-time code | `one-time-code` |

Keyboard: set the Input's Input Type to Email, Phone Number, Password or Number where it fits. For a code or a card number keep Text and add the attribute `inputmode` = `"numeric"`, so no number spinner appears. For money use `inputmode` = `"decimal"`. Add `spellcheck` = `"false"` on emails, codes and user names.

Mark required inputs with Mandatory (the platform marks them) and explain the mark once per form. Trim values before you check them: autofill adds trailing spaces.

## Keyboard patterns

Use the OutSystems UI pattern when one exists, and check these keys on the published page:

| Widget | Keys |
| --- | --- |
| Popup, Sidebar, BottomSheet | Tab stays inside while open; Escape closes |
| Tabs | Arrow keys move between tabs; Home and End go to the first and last |
| Accordion | The header is a button; Enter and Space toggle |
| DropdownSearch | Down arrow opens; Enter picks; Escape closes and returns to the input |
| Radio group | Arrow keys move the choice; one Tab stop for the group |
