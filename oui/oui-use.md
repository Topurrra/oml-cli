# How to use the OUI kit

The OUI kit builds OUI components in any OutSystems 11 Reactive module. Use it instead of copy and paste, which Service Studio refuses between infrastructures. The kit is in `oui/oui-kit`.

You need Python 3, `oml`, and a Reactive module in an application. Run the commands from your project folder (made by `oml create`). Replace `<oml-repo>` with the path of this repository, and `MyApp` with your module.

## 1. Choose components

Read `oui/oui-kit/kit/catalog.md`. Page templates are at the top. Each block lists what it does, its inputs, events and placeholders, the blocks it uses, and an example.

## 2. Get the module

Open the module in Service Studio, then:

```bash
oml pull MyApp --out modules/MyApp.oml
```

## 3. Build the spec

Use one of these. Dependencies come with the components.

Some blocks:

```bash
python <oml-repo>/oui/oui-kit/ouikit.py --blocks OuiCard,OuiTableWrap --new-theme MyTheme --out specs/kit
```

A page template. It brings the other templates its menu links to:

```bash
python <oml-repo>/oui/oui-kit/ouikit.py --screens OuiTplDashboard --new-theme MyTheme --out specs/kit
```

All blocks and screens:

```bash
python <oml-repo>/oui/oui-kit/ouikit.py --all --new-theme MyTheme --out specs/kit
```

All blocks, no screens:

```bash
python <oml-repo>/oui/oui-kit/ouikit.py --all-blocks --new-theme MyTheme --out specs/kit
```

### Options

| Option | Use |
|---|---|
| `--rename Oui=Bank` | Renames every element: `OuiCard` becomes `BankCard`. |
| `--flow UI` | The UI flow for the screens and blocks. The default is `UI`. |
| `--new-theme MyTheme` | The module has no theme of its own. Creates one and makes it the default. |
| `--theme MyTheme --current theme.css` | The module has its own theme. Your CSS stays and the kit CSS is added after it. |

To get `theme.css` for `--current`, save the style sheet from `oml describe modules/MyApp.oml MyTheme --css` to a file. Running the kit again replaces only the kit part.

## 4. Apply to a staged copy

```bash
oml apply modules/MyApp.oml specs/kit/spec.json staged/MyApp-kit.oml
```

A good result has `saved: true` and no `newErrors`. The warning `InvalidResourceURL_Offline` on the theme is expected. It matters only for offline mobile apps.

## 5. Install and publish

An agent runs these only after the person says yes.

```bash
oml install staged/MyApp-kit.oml modules/MyApp.oml
```

```bash
oml publish modules/MyApp.oml --close
```

`--close` closes the module's tab in Service Studio first. The tab must have no unsaved changes.

## Good to know

- Screens are for registered users. Change access only if the person asks.
- Page templates use demo data. Replace each screen's `Load` action with your aggregates.
- A template built alone has no links to screens that were not built.
- The showcase menu (`OuiShowcaseNav`) comes only with `--all`.
- `OuiMarkdownView` brings KaTeX and Mermaid, about 5.8 MB of scripts.
- Other modules that use yours need a republish after a change.
