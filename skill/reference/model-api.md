# Model API reference for extending oml

This file belongs to the `outsystems-oml` skill. Read it when you change `oml` itself or work directly with the OutSystems Model API.

Use this reference when adding a CLI feature or building a typed host.
Section numbers §1–§18 below refer to this appendix.


### 1. Establish scope and inspect the machine

Resolve these before editing:

- Source OML and staged output absolute paths
- Exact screens, actions, entities, structures, and references already present
- Input/output contracts and representative fixtures
- Installed Service Studio version
- Required producer dependencies such as BinaryData, OutSystems UI, REST, or System

Default installation root:

```text
C:\Program Files\OutSystems\Service Studio 11\Service Studio
```

Read the first token from `Version.txt`.
Verified builds: `11.55.84.65120`, `11.55.85.65133` and `11.55.92.65269`, all model V12.
Report the build with results.
Run a self-test for each unseen build.

Inspect by typed API, not by assuming the Service Studio tree is complete:

```csharp
foreach (var action in module.ServerActions)
    Console.WriteLine($"{action.Name}: {action.Nodes.Count()} nodes");

foreach (var flow in module.MobileFlows)
foreach (var screen in flow.Nodes.OfType<IMobileScreen>())
    Console.WriteLine(((IScreenSignature)screen).Name);

foreach (var entity in module.Entities)
    Console.WriteLine(entity.Name);
```

Find existing objects by stable semantic name.
Generate keys only for new objects with `services.NewKey()`.

Before a large change, count objects by concrete type through `IObjectSignature.Children`.
A census shows whether the work covers 20 objects or 1,000, and which types need handling.
`ParsedExpression` nodes can dominate the count.
Set their text instead of constructing them (§9).

### 2. Choose the execution host

#### Headless host

Use a .NET child process that passes its self-test for offline changes:

```csharp
IModelServices services = PluginProvider.ModelServices;
using IESpace module = services.LoadESpace(
    File.ReadAllBytes(sourceOml), null, null, null);
```

Save through the inherited module contract:

```csharp
((IModule)module).Save(outputOml, addEntryToVersionHistory: false);
```

#### In-process Service Studio plugin

Use an in-process plugin for selection, undo, dirty state, templates, editor refresh, or dialog-driven operations:

```csharp
IESpace module = args.Workspace.OwnerESpace;
IModelServices services = args.Workspace.Context.ModelServices;
```

There is no public `IWorkspace.Save`.
Change `OwnerESpace`; let Service Studio manage commands and saving.

Use the CLI for repeated construction. Use Service Studio for final environment checks and host-only operations.

#### What a headless host cannot do

These operations need the Service Studio application:

- **Dialog-driven producer reference changes** in Manage Dependencies.
Local public producer imports work through generic AddDependency (§13.1).
- Dialogs call `ServiceStudio.Runtime.ShowMessage`, which throws `NotImplementedException` headless.
Report the question through the diagnostic runtime (§4, §13).
- Import/refresh wizards for REST and SOAP.

Report these as manual steps.
Do not bypass their checks.

### 3. Reference the installed assemblies

A separate Windows host can use `net8.0-windows` with `<RollForward>Major</RollForward>` and a newer SDK.
The CLI itself uses net8.0 and LatestMajor.
Reference the installed assemblies:

```text
OutSystems.Model.V1.dll
OutSystems.Model.Plugins.dll
ServiceStudio.PluginAPI.dll
ServiceStudio.Framework.dll
ServiceStudio.Common.dll
OutSystems.ServiceStudio.CrashAPI.dll
```

For Reactive widgets also reference:

```text
Plugins\ServiceStudio\ServiceStudio.Plugin.NRWidgets.dll
```

Service Studio 11.55.92 uses its own .NET 8.0 runtime with some .NET 10 libraries:
`System.Diagnostics.DiagnosticSource`, `System.Text.Json`, and `System.Text.Encodings.Web`.
A host using shared .NET 8 can fail with "FileLoadException ... DiagnosticSource, Version=10.0.0.0".
Do not bundle these libraries.
Their versions can differ by build.

Load model code in a custom `AssemblyLoadContext`.
Prefer Service Studio's copy unless the running runtime has the same or newer version.
Always share `System.Private.CoreLib`.
Keep model types out of the entry point.
Load model code into the context through reflection.
Reference `ServiceStudio.Framework.dll` and `ServiceStudio.Common.dll` at compile time for providers (§4).
See `src/Program.cs`.

For a separate local host, use `<Private>true</Private>` if plugin types cause resolution before `Main`.
This copies assemblies into the local build output only.
Do not distribute them.
The CLI uses its launcher instead.

Use these project settings:

```xml
<PropertyGroup>
  <OutputType>Exe</OutputType>
  <TargetFramework>net8.0-windows</TargetFramework>
  <RollForward>Major</RollForward>
  <ImplicitUsings>enable</ImplicitUsings>
  <Nullable>enable</Nullable>
</PropertyGroup>
```

#### Namespaces you will need

The V1 interfaces are spread across namespaces and several names are duplicated.
Resolve by full name when in doubt:

```text
OutSystems.Model                     IESpace, IKey, IExpression, IArgument, IFolder, IReference
OutSystems.Model.Types               ITypeSignature, IListType, IBasicType, IRecordType
OutSystems.Model.Data                IEntity, IStructure, IEntityAttribute, IStructureAttribute,
                                     IResource, ISiteProperty
OutSystems.Model.Logic               IServerAction, IAction, IUserException, IAssignment
OutSystems.Model.Logic.Nodes         IActionNode, IStartNode, IEndNode, IAssignNode, IIfNode,
                                     IForEachNode, IAggregateNode, IExecuteServerActionNode,
                                     IJSONDeserializeNode, IRaiseExceptionNode
OutSystems.Model.UI                  IWidget, IScreen, IBlock
OutSystems.Model.UI.Mobile           IMobileFlow, IDataAction
OutSystems.Model.Applications        IModule
ServiceStudio.PluginAPI              PluginProvider
ServiceStudio.PluginAPI.Descriptors  CustomObjectDescriptor
ServiceStudio.Plugin.NRWidgets       IContainer, IExpression, ITableRecords, IRowCell, IHeaderCell
```

`OutSystems.Model.Extensions` also declares `IEntity`, `IStructure` and friends for extension modules.
Importing both namespaces makes those names ambiguous : prefer `OutSystems.Model.Data`.

### 4. Bootstrap the model runtime

Install an assembly resolver for both the Service Studio root and `Plugins\ServiceStudio`.
Then preload:

```text
ServiceStudio.BuiltInBehaviors
ServiceStudio.Presenter
OutSystems.Model.Implementation.Applications
OutSystems.Model.Implementation.Keys
OutSystems.Model.Implementation.Extensions
```

Register no-op `ILoggersProvider` and `ITracerProvider` before runtime initialization.
`Init` logs immediately.
Without a logger, it fails with "The default provider for 'ILoggersProvider' is not registered!".
Both providers live in `OutSystems.Model.DependenciesProviders` in `ServiceStudio.Framework.dll`.
Use `Providers.Instance.Register<T>(provider, id)`.
Use the default value of the `Providers.Get` parameter as the default id.
A `DispatchProxy` can return no-op interface results and run `ITracer.DoWithContext` callbacks.
See `src/Core/NoOp.cs` and `Model.Services`.

Initialize the V1 runtime:

```csharp
var framework = AssemblyLoadContext.Default.LoadFromAssemblyPath(
    Path.Combine(serviceStudioDirectory, "ServiceStudio.Framework.dll"));
var runtimeType = framework.GetType(
    "ServiceStudio.Model.API.V1.ModelAPIRuntimeImplementation",
    throwOnError: true)!;
runtimeType.GetMethod("Init", BindingFlags.Public | BindingFlags.Static)!
    .Invoke(null, new object[]
    {
        new Configuration { DeveloperId = "outsystems-typed-host" }
    });

var services = PluginProvider.ModelServices
    ?? throw new InvalidOperationException("ModelServices was not initialized.");
```

Use this resolver pattern:

```csharp
AssemblyLoadContext.Default.Resolving += (_, name) => Resolve(name);
AppDomain.CurrentDomain.AssemblyResolve += (_, args) =>
    Resolve(new AssemblyName(args.Name));

Assembly? Resolve(AssemblyName name)
{
    var loaded = AppDomain.CurrentDomain.GetAssemblies()
        .FirstOrDefault(a => string.Equals(
            a.GetName().Name, name.Name, StringComparison.OrdinalIgnoreCase));
    if (loaded is not null) return loaded;

    foreach (var directory in new[]
    {
        serviceStudioDirectory,
        Path.Combine(serviceStudioDirectory, "Plugins", "ServiceStudio")
    })
    {
        var candidate = Path.Combine(directory, $"{name.Name}.dll");
        if (File.Exists(candidate))
            return AssemblyLoadContext.Default.LoadFromAssemblyPath(candidate);
    }
    return null;
}
```

Load the widget and integration plugins explicitly before touching Reactive widgets or `CreateIntegration<T>`:

```text
Plugins\ServiceStudio\ServiceStudio.Plugin.Widgets.dll
Plugins\ServiceStudio\ServiceStudio.Plugin.NRWidgets.dll
Plugins\ServiceStudio\ServiceStudio.Plugin.REST.dll
Plugins\ServiceStudio\ServiceStudio.Plugin.RESTService.dll
Plugins\ServiceStudio\ServiceStudio.Plugin.SOAP.dll
```

If `LoadESpace` reports that a provider is not initialized, the runtime bootstrap is incomplete.
Do not bypass it with XML editing.

#### Surfacing headless dialogs

`ServiceStudio.Runtime` is a public abstract singleton in a private static `instance` field.
Every headless `ShowMessage` overload throws `NotImplementedException`.
The exception can hide the dialog text.

For diagnostics, subclass `ServiceStudio.Runtime` and forward members to the installed instance.
Override all four `ShowMessage` overloads to print the message and return a safe default.
Clear the field before replacing it.
The base constructor refuses an existing instance and registers the new instance itself.

Return `Cancel`, print the question, and report it to the user.
Never use the diagnostic runtime to bypass licensing or IPP (§13).

### 5. Load safely and establish a baseline

Reject missing files, identical source/output paths, an existing staged output, and Service Studio builds that fail the capability self-test before loading.

```csharp
sourcePath = Path.GetFullPath(sourcePath);
outputPath = Path.GetFullPath(outputPath);

if (!File.Exists(sourcePath)) throw new FileNotFoundException(null, sourcePath);
if (string.Equals(sourcePath, outputPath, StringComparison.OrdinalIgnoreCase))
    throw new ArgumentException("Output must differ from source.");
if (File.Exists(outputPath))
    throw new IOException($"Staged output already exists: {outputPath}");

using var module = services.LoadESpace(
    File.ReadAllBytes(sourcePath), null, null, null);
```

Run validation before mutation.
Record existing errors separately; do not claim a new change caused a pre-existing error.

```csharp
var baselineErrors = ((IObjectSignature)module)
    .GetValidationMessages(true)
    .Where(x => x.Type == ValidationMessageType.Error)
    .ToArray();
```

Group validation output by `MessageID`.
Several messages can share one cause.

### 6. The reflection contract

Generic model code must follow these rules.
A skipped or ignored write can cause unrelated validation errors.

#### 6.1 Read and write through the declaring interface

Concrete Service Studio classes implement most members explicitly.
A concrete property may have no writable setter:

```csharp
// Wrong: Target is not writable on the concrete node class.
node.GetType().GetProperty("Target")!.SetValue(node, next);

// Right: take the setter from the interface that declares it.
var property = node.GetType().GetInterfaces()
    .Where(i => i.Namespace?.StartsWith("OutSystems.Model") == true)
    .Select(i => i.GetProperty("Target"))
    .First(p => p is { CanWrite: true });
property.SetValue(node, next);
```

#### 6.2 Some members are declared on more than one interface

`DataType` belongs to read-only `IParameterSignature` and writable `IParameter`.
`GetType().GetProperty("DataType")` throws `AmbiguousMatchException`.
Do not catch and continue.
Variables can keep default types and cause `ExpressionUnknownObject` errors.

Enumerate interface properties and read through the `PropertyInfo` you already hold:

```csharp
foreach (var iface in ModelInterfaces(source))
foreach (var declared in iface.GetProperties())
{
    var value = declared.GetValue(source);   // interface dispatch: never ambiguous
    ...
}
```

#### 6.3 Some members exist only on the concrete class

These are **not** on any interface, so an interface-only walker misses them entirely:

| Member | Owner | Why it matters |
|---|---|---|
| `CustomProperties` | Reactive widgets | every widget property lives here |
| `ValueExpression` | `CustomProperty` | the property's actual value |
| `CustomObjectImplKey` | Reactive widgets | the descriptor GUID used to recreate it |
| `EntityActions` | `IEntity` | the generated CRUD actions |
| `SourceNode` / `TargetNode` | connectors | flow topology |

Always fall back to `GetType().GetProperty(name)` when no interface declares the member.

#### 6.4 Some properties are write-only in practice

`Target`, `TrueTarget`, `FalseTarget` and `CycleTarget` **read back `null`** even on a fully connected flow.
You cannot round-trip topology through them.
Read it from the connector children instead (§9.3).

Compare source and result after reopening.
Assert that every required value persists.

#### 6.5 `GetInterface()` is the key to generic construction

Every model object exposes `Type GetInterface()`, which returns its V1 interface.
Use it for generic factories instead of switching across 56 concrete classes:

```csharp
var contract = node.GetInterface();            // for example IAssignNode
var factory  = typeof(IAction).GetMethods()
    .First(m => m.Name == "CreateNode" && m.IsGenericMethodDefinition)
    .MakeGenericMethod(contract);
var created  = factory.Invoke(targetAction, [name, services.NewKey()]);
```

For Reactive widgets `GetInterface()` returns only `ICustomWidget`; use the descriptor instead (§11.2).

#### 6.6 Setter names do not mirror property names

Expression-valued properties are written with a `Set*(string)` method whose name often differs:

| Property | Setter |
|---|---|
| `Condition` | `SetCondition` |
| `JSONString` | `SetJSONString` on Service Studio 11.55.92. Reflect `SetJSON` only when the installed API exposes it. |
| `ExceptionMessage` | `SetExceptionMessage` on Service Studio 11.55.92. Reflect `SetMessage` only when the installed API exposes it. |
| `ValueExpression` | `SetValueExpression` |

Prefer an exact setter, then an exact writable property, then a unique fuzzy setter.
Fuzzy matching accepts a property name at the start or end of the setter suffix.
This order keeps exception references out of SetExceptionMessage.
See `Reflect.Plan`.

### 7. Reuse and modify existing model elements

Use exact-name lookups and require uniqueness:

```csharp
var action = module.ServerActions.Single(x => x.Name == "SaveData");
var entity = module.Entities.Single(x => x.Name == "Report");
var structure = module.Structures.Single(x => x.Name == "ReportJson");
var flow = module.MobileFlows.Single(x => ((IFlowSignature)x).Name == "MainFlow");
var screen = flow.Nodes.OfType<IMobileScreen>()
    .Single(x => ((IScreenSignature)x).Name == "Dashboard");
```

Modify writable typed properties directly:

```csharp
entity.Attributes.Single(x => x.Name == "Position").DataType = module.IntegerType;
structure.Attributes.Single(x => x.Name == "SourceRow").DataType = module.LongIntegerType;
```

Preserve unrelated objects and user changes.
If the expected starter state differs, stop and inspect instead of force-applying a stale recipe.

### 8. Create top-level model elements

Use the typed factories:

```csharp
var server = module.CreateServerAction("GetOrders", services.NewKey());
var client = module.CreateClientAction("RefreshOrders", services.NewKey());
var service = module.CreateServiceAction("PublicAction", services.NewKey());
var entity = module.CreateServerEntity("Order", services.NewKey());
var structure = module.CreateStructure("OrderDto", services.NewKey());
var process = module.CreateProcess("ProcessOrder", services.NewKey());
var timer = module.CreateTimer("RetryOrders", services.NewKey());
var role = module.CreateRole("OrderManager", services.NewKey());
var resource = module.CreateResource(bytes, "SampleJson", services.NewKey());
var exception = module.CreateUserException("InvalidData", services.NewKey());
var screen = flow.CreateScreen("Dashboard", services.NewKey());
var block = flow.CreateBlock("ReportCard", services.NewKey());
```

Bare action and process factories do not guarantee standard Start/End nodes.
Create and connect them explicitly.
Bare screen creation does not guarantee the normal layout template.
In an in-process plugin, prefer host helpers such as `ICreateScreenService` when standard generated templates are required.

#### 8.1 Factories seed defaults you must reconcile

Factories can create default children.
Match or remove them before adding replacements:

- `CreateServerEntity` seeds `Id` on some builds, but not on Service Studio 11.55.92.
  Check `IdentifierAttribute`. Configure the existing Id, or create one when absent.
  Use `DataType = module.LongIntegerType` for an entity and `module.IntegerType` for a static entity.
  For entities, set `IsAutoNumber = AutoNumber.Yes` and `IsMandatory = true`.
  Assign Id to IdentifierAttribute so `<Entity> Identifier` can resolve through its `IdentifierType`.
  Add at least one other attribute.
- Never type Id with generic `LongIntegerIdentifierType` or `IntegerIdentifierType`.
  On Service Studio 11.55.92, validation passes but publishing fails with:
  "Invalid compiler output. Object reference not set to an instance of an object".
- Factories **rename silently on a name clash**: `CreateServerAction("GetOrder")` returns `GetOrder2` when entity `Order` exists, because its generated CRUD action already owns the name.
Compare the returned `Name` with the requested one and fail instead of continuing.
- `CreateDataAction` creates an output parameter named `Out1` : delete it if the source has no such output.
- Some action factories pre-create Start/End nodes : reuse them by interface instead of creating duplicates.

For a copier, record target children before construction. Delete only unmatched factory defaults, preserving unrelated existing content.

#### 8.2 Build entity CRUD calls in two passes

`CreateServerEntity` does **not** immediately produce `CreateEntity`, `GetEntity`, `UpdateEntity`, `DeleteEntity`, `GetEntityForUpdate`, `CreateOrUpdateEntity`.
Those are generated when the module is saved and loaded again.
The public V1 contract does not expose the internal `RefreshEntityActions()`.

Build entity data first, then reopen before creating CRUD calls:

```text
pass 1  create folders, exceptions, structures, entities, resources, site properties
        validate -> save to an intermediate file
pass 2  reopen the intermediate file, pair the data model up by name,
        then build actions, screens and blocks against the generated CRUD actions
        validate -> save the staged output
```

After reopening, read the generated actions from the concrete `EntityActions` collection and pair them to the source by their concrete type name (`CreateEntity`, `GetEntity`, …) rather than by display name.

An entity's record type : `"<Entity> Record"`, an anonymous structure : is reachable as the generated Create action's single input parameter `DataType`.
That is the type to use for a local variable that holds a row.

### 9. Build action flows

Create parameters and locals with typed data signatures:

```csharp
var input = action.CreateInputParameter("JsonText", services.NewKey());
input.DataType = module.TextType;
input.IsMandatory = true;

var output = action.CreateOutputParameter("ReportId", services.NewKey());
output.DataType = report.IdentifierAttribute.DataType;

var local = action.CreateLocalVariable("ReportRecord", services.NewKey());
local.DataType = report.CreateAction.InputParameters.Single().DataType;
```

Create and connect nodes:

```csharp
var start = action.CreateNode<IStartNode>("Start", services.NewKey());
var condition = action.CreateNode<IIfNode>("Has input", services.NewKey());
condition.SetCondition("Trim(JsonText) <> \"\"");
var assign = action.CreateNode<IAssignNode>("Prepare record", services.NewKey());
var end = action.CreateNode<IEndNode>("End", services.NewKey());

start.Target = condition;
condition.TrueTarget = assign;
assign.Target = end;
```

Add assignments by expression text:

```csharp
var assignment = assign.CreateAssignment();
assignment.SetVariable("ReportRecord.Report.Name");
assignment.SetValue("ParsedJson.Name");
```

Deserialize JSON with a real structure type:

```csharp
var deserialize = action.CreateNode<IJSONDeserializeNode>(
    "DeserializeReport", services.NewKey());
deserialize.DataType = module.Structures.Single(x => x.Name == "ReportJson");
deserialize.SetJSONString("JsonText");           // JSONString setter on Service Studio 11.55.92
deserialize.Target = nextNode;
```

Execute an action and bind arguments by parameter name, never collection index:

```csharp
var execute = action.CreateNode<IExecuteServerActionNode>(
    "CreateReport", services.NewKey());
execute.Action = report.CreateAction;
execute.Name = "CreateReport";
execute.Arguments.Single(x => x.Parameter.Name == "Source")
    .SetValue("ReportRecord");
execute.Target = nextNode;
```

Setting `Action` creates `Arguments`.
Bind values by `Parameter.Name`; collection order is not stable.
In generic code, bind Action first and argument values in a later pass.

#### 9.1 Expressions move as text

Read an expression property's `DisplayName` to get its source text.
Do not use `ToString()` : it appends the property descriptor:

```text
DisplayName  ->  GetLatestPayload.List.Empty
ToString()   ->  GetLatestPayload.List.Empty [Condition[ServiceStudio.Expressions.AbstractExpression]]
```

Write it back with the matching `Set*(string)` method (§6.6).

#### 9.2 Apply expressions last

Expressions name nodes, variables, and parameters.
Create all referents before setting any expression.
Otherwise, expression binding can fail or retain unresolved references.

Use this build order:

```text
create everything  ->  wire object references and data types
                   ->  wire flow connectors
                   ->  set expressions, arguments and custom property values
```

#### 9.3 Flow topology comes from connector children

`Target` and friends read back `null` (§6.4).
The real topology is in each node's children:

| Child type | Property to set on the rebuilt node |
|---|---|
| `Sequence` | `Target` |
| `True` | `TrueTarget` |
| `False` | `FalseTarget` |
| `Cycle` | `CycleTarget` |

Each connector exposes `SourceNode` and `TargetNode`.
Read those, map both ends to the rebuilt nodes, then write through the interface property.

#### 9.4 For Each expressions

Bind the loop to the list expression:

```csharp
const string records = "DeserializeReport.Data.Records";
var loop = action.CreateNode<IForEachNode>("ForEachRecord", services.NewKey());
loop.SetRecordList(records);
```

Inside the loop, reference the source list, not the node label:

```text
DeserializeReport.Data.Records.Current
DeserializeReport.Data.Records.CurrentRowNumber
DeserializeReport.Data.Records.Current.Children.Current
```

`ForEachRecord.Current` is invalid and produces `ExpressionUnknownObject`.
Connect `CycleTarget` to the body and `Target` to the node after the loop.

#### 9.5 Exceptions

Create or reuse a typed exception and terminate invalid branches explicitly:

```csharp
var invalid = module.UserExceptions.SingleOrDefault(x => x.Name == "InvalidData")
    ?? module.CreateUserException("InvalidData", services.NewKey());
var raise = action.CreateNode<IRaiseExceptionNode>("Invalid data", services.NewKey());
raise.Exception = invalid;
raise.SetExceptionMessage("\"The supplied data is invalid.\""); // Service Studio 11.55.92
```

Validate trust-boundary input before database writes.

### 10. Build aggregates and resources

Create a latest-record aggregate:

```csharp
var aggregate = action.CreateNode<IAggregateNode>("GetLatest", services.NewKey());
aggregate.AsDatabaseAggregate.CreateSource(report, "Report");
aggregate.SetMaxRecords("1");
var sort = aggregate.AsDatabaseAggregate.CreateSort();
sort.SetAttribute("Report.Id");
sort.SortDirection = OutSystems.Model.Enumerations.Sort.Descending;
```

A sort's attribute reads back from `OriginalAttribute`, not `Attribute`.

Read the result with normal OutSystems expressions:

```text
GetLatest.List.Empty
GetLatest.List.Current.Report.Id
```

Store fixed test fixtures as private resources to avoid large expression literals:

```csharp
var resource = module.CreateResource(
    File.ReadAllBytes(fixturePath), "DummyJson", services.NewKey());
resource.Public = false;
```

Use BinaryData producer actions when converting resource or database payloads:

```text
Resources.DummyJson.Content
BinaryDataToText(BinaryData, "utf-8")
TextToBinaryData(Text, "utf-8")
```

Find referenced actions by semantic name:

```csharp
var binaryToText = module.References
    .SelectMany(x => x.ServerActions)
    .Single(x => x.Name == "BinaryDataToText");
```

### 11. Build Reactive Web screens and blocks

Create screen locals and an initialization action:

```csharp
var data = screen.CreateLocalVariable("Data", services.NewKey());
data.DataType = dtoStructure;
var hasData = screen.CreateLocalVariable("HasData", services.NewKey());
hasData.DataType = module.BooleanType;
hasData.SetDefaultValue("False");

var load = screen.CreateScreenAction("LoadData", services.NewKey());
screen.OnInitialize.Destination = load;
```

Assign screen CSS through the typed screen contract:

```csharp
((OutSystems.Model.UI.IScreen)screen).StyleSheet = css;
```

`StyleSheet` takes a string.
Concrete module and block setters can expect `ServiceStudio.Model.JavaScript`.
The V1 `IESpace.JavaScript` interface accepts a string on 11.55.92. Use the declaring interface through `Reflect.Plan`; module settings verify it after reopening.

#### 11.1 Containment

A screen or block owns a widget tree.
The containment rules are not uniform:

| Parent | Where its children live |
|---|---|
| screen / block | `Widgets`, or `Children` filtered to widgets |
| `If` widget | `TrueBranch` / `FalseBranch`, each an auto-created `IfBranch` |
| custom widget (Container, Table, …) | its **`Placeholders`** collection, *not* `Children` |
| `TableRecords` | `Row`, `Header` |

Put custom widget content in a placeholder.
Direct attachment can fail with "cannot host widget".
`Children` can repeat placeholder contents.
Descend through `Placeholders` and skip those duplicates.

`If` branches and placeholders already exist on a new parent.
Match them by concrete type and name.
Track matched target children to avoid duplicates.

#### 11.2 Creating widgets

Built-in widgets take the typed factory:

```csharp
var state = screen.CreateWidget<IIfWidget>("State", services.NewKey());
state.SetCondition("HasData");
```

Reactive plugin widgets need a descriptor.
Read `CustomObjectImplKey` and look up its descriptor at runtime.
This avoids assuming a stable GUID across builds:

```csharp
var guid = (Guid)sourceWidget.GetType()
    .GetProperty("CustomObjectImplKey")!.GetValue(sourceWidget)!;
var descriptor = PluginProvider.GetImplementation(guid);
var created = parent.CreateWidget(descriptor);      // ICustomWidget CreateWidget(CustomObjectDescriptor)
created.Name = "Rows";                              // the descriptor overload takes no name or key
```

A bare headless host does not register Reactive widgets. `PluginProvider.Implementations` is empty.
Creation fails with "Couldn't find any concrete class implementing ServiceStudio.Plugin.NRWidgets.IContainer".
Service Studio's `Runtime.LoadAssemblies` returns nothing headless.

1. Load `ServiceStudio.Plugin.Widgets`, `NRWidgets`, `REST`, `RESTService`, and `SOAP` into the host context.
2. Read static `Instance` from each nested `Kind` class to register concrete classes.
3. Construct each non-abstract `CustomObjectDescriptor`. Its constructor registers it; plugins may repeat a key.
4. Use descriptors for plugin widgets. Even after registration, `CreateWidget<NRWidgets.IContainer>` fails.
5. Use typed factories for built-ins: `IIfWidget`, `ITextWidget`, and `IMobileBlockInstanceWidget`.

See `src/Core/Model.cs`.

`PluginProvider.Implementations` enumerates every registered descriptor.
Known GUIDs for this generation, useful for authoring from scratch:

```text
Container     86c6c8e9-006e-f523-6d5a-c1e803877203
Expression    1d5edc0a-ee7b-31cc-6845-364937d7ffff
HeaderCell    ae551904-b3f0-2a89-ecd0-142aa261609f
RowCell       e907942d-ff32-f7ab-d641-997f3c1c655e
TableRecords  98e54ba3-0cd3-01d3-896f-ef4b33c324be
```

#### 11.3 Widget properties live in `CustomProperties`

A Reactive widget's `Style`, `Visible`, `Align`, `Width`, `Margin*` and its main value are all `CustomProperty` objects on the concrete class.
Each holds a `ValueExpression`; write it with `SetValueExpression(string)`.

Set `DefaultCustomProperty` values on the widget itself, not through `CustomProperty`.
Examples include Expression `Value` and Table `Source`.
The CustomProperty call succeeds but changes nothing:

```csharp
expression.SetValue("Data.Rows.Current.Name");      // works
valueCustomProperty.SetValueExpression("...");      // accepted, silently ignored
```

Direct plugin properties, such as `IExpression.Style` / `SetStyle`, persist normally.
Check default values separately; a correct style does not prove correct data binding.

### 12. Data actions, screen actions and events

A screen or block exposes several flow-shaped collections:

```text
ScreenActions     screen/block actions        CreateScreenAction(name, key)
DataActions       data actions                CreateDataAction(name, key)
Events            block events                CreateEvent(name, key)
OnInitialize, OnReady, OnRender, OnDestroy, OnParametersChanged
                  lifecycle points : already exist; only set their Destination
```

A data action uses `OutSystems.Model.UI.Mobile.IDataAction`, not `IAction`.
It exposes `Nodes`, `OutputParameters`, `LocalVariables`, `CreateNode<T>`, and `CreateOutputParameter`.
Build flows through declaring interfaces.
Code restricted to IAction skips data actions and leaves them without Start.
That also invalidates expressions reading their outputs.

Remember `CreateDataAction` seeds an `Out1` output (§8.1).

### 13. Copy between modules, and IPP

`IESpace` exposes Service Studio's own clipboard:

```csharp
byte[] payload = source.Serialize(objects);                    // objects: IEnumerable<IObjectSignature>
IEnumerable<IObjectSignature> pasted = target.DeserializeInto(payload, destination);
```

Also available: `Merge(objects, options, pairings)`, `Duplicate(objects)`, `Clone`, and `CloneESpace`.

Clipboard operations preserve model content.
Target the espace for top-level objects and the matching flow for screens or blocks.

Clipboard operations check IPP.
Different activation codes make `ClipboardManager.IPPCanPaste` call `ServerLicensingInfo.CheckLocalForeignCodes`.
Its dialog throws `NotImplementedException` from headless `Runtime.ShowMessage`.
The hidden message is:

```text
IPPErrorNotConnected — Error — Intellectual Property Error
You are trying to paste elements from an eSpace whose Intellectual Property is Protected.
You can only paste elements from eSpaces with Protected Intellectual Property if connected
to a server belonging to the OutSystems Infrastructure where the module was created.
Buttons: Connect | Cancel
```

The only affirmative answer is *authenticate to the source infrastructure*. `CloneESpace` refuses to re-home a module: "It is not possible to change the Activation Code of an eSpace. You must use IPP cleaner for that."

**Do not defeat this check.** Surface the message (§4) and report it.
Legitimate routes: open the source in Service Studio while signed in to the infrastructure that created it and answer **Connect**; disable IPP at the source infrastructure and re-export; or use OutSystems' IPP cleaner.

When the content is the developer's own and the clipboard is unavailable, **rebuilding** through the public creation API is a clean alternative : it constructs elements rather than transferring them, so no IPP question arises.
That is ordinary authoring, and it is what §6–§12 describe.

#### 13.1 Producer references from local modules

A loaded producer's actual public elements implement `IShareable<ConcreteT, SignatureT>`; invoke `IESpace.AddDependency<ConcreteT, SignatureT>` with those generic arguments.
The model creates or extends the producer reference and brings along signature dependencies.
Compare global keys to recognize existing imports.
A reference element (`ServiceStudio.Model.ReferenceAction`) is a signature proxy, not an `IShareable`:

- `AddDependency(IAction)` / `AddDependency(IShareable<,>)` cannot import signature proxies; use actual producer elements.
- `RefreshDependencies(IEnumerable<IShareable>)` rejects the cast.
- On Service Studio 11.55.92: `RefreshDependencies(IESpace[])` adds nothing when passed a module containing signature proxies, or a concrete producer that is not yet referenced.
With an existing reference and a concrete producer, it **does refresh signatures**: a new DTO attribute appears in the consumer signature.
Self-test checks all three cases.
The `references` spec imports missing elements and leaves existing signatures unchanged.

V1 on Service Studio 11.55.92 has no `ResolveModuleSignature` event.
Inspect its members and events:

```powershell
oml api "OutSystems.Model.IModelServices"
```

V1 also has no global-key `AddDependency` overload.
Other in-process API contracts can differ.
A resolver is unnecessary for importing actual loaded elements; no global resolver is installed or changed.

For missing producer exports, report the gap and obtain the deployed producer's `.oml` or use Service Studio's **Manage Dependencies**:

```csharp
var missing = source.References
    .Where(r => !target.References.Any(x => x.Name == r.Name))
    .Select(r => r.Name)
    .ToArray();
```

The producer `.oml` must match the version deployed in the target environment.
A mismatched export can cause publish failures or stale dependencies.
After publishing the producer, Service Studio's `oml references` command can Refresh All against the target environment.
Use authorized exports; report load/import/IPP refusals without trying to bypass them (§13).
User exceptions on 11.55.92 expose neither `Public` nor `IShareable<,>` and cannot be imported.

### 14. Other typed creation surfaces

Use the same pattern for:

- Blocks and emails through their flow factories
- Client and service actions through `CreateClientAction` and `CreateServiceAction`
- Processes through `CreateProcess` and typed process nodes
- Timers through `CreateTimer`, then assign action, schedule, timeout, and priority
- Roles through `CreateRole`
- Scripts, images, locales, site/session/client variables, and folders through `IESpace`
- Consumed REST through `CreateIntegration<IRestClient>`
- Exposed REST through `CreateIntegration<IRestService>`
- SOAP through `CreateIntegration<ISOAPClient>`

Load and register the appropriate integration plugin before calling `CreateIntegration<T>`.
Manual typed REST/SOAP construction is public; Service Studio import/refresh wizards use more host-owned internals, so prefer an in-process helper for full wizard fidelity.

Do not use the ODC V13 model loader for an O11 V12 module.

### 15. Validate, save, reopen, and verify behavior

Validation before save:

```csharp
var errors = ((IObjectSignature)module)
    .GetValidationMessages(true)
    .Where(x => x.Type == ValidationMessageType.Error)
    .ToArray();
if (errors.Length != 0)
    throw new InvalidOperationException(string.Join(" | ", errors.Select(x => x.ToString())));
```

Save only the staged file:

```csharp
((IModule)module).Save(stagedPath, addEntryToVersionHistory: false);
```

Reopen in a fresh model instance and repeat full validation:

```csharp
using var reopened = services.LoadESpace(
    File.ReadAllBytes(stagedPath), null, null, null);
var reopenErrors = ((IObjectSignature)reopened)
    .GetValidationMessages(true)
    .Where(x => x.Type == ValidationMessageType.Error)
    .ToArray();
```

Leave one runnable typed verifier.
Assert the requested semantics, not only zero errors:

- Expected action and screen names exist exactly once : check for duplicates as well as absences
- Start/End and every branch/loop target are connected
- Execute nodes call the expected actions
- Arguments are bound to the expected parameter names
- Entity and structure attribute name sets match
- Action node counts and parameter name sets match
- Screen lifecycle points to the expected action
- Widget names, sources, expressions, and CSS markers exist
- Resource bytes exactly match the fixture
- Optional minimum and maximum list sizes are represented safely

Test the smallest and largest supported fixture shapes.
For a 1-to-5-column screen, verify both one and five columns.

#### 15.1 Isolate environmental errors from real defects

A staged file can carry target-environment errors.
Check these causes:

- `InvalidGridUnitsOutsideGrid` : grid-unit margins in a module with no theme.
Appears when building into a bare `CreateESpace` shell; absent from a real app.
- Unresolved calls into a producer the target does not reference (§13.1).

Compare controlled targets to identify each cause.
One may have a theme but lack a reference; another may have the reverse.
Disjoint error sets alone do not prove correctness.
Attribute each error with evidence; do not ignore new errors.
Apply still uses its baseline comparison and final validation gates.

### 16. Install without risking the source

After staged validation succeeds:

1.
Resolve source, staged, and backup absolute paths.
2.
Record SHA-256 hashes.
3.
Create a recovery copy without overwriting an existing backup.
4.
Copy staged output over the source.
5.
Reopen and run the typed verifier against the installed path.
6.
Confirm installed hash equals staged hash and backup hash equals the pre-install source hash.
7.
Restore the backup automatically if installation verification fails.

Then open the installed OML in the matching Service Studio version, publish to a non-production environment, exercise the real screen/action path, and inspect runtime errors and written database rows.
Typed validation proves model integrity; it does not replace environment compilation, permissions, database, or browser validation.

### 17. Failure playbook for model API extensions

Use section 6 for CLI failures. These rows cover implementation details for typed hosts.
Unless stated otherwise, build-specific behavior below applies to Service Studio 11.55.92.

| Symptom | Cause | Fix |
|---|---|---|
| `CreateESpaceFromTemplate(IESpace, ...)` throws in `OmlHeader.IsAllowed` | The loaded object has no OML permission header in this headless host. | Use the public byte-array overload with the source activation code. It performs the normal permission checks. Never patch the header or licensing checks. |
| Package input exports have the same module key | Two exports can represent different revisions of one module. | Pack one revision. Duplicate names or keys are refused. |
| A census JSON comparison differs after package reload | Equal-count types can appear in a different order. | Compare the byType mapping by key, not JSON property order. |
| Extension metadata changes null to empty on reload | Save normalizes empty descriptions. | Normalize descriptions when comparing signatures. |
| Module key inspection returns `/` | IObjectSignature.Key is an object path. | Read IModule.Key through its declaring interface. |
| Handler path crosses main path or has ambiguous End | Exception paths cannot share the main End. | Create one End per handler path. Never connect Start to a handler. |
| Handler expression cannot identify a main-path node | The handler has a separate entry path. | Keep recovery data in action inputs or locals. |
| OnException CreateNode fails for Start | IOnExceptionAction has only exception entry points. | Create at least one exceptionHandler and its continuation; no Start. |
| AllExceptions or DatabaseException lookup fails | Built-in exception objects have no matching Name. | Resolve the IESpace property by its API name before normal name lookup. |
| Excel export says at least one attribute must be selected | Setting RecordList does not choose columns. | Call AddAttributeSelection after SetRecordList. |
| Client variable IsSecure is absent | IClientVariable has no such member on 11.55.92. | Report a problem when requested. Do not silently ignore it. |
| Session variable or OnSessionStart cannot be a Module child | Reactive and Service kinds reject these objects. | Use Traditional Web. Report the model refusal. |
| A generic reference walker throws an invalid cast from a logic Start to a process node | Concrete model classes can implement interfaces for other element kinds. Their getters are not all valid. | Walk properties on `GetInterface()` and its base interfaces. Include plugin interfaces for custom widgets. Walk expression `Root` bindings to find expression dependencies. |
| Clipboard leaves an unknown action instead of importing a producer signature | Missing producer references are not automatically resolved by `DeserializeInto`. | Check typed properties, expression bindings, identifier types, and script requirements on the source before paste. List missing producer elements as problems. |
| Clipboard pastes a screen but drops an action in a mixed selection | The flow paste target only accepts UI elements. | Paste module elements into the espace first, then screens and blocks into a flow. Check every returned name. |
| Copying an element again silently replaces it | Clipboard can match the existing global key. | Reject existing target names before paste. Report new names after paste as well. |
| `SaveTextResources` creates empty files or throws `AbortCommandException` | On 11.55.92, `ExtendedResXResourceWriter.Generate` throws `NotImplementedException`. Excel export also refuses headlessly. | Report the refusal. Stage exports in a scratch folder; do not deliver empty files. Use direct module translations or Service Studio export. |
| Translation export treats a filename as a directory | `SaveTextResources` takes an output folder for both formats. | Export to a scratch directory, then move the returned Excel file or ResX directory. |
| ResX import reports `Couldn't determine resources format from file name: Object not set` | The 11.55.92 headless host cannot detect the ResX handler. | Report it as a problem. Do not claim a file round trip. Direct `CreateOrUpdateTranslation` persists values. |
| A server entity passes an IStaticEntity cast | Concrete entities can implement several storage contracts. | Check EntityKind == Static; a cast alone does not prove static kind. |
| Static record attribute roles are absent | Bare factories need Identifier, Label, Order, and IsActive role assignments. | Set those roles. Records seed value slots; use SetValue(string) instead of adding duplicate slots. |
| Type becomes Type2 or its foreign key targets a library entity | OutSystemsMaps.Type occupies the name. | Alias the reference explicitly before creation. Never accept a wrong Identifier type. |
| REST CreateIntegration<T> cannot find a concrete class despite descriptors | Headless generic plugin discovery misses the implementation. | Find the descriptor by ConcreteInterfaceType. Use its concrete Kind.Instance.CreateInstanceInParent; typed action/parameter factories work afterward. |
| "InvalidCustomObjectRecordDefinition" on a consumed body/response | REST validator forbids module-owned DTOs. | Copy structures recursively into client-owned DTOs and prefix their globally unique names. |
| JSON REST method needs one structure/list body | Scalar Text was configured as JSON. | Infer PlainText for Text, Binary for Binary Data, JSON for structures/lists. Wrap other scalars in a DTO. |
| Unused REST input or optional URL placeholder | Query inputs share UrlPlacement / ReceiveIn.URL with path inputs. | Add query placeholders and make path inputs mandatory. |
| More than one Start in exposed REST | Factory seeds Start and End. | Delete seeded nodes before Builder.Flow. |
| Web screen lacks CreateScreenAggregate or lifecycle | Traditional Web uses server flows. | Use preparation with aggregate nodes and server-side screen actions. |
| TableRecords row or EditRecord lacks CreateWidget | Rows own cells; EditRecord owns rows. | Use CreateCell / CreateRow, table headerRow/row arrays, or form rows/cells. |
| No IFormWidget; Button rejects children | Build exposes IEditRecordWidget and a label expression. | Form aliases EditRecord. Map Text children to Button label. |
| Switch otherwise refuses with "Cannot change the target node to End" | Otherwise duplicates a case target. | Use a separate End or branch. Report refusal without saving. |
| Link descriptor lacks SetOnClickHandler | Link uses SetOnClickHandlerTarget. | Prefer typed OnClick.Destination, then bind OnClick.Arguments. |
| Switch case lacks TargetNode | ISwitchCondition wraps a connector. | Read its concrete Link.TargetNode. Read ordinary connectors directly. |
| Connector target lacks Name | Some nodes use Label or display text. | Fall back to Label / DisplayName. |
| Unused data appears used by SampleRecord or anonymous CRUD records | Generated model artifacts. | Follow anonymous type references to real consumers. Exclude owned sample values; retain real CRUD calls. |
| Data deletion misses same-spec expressions | Expressions were deferred. | Delete data after expression binding, then validate. |
| SQL output does not resolve downstream | Outputs or inputs were bound too late. | Create ordered ISQLNode.CreateOutput(recordType, name) and typed inputs before statements, arguments, and expressions. |
| SELECT {Entity}.* has no attribute referers | Graph records the entity only. | Guard attribute deletion against queries on the owning entity too. |
| "OnlyImplicitCanBeMandatoryProcess" | Process input factories default mandatory. | With Launch On, make explicit inputs optional. Only its generated entity input may be mandatory. |
| Process node lacks a target property | BPT wiring differs from action wiring. | Use Targets.Add; decisions use CreateConnector. Ordinary flow wiring applies inside automatic activities only. |
| Layout content is absent | SourceBlock was not set first. | Set it to create PlaceholdersContent. Match Placeholder.Name, including referenced blocks. Plugin Placeholders is a different mechanism. |
| Clone keeps its name or reports activation-code trouble | CloneESpace(bytes, activationCode, secretKey, productKey, key) has no name argument. | Preserve the source activation code and set IModule.Name afterward. Never pass a name as an activation code. |
| "Couldn't find any concrete class implementing ServiceStudio.Plugin.NRWidgets.I..." | Plugin registration is missing, or a typed factory was used. | Register Kinds and descriptors. Use the descriptor factory (§11.2). |
| Set* method is missing, such as SetJSON or SetMessage | Setter names vary by build. | On 11.55.92 use SetJSONString / SetExceptionMessage. Match by name (§6.6). |
| "Exception must be set" despite a message | Fuzzy matching chose SetExceptionMessage for the exception name. | Prefer exact setter, then exact property, then a unique fuzzy setter. |
| Publish compiler fails for a validating entity Id | Generic LongIntegerIdentifierType / IntegerIdentifierType is not a physical column type. | Use LongIntegerType / IntegerType, as AddServerEntity.cs / AddStaticEntity.cs do. See section 6 for the full error text. |
| Link/Button keeps seeded Text "link" | Explicit children were appended to defaults. | Delete seeded placeholder widgets before adding children. The CLI does this when children is supplied. |
| "The default provider for 'ILoggersProvider' is not registered!" | Init ran before provider registration. | Register no-op logger and tracer first (§4). |
| FileLoadException for "System.Diagnostics.DiagnosticSource, Version=10.0.0.0" | Host loaded incompatible shared libraries. | Load model code in a context that prefers Service Studio copies (§3). |
| New entity has no Id or Identifier type | Factory seeded no identifier. | Create Id and assign IdentifierAttribute (§8.1). |
| "Provider not initialized" | Runtime bootstrap is incomplete. | Register providers and run ModelAPIRuntimeImplementation.Init. Do not call concrete ESpace.Load directly. |
| "FileNotFoundException: OutSystems.Model.V1" before Main | Compile-time plugin types load too early. | For a local host, use Private=true or a module initializer resolver. |
| Runtime.ShowMessage throws NotImplementedException | Operation wants a dialog. | Install a reporting runtime (§4). Never bypass the decision. |
| Reference added but target.References unchanged | RefreshDependencies(IESpace[]) does not import new producers or signature proxies. | Load the concrete producer and call generic AddDependency (§13.1). |
| ResolveModuleSignature absent from V1 | Another API contract was assumed. | Import concrete public elements with generic AddDependency. V1 on 11.55.92 needs no resolver. |
| Public element has several IShareable<,> interfaces | Entities, screens, and themes can expose base contracts. | Prefer the contract matching GetInterface(), then the most specific concrete interface. |
| Imported public screen missing from flow Nodes | Referenced screens can live outside the collection. | Walk reference descendants and select IScreenSignature for inspection and target lookup. |
| CheckSupportRole cannot be found | Generated check names append Role. | SupportRole generates CheckSupportRoleRole. Inspect producer role actions. |
| AmbiguousMatchException or all variables use Text | GetType().GetProperty("DataType") is ambiguous. | Read through the declaring interface (§6.2). Never catch { continue; } around failed reads. |
| Property write does not persist | Explicit interface member or default widget custom property. | Use §6.1 or §11.3. |
| Edit loses node labels after reopening | CreateNode(name, key) does not persist If/Assign/For Each labels on this build. | Set Label explicitly. Use tree paths for nodes without names. |
| Widget index does not move | V1 MoveBeforeSibling did not preserve order on this build. | Use plugin MoveToIndex and check tree.widgetOrder. |
| Deferred write fails with "Condition is true" after deletion | Queued assignment targets a deleted object. | Check IObjectSignature.WasDeleted before binding expressions. |
| JSON flow node rejects a basic type | Serialize requires a record or record list; deserialize also refuses basic types. | Use a structure or record list as the JSON data type. |
| Summary repeats widget content | Some widget collections also expose placeholder descendants. | Track visited model objects across the widget walk. |
| RequiredConnector_In / _Out across a flow | Topology was read from Target, which returns null. | Read connector children (§9.3). |
| ExpressionUnknownObject after rebuild | Referents missing, data action skipped, or types lost. | Check expression order (§9.2), IDataAction handling (§12), and interface properties (§6.2). |
| Execute inputs are swapped | Argument collection order was assumed. | Select argument.Parameter.Name, never index. |
| New action is invalid or blank | Standard flow nodes are absent or disconnected. | Add and connect Start/End and all flow nodes. |
| Canonical CRUD actions are absent | They appear after save/reopen. | Build in two passes (§8.2). |
| "InvalidDataAction_OutputMustBeSet" names Out1 | Factory output remains unmatched. | Delete unmatched generated children (§8.1). |
| "InvalidFlow_NoStart" on a data action | IAction-only code skipped IDataAction. | Use declaring interfaces (§12). |
| Widget parent rejects content | Custom widget content needs Placeholders. | Follow §11.1. |
| "InvalidGridUnitsOutsideGrid" | Target has no theme. | Verify the environmental cause (§15.1); do not ignore new errors. |
| Reactive custom widget cannot be created | NRWidgets or descriptor construction is missing. | Load NRWidgets and use PluginProvider.GetImplementation(CustomObjectImplKey) (§11.2). |
| Descriptor inspection crashes on null interface | Default name or ConcreteInterfaceType is null. | Handle both as nullable. |
| Client screen action inspection throws NotImplementedException | Unsupported output-parameter contract. | Inspect only the needed supported members. |

### Implementation map

| File | Purpose |
|---|---|
| `src/Program.cs` | Runtime checks and load context that prefers Service Studio libraries. |
| `src/Core/Model.cs` | Providers, runtime Init, type resolution, and validation helpers. |
| `src/Core/SelfTest.cs` | Per-build behavior check and cached pass. |
| `src/Core/NoOp.cs` | Generic no-op logger and tracer providers. |
| `src/Core/Apply.cs` | Data phase, scratch save/reopen, logic phase, baseline comparison, staged save/reopen. |
| `src/Core/Builder.cs` | Actions, nodes, connectors, arguments, aggregates, screens, blocks, widgets, and events. |
| `src/Core/Reflect.cs` | Interface-first members, name-matched setters, and Refs lookup. |
| `src/Core/Inspect.cs` | Module summary, census, and tree. |
| `src/Core/Api.cs` | Installed API and widget discovery. |
| `src/Core/Cli.cs` | Commands, staged paths, and installation with backup/restore. |

Extend this host for new spec shapes. Reuse its bootstrap and build checks.

### 18. Definition of done

Do not report completion until all applicable checks pass:

- Exact installed Service Studio build was detected and accepted
- Original OML remained untouched during development (verify by hash)
- Existing elements were reused; no accidental duplicates exist
- All mutations used typed APIs
- In-memory validation reports zero new errors
- Staged save succeeds
- Staged OML reopens with zero new errors
- A runnable semantic verifier passes
- Boundary fixtures pass
- Every remaining error is either fixed or explicitly attributed to the environment with evidence (§15.1)
- Manual steps that a headless host cannot perform are listed explicitly
- Original backup exists and hashes match
- Installed OML reopens and verifies
- Service Studio publish/runtime test is either passed or explicitly reported as still required
