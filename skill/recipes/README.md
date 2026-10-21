# Spec recipes

Copy one `.jsonc` file to start a change. Its first comments state the base module,
names to replace, and related keys. Comments are accepted by `oml apply`.
Use a new staged output path for each apply. Do not edit the source module.

| File | Builds | Base |
|---|---|---|
| [entity-with-crud.jsonc](entity-with-crud.jsonc) | Product, static status, checked save and delete | Fresh CrossDevice |
| [list-detail-screens.jsonc](list-detail-screens.jsonc) | Registered-only list/detail, server save | CRUD output on a UI base |
| [edit-existing-flow.jsonc](edit-existing-flow.jsonc) | Insert, add widget, set, rename | List/detail output, once |
| [aggregate-join-groupby.jsonc](aggregate-join-groupby.jsonc) | Join, filter, sort, Sum/Count per category | Fresh CrossDevice |
| [rest-client.jsonc](rest-client.jsonc) | GET/POST and server call | Fresh CrossDevice |
| [references-from-producer.jsonc](references-from-producer.jsonc) | Qualified public entity/action | Fresh CrossDevice and producer export |
| [sql-query.jsonc](sql-query.jsonc) | Parameterized SQL, entity output, loop | Fresh CrossDevice |
| [web-screen.jsonc](web-screen.jsonc) | Preparation, table, save button/action | Fresh WebResponsive; see warning below |
| [timer-role-site-property.jsonc](timer-role-site-property.jsonc) | Timer, role-only screen, site property | UI base |
| [form-with-validation.jsonc](form-with-validation.jsonc) | Contact form, client messages, server check and exception | UI base |
| [dropdown-from-static.jsonc](dropdown-from-static.jsonc) | Static entity, Dropdown bound to it, label table | UI base |
| [list-search-paging.jsonc](list-search-paging.jsonc) | Search filter, sort, Previous/Next paging, empty state | UI base |
| [aggregate-on-demand-detail.jsonc](aggregate-on-demand-detail.jsonc) | Master-detail: on-demand detail aggregate refreshed on selection, index | UI base |
| [loading-empty-error-states.jsonc](loading-empty-error-states.jsonc) | List screen with failed (Retry), loading and empty states | UI base |
| [expose-rest-api.jsonc](expose-rest-api.jsonc) | Exposed GET (path) and POST (body); see `oml openapi` | Fresh CrossDevice |
| [role-protected-screen.jsonc](role-protected-screen.jsonc) | Role-only screen, server role check, role-only link | UI base |
| [block-popup-event.jsonc](block-popup-event.jsonc) | Confirm popup block with events, screen handlers, delete | UI base |
| [send-email.jsonc](send-email.jsonc) | Reactive email, server action that loads data and sends it | Fresh CrossDevice |
| [bdd-test-screen.jsonc](bdd-test-screen.jsonc) | BDD test screen for a public server action; output of `oml test scaffold`, unverified against the real BDDFramework | New CrossDevice test module, BDDFramework and producer exports |

Create a blank base with `oml new tmp/base.oml --kind CrossDevice` (or `WebResponsive`).
Then run `oml apply tmp/base.oml skill/recipes/sql-query.jsonc tmp/sql.staged.oml`.
Set `TEMP` and `TMP` to your worktree's `tmp` folder before running model commands.

For the UI recipes, use a copy of your own Reactive module as the UI base. It needs `LayoutTopMenu` with the Title, Actions and MainContent placeholders, and a configured exception handler.
Apply entity-with-crud, then list-detail-screens, then edit-existing-flow, each to a new staged file.
Apply timer-role-site-property, form-with-validation, dropdown-from-static, list-search-paging, aggregate-on-demand-detail, loading-empty-error-states, role-protected-screen and block-popup-event separately, each to its own copy of the UI base. Each adds its own entities, so none needs the CRUD chain.
Use the module's own `LayoutTopMenu`, not a guessed producer block.
`tests/recipes-check.ps1` builds such a UI base itself, so every recipe runs in every clone.
A local layout alone works on blank CrossDevice, but does not provide an exception handler.

For references, run `oml find Product <consumer.oml> --in <producer.oml>` and
`oml find Product_GetById <consumer.oml> --in <producer.oml>`. Ask the user to approve
the candidates. Replace the path and `RecipeProducer` names in the spec.
The check script creates a disposable producer named RecipeProducer with public Product
(Name: Text) and Product_GetById(Id: Product Identifier). It writes `tmp/recipes/producer.oml`.
Use the producer version deployed in your target environment.

Only `entity-with-crud` makes its server actions public: it is a core module that other modules call.
Each public server action adds a `PublicServerAction` new warning (rule 7): ask the user, or leave out `public`.
The other recipes keep their server actions private. In a standalone module some of them have no caller, so apply
reports `UnusedUserAction` for them (aggregate-join-groupby, references-from-producer, rest-client, send-email,
sql-query): call them from your own logic. Enforce authorization in your application.
The list/detail recipe overrides the template's Save to call the checked server action.
The default template Save calls generated CRUD directly and produces a security warning.
REST creates client-owned DTO copies; the source ProductDto is public to avoid an unused-structure warning.

On Service Studio 11.55.92, fresh WebResponsive screens produce one
`NoErrorHandlingInFlow` warning. Model validation still passes. To remove it, add a UI
flow handler with the `onException` key (`oml spec onException`). Input maxLength must match the entity attribute length.
SQL, REST, timers, access and browser behavior still need tests in the target environment.

Build first, then run the check from the repository root:

```powershell
dotnet build -c Release src/OmlCli.csproj
pwsh -NoProfile -File tests/recipes-check.ps1
```

The script checks saved output, problems, new errors, reopened errors and new warnings.
It also runs from `tests/regress.ps1`. It never installs or publishes.
