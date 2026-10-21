# Testing with BDDFramework

Read this when the user asks for automated tests of an O11 module. oml does not run tests. It can build the test screen: screen actions with `assign` and `execute` nodes, locals, and framework block instances with inputs and placeholders. The user must install the Forge component, publish, open the screen and read the results.

## What BDDFramework is

Two Forge components give Given/When/Then tests written as OutSystems code:

| Component | Tests | Test module (inferred, check with `oml inspect`) |
|---|---|---|
| BDDFramework | Public server actions | Traditional Web: web screens, server screen actions |
| BDDFramework Client Side | Public client actions | Reactive: client screen actions |

Server test screens are Traditional Web screens. See `oml spec "Traditional Web UI"` (no `aggregates` or `dataActions`; use `preparation`).
The framework gives four blocks: `BDDScenario`, `BDDStep`, `FinalResult` and `SetupOrTeardownStep`. Confirm their module, inputs and placeholders with `oml inspect` and `oml describe`. The test logic is screen actions that you bind to the step blocks.
oml cannot download Forge components. The user installs them in Service Studio (Forge tab).

## Test module layout

- Put tests in a separate test application, never in the application under test, so they do not reach production. Give both the same version number.
- The test module references only public elements of the module under test (public actions, entities, services, REST) and entities that module also uses.
- Name test applications after the app they test (the source docs use `Cases_Tests` for `Cases`, with module `Cases_UnitTests`). Mirror the action folders as UI flows. One test screen per group of related actions.
- Code shared by test modules goes in one shared test module. Do not copy it.

## Building blocks

- `BDDScenario`: one test case. `BDDStep`: one Given, When or Then step inside a scenario, bound to a screen action. In server-side BDDFramework the steps of a scenario run in order within one request.
- `FinalResult`: place it last on the screen. It shows how many tests passed and failed.
- `SetupOrTeardownStep`: work before or after a scenario, such as removing the data it created.
- A screen can hold many scenarios. Put each scenario in its own block and use the blocks on the screen. This runs faster.

Screen actions are named `<Case>_Given`, `<Case>_When`, `<Case>_Then`, for example `Create_Given`.
- **Given**: check the starting state and store what Then needs (for example a count).
- **When**: set inputs, call ONE action under test, store its results. No checks here.
- **Then**: assert the result, the stored data, and that no input was lost or truncated. Name each assertion for what it expects: positive ("Case was created") when it expects true, negative ("History not empty") when it expects false. The screen shows which assertion failed.
- Test data: a `TestData_Create` action runs in the first scenario's setup and a `TestData_CleanUp` action in the last scenario's teardown. Use the app's own actions, not direct entity writes. Do not wrap data-creating actions in an exception handler: it hides a broken setup. Tests must be repeatable.

## What makes logic testable

- Business logic and validation live in public core actions. Screen actions only call them and give feedback. Logic inside a screen action cannot be tested.
- Wrap each external API in one module with public actions that all callers use. One place can then switch to a mock. A mock switch is a small shared module with an on/off flag, off in production.
- A complex action can take a test-mode input that skips the final commit.

## Test levels

| Level | Tests | Tool |
|---|---|---|
| Unit (component) | One action and its business rules | BDDFramework; Client Side for public client actions |
| Integration (API) | Exposed and consumed APIs: valid and invalid input, mandatory and optional fields, create-read-update-delete chains | BDDFramework or an API test tool |
| End to end | A whole user flow, mostly through the UI | A browser UI test tool, outside oml |

Write many unit tests and few UI tests. Denied path: for each action that checks a role, add a scenario signed in as a user without the role that expects `Not<Role>` (or pass that user's id to `Check<Role>Role` and expect False).

## How tests run

Open the test screen in a browser: it runs and marks each step passed or failed. BDDFramework also has a REST API that runs a screen and returns results, for CI.

## How an oml agent builds a BDD test

`oml test scaffold` writes the spec. It reads the module under test and the framework module, and never changes them.

1. Get the files. oml cannot download Forge components: ask the user to install BDDFramework Client Side, open it in Service Studio and run `oml pull --out BDDFramework.oml`. Do the same for the module under test if you have no file.
2. Get the test module. Best: the user creates a test app in Service Studio from the "BDD Framework Client Side" app type (it brings the references and a `TestFlow`) and you save it with `oml pull --out Orders_Tests.oml`. Fallback: `oml new Orders_Tests.oml --name Orders_Tests --kind CrossDevice`.
3. `oml test scaffold Orders.oml PlaceOrder CancelOrder --bdd BDDFramework.oml` writes `Orders_Tests.bdd-spec.json`. Only public server actions.
4. `oml spec check`, then `oml apply` the spec to the test module. Never apply it to the module under test.
5. Replace every `TODO` (values, conditions, comments) with real test data and expected results; checks without a value start as `False` and fail until you write them. Ask the user before you add a `SetupOrTeardownStep` that creates or removes data. Tell the user to publish and open the screen.

You get one screen `<Action>_Tests` per action in `TestFlow`; merge them into groups as needed. Each has one `BDDScenario` per case, three `BDDStep` blocks bound to `<Case>_Given`, `<Case>_When` and `<Case>_Then`, and `FinalResult`. Cases: `HappyPath`, `Raises<Exception>` for each user exception the action's own flow raises, and `Empty<Input>` for each mandatory Text or identifier input. Then steps call `AssertTrue`.

This is not the documented server-side setup above. The scaffold tests server actions from a Reactive test module through BDDFramework Client Side: a step runs its screen action through the step block's event (only Reactive blocks have events), and each server call is its own request. Server-side BDDFramework (Traditional Web) is not supported yet: a Traditional Web framework module is refused.

Unverified: oml's tests use a stand-in BDDFramework with the documented block names. Its inputs, placeholder, step event and assert action are assumptions. The scaffold reads these names from the `--bdd` file, stops with an error when they do not fit, and lists what it found in `framework`. Sample output: `recipes/bdd-test-screen.jsonc`. Details: commands.md, "Test scaffold".
