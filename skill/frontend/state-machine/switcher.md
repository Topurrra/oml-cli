# The switcher

The control that switches states on the test screen. It sits beside the block being judged, so build it as below and leave it alone. `variant` injects a twin of this control (its `picker.md`); keep the two looking the same.

## Outside the design system

Never style the switcher with the app's theme tokens, fonts or colors: a switcher that looks like the app becomes part of what you judge. One dark surface, the system font, no theme variables. It does not follow the dark theme.

## Structure

A Container with class `state-page` holding two Containers: `state-switcher` and `state-stage`. In `state-switcher`, one Container per group with class `state-switcher-group`; in each group, a Text heading with class `state-switcher-heading` and one Link per state. Each Link goes to the test screen itself with `State` set to its value. Its Style Classes: `If(State = "empty", "is-active", "")`. Give the switcher Container the attribute `aria-label` = `"States"`, and each active Link `aria-current` = `"true"`.

Groups and states keep the order you wrote them down in. `state-stage` holds the block, in a Container as wide as the block is in production.

## CSS

Put it in the test screen's style sheet. Change `644px` to the block's production width and `876px` to that width plus 232.

```css
.state-page {
  padding: 24px;
}
.state-page .state-switcher {
  display: flex;
  flex-wrap: wrap;
  gap: 16px;
  margin-bottom: 16px;
  padding: 6px;
  border-radius: 14px;
  background: rgba(20, 20, 20, 0.92);
  box-shadow: inset 0 0 0 1px rgba(255, 255, 255, 0.1);
  font: 13px/1 system-ui, -apple-system, "Segoe UI", sans-serif;
}
.state-page .state-switcher-group {
  display: flex;
  flex-direction: column;
  gap: 2px;
}
.state-page .state-switcher-heading {
  padding: 4px 10px 6px;
  font-size: 11px;
  font-weight: 500;
  color: rgba(255, 255, 255, 0.45);
}
.state-page .state-switcher a {
  padding: 8px 10px;
  border-radius: 8px;
  color: rgba(255, 255, 255, 0.6);
  text-decoration: none;
  white-space: nowrap;
}
.state-page .state-switcher a:hover {
  color: rgba(255, 255, 255, 0.85);
}
.state-page .state-switcher a.is-active {
  background: rgba(255, 255, 255, 0.14);
  color: #fff;
  font-weight: 500;
}
.state-page .state-switcher a:focus-visible {
  outline: 2px solid rgba(255, 255, 255, 0.7);
  outline-offset: 2px;
}
.state-page .state-stage {
  max-width: 644px;
}
@media (min-width: 876px) {
  .state-page {
    display: grid;
    grid-template-columns: 208px 644px;
    gap: 24px;
    align-items: start;
    justify-content: center;
  }
  .state-page .state-switcher {
    flex-direction: column;
    flex-wrap: nowrap;
    margin-bottom: 0;
    position: sticky;
    top: 24px;
    max-height: calc(100dvh - 48px);
    overflow-y: auto;
  }
}
```

The 6 px padding keeps the list's own scrolling from cutting off focus rings. This is the one place a screen-level media query is right: the switcher is a test tool, not app UI.
