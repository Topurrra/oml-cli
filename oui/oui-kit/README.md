# OUI kit: build OUI components in any OutSystems 11 Reactive module

Service Studio refuses to paste OUI into a module of another infrastructure. This kit builds the components again from source.

You need Python 3 and `oml`. The target module must be open in Service Studio (or be a local .oml file).

## Steps

1. Read `kit/catalog.md`. Choose blocks by their description. Page templates are at the top.
2. Build the spec:
   - Some components: `python ouikit.py --blocks OuiCard,OuiTableWrap --new-theme AppTheme --out build`
   - One page template: `python ouikit.py --screens OuiTplDashboard --new-theme AppTheme --out build`
   - Everything: `python ouikit.py --all --new-theme AppTheme --out build`
3. Apply: `oml apply <App.oml> build/spec.json <new staged.oml>`. Expect `saved: true` and no `newErrors`.
4. Install and publish after the person says yes.

## Options

- `--rename Oui=Acme` renames every element, so the names fit your project.
- `--flow UI` puts screens and blocks in that UI flow (the default is UI).
- The module has its own theme: `oml describe <App.oml> <Theme> --css`, save its style sheet to a file, then use `--theme <Theme> --current <file>` instead of `--new-theme`. Your CSS stays.

## Notes

- Dependencies come with the block: other blocks, structures, entities, actions and the script.
- Screens are for registered users. Change their access only if the person asks.
- Templates use demo data. Replace the Load action with an aggregate.
- Blocks that use demo entities need them. The kit adds them.
- `InvalidResourceURL_Offline` on the theme is expected: a checkbox icon is inside the CSS. It matters only for offline mobile apps.
