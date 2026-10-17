# Spec recipes

Copy one `.jsonc` file to start a change. Its first comments state the base module,
names to replace, and related keys. Comments are accepted by `oml apply`.
Use a new staged output path for each apply. Do not edit the source module.

| File | Builds | Base |
|---|---|---|
| [entity-with-crud.jsonc](entity-with-crud.jsonc) | Product, static status, checked save and delete | Fresh CrossDevice |
| [list-detail-screens.jsonc](list-detail-screens.jsonc) | Registered-only list/detail, server save | CRUD output on a Falcon copy |
| [edit-existing-flow.jsonc](edit-existing-flow.jsonc) | Insert, add widget, set, rename | List/detail output, once |
| [aggregate-join-groupby.jsonc](aggregate-join-groupby.jsonc) | Join, filter, sort, Sum/Count per category | Fresh CrossDevice |
| [rest-client.jsonc](rest-client.jsonc) | GET/POST and server call | Fresh CrossDevice |
| [references-from-producer.jsonc](references-from-producer.jsonc) | Qualified public entity/action | Fresh CrossDevice and producer export |
| [sql-query.jsonc](sql-query.jsonc) | Parameterized SQL, entity output, loop | Fresh CrossDevice |
| [web-screen.jsonc](web-screen.jsonc) | Preparation, table, save button/action | Fresh WebResponsive; see warning below |
| [timer-role-site-property.jsonc](timer-role-site-property.jsonc) | Timer, role-only screen, site property | Falcon copy |

Create a blank base with `oml new tmp/base.oml --kind CrossDevice` (or `WebResponsive`).
Then run `oml apply tmp/base.oml skill/recipes/sql-query.jsonc tmp/sql.staged.oml`.
Set `TEMP` and `TMP` to your worktree's `tmp` folder before running model commands.

For the UI chain, copy `ref/falcon.oml` to `tmp/ui-base.oml`. Apply entity-with-crud,
then list-detail-screens, then edit-existing-flow, each to a new staged file.
Apply timer-role-site-property separately to another Falcon copy.
Use the module's own `LayoutTopMenu`, not a guessed producer block.
Falcon has a configured exception handler and existing baseline warnings.
The recipes add no errors or warnings to it. The export is private test input and is not committed.
Without it, the check script prints an explicit skip for these three UI recipes.
A local layout alone works on blank CrossDevice, but does not provide an exception handler.

For references, run `oml find Product <consumer.oml> --in <producer.oml>` and
`oml find Product_GetById <consumer.oml> --in <producer.oml>`. Ask the user to approve
the candidates. Replace the path and `RecipeProducer` names in the spec.
The check script creates a disposable producer named RecipeProducer with public Product
(Name: Text) and Product_GetById(Id: Product Identifier). It writes `tmp/recipes/producer.oml`.
Use the producer version deployed in your target environment.

The action recipes expose public actions to avoid unused-action warnings in a standalone module.
Remove public access when you add real internal callers. Enforce authorization in your application.
The list/detail recipe overrides the template's Save to call the checked server action.
The default template Save calls generated CRUD directly and produces a security warning.
REST creates client-owned DTO copies; the source ProductDto is public to avoid an unused-structure warning.

On Service Studio 11.55.92, fresh WebResponsive screens produce one
`NoErrorHandlingInFlow` warning. The current spec cannot create a UI flow's OnException
handler. Model validation still passes. A configured handler in an exported base module
is needed to remove this warning. Input maxLength must match the entity attribute length.
SQL, REST, timers, access and browser behavior still need tests in the target environment.

Build first, then run the check from the repository root:

```powershell
dotnet build -c Release src/OmlCli.csproj
pwsh -NoProfile -File tests/recipes-check.ps1
```

The script checks saved output, problems, new errors, reopened errors and new warnings.
It also runs from `tests/regress.ps1`. It never installs or publishes.
