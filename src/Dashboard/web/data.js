// Command cookbook: every oml command with a realistic example. Keep it in step with the usage text in src/Core/Cli.cs.
window.omlCookbook = [
  { group: 'Start and check', items: [
    ['oml version', 'Show the oml version, the Service Studio build and whether the self-test passed.'],
    ['oml doctor --text', 'Check your setup and get a fix for each problem.'],
    ['oml doctor --selftest', 'Check your setup and run the self-test on this Service Studio build now.'],
    ['oml dashboard', 'Open this dashboard in your browser.'],
    ['oml dashboard --port 5050 --no-open', 'Start the dashboard on port 5050 without opening the browser.'],
    ['oml dashboard --module C:\\Work\\Orders.oml', 'Open the dashboard with this module in the Module explorer.'],
    ['oml setup', 'Add oml entries to the Explorer right-click menu (fonts, images, .oml files, folders). On Windows 11: Show more options.'],
    ['oml setup --status', 'List the Explorer entries and whether they are installed.'],
    ['oml setup --remove', 'Remove the Explorer entries that oml setup added, and nothing else.'],
    ['oml selftest', 'Run the build check on the installed Service Studio.'],
  ] },
  { group: 'Read a module', items: [
    ['oml inspect C:\\Work\\Orders.oml --own --only entities,actions,screens', 'List the entities, actions and screens that the module owns.'],
    ['oml inspect C:\\Work\\Orders.oml --only references', 'List the modules and elements that Orders uses.'],
    ['oml describe C:\\Work\\Orders.oml Order_Save', 'Explain one action in a few readable lines.'],
    ['oml describe C:\\Work\\Orders.oml OrderDetail Order_Save Order', 'Explain several elements in one call.'],
    ['oml describe C:\\Work\\Orders.oml Order_Save --depth 1 --in C:\\Work\\Producers', 'Also show the bodies of called actions from producer modules.'],
    ['oml find Customer_Validate C:\\Work\\Orders.oml --in C:\\Work\\Producers', 'Find which module owns an element.'],
    ['oml find "Order*" C:\\Work\\Orders.oml', 'Find every element whose name starts with Order.'],
    ['oml census C:\\Work\\Orders.oml', 'Count the elements in the module.'],
    ['oml tree C:\\Work\\Orders.oml Order_Save', 'Show the object tree of one element with exact paths.'],
    ['oml validate C:\\Work\\Orders.oml --warnings', 'Show validation errors and warnings.'],
  ] },
  { group: 'Change a module', items: [
    ['oml new C:\\Work\\Sandbox.oml --name Sandbox --kind CrossDevice', 'Create a new blank Reactive module.'],
    ['oml spec entities', 'Read the reference for one spec key.'],
    ['oml spec check C:\\Work\\change.json', 'Check a spec for typos and wrong types in milliseconds.'],
    ['oml spec schema --out C:\\Work\\spec.schema.json', 'Write a JSON Schema for autocomplete in VS Code.'],
    ['oml apply C:\\Work\\Orders.oml C:\\Work\\change.json C:\\Work\\Orders.staged.oml', 'Apply a spec to a new staged file. The source does not change.'],
    ['oml apply C:\\Work\\Orders.oml C:\\Work\\change.json C:\\Work\\Orders.staged.oml --describe-created', 'Apply and describe the new elements in the same call.'],
    ['oml run C:\\Work\\steps.json', 'Run several commands in one process, so Service Studio loads once.'],
    ['oml install C:\\Work\\Orders.staged.oml C:\\Work\\Orders.oml', 'Replace the module with the staged file. Only after you check it.'],
    ['oml translations export C:\\Work\\Orders.oml C:\\Work\\Orders-translations.xlsx', 'Export the translations to Excel.'],
  ] },
  { group: 'User interface', items: [
    ['oml preview C:\\Work\\Orders.staged.oml StatusBadge --theme light --screenshot C:\\Work\\badge-light.png', 'Render a block and take a screenshot.'],
    ['oml preview C:\\Work\\Orders.staged.oml StatusBadge --theme dark --width 390 --screenshot C:\\Work\\badge-dark-390.png', 'Check the dark theme at phone width.'],
    ['oml preview C:\\Work\\Orders.staged.oml OrderCard --arg Title="\\"Order 1042\\""', 'Render a block with an input value.'],
  ] },
  { group: 'Service Studio', items: [
    ['oml studio', 'Check that Service Studio\'s MCP server answers and list the open modules.'],
    ['oml pull Orders --out C:\\Work\\Orders.oml', 'Save the module open in Service Studio to a file.'],
    ['oml publish C:\\Work\\Orders.oml', '1-Click Publish through Service Studio. Ask before you publish.'],
    ['oml references Orders', 'Refresh All references through Service Studio.'],
  ] },
  { group: 'REST and architecture', items: [
    ['oml openapi C:\\Work\\Orders.oml --api OrdersApi --out C:\\Work\\OrdersApi.openapi.json', 'Export one exposed REST API as OpenAPI 3.0.3.'],
    ['oml openapi C:\\Work\\Orders.oml --server https://dev.example.com/Orders/rest/OrdersApi', 'Export every exposed API with a real server address.'],
    ['oml architecture --in C:\\Exports\\Payments --out C:\\Exports\\payments-architecture.html', 'Make an architecture page from a folder of .oap and .oml files.'],
    ['oml architecture --in C:\\Exports --app Payments --json C:\\Exports\\payments.json', 'Keep one application and also write the data as JSON.'],
    ['oml architecture --team Payments --out C:\\Exports\\team.html', 'Read the team\'s applications from LifeTime.'],
  ] },
  { group: 'LifeTime', items: [
    ['oml lt environments', 'List the environments.'],
    ['oml lt apps --env Development', 'List the applications in Development with their tags.'],
    ['oml lt status 6f1c2a9e-3b7d-4c55-9e0a-1d2b3c4d5e6f', 'Read the status of a deployment.'],
    ['oml lt tag Orders --version 1.4.0 --description "Invoice export"', 'Tag the application. Ask before you tag.'],
    ['oml lt deploy Orders --from Development --to Test --wait', 'Deploy the latest tag to Test and wait. Ask before you deploy.'],
  ] },
  { group: 'Packages and extensions', items: [
    ['oml app list C:\\Exports\\Orders.oap', 'List the modules in an application package.'],
    ['oml app extract C:\\Exports\\Orders.oap C:\\Work\\Orders-modules', 'Extract the modules of a package.'],
    ['oml app pack C:\\Work\\Orders.oap --name Orders C:\\Work\\Orders.oml C:\\Work\\Orders_CS.oml', 'Pack modules into a new application package.'],
    ['oml extension inspect C:\\Work\\PdfTools.xif', 'Show the actions and structures of an extension.'],
    ['oml extension new C:\\Work\\PdfTools.xif --name PdfTools --spec C:\\Work\\pdf-actions.json', 'Create extension metadata from a spec.'],
    ['oml templates', 'List the local template modules.'],
  ] },
  { group: 'Fonts and images', items: [
    ['oml font woff2 C:\\Assets\\Brand.ttf', 'Convert one font to WOFF2.'],
    ['oml font batch C:\\Assets\\Fonts --out C:\\Assets\\WebFonts', 'Convert every font in a folder.'],
    ['oml image C:\\Assets\\Hero.png --to webp --max-width 1280 --quality 85', 'Convert and shrink an image.'],
    ['oml image C:\\Assets\\Logo.svg --to svg --precision 3 --out C:\\Assets\\Logo.min.svg', 'Make an SVG smaller.'],
    ['oml image batch C:\\Assets\\Photos --to webp --max-height 800 --out C:\\Assets\\WebPhotos', 'Convert every image in a folder.'],
  ] },
  { group: 'Diagnostics', items: [
    ['oml log summary', 'Show which commands agents use and where they fail. Needs OML_LOG=1.'],
    ['oml api IEntity', 'Show the members of a model type on this Service Studio build.'],
    ['oml widgets', 'List the registered widget types.'],
  ] },
];

// Short help for the setup checks of `oml doctor`.
window.omlCheckNames = {
  serviceStudio: 'Service Studio', dotnet: '.NET runtime', model: 'Model API', selfTest: 'Self-test',
  studioMcp: 'MCP server', lifeTime: 'LifeTime', browser: 'Browser', path: 'PATH',
};
