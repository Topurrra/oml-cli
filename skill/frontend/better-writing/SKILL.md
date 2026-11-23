---
name: better-writing
description: Writes and reviews the words in OutSystems screens, from labels and messages to empty states and confirmations, so they match the app's voice and tell people what to do next.
---

# Writing

This skill writes and reviews interface copy in OutSystems apps: labels, button texts, messages, validation texts, empty states and confirmations, and the terms that run through them. It matches the app's existing voice and flags copy that misleads people or leaves them stuck.

A deliberate voice is not a defect. Raise plain-language issues only when they cause inconsistency, ambiguity, translation risk or a tone the stakes do not support. Rewording to your taste is not a finding.

How text renders (case, truncation, punctuation characters) belongs to `better-typography`. Names and announcements for assistive technology belong to `better-accessibility`. Room for long text belongs to `better-layout`.

## Inventory the existing words first

Copy in an OutSystems module lives in widget texts, Expression values, Input prompts, `confirmationMessage`, Message nodes, validation messages and static entity labels. The module's default validation texts are in `oml inspect <module.oml>` under `module.settings` (`mandatoryInput`: "Required field!"). Before you write:

1. Read the screens you change with `oml describe <module.oml> <Screen>`, and find a term across the module with `oml find <module.oml> --text "<word>"`.
2. List the noun for each object and the verb for each action: "order" or "request", "Delete" or "Remove".
3. Note the case used per element type, and any team rule (`oml team-rules`).

New copy uses the terms on that list. A synonym is a finding only where one thing has two names.

## One voice, one vocabulary

The app's existing copy sets its voice; a local change does not invent a new one. If it is "Archive" in the menu, it is not "Move to storage" in the message. A multi-step flow (an OutSystems UI `Wizard`) uses one set: "Start" to enter, "Next" (or "Continue", never both) to advance, "Done" to finish.

| Context | Tone |
| --- | --- |
| Success, first use, empty states | Warm, may be light |
| Routine actions, settings | Neutral, short |
| Errors, destructive confirmations | Calm, plain, no jokes |
| Data loss, security | Serious, explicit |

## Talk to the reader

Write "you", not "the user". In errors avoid "we": "Cannot load the orders. Check your connection and try again." Never mix "My account" and "Your settings" in one flow.

## Plain words

Words a tired reader gets on the first pass; delete words that do no work. No idioms or humor that will not translate. "Select" works for mouse and touch; use "click" or "tap" only when one input applies.

## Write whole sentences

Never build a sentence from pieces around a value (`"You have " + Count + " new messages"`): word order changes per language. Keep the sentence in one literal and put the value in with `Replace`: `Replace("{count} new messages", "{count}", Count)`. OutSystems has no plural rules: choose between whole sentences with `If`. Format numbers and dates with `FormatDecimal`, `FormatCurrency`, `FormatPercent` and `FormatDateTime`, never by joining parts. When the module is multilingual (`ui.translations` in the profile), every new text needs its translations (spec keys `locales` and `translate`: `oml spec translate`).

## Buttons start with a verb

"Save", "Send invoice", "Delete order". Never "OK!" or "Let's go!". A new Button shows "Button" and a new Link "link": replace that text. An icon-only Button's `aria-label` names the action ("Delete order"), never the glyph ("Trash icon").

## Links name their destination

Link text makes sense alone, because screen-reader users list a page's links: "Read the billing rules", never "Click here". Two "Learn more" links on one screen need suffixes: "Learn more about exports".

## One capitalization rule

Pick sentence case or title case per element type and use it everywhere for that type. Sentence case is the default. "Save Changes" next to "Discard changes" looks careless.

## Settings describe the ON state

A Switch or Checkbox label says what happens when it is on: "Send read receipts", not "Don't send read receipts". Link straight to a setting rather than describing the path to it.

## Errors say how to fix it, next to where it broke

An error is an instruction, shown at the input that failed (`<Input>.ValidationMessage`), not only in a Message at the top:

| Bad | Good |
| --- | --- |
| Password too short | Use at least 8 characters |
| Invalid date | Enter a date as DD/MM/YYYY |
| Oops! Something went wrong. | Cannot save the order. Check your connection and try again. |

No blame, no "oops", no exclamation marks. Show a known rule as help text before the user types, not only in the error. A user exception's message reaches the user: write it the same way. When the same error keeps firing, change the screen so it cannot happen.

## Undo beats confirmation

Prefer undo when the action can be reversed and people do it often (archive, move): act at once and say "Order archived" with an Undo action.

Confirm first when the action cannot be reversed, affects other people or removes many items. The confirmation repeats the consequence:

- The title names the action and the object: "Delete order 4521?", never "Are you sure?".
- The body says what is lost, with counts: "This deletes the order and its 3 invoices. You cannot undo this."
- The buttons are verb plus object: "Delete order" and "Cancel", never "Yes" and "No".
- For a whole account or workspace, ask the user to type its name.

A Button's `confirmationMessage` shows the browser's own OK and Cancel buttons. Use it only for a small reversible action; for an irreversible delete build a Popup with your own buttons. The danger color is `better-colors`.

## Empty states point forward

Say what this place is and how to fill it, with one next action. In OutSystems UI use `BlankSlate` (placeholders `Icon`, `Content`, `Actions`), shown when `<Agg>.IsDataFetched and <Agg>.List.Empty`:

- Title: "No orders yet". Body: "Orders you create appear here." Action: "Create order".
- Filtered: "No orders match 'Lisbon'." Action: "Clear filters".

Never keep lasting information in an empty state: it goes away when data arrives.

## Status messages

| State | Pattern | Example |
| --- | --- | --- |
| In progress | Verb in -ing plus the object | "Saving changes..." |
| Done | Object plus past verb, no "successfully" | "Changes saved" |
| Failed | What did not happen, then the next step | "Changes not saved. Check your connection and try again." |
| Unavailable | Why, and what unlocks it, next to the control | "Add a payment method to publish" |
| Partial | Counts for both outcomes | "9 of 12 files uploaded. Retry 3 files" |

Keep the pairs parallel: "Saving changes..." ends as "Changes saved". The Message node's `type` (`info`, `success`, `warning`, `error`) matches the text.

## Prompts show an example

An Input's prompt shows a real example in the accepted format (`name@example.com`, `DD/MM/YYYY`), never an instruction. Whether the input also needs a Label is `better-accessibility` (it does).

## Before you finish

| Pattern | Fix |
| --- | --- |
| "Click here", "here", or two equal "Learn more" links | Name the destination |
| "Oops", "Something went wrong" or "!" in an error | Say what failed and the next step |
| `"..." + Count + "..."` around a sentence | One literal with `Replace` |
| Popup buttons "OK", "Yes", "No" | Verb plus object |
| "Are you sure" | Name the action and the object |
| "successfully" | Cut it: "Changes saved" |
| "Please" in a routine instruction | Cut it |
| A Switch label starting "Don't", "Disable" or "Hide" | Describe the ON state |
| `aria-label` "Trash", "Close icon" or "X" | Name the action: "Delete order", "Close" |
| "Button", "link", "Text", "Lorem ipsum" left in a widget | Real words |
| "Save Changes" next to "Discard changes" | One case per element type |

## Reporting

**Verification.** Check every label against the action it runs, every error for a stated fix, and every term against the inventory. Read static entity labels and server exception messages as well as widgets. Report texts from the database or another module that you could not see as `Not verified`.

**Standalone severity.** `HIGH` misleads or hides how to recover; an error with no way to recover and a destructive action with no confirmation or undo are always `HIGH`. `MEDIUM` makes copy harder to act on or to translate, or breaks voice, terms or case. `LOW` is isolated wording. One row per root cause with every place: Severity, Location, Before, After, Why. End with `Block` when a `HIGH` remains, else `Approve`. With nothing to report: "No actionable writing findings", then the verification.
