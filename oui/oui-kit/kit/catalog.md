# OUI component catalog

186 blocks and 23 screens. Build any of them in your own module: `python ouikit.py --blocks A,B --out <folder>` (dependencies
included), then `oml apply`. Per block: what it does, inputs (`*` mandatory), events, placeholders, blocks it uses, the OUI screen that shows it.

# Page templates

Build one with `--screens <Name>`. Replace the Load action with your own data.

- **OuiTplDashboard**: Template: a business overview. Key metrics for a period, a revenue chart, revenue by channel and a recent-activity table with a status filter.
- **OuiTplListDetail**: Template: browse records and read one. Search and segment filters above a list, and the selected record in the detail pane. On a phone the list and the detail take turns.
- **OuiTplWizard**: Template: a form in steps. Each step checks its fields and shows the errors, a review step sums up the answers, and a success state ends the flow.
- **OuiTplSettings**: Template: account settings. Sections with inputs and switches, a save bar that appears when something changed, and a dangerous zone with a confirm dialog.
- **OuiTplInbox**: Template: conversations. A searchable list with an unread filter, a reading pane and a reply composer. On a phone the list and the conversation take turns.

# Blocks

## OuiButton
Button. Put the label in the Label placeholder.
- Inputs: Variant: Text = "primary", Size: Text = "md", Icon: Text = "", IsLoading: Boolean, IsDisabled: Boolean, IsFullWidth: Boolean
- Events: OnClick()
- Placeholders: Label
- Shown on: OuiActions, OuiAssistant, OuiChat, OuiComponents, OuiData, OuiHome, OuiLayoutBlankDemo, OuiMotion, OuiNavigation, OuiOverlays, OuiProcesses, OuiTplSettings, OuiTplWizard
- Example: `{"type": "BlockInstance", "sourceBlock": "OuiButton", "args": {"Variant": "\"primary\""}, "placeholders": {"Label": [{"type": "Text", "text": "Save changes"}]}}`

## OuiIconButton
Round button with only an icon. Label is read by screen readers and shown as a tooltip.
- Inputs: *Icon: Text, *Label: Text, Variant: Text = "ghost", Size: Text = "md", IsDisabled: Boolean
- Events: OnClick()
- Shown on: OuiChat, OuiComponents, OuiNavigation, OuiTplInbox, OuiTplListDetail
- Example: `{"type": "BlockInstance", "sourceBlock": "OuiIconButton", "args": {"Icon": "\"edit\"", "Label": "\"Edit\""}}`

## OuiSegmented
Segmented control for 2 to 5 views. Keep Value in a local of the parent and set it in OnChange.
- Inputs: Option1: Text = "", Option2: Text = "", Option3: Text = "", Option4: Text = "", Option5: Text = "", Value: Text, Size: Text = "md", IsFullWidth: Boolean
- Events: OnChange(Value: Text)
- Shown on: OuiComponents, OuiHome, OuiTplDashboard
- Example: `{"type": "BlockInstance", "sourceBlock": "OuiSegmented", "args": {"Option1": "\"Day\"", "Option2": "\"Week\"", "Option3": "\"Month\"", "Option4": "\"Year\"", "Value": "Period"}, "events": {"OnChange": "SetPeriod"}}`

## OuiBadge
Small status label. Put the text in the Content placeholder.
- Inputs: Tone: Text = "neutral", IsSolid: Boolean, HasDot: Boolean
- Placeholders: Content
- Shown on: OuiComponents, OuiData, OuiLayoutSideDemo, OuiTplDashboard, OuiTplInbox, OuiTplListDetail
- Example: `{"type": "BlockInstance", "sourceBlock": "OuiBadge", "args": {"Tone": "\"neutral\""}, "placeholders": {"Content": [{"type": "Text", "text": "Neutral"}]}}`

## OuiKbd
Keyboard key, for example Ctrl or K.
- Inputs: *Keys: Text
- Shown on: OuiComponents, OuiOverlays
- Example: `{"type": "BlockInstance", "sourceBlock": "OuiKbd", "args": {"Keys": "\"Ctrl\""}}`

## OuiAlert
Message box for info, success, warning and danger. Dismissible alerts hide themselves and fire OnDismiss.
- Inputs: Tone: Text = "info", Title: Text = "", Description: Text = "", IsDismissible: Boolean
- Events: OnDismiss()
- Placeholders: Actions
- Shown on: OuiComponents, OuiTplSettings, OuiTplWizard
- Example: `{"type": "BlockInstance", "sourceBlock": "OuiAlert", "args": {"Tone": "\"info\"", "Title": "\"New version available\"", "Description": "\"Version 2.4 adds faster exports and a new dashboard.\"", "IsDismissible": "True"}}`

## OuiCard
Surface with Header, Content and Footer placeholders. Empty parts take no space.
- Inputs: Padding: Text = "md", IsHoverable: Boolean, IsMuted: Boolean, IsSelected: Boolean
- Placeholders: Header, Content, Footer
- Shown on: OuiComponents, OuiHome, OuiTplDashboard, OuiTplSettings
- Example: `{"type": "BlockInstance", "sourceBlock": "OuiCard", "args": {"IsHoverable": "True"}, "placeholders": {"Content": [{"type": "BlockInstance", "sourceBlock": "OuiMetric", "args": {"Label": "\"Revenue\"", "Value": "\"$84,320\"", "ShowDelta": "True", "Delta": "12.4", "Caption": "\"vs last month\""}}]}}`

## OuiMetric
A number with a label, a change and a caption.
- Inputs: *Label: Text, *Value: Text, ShowDelta: Boolean, Delta: Decimal, DeltaSuffix: Text = "%", LowerIsBetter: Boolean, Caption: Text = ""
- Shown on: OuiComponents, OuiHome, OuiLayoutSideDemo, OuiMotion, OuiTplDashboard, OuiTplListDetail
- Example: `{"type": "BlockInstance", "sourceBlock": "OuiMetric", "args": {"Label": "\"Revenue\"", "Value": "\"$84,320\"", "ShowDelta": "True", "Delta": "12.4", "Caption": "\"vs last month\""}}`

## OuiProgressBar
Horizontal progress from 0 to 100, or indeterminate.
- Inputs: Value: Decimal, Label: Text = "", ShowValue: Boolean, IsIndeterminate: Boolean, Tone: Text = "accent"
- Shown on: OuiComponents
- Example: `{"type": "BlockInstance", "sourceBlock": "OuiProgressBar", "args": {"Value": "Progress", "Label": "\"Uploading statements\"", "ShowValue": "True"}}`

## OuiProgressRing
Circular progress from 0 to 100. Changes animate.
- Inputs: Value: Decimal, Size: Integer = 44, ShowValue: Boolean = True
- Shown on: OuiComponents
- Example: `{"type": "BlockInstance", "sourceBlock": "OuiProgressRing", "args": {"Value": "Progress"}}`

## OuiSpinner
Loading spinner.
- Inputs: Size: Text = "md", Label: Text = "Loading"
- Shown on: OuiComponents
- Example: `{"type": "BlockInstance", "sourceBlock": "OuiSpinner", "args": {"Size": "\"sm\""}}`

## OuiSkeleton
Placeholder shape that shimmers while content loads.
- Inputs: Width: Text = "100%", Height: Text = "16px", Shape: Text = "rect"
- Shown on: OuiComponents
- Example: `{"type": "BlockInstance", "sourceBlock": "OuiSkeleton", "args": {"Width": "\"44px\"", "Height": "\"44px\"", "Shape": "\"circle\""}}`

## OuiAvatar
Round picture or initials, with an optional status dot.
- Inputs: *Name: Text, ImageUrl: Text = "", Size: Text = "md", Status: Text = ""
- Shown on: OuiComponents, OuiData, OuiHome, OuiLayoutSideDemo, OuiOverlays, OuiTplDashboard, OuiTplInbox, OuiTplListDetail
- Example: `{"type": "BlockInstance", "sourceBlock": "OuiAvatar", "args": {"Name": "\"Ana Silva\"", "Size": "\"sm\""}}`

## OuiAvatarGroup
Overlapping avatars. Put OuiAvatar blocks in the Content placeholder.
- Inputs: none
- Placeholders: Content
- Shown on: OuiComponents
- Example: `{"type": "BlockInstance", "sourceBlock": "OuiAvatarGroup", "placeholders": {"Content": [{"type": "BlockInstance", "sourceBlock": "OuiAvatar", "args": {"Name": "\"Ana Silva\""}}, {"type": "BlockInstance", "sourceBlock": "OuiAvatar", "args": {"Name": "\"Mei Chen\""}}, {"type": "BlockInstance", "sourceBlock": "OuiAvatar", "args": {"Name": "\"Omar Haddad\""}}, {"type": "BlockInstance", "sourceBlock": `

## OuiEmptyState
Shown when a list has nothing to show. Put a button in the Action placeholder.
- Inputs: Icon: Text = "inbox", *Title: Text, Description: Text = ""
- Placeholders: Action
- Shown on: OuiComponents, OuiTplDashboard, OuiTplInbox, OuiTplListDetail
- Example: `{"type": "BlockInstance", "sourceBlock": "OuiEmptyState", "args": {"Icon": "\"folder\"", "Title": "\"No statements yet\"", "Description": "\"Statements appear here at the end of each month. You can also request one now.\""}, "placeholders": {"Action": [{"type": "BlockInstance", "sourceBlock": "OuiButton", "args": {"Variant": "\"secondary\"", "Icon": "\"plus\""}, "placeholders": {"Label": [{"type":`

## OuiTimelineItem
One event in a vertical timeline. Set IsLast on the last item.
- Inputs: *Title: Text, Time: Text = "", Description: Text = "", Tone: Text = "neutral", IsLast: Boolean
- Placeholders: Content
- Shown on: OuiComponents, OuiOverlays, OuiTplListDetail
- Example: `{"type": "BlockInstance", "sourceBlock": "OuiTimelineItem", "args": {"Title": "\"Application received\"", "Time": "\"09:12\"", "Description": "\"Submitted online by Ana Silva.\"", "Tone": "\"accent\""}}`

## OuiDivider
Horizontal line, with an optional label in the middle.
- Inputs: Label: Text = ""
- Shown on: OuiComponents, OuiTplWizard
- Example: `{"type": "BlockInstance", "sourceBlock": "OuiDivider", "args": {"Label": "\"or\""}}`

## OuiField
Label, hint and error around an input. Put an Input, Dropdown or TextArea in the Input placeholder.
- Inputs: *Label: Text, Hint: Text = "", ErrorMessage: Text = "", IsRequired: Boolean
- Placeholders: Input
- Shown on: OuiComponents, OuiInputs, OuiLayoutBlankDemo, OuiTplSettings, OuiTplWizard
- Example: `{"type": "BlockInstance", "sourceBlock": "OuiField", "args": {"Label": "\"Full name\"", "Hint": "\"As it appears on your ID.\"", "IsRequired": "True"}, "placeholders": {"Input": [{"type": "Input", "name": "NameInput", "variable": "FullName", "prompt": "\"Ana Silva\""}]}}`

## OuiTabs
Tabs with up to 5 panels. Put each panel's content in Panel1 to Panel5.
- Inputs: Tab1: Text = "", Tab2: Text = "", Tab3: Text = "", Tab4: Text = "", Tab5: Text = "", InitialIndex: Integer = 1, Variant: Text = "line"
- Events: OnChange(Index: Integer)
- Placeholders: Panel1, Panel2, Panel3, Panel4, Panel5
- Shown on: OuiComponents, OuiTplListDetail
- Example: `{"type": "BlockInstance", "sourceBlock": "OuiTabs", "args": {"Tab1": "\"Overview\"", "Tab2": "\"Activity\"", "Tab3": "\"Settings\""}, "placeholders": {"Panel1": [{"type": "Expression", "value": "\"Account overview: balances, limits and recent statements.\"", "style": "\"oui-section__desc\""}], "Panel2": [{"type": "Expression", "value": "\"Activity: logins, transfers and changes by date.\"", "style`

## OuiAccordionItem
A section that opens and closes with a smooth height animation.
- Inputs: *Title: Text, IsOpen: Boolean
- Placeholders: Content
- Shown on: OuiComponents
- Example: `{"type": "BlockInstance", "sourceBlock": "OuiAccordionItem", "args": {"Title": "\"How long does a transfer take?\"", "IsOpen": "True"}, "placeholders": {"Content": [{"type": "Text", "text": "Transfers inside the country arrive the same day. International transfers take one to three working days."}]}}`

## OuiStepper
Progress through up to 5 steps. Steps before Current are done.
- Inputs: Step1: Text = "", Step2: Text = "", Step3: Text = "", Step4: Text = "", Step5: Text = "", Current: Integer = 1
- Shown on: OuiComponents, OuiTplWizard
- Example: `{"type": "BlockInstance", "sourceBlock": "OuiStepper", "args": {"Step1": "\"Details\"", "Step2": "\"Documents\"", "Step3": "\"Review\"", "Step4": "\"Done\"", "Current": "Step"}}`

## OuiBreadcrumb
Path to the current page. Put Links and a last Text in the Content placeholder.
- Inputs: none
- Placeholders: Content
- Shown on: OuiComponents
- Example: `{"type": "BlockInstance", "sourceBlock": "OuiBreadcrumb", "placeholders": {"Content": [{"type": "Link", "onClick": "OuiComponents", "children": [{"type": "Text", "text": "Home"}]}, {"type": "Link", "onClick": "OuiComponents", "children": [{"type": "Text", "text": "Accounts"}]}, {"type": "Text", "text": "Statements"}]}}`

## OuiTooltip
Short text on hover or keyboard focus. Put the element in the Content placeholder.
- Inputs: *Text: Text, Placement: Text = "top"
- Placeholders: Content
- Shown on: OuiComponents
- Example: `{"type": "BlockInstance", "sourceBlock": "OuiTooltip", "args": {"Text": "\"Copies the link to the clipboard\""}, "placeholders": {"Content": [{"type": "BlockInstance", "sourceBlock": "OuiButton", "args": {"Variant": "\"outline\"", "Icon": "\"link\""}, "placeholders": {"Label": [{"type": "Text", "text": "Hover me"}]}}]}}`

## OuiThemeToggle
Sun and moon button that switches between light and dark with a circular reveal. The choice is saved in the browser.
- Inputs: Size: Text = "md"
- Example: `{"type": "BlockInstance", "sourceBlock": "OuiThemeToggle"}`

## OuiAccentPicker
Swatches that change the accent colour of every OUI component. Brand follows the theme's primary colour.
- Inputs: none
- Example: `{"type": "BlockInstance", "sourceBlock": "OuiAccentPicker"}`

## OuiLayout
Page layout: a sticky top bar (brand, Navigation placeholder, accent picker, theme toggle), a page title with actions, and the content.
- Inputs: AppName: Text = "OUI", ShowAccentPicker: Boolean = True, ShowThemeToggle: Boolean = True
- Placeholders: Navigation, HeaderActions, Title, Actions, MainContent
- Uses: OuiAccentPicker, OuiThemeToggle

## OuiDropdownMenu
A menu that opens from a trigger. Put a button-like element in Trigger and OuiMenuItem blocks in Items.
- Inputs: Side: Text = "bottom", Align: Text = "start", Label: Text = "Open menu"
- Placeholders: Trigger, Items
- Shown on: OuiActions, OuiNavigation
- Example: `{"type": "BlockInstance", "sourceBlock": "OuiDropdownMenu", "args": {"Label": "\"Row actions\""}, "placeholders": {"Trigger": [{"type": "Container", "style": "\"oui-btn oui-btn--secondary\"", "children": [{"type": "Text", "text": "Options"}, {"type": "Container", "style": "\"oui-i oui-i--chevron-down\"", "attributes": {"aria-hidden": "\"true\""}}]}], "Items": [{"type": "BlockInstance", "sourceBloc`

## OuiMenuItem
One item of a menu. Closes its menu when clicked.
- Inputs: *Label: Text, Icon: Text = "", Shortcut: Text = "", IsDanger: Boolean, IsDisabled: Boolean, IsCheckable: Boolean, IsChecked: Boolean
- Events: OnClick()
- Shown on: OuiActions, OuiData, OuiNavigation
- Example: `{"type": "BlockInstance", "sourceBlock": "OuiMenuItem", "args": {"Label": "\"Download PDF\"", "Icon": "\"download\"", "Shortcut": "\"Ctrl+D\""}, "events": {"OnClick": {"action": "Log", "args": {"Message": "\"Download PDF\""}}}}`

## OuiMenuLabel
A small heading inside a menu.
- Inputs: *Label: Text
- Shown on: OuiActions
- Example: `{"type": "BlockInstance", "sourceBlock": "OuiMenuLabel", "args": {"Label": "\"Statement\""}}`

## OuiMenuSeparator
A line between groups of menu items.
- Inputs: none
- Shown on: OuiActions, OuiData
- Example: `{"type": "BlockInstance", "sourceBlock": "OuiMenuSeparator"}`

## OuiButtonGroup
Buttons joined into one control. Put OuiButton blocks in Content.
- Inputs: Label: Text = ""
- Placeholders: Content
- Shown on: OuiActions
- Example: `{"type": "BlockInstance", "sourceBlock": "OuiButtonGroup", "args": {"Label": "\"Alignment\""}, "placeholders": {"Content": [{"type": "BlockInstance", "sourceBlock": "OuiButton", "args": {"Variant": "\"secondary\""}, "placeholders": {"Label": [{"type": "Text", "text": "Left"}]}}, {"type": "BlockInstance", "sourceBlock": "OuiButton", "args": {"Variant": "\"secondary\""}, "placeholders": {"Label": [{`

## OuiSplitButton
A main action and a menu of related actions. Put OuiMenuItem blocks in Items.
- Inputs: *Label: Text, Icon: Text = "", Variant: Text = "primary", Size: Text = "md", IsDisabled: Boolean, MenuLabel: Text = "More actions"
- Events: OnClick()
- Placeholders: Items
- Shown on: OuiActions
- Example: `{"type": "BlockInstance", "sourceBlock": "OuiSplitButton", "args": {"Label": "\"Send transfer\"", "Icon": "\"send\""}, "placeholders": {"Items": [{"type": "BlockInstance", "sourceBlock": "OuiMenuItem", "args": {"Label": "\"Schedule for later\"", "Icon": "\"clock\""}, "events": {"OnClick": {"action": "Log", "args": {"Message": "\"Schedule for later\""}}}}, {"type": "BlockInstance", "sourceBlock": "`

## OuiCopyButton
Copies a text to the clipboard and shows a check for a moment.
- Inputs: *Text: Text, Label: Text = "Copy", IsIconOnly: Boolean, Variant: Text = "secondary", Size: Text = "sm"
- Shown on: OuiActions
- Example: `{"type": "BlockInstance", "sourceBlock": "OuiCopyButton", "args": {"Text": "\"GE29 NB00 0000 0101 9049 17\"", "Label": "\"Copy IBAN\""}}`

## OuiHoldButton
Press and hold to confirm a destructive action. Works with the mouse, touch and the keyboard (hold Space).
- Inputs: *Label: Text, Variant: Text = "danger", Duration: Integer = 1200, Size: Text = "md"
- Events: OnConfirm()
- Shown on: OuiActions
- Example: `{"type": "BlockInstance", "sourceBlock": "OuiHoldButton", "args": {"Label": "\"Hold to delete account\""}, "events": {"OnConfirm": {"action": "Log", "args": {"Message": "\"Account deleted (demo)\""}}}}`

## OuiToggleGroup
Several on and off toggles. Values is a comma separated list of the options that are on.
- Inputs: Option1: Text = "", Option2: Text = "", Option3: Text = "", Option4: Text = "", Option5: Text = "", Values: Text = ""
- Events: OnChange(Values: Text)
- Shown on: OuiActions
- Example: `{"type": "BlockInstance", "sourceBlock": "OuiToggleGroup", "args": {"Option1": "\"Bold\"", "Option2": "\"Italic\"", "Option3": "\"Underline\"", "Values": "Formats"}, "events": {"OnChange": "SetFormats"}}`

## OuiConfirmButton
A button that asks for a second click before it acts. It disarms itself after 3 seconds.
- Inputs: *Label: Text, ConfirmLabel: Text = "Click again to confirm", Variant: Text = "secondary", Size: Text = "md", Icon: Text = ""
- Events: OnConfirm()
- Shown on: OuiActions, OuiTplListDetail
- Example: `{"type": "BlockInstance", "sourceBlock": "OuiConfirmButton", "args": {"Label": "\"Cancel subscription\"", "Icon": "\"close\""}, "events": {"OnConfirm": {"action": "Log", "args": {"Message": "\"Subscription cancelled (demo)\""}}}}`

## OuiStatusButton
A button that shows the state of the work it starts: idle, loading, success or error. The parent sets Status.
- Inputs: *Label: Text, Status: Text = "idle", LoadingLabel: Text = "Saving", SuccessLabel: Text = "Saved", ErrorLabel: Text = "Try again", Variant: Text = "primary", Size: Text = "md"
- Events: OnClick()
- Shown on: OuiActions
- Example: `{"type": "BlockInstance", "sourceBlock": "OuiStatusButton", "args": {"Label": "\"Save changes\"", "Status": "SaveStatus"}, "events": {"OnClick": "RunSave"}}`

## OuiReactions
Emoji reactions with counts. Mine is the reaction of the current user (0 for none).
- Inputs: Emoji1: Text = "", Emoji2: Text = "", Emoji3: Text = "", Emoji4: Text = "", Count1: Integer, Count2: Integer, Count3: Integer, Count4: Integer, Mine: Integer
- Events: OnReact(Index: Integer)
- Shown on: OuiActions, OuiChat
- Example: `{"type": "BlockInstance", "sourceBlock": "OuiReactions", "args": {"Emoji1": "\"👍\"", "Emoji2": "\"🎉\"", "Emoji3": "\"👀\"", "Emoji4": "\"❤️\"", "Count1": "12 + If(MyReaction = 1, 1, 0)", "Count2": "4 + If(MyReaction = 2, 1, 0)", "Count3": "If(MyReaction = 3, 1, 0)", "Count4": "7 + If(MyReaction = 4, 1, 0)", "Mine": "MyReaction"}, "events": {"OnReact": "React"}}`

## OuiSpeedDial
A round main button that opens up to 4 actions with a staggered animation.
- Inputs: Icon1: Text = "", Icon2: Text = "", Icon3: Text = "", Icon4: Text = "", Label1: Text = "", Label2: Text = "", Label3: Text = "", Label4: Text = "", Label: Text = "Actions"
- Events: OnAction(Index: Integer)
- Shown on: OuiActions
- Example: `{"type": "BlockInstance", "sourceBlock": "OuiSpeedDial", "args": {"Icon1": "\"edit\"", "Label1": "\"New note\"", "Icon2": "\"upload\"", "Label2": "\"Upload\"", "Icon3": "\"calendar\"", "Label3": "\"Schedule\""}, "events": {"OnAction": {"action": "Log", "args": {"Message": "\"Speed dial action \" + Index"}}}}`

## OuiUserMenu
Avatar and name that open the account menu. Put OuiMenuItem blocks in Items.
- Inputs: *Name: Text, Email: Text = "", ImageUrl: Text = "", Status: Text = "", ShowName: Boolean = True
- Placeholders: Items
- Uses: OuiAvatar
- Shown on: OuiActions, OuiNavigation
- Example: `{"type": "BlockInstance", "sourceBlock": "OuiUserMenu", "args": {"Name": "\"Ana Silva\"", "Email": "\"ana.silva@example.com\"", "Status": "\"online\""}, "placeholders": {"Items": [{"type": "BlockInstance", "sourceBlock": "OuiMenuItem", "args": {"Label": "\"Profile\"", "Icon": "\"user\""}, "events": {"OnClick": {"action": "Log", "args": {"Message": "\"Profile\""}}}}, {"type": "BlockInstance", "sour`

## OuiChoice
Label and description for a Checkbox, Switch or RadioButton. Put the widget, with the class oui-check, oui-switch or oui-radio, in Control.
- Inputs: *Label: Text, Description: Text = "", ControlAtEnd: Boolean
- Placeholders: Control
- Shown on: OuiActions, OuiProcesses, OuiTplSettings, OuiTplWizard
- Example: `{"type": "BlockInstance", "sourceBlock": "OuiChoice", "args": {"Label": "\"Email me statements\"", "Description": "\"A PDF on the first day of each month.\""}, "placeholders": {"Control": [{"type": "Checkbox", "name": "StatementsCheck", "variable": "EmailStatements", "style": "\"oui-check\""}]}}`

## OuiRadioCards
Large choices with a title, description and icon. Value is the selected title.
- Inputs: Title1: Text = "", Title2: Text = "", Title3: Text = "", Title4: Text = "", Description1: Text = "", Description2: Text = "", Description3: Text = "", Description4: Text = "", Icon1: Text = "", Icon2: Text = "", Icon3: Text = "", Icon4: Text = "", Value: Text = ""
- Events: OnChange(Value: Text)
- Shown on: OuiActions, OuiTplWizard
- Example: `{"type": "BlockInstance", "sourceBlock": "OuiRadioCards", "args": {"Title1": "\"Personal\"", "Description1": "\"One owner, free transfers in EUR.\"", "Icon1": "\"user\"", "Title2": "\"Business\"", "Description2": "\"Up to 25 users and approvals.\"", "Icon2": "\"chart\"", "Title3": "\"Private\"", "Description3": "\"A personal banker and lounges.\"", "Icon3": "\"star\"", "Value": "Plan"}, "events": `

## OuiChip
A compact choice or filter. Removable chips show a close button.
- Inputs: *Label: Text, Icon: Text = "", IsSelected: Boolean, IsRemovable: Boolean
- Events: OnClick(), OnRemove()
- Shown on: OuiActions, OuiData, OuiTplDashboard, OuiTplInbox, OuiTplListDetail
- Example: `{"type": "BlockInstance", "sourceBlock": "OuiChip", "args": {"Label": "\"Income\"", "IsSelected": "FilterIncome"}, "events": {"OnClick": "ToggleIncome"}}`

## OuiRating
Stars from 0 to Max. Clicking the current value again clears it.
- Inputs: Value: Integer, Max: Integer = 5, IsReadOnly: Boolean, Size: Text = "md", Label: Text = "Rating"
- Events: OnChange(Value: Integer)
- Shown on: OuiActions
- Example: `{"type": "BlockInstance", "sourceBlock": "OuiRating", "args": {"Value": "Stars"}, "events": {"OnChange": "SetStars"}}`

## OuiSlider
Pick a number by dragging. OnChange fires when the user lets go.
- Inputs: Value: Decimal, Label: Text = "", Min: Decimal = 0, Max: Decimal = 100, Step: Decimal = 1, ShowValue: Boolean = True, Suffix: Text = "", IsDisabled: Boolean
- Events: OnChange(Value: Decimal)
- Shown on: OuiActions, OuiHome
- Example: `{"type": "BlockInstance", "sourceBlock": "OuiSlider", "args": {"Value": "Radius", "Max": "28", "Suffix": "\" px\""}, "events": {"OnChange": {"action": "SetRadius", "args": {"Value": "Value"}}}}`

## OuiRangeSlider
Pick a range with two handles. OnChange fires when the user lets go.
- Inputs: From: Decimal, To: Decimal = 100, Label: Text = "", Min: Decimal = 0, Max: Decimal = 100, Step: Decimal = 1, ShowValue: Boolean = True, Suffix: Text = "", IsDisabled: Boolean
- Events: OnChange(From: Decimal, To: Decimal)
- Shown on: OuiActions
- Example: `{"type": "BlockInstance", "sourceBlock": "OuiRangeSlider", "args": {"Label": "\"Amount between\"", "From": "AmountFrom", "To": "AmountTo", "Max": "2000", "Step": "10"}, "events": {"OnChange": "SetAmounts"}}`

## OuiInputGroup
One bordered control with a prefix and a suffix (text or icon) around an Input in the Input placeholder. Clearable adds a clear button.
- Inputs: Prefix: Text = "", PrefixIcon: Text = "", Suffix: Text = "", IsClearable: Boolean, Size: Text = "md", HasError: Boolean
- Placeholders: Input
- Shown on: OuiInputs
- Example: `{"type": "BlockInstance", "sourceBlock": "OuiInputGroup", "args": {"Prefix": "\"https://\"", "Suffix": "\".com\""}, "placeholders": {"Input": [{"type": "Input", "name": "SiteInput", "variable": "Website", "prompt": "\"northwind\""}]}}`

## OuiPasswordField
An Input of type Password with a show and hide button. Put the Input in the Input placeholder.
- Inputs: ShowLabel: Text = "Show password", Size: Text = "md", HasError: Boolean
- Placeholders: Input
- Shown on: OuiInputs
- Example: `{"type": "BlockInstance", "sourceBlock": "OuiPasswordField", "placeholders": {"Input": [{"type": "Input", "name": "PasswordInput", "variable": "Password", "inputType": "Password"}]}}`

## OuiSearchField
Search control: icon, the Input (Search type) in the Input placeholder, a clear button and an optional shortcut hint.
- Inputs: Shortcut: Text = "", Size: Text = "md", HasError: Boolean
- Placeholders: Input
- Shown on: OuiData, OuiInputs, OuiTplInbox, OuiTplListDetail
- Example: `{"type": "BlockInstance", "sourceBlock": "OuiSearchField", "args": {"Shortcut": "\"Ctrl K\""}, "placeholders": {"Input": [{"type": "Input", "name": "SearchInput", "variable": "Query", "prompt": "\"Search\""}]}}`

## OuiNumberField
A number Input (in the Input placeholder) with minus and plus buttons. The buttons update the parent's variable.
- Inputs: Step: Decimal = 1, Min: Decimal = 0, Max: Decimal = 0, Suffix: Text = "", Size: Text = "md", HasError: Boolean
- Placeholders: Input
- Shown on: OuiInputs, OuiTplWizard
- Example: `{"type": "BlockInstance", "sourceBlock": "OuiNumberField", "args": {"Min": "1", "Max": "12"}, "placeholders": {"Input": [{"type": "Input", "name": "GuestsInput", "variable": "Guests", "inputType": "Number"}]}}`

## OuiMoneyField
A money Input (in the Input placeholder): currency before, digits right-aligned and tabular.
- Inputs: Currency: Text = "EUR", Size: Text = "md", HasError: Boolean
- Placeholders: Input
- Shown on: OuiInputs
- Example: `{"type": "BlockInstance", "sourceBlock": "OuiMoneyField", "args": {"Currency": "\"EUR\""}, "placeholders": {"Input": [{"type": "Input", "name": "AmountInput", "variable": "Amount"}]}}`

## OuiAutoGrow
A TextArea (in the Input placeholder) that grows with its text.
- Inputs: none
- Placeholders: Input
- Shown on: OuiInputs
- Example: `{"type": "BlockInstance", "sourceBlock": "OuiAutoGrow", "placeholders": {"Input": [{"type": "TextArea", "name": "MessageArea", "variable": "Message", "prompt": "\"Write a note for the payee\""}]}}`

## OuiTagInput
Tags typed in and removed with a click or Backspace. Values is a comma separated list.
- Inputs: Values: Text = "", Placeholder: Text = "Add a tag", MaxTags: Integer = 0
- Events: OnChange(Values: Text)
- Shown on: OuiInputs
- Example: `{"type": "BlockInstance", "sourceBlock": "OuiTagInput", "args": {"Values": "Tags", "MaxTags": "6"}, "events": {"OnChange": "SetTags"}}`

## OuiOtpInput
A one-time code in separate boxes: auto advance, paste, Backspace. OnComplete fires when every box is filled.
- Inputs: Length: Integer = 6, Label: Text = "Verification code"
- Events: OnChange(Value: Text), OnComplete(Value: Text)
- Shown on: OuiInputs
- Example: `{"type": "BlockInstance", "sourceBlock": "OuiOtpInput", "args": {"Length": "6"}, "events": {"OnComplete": "CodeDone"}}`

## OuiPasswordStrength
Four bars and a word for how strong a password is. Checked in the browser; the server must check again.
- Inputs: *Password: Text
- Shown on: OuiInputs
- Example: `{"type": "BlockInstance", "sourceBlock": "OuiPasswordStrength", "args": {"Password": "Password"}}`

## OuiInlineEdit
Text that turns into an input when clicked. Enter saves, Escape cancels.
- Inputs: Value: Text = "", Placeholder: Text = "Click to edit", Label: Text = "Edit"
- Events: OnChange(Value: Text)
- Shown on: OuiInputs
- Example: `{"type": "BlockInstance", "sourceBlock": "OuiInlineEdit", "args": {"Value": "Nickname", "Label": "\"Account nickname\""}, "events": {"OnChange": "SetNickname"}}`

## OuiScrubField
A number you change by dragging its label left and right (Shift for x10, Alt for x0.1) or with the arrow keys.
- Inputs: *Label: Text, Value: Decimal, Step: Decimal = 1, Min: Decimal = 0, Max: Decimal = 0, Decimals: Integer = 0, Suffix: Text = ""
- Events: OnChange(Value: Decimal)
- Shown on: OuiData, OuiInputs
- Example: `{"type": "BlockInstance", "sourceBlock": "OuiScrubField", "args": {"Label": "\"Opacity\"", "Value": "Opacity", "Max": "100", "Suffix": "\"%\""}, "events": {"OnChange": "SetOpacity"}}`

## OuiShortcutRecorder
Focus it and press a key combination to record a shortcut. Backspace clears it.
- Inputs: Value: Text = "", Placeholder: Text = "Record shortcut"
- Events: OnChange(Value: Text)
- Shown on: OuiInputs
- Example: `{"type": "BlockInstance", "sourceBlock": "OuiShortcutRecorder", "args": {"Value": "Shortcut"}, "events": {"OnChange": "SetShortcut"}}`

## OuiSelectWrap
Adds the chevron to a Dropdown widget with the class oui-select. Put the Dropdown in Content.
- Inputs: none
- Placeholders: Content
- Shown on: OuiInputs, OuiTplSettings, OuiTplWizard
- Example: `{"type": "BlockInstance", "sourceBlock": "OuiSelectWrap", "placeholders": {"Content": [{"type": "Dropdown", "name": "StatusDropdown", "variable": "StatusId", "style": "\"oui-select\"", "list": "FetchStatuses.List", "values": "OuiDemoTaskStatus.Id", "labels": "OuiDemoTaskStatus.Label"}]}}`

## OuiCombobox
Type to filter a list of options, then pick one. Options is a comma separated list.
- Inputs: *Options: Text, Value: Text = "", Placeholder: Text = "Search"
- Events: OnChange(Value: Text)
- Shown on: OuiInputs
- Example: `{"type": "BlockInstance", "sourceBlock": "OuiCombobox", "args": {"Options": "\"Tbilisi Central,Tbilisi Vake,Batumi,Kutaisi,Rustavi,Zugdidi,Telavi\"", "Value": "Branch"}, "events": {"OnChange": "SetBranch"}}`

## OuiMultiSelect
Pick several options from a list; the picked ones show as tags. Options and Values are comma separated.
- Inputs: *Options: Text, Values: Text = "", Placeholder: Text = "Choose"
- Events: OnChange(Values: Text)
- Shown on: OuiInputs
- Example: `{"type": "BlockInstance", "sourceBlock": "OuiMultiSelect", "args": {"Options": "\"Current account,Savings,Credit card,Mortgage,Insurance\"", "Values": "Products"}, "events": {"OnChange": "SetProducts"}}`

## OuiCalendar
A month calendar. Single mode fires OnChange(Date); range mode (IsRange) fires OnRangeChange(From, To). Weeks start on Monday.
- Inputs: Value: Date, IsRange: Boolean, From: Date, To: Date, MinDate: Date
- Events: OnChange(Date: Date), OnRangeChange(From: Date, To: Date)
- Shown on: OuiInputs
- Example: `{"type": "BlockInstance", "sourceBlock": "OuiCalendar", "args": {"Value": "PickedDate"}, "events": {"OnChange": "SetDate"}}`

## OuiDatePicker
A date field that opens OuiCalendar in a popover.
- Inputs: Value: Date, Placeholder: Text = "Pick a date", Format: Text = "d MMM yyyy", MinDate: Date, Label: Text = "Date"
- Events: OnChange(Date: Date)
- Uses: OuiCalendar
- Shown on: OuiInputs
- Example: `{"type": "BlockInstance", "sourceBlock": "OuiDatePicker", "args": {"Value": "PickedDate"}, "events": {"OnChange": "SetDate"}}`

## OuiDateRangePicker
A range field that opens a range calendar. OnChange fires with both dates; the popover closes when the range is complete.
- Inputs: From: Date, To: Date, Placeholder: Text = "Pick a range", Format: Text = "d MMM yyyy", Label: Text = "Date range"
- Events: OnChange(From: Date, To: Date)
- Uses: OuiCalendar
- Shown on: OuiInputs
- Example: `{"type": "BlockInstance", "sourceBlock": "OuiDateRangePicker", "args": {"From": "RangeFrom", "To": "RangeTo"}, "events": {"OnChange": "SetRange"}}`

## OuiTimeDial
A clock face to pick an hour (1 to 12) or minutes (0 to 55 in steps of 5).
- Inputs: Value: Integer, Mode: Text = "hours", Size: Integer = 220
- Events: OnChange(Value: Integer)
- Shown on: OuiInputs
- Example: `{"type": "BlockInstance", "sourceBlock": "OuiTimeDial", "args": {"Value": "Hour"}, "events": {"OnChange": "SetHour"}}`

## OuiWheelPicker
A scroll wheel that snaps to one option, as on phones. Options is comma separated.
- Inputs: *Options: Text, Value: Text = "", Label: Text = "Pick a value"
- Events: OnChange(Value: Text)
- Shown on: OuiInputs
- Example: `{"type": "BlockInstance", "sourceBlock": "OuiWheelPicker", "args": {"Options": "\"Jan,Feb,Mar,Apr,May,Jun,Jul,Aug,Sep,Oct,Nov,Dec\"", "Value": "MonthName"}, "events": {"OnChange": "SetMonth"}}`

## OuiColorPicker
Preset swatches and a full colour picker. Colors is a comma separated list of hex colours.
- Inputs: Value: Text = "#2F6FE4", Colors: Text = "#2F6FE4,#6A4CF5,#16803C,#B56A00,#D4590F,#DC4F47,#D23671,#33333B", Label: Text = "Colour"
- Events: OnChange(Value: Text)
- Shown on: OuiData, OuiInputs
- Example: `{"type": "BlockInstance", "sourceBlock": "OuiColorPicker", "args": {"Value": "Color"}, "events": {"OnChange": "SetColor"}}`

## OuiRulerSlider
A slider with ruler ticks, for precise values such as zoom or rotation.
- Inputs: Value: Decimal, Ticks: Integer = 20, Label: Text = "", Min: Decimal = 0, Max: Decimal = 100, Step: Decimal = 1, Suffix: Text = ""
- Events: OnChange(Value: Decimal)
- Shown on: OuiInputs
- Example: `{"type": "BlockInstance", "sourceBlock": "OuiRulerSlider", "args": {"Label": "\"Zoom\"", "Value": "Zoom", "Min": "25", "Max": "400", "Step": "5", "Suffix": "\"%\""}, "events": {"OnChange": "SetZoom"}}`

## OuiHistogramSlider
A range slider over a histogram, for filtering by amount or price. Values is a comma separated list of bar heights.
- Inputs: *Values: Text, From: Decimal, To: Decimal = 100, Label: Text = "", Min: Decimal = 0, Max: Decimal = 100, Step: Decimal = 1, Suffix: Text = ""
- Events: OnChange(From: Decimal, To: Decimal)
- Shown on: OuiInputs
- Example: `{"type": "BlockInstance", "sourceBlock": "OuiHistogramSlider", "args": {"Label": "\"Transaction amount\"", "Values": "\"2,4,7,12,18,24,30,27,22,17,13,10,8,6,5,4,3,2,2,1\"", "Max": "2000", "Step": "10", "From": "HistFrom", "To": "HistTo", "Suffix": "\" EUR\""}, "events": {"OnChange": "SetHist"}}`

## OuiProcessWatcher
Watches 1 to n background processes. It fires OnPoll every IntervalSeconds while any process is queued or running (not while the browser tab is hidden): refresh your data action there. OnFinished fires once when the last one ends.
- Inputs: *Processes: List of OuiProcessStatus, Title: Text = "Background processes", IntervalSeconds: Integer = 3, AutoReload: Boolean, ReloadSeconds: Integer = 5, ShowRefresh: Boolean = True, RefreshLabel: Text = "Refresh page", IsCompact: Boolean, RefreshReloadsPage: Boolean = True
- Events: OnPoll(), OnFinished(Succeeded: Integer, Failed: Integer), OnRefresh()
- Uses: OuiProgressBar
- Shown on: OuiProcesses
- Example: `{"type": "BlockInstance", "sourceBlock": "OuiProcessWatcher", "args": {"Processes": "FetchJobs.Processes", "Title": "\"Month-end jobs\"", "IntervalSeconds": "2", "AutoReload": "AutoReload", "RefreshReloadsPage": "False"}, "events": {"OnPoll": "Poll", "OnFinished": "Finished", "OnRefresh": "Poll"}}`

## OuiSidebar
App sidebar: Header, Items (OuiNavSection and OuiNavItem blocks) and Footer placeholders. It can collapse to icons.
- Inputs: IsCollapsed: Boolean
- Placeholders: Header, Items, Footer
- Shown on: OuiNavigation
- Example: `{"type": "BlockInstance", "sourceBlock": "OuiSidebar", "args": {"IsCollapsed": "Collapsed"}, "placeholders": {"Header": [{"type": "Container", "style": "\"oui-brand\"", "children": [{"type": "Container", "style": "\"oui-brand__mark\""}, {"type": "Expression", "value": "\"Northwind\"", "style": "\"oui-sidebar__title\""}]}], "Items": [{"type": "BlockInstance", "sourceBlock": "OuiNavSection", "args":`

## OuiNavSection
A group of nav items with a small heading. Put OuiNavItem blocks in Items.
- Inputs: Label: Text = ""
- Placeholders: Items
- Shown on: OuiNavigation
- Example: `{"type": "BlockInstance", "sourceBlock": "OuiNavSection", "args": {"Label": "\"Banking\""}, "placeholders": {"Items": [{"type": "BlockInstance", "sourceBlock": "OuiNavItem", "args": {"Label": "\"Accounts\"", "Icon": "\"home\"", "IsActive": "Section = \"Accounts\""}, "events": {"OnClick": {"action": "SetSection", "args": {"Value": "\"Accounts\""}}}}, {"type": "BlockInstance", "sourceBlock": "OuiNav`

## OuiNavItem
One sidebar link: icon, label, optional count. OnClick navigates (handle it with a destination).
- Inputs: *Label: Text, Icon: Text = "", Badge: Text = "", IsActive: Boolean
- Events: OnClick()
- Shown on: OuiNavigation
- Example: `{"type": "BlockInstance", "sourceBlock": "OuiNavItem", "args": {"Label": "\"Accounts\"", "Icon": "\"home\"", "IsActive": "Section = \"Accounts\""}, "events": {"OnClick": {"action": "SetSection", "args": {"Value": "\"Accounts\""}}}}`

## OuiPagination
Page numbers with previous and next. Shows the first, the last and two pages around the current one.
- Inputs: Page: Integer = 1, *PageCount: Integer, ShowInfo: Boolean
- Events: OnChange(Page: Integer)
- Shown on: OuiData, OuiNavigation
- Example: `{"type": "BlockInstance", "sourceBlock": "OuiPagination", "args": {"Page": "Page", "PageCount": "24", "ShowInfo": "True"}, "events": {"OnChange": {"action": "SetPage", "args": {"Value": "Page"}}}}`

## OuiPathBar
A path such as a folder location, one button per part. Path is separated by slashes; OnNavigate gets the part's position (1 = first).
- Inputs: *Path: Text
- Events: OnNavigate(Position: Integer)
- Shown on: OuiNavigation
- Example: `{"type": "BlockInstance", "sourceBlock": "OuiPathBar", "args": {"Path": "\"Documents/Statements/2025/October\""}, "events": {"OnNavigate": {"action": "Log", "args": {"Message": "\"Path part \" + Position"}}}}`

## OuiTableOfContents
Links to sections of a long page. Items are comma separated; start a title with > for a sub level. Anchors are the ids of the targets, in the same order.
- Inputs: *Items: Text, *Anchors: Text, ActiveIndex: Integer, Title: Text = "On this page"
- Events: OnChange(Index: Integer)
- Shown on: OuiNavigation
- Example: `{"type": "BlockInstance", "sourceBlock": "OuiTableOfContents", "args": {"Items": "\"Overview,>Limits,>Fees,Statements,Security\"", "Anchors": "\"toc-a,toc-b,toc-c,toc-d,toc-e\"", "ActiveIndex": "TocIndex"}, "events": {"OnChange": {"action": "SetToc", "args": {"Value": "Index"}}}}`

## OuiTabBar
Bottom tab bar for phones: up to 5 items with icons.
- Inputs: Label1: Text = "", Label2: Text = "", Label3: Text = "", Label4: Text = "", Label5: Text = "", Icon1: Text = "", Icon2: Text = "", Icon3: Text = "", Icon4: Text = "", Icon5: Text = "", Active: Integer = 1
- Events: OnChange(Index: Integer)
- Shown on: OuiNavigation
- Example: `{"type": "BlockInstance", "sourceBlock": "OuiTabBar", "args": {"Label1": "\"Home\"", "Icon1": "\"home\"", "Label2": "\"Cards\"", "Icon2": "\"file\"", "Label3": "\"Pay\"", "Icon3": "\"send\"", "Label4": "\"Alerts\"", "Icon4": "\"bell\"", "Active": "MobileTab"}, "events": {"OnChange": {"action": "SetMobileTab", "args": {"Value": "Index"}}}}`

## OuiMenuBar
A desktop menu bar. Put OuiDropdownMenu blocks (each with a text trigger) in Menus.
- Inputs: none
- Placeholders: Menus
- Shown on: OuiNavigation
- Example: `{"type": "BlockInstance", "sourceBlock": "OuiMenuBar", "placeholders": {"Menus": [{"type": "BlockInstance", "sourceBlock": "OuiDropdownMenu", "args": {"Label": "\"File\""}, "placeholders": {"Trigger": [{"type": "Text", "text": "File"}], "Items": [{"type": "BlockInstance", "sourceBlock": "OuiMenuItem", "args": {"Label": "\"New statement\"", "Shortcut": "\"Ctrl+N\""}, "events": {"OnClick": {"action"`

## OuiToolbar
A floating group of tool buttons. Put OuiIconButton, OuiButton and OuiToolbarSeparator blocks in Content.
- Inputs: Label: Text = "Toolbar"
- Placeholders: Content
- Shown on: OuiNavigation
- Example: `{"type": "BlockInstance", "sourceBlock": "OuiToolbar", "args": {"Label": "\"Formatting\""}, "placeholders": {"Content": [{"type": "BlockInstance", "sourceBlock": "OuiIconButton", "args": {"Icon": "\"edit\"", "Label": "\"Edit\""}, "events": {"OnClick": {"action": "Log", "args": {"Message": "\"Toolbar: edit\""}}}}, {"type": "BlockInstance", "sourceBlock": "OuiIconButton", "args": {"Icon": "\"copy\""`

## OuiToolbarSeparator
A thin line between groups in OuiToolbar.
- Inputs: none
- Shown on: OuiNavigation
- Example: `{"type": "BlockInstance", "sourceBlock": "OuiToolbarSeparator"}`

## OuiDocTabs
Document tabs with close buttons, as in an editor. Tabs is comma separated; Dirty lists the unsaved ones.
- Inputs: *Tabs: Text, Active: Text = "", Dirty: Text = ""
- Events: OnSelect(Name: Text), OnClose(Name: Text)
- Shown on: OuiNavigation
- Example: `{"type": "BlockInstance", "sourceBlock": "OuiDocTabs", "args": {"Tabs": "OpenTabs", "Active": "ActiveTab", "Dirty": "\"Q4 report\""}, "events": {"OnSelect": {"action": "SetTab", "args": {"Value": "Name"}}, "OnClose": {"action": "CloseTab", "args": {"Value": "Name"}}}}`

## OuiStatusBar
A thin bar at the bottom of an app. Put OuiStatusItem blocks in Left and Right.
- Inputs: none
- Placeholders: Left, Right
- Shown on: OuiNavigation
- Example: `{"type": "BlockInstance", "sourceBlock": "OuiStatusBar", "placeholders": {"Left": [{"type": "BlockInstance", "sourceBlock": "OuiStatusItem", "args": {"Label": "\"Synced\"", "Icon": "\"check\"", "Tone": "\"success\""}}, {"type": "BlockInstance", "sourceBlock": "OuiStatusItem", "args": {"Label": "\"3 pending transfers\"", "Icon": "\"clock\""}}], "Right": [{"type": "BlockInstance", "sourceBlock": "Ou`

## OuiStatusItem
One item of OuiStatusBar: an icon and a short text, with an optional tone.
- Inputs: *Label: Text, Icon: Text = "", Tone: Text = "neutral"
- Shown on: OuiNavigation
- Example: `{"type": "BlockInstance", "sourceBlock": "OuiStatusItem", "args": {"Label": "\"Synced\"", "Icon": "\"check\"", "Tone": "\"success\""}}`

## OuiPopover
Rich content that opens from a trigger: Trigger, then Content. Esc and outside clicks close it.
- Inputs: Title: Text = "", Side: Text = "bottom", Align: Text = "start", Label: Text = "Open details"
- Placeholders: Trigger, Content
- Shown on: OuiOverlays
- Example: `{"type": "BlockInstance", "sourceBlock": "OuiPopover", "args": {"Title": "\"Exchange rate\"", "Label": "\"Show exchange rate\""}, "placeholders": {"Trigger": [{"type": "Container", "style": "\"oui-btn oui-btn--secondary\"", "children": [{"type": "Container", "style": "\"oui-i oui-i--info\"", "attributes": {"aria-hidden": "\"true\""}}, {"type": "Text", "text": "EUR to GEL"}]}], "Content": [{"type":`

## OuiHoverCard
A preview card that opens when the pointer rests on the trigger (or it gets focus), for people, links and records.
- Inputs: Delay: Integer = 450
- Placeholders: Trigger, Content
- Shown on: OuiOverlays
- Example: `{"type": "BlockInstance", "sourceBlock": "OuiHoverCard", "placeholders": {"Trigger": [{"type": "Container", "style": "\"oui-row\"", "children": [{"type": "BlockInstance", "sourceBlock": "OuiAvatar", "args": {"Name": "\"Mei Chen\"", "Size": "\"sm\"", "Status": "\"online\""}}, {"type": "Expression", "value": "\"Mei Chen\"", "style": "\"oui-choice__label\""}]}], "Content": [{"type": "Container", "sty`

## OuiConfirmPopover
Asks for confirmation next to the button that started the action.
- Inputs: *Message: Text, ConfirmLabel: Text = "Confirm", CancelLabel: Text = "Cancel", IsDanger: Boolean, Label: Text = "Open confirmation"
- Events: OnConfirm()
- Placeholders: Trigger
- Shown on: OuiOverlays
- Example: `{"type": "BlockInstance", "sourceBlock": "OuiConfirmPopover", "args": {"Message": "\"Cancel the scheduled payment of EUR 420.00?\"", "ConfirmLabel": "\"Cancel payment\"", "IsDanger": "True"}, "placeholders": {"Trigger": [{"type": "Container", "style": "\"oui-btn oui-btn--outline\"", "children": [{"type": "Container", "style": "\"oui-i oui-i--close\"", "attributes": {"aria-hidden": "\"true\""}}, {"`

## OuiDialog
A modal dialog with title, Content and Footer. Controlled: the parent opens it with IsOpen and closes it in OnClose. Esc, the close button and the backdrop ask to close; focus stays inside.
- Inputs: IsOpen: Boolean, *Title: Text, Description: Text = "", CloseOnBackdrop: Boolean = True, Width: Integer = 460
- Events: OnClose()
- Placeholders: Content, Footer
- Shown on: OuiOverlays, OuiTplSettings
- Example: `{"type": "BlockInstance", "sourceBlock": "OuiDialog", "args": {"IsOpen": "DialogOpen", "Title": "\"Delete saved payee?\"", "Description": "\"Northwind Traders will be removed from your payees. Past transfers stay in your statements.\""}, "placeholders": {"Footer": [{"type": "BlockInstance", "sourceBlock": "OuiButton", "args": {"Variant": "\"ghost\""}, "placeholders": {"Label": [{"type": "Text", "t`

## OuiSheet
A panel that slides in from a side, for details and forms. Controlled like OuiDialog.
- Inputs: IsOpen: Boolean, *Title: Text, Description: Text = "", CloseOnBackdrop: Boolean = True, Side: Text = "right", Size: Integer = 420
- Events: OnClose()
- Placeholders: Content, Footer
- Shown on: OuiOverlays
- Example: `{"type": "BlockInstance", "sourceBlock": "OuiSheet", "args": {"IsOpen": "SheetOpen", "Title": "\"Transfer details\"", "Description": "\"TRF-2025-10-0412\""}, "placeholders": {"Content": [{"type": "BlockInstance", "sourceBlock": "OuiTimelineItem", "args": {"Title": "\"Created\"", "Time": "\"09:12\"", "Tone": "\"accent\""}}, {"type": "BlockInstance", "sourceBlock": "OuiTimelineItem", "args": {"Title`

## OuiCommandPalette
Ctrl+K search over commands. Commands is comma separated; write Group/Label for a group. OnSelect gets the label.
- Inputs: *Commands: Text, IsOpen: Boolean, Placeholder: Text = "Type a command or search"
- Events: OnSelect(Command: Text), OnOpenChange(IsOpen: Boolean)
- Shown on: OuiHome, OuiOverlays
- Example: `{"type": "BlockInstance", "sourceBlock": "OuiCommandPalette", "args": {"IsOpen": "PaletteOpen", "Commands": "\"Navigate/Accounts,Navigate/Cards,Actions/New transfer,Actions/Freeze card,Help/Shortcuts\""}, "events": {"OnSelect": {"action": "PickCommand", "args": {"Command": "Command"}}, "OnOpenChange": {"action": "SetPalette", "args": {"Value": "IsOpen"}}}}`

## OuiBanner
A full-width message at the top of a page or section, with an optional action.
- Inputs: *Text: Text, Tone: Text = "accent", IsSolid: Boolean, ActionLabel: Text = "", IsDismissible: Boolean
- Events: OnAction(), OnDismiss()
- Shown on: OuiData
- Example: `{"type": "BlockInstance", "sourceBlock": "OuiBanner", "args": {"Text": "\"Scheduled maintenance on Sunday 02:00 to 04:00. Transfers will be queued.\"", "Tone": "\"warning\"", "ActionLabel": "\"Details\"", "IsDismissible": "True"}, "events": {"OnAction": {"action": "Log", "args": {"Message": "\"Banner action\""}}}}`

## OuiCountdown
Days, hours, minutes and seconds to a date and time. OnDone fires at zero.
- Inputs: *Target: Date Time, ShowDays: Boolean = True
- Events: OnDone()
- Shown on: OuiData
- Example: `{"type": "BlockInstance", "sourceBlock": "OuiCountdown", "args": {"Target": "AddDays(CurrDateTime(), 3)"}}`

## OuiMeter
Usage against a limit, for example storage or a card limit. It turns amber at 75% and red at 90%.
- Inputs: Value: Decimal, Max: Decimal = 100, Label: Text = "", ValueText: Text = ""
- Shown on: OuiData
- Example: `{"type": "BlockInstance", "sourceBlock": "OuiMeter", "args": {"Value": "1840", "Max": "2500", "Label": "\"Card limit used\"", "ValueText": "\"EUR 1,840 of 2,500\""}}`

## OuiGauge
A half-circle gauge from 0 to 100, for scores and health.
- Inputs: Value: Decimal, Caption: Text = "", Size: Integer = 160, Tone: Text = "accent"
- Shown on: OuiData
- Example: `{"type": "BlockInstance", "sourceBlock": "OuiGauge", "args": {"Value": "82", "Caption": "\"Credit score\"", "Tone": "\"success\""}}`

## OuiLoader
Small animated loaders: dots, bars, pulse, orbit or grid.
- Inputs: Variant: Text = "dots", Label: Text = "Loading"
- Shown on: OuiData
- Example: `{"type": "BlockInstance", "sourceBlock": "OuiLoader", "args": {"Variant": "\"dots\""}}`

## OuiChecklist
An onboarding checklist with progress. Done lists the finished step numbers, comma separated.
- Inputs: Title: Text = "Get started", Step1: Text = "", Step2: Text = "", Step3: Text = "", Step4: Text = "", Step5: Text = "", Done: Text = ""
- Events: OnStepClick(Step: Integer)
- Uses: OuiProgressBar
- Shown on: OuiData
- Example: `{"type": "BlockInstance", "sourceBlock": "OuiChecklist", "args": {"Title": "\"Finish setting up your account\"", "Step1": "\"Verify your email\"", "Step2": "\"Add a payee\"", "Step3": "\"Turn on two-factor login\"", "Step4": "\"Order a card\"", "Done": "DoneSteps"}, "events": {"OnStepClick": "ToggleStep"}}`

## OuiTableWrap
Styles a TableRecords in Table: hairline rows, sticky header, hover, optional stripes and compact rows.
- Inputs: IsStriped: Boolean, IsCompact: Boolean, MaxHeight: Integer = 0
- Placeholders: Table
- Shown on: OuiData, OuiHome, OuiTplDashboard
- Example: `{"type": "BlockInstance", "sourceBlock": "OuiTableWrap", "args": {"IsCompact": "True", "MaxHeight": "220"}, "placeholders": {"Table": [{"type": "TableRecords", "name": "TopTable", "source": "FetchTop.List", "style": "\"oui-grid-table\"", "headerRow": [{"type": "HeaderCell", "children": [{"type": "Text", "text": "Name"}]}, {"type": "HeaderCell", "children": [{"type": "Text", "text": "Country"}]}, {`

## OuiSortHeader
A column title that sorts: put it in a HeaderCell. SortBy is the parent's current sort column; OnSort gets this Column.
- Inputs: *Label: Text, *Column: Text, SortBy: Text = "", IsDescending: Boolean
- Events: OnSort(Column: Text)
- Shown on: OuiData
- Example: `{"type": "BlockInstance", "sourceBlock": "OuiSortHeader", "args": {"Label": "\"Name\"", "Column": "\"Name\"", "SortBy": "SortBy", "IsDescending": "IsDescending"}, "events": {"OnSort": "SortBy_Changed"}}`

## OuiFilterBar
Above a table: a Search placeholder, filter Chips, a result count and Actions.
- Inputs: CountText: Text = ""
- Placeholders: Search, Chips, Actions
- Shown on: OuiData, OuiTplListDetail
- Example: `{"type": "BlockInstance", "sourceBlock": "OuiFilterBar", "args": {"CountText": "LongIntegerToText(FetchCustomers.Count) + \" customers\""}, "placeholders": {"Search": [{"type": "BlockInstance", "sourceBlock": "OuiSearchField", "placeholders": {"Input": [{"type": "Input", "name": "SearchInput", "variable": "Query", "prompt": "\"Search name or company\"", "onChange": "Search"}]}}], "Chips": [{"type"`

## OuiListItem
A row with Leading (avatar or icon), a title and subtitle, and Trailing (badge, action). Clickable rows fire OnClick.
- Inputs: *Title: Text, Subtitle: Text = "", IsSelected: Boolean, IsClickable: Boolean = True
- Events: OnClick()
- Placeholders: Leading, Trailing
- Shown on: OuiData, OuiTplInbox, OuiTplListDetail
- Example: `{"type": "BlockInstance", "sourceBlock": "OuiListItem", "args": {"Title": "\"Ana Silva\"", "Subtitle": "\"Retail, Lisbon\"", "IsSelected": "Picked = \"Ana Silva\""}, "placeholders": {"Leading": [{"type": "BlockInstance", "sourceBlock": "OuiAvatar", "args": {"Name": "\"Ana Silva\""}}], "Trailing": [{"type": "BlockInstance", "sourceBlock": "OuiBadge", "args": {"Tone": "\"accent\""}, "placeholders": `

## OuiTreeItem
One row of a tree, indented by Depth. Render the visible rows from a flat list; OnToggle opens or closes, OnSelect picks.
- Inputs: *Label: Text, Icon: Text = "folder", Depth: Integer, HasChildren: Boolean, IsOpen: Boolean, IsSelected: Boolean, Meta: Text = ""
- Events: OnSelect(), OnToggle()
- Shown on: OuiData, OuiTools
- Example: `{"type": "BlockInstance", "sourceBlock": "OuiTreeItem", "args": {"Label": "\"Statements\"", "Depth": "0", "HasChildren": "True", "IsOpen": "True", "Icon": "\"folder\"", "Meta": "\"\"", "IsSelected": "Picked = \"Statements\""}, "events": {"OnSelect": {"action": "SetPicked", "args": {"Value": "\"Statements\""}}, "OnToggle": {"action": "Log", "args": {"Message": "\"Toggle Statements\""}}}}`

## OuiExpandableText
Long text cut to a few lines, with Show more and Show less.
- Inputs: *Text: Text, Lines: Integer = 3
- Shown on: OuiData
- Example: `{"type": "BlockInstance", "sourceBlock": "OuiExpandableText", "args": {"Text": "\"Transfers to accounts in the same bank arrive at once. Transfers to other banks in the country arrive the same day if sent before 17:00. International transfers take one to three working days and the receiving bank can charge a fee. You can follow every transfer in Activity, and download a confirmation as PDF.\""}}`

## OuiExpandableCard
A card that opens to show Content, with a smooth height animation.
- Inputs: *Title: Text, Summary: Text = "", Icon: Text = "", IsOpen: Boolean
- Placeholders: Content
- Shown on: OuiData
- Example: `{"type": "BlockInstance", "sourceBlock": "OuiExpandableCard", "args": {"Title": "\"Fees and limits\"", "Summary": "\"What a transfer costs\"", "Icon": "\"info\""}, "placeholders": {"Content": [{"type": "Text", "text": "Domestic transfers are free. International transfers cost EUR 4.90. Your daily limit is EUR 10,000."}]}}`

## OuiReorderList
Drag rows by their handle (or use the arrow keys on it) to reorder. Put OuiReorderItem blocks in Items; OnReorder gets the keys in the new order.
- Inputs: none
- Events: OnReorder(Keys: Text)
- Placeholders: Items
- Shown on: OuiData
- Example: `{"type": "BlockInstance", "sourceBlock": "OuiReorderList", "placeholders": {"Items": [{"type": "BlockInstance", "sourceBlock": "OuiReorderItem", "args": {"Key": "\"Salary account\""}, "placeholders": {"Content": [{"type": "Text", "text": "Salary account"}]}}, {"type": "BlockInstance", "sourceBlock": "OuiReorderItem", "args": {"Key": "\"Savings\""}, "placeholders": {"Content": [{"type": "Text", "te`

## OuiReorderItem
One row of OuiReorderList: a drag handle and Content.
- Inputs: *Key: Text, Label: Text = "Drag to reorder"
- Placeholders: Content
- Shown on: OuiData
- Example: `{"type": "BlockInstance", "sourceBlock": "OuiReorderItem", "args": {"Key": "\"Salary account\""}, "placeholders": {"Content": [{"type": "Text", "text": "Salary account"}]}}`

## OuiSwipeItem
A row that slides left to show Actions (touch or mouse). Put buttons in Actions.
- Inputs: none
- Placeholders: Actions, Content
- Shown on: OuiData
- Example: `{"type": "BlockInstance", "sourceBlock": "OuiSwipeItem", "placeholders": {"Content": [{"type": "BlockInstance", "sourceBlock": "OuiListItem", "args": {"Title": "\"Netflix subscription\"", "Subtitle": "\"Monthly, EUR 13.99\"", "IsClickable": "False"}}], "Actions": [{"type": "BlockInstance", "sourceBlock": "OuiButton", "args": {"Variant": "\"danger\"", "Icon": "\"trash\""}, "placeholders": {"Label":`

## OuiContextMenu
Right-click (or Shift+F10) on Target opens a menu of OuiMenuItem blocks at the pointer.
- Inputs: none
- Placeholders: Target, Items
- Shown on: OuiData
- Example: `{"type": "BlockInstance", "sourceBlock": "OuiContextMenu", "placeholders": {"Target": [{"type": "Container", "style": "\"oui-context-demo\"", "children": [{"type": "Text", "text": "Right-click this card, or focus it and press Shift+F10"}]}], "Items": [{"type": "BlockInstance", "sourceBlock": "OuiMenuItem", "args": {"Label": "\"Open\"", "Icon": "\"eye\""}, "events": {"OnClick": {"action": "Log", "a`

## OuiInspectorGroup
A folding group of OuiPropertyRow blocks, for settings and property panels.
- Inputs: *Title: Text, IsOpen: Boolean = True
- Placeholders: Rows
- Shown on: OuiData
- Example: `{"type": "BlockInstance", "sourceBlock": "OuiInspectorGroup", "args": {"Title": "\"Layout\""}, "placeholders": {"Rows": [{"type": "BlockInstance", "sourceBlock": "OuiPropertyRow", "args": {"Label": "\"Opacity\""}, "placeholders": {"Control": [{"type": "BlockInstance", "sourceBlock": "OuiScrubField", "args": {"Label": "\"%\"", "Value": "Opacity", "Max": "100"}, "events": {"OnChange": "SetOpacity"}}`

## OuiPropertyRow
A label and a Control (Input, Dropdown, Switch, OuiScrubField, OuiColorPicker) in an inspector.
- Inputs: *Label: Text
- Placeholders: Control
- Shown on: OuiData, OuiHome, OuiTools
- Example: `{"type": "BlockInstance", "sourceBlock": "OuiPropertyRow", "args": {"Label": "\"Radius\""}, "placeholders": {"Control": [{"type": "BlockInstance", "sourceBlock": "OuiSlider", "args": {"Value": "Radius", "Max": "28", "Suffix": "\" px\""}, "events": {"OnChange": {"action": "SetRadius", "args": {"Value": "Value"}}}}]}}`

## OuiLogConsole
Log lines with level colours (ERROR, WARN, DEBUG), a filter, and follow-the-end scrolling. Text is newline separated.
- Inputs: *Text: Text, Title: Text = "Log", Height: Integer = 240, Follow: Boolean = True
- Shown on: OuiData
- Example: `{"type": "BlockInstance", "sourceBlock": "OuiLogConsole", "args": {"Text": "\"2025-10-14 09:12:01 INFO  Statement import started\n2025-10-14 09:12:03 DEBUG Reading 1,284 rows\n2025-10-14 09:12:05 WARN  3 rows have no IBAN\n2025-10-14 09:12:06 ERROR Row 1021: amount is not a number\n2025-10-14 09:12:07 INFO  Import finished: 1,280 rows, 1 error\"", "Title": "\"import.log\""}}`

## OuiCodeBlock
Read-only code with light highlighting, line numbers and a copy button.
- Inputs: *Code: Text, Language: Text = "", ShowLineNumbers: Boolean = True, Title: Text = "", MaxHeight: Integer = 420
- Uses: OuiCopyButton
- Shown on: OuiCode
- Example: `{"type": "BlockInstance", "sourceBlock": "OuiCodeBlock", "args": {"Code": "\"SELECT c.Name, SUM(t.Amount) AS Total\nFROM {OuiDemoCustomer} c\nJOIN {OuiDemoTransaction} t ON t.CustomerId = c.Id\nWHERE t.BookedOn >= @From -- this month\nGROUP BY c.Name\nORDER BY Total DESC\"", "Language": "\"SQL\"", "Title": "\"TopCustomers.sql\""}}`

## OuiCodeEditor
A plain code editor: monospace, line numbers, Tab inserts spaces. OnChange fires when it loses focus.
- Inputs: Value: Text = "", Height: Integer = 260, Label: Text = "Code"
- Events: OnChange(Value: Text)
- Shown on: OuiCode
- Example: `{"type": "BlockInstance", "sourceBlock": "OuiCodeEditor", "args": {"Value": "EditorText", "Height": "200"}, "events": {"OnChange": "SetEditor"}}`

## OuiDiffViewer
Line differences between two texts: added lines green, removed lines red.
- Inputs: *Before: Text, *After: Text, Mode: Text = "inline"
- Shown on: OuiCode
- Example: `{"type": "BlockInstance", "sourceBlock": "OuiDiffViewer", "args": {"Before": "\"function fee(amount) {\n    if (amount > 1000) {\n        return 4.90;\n    }\n    return 0;\n}\"", "After": "\"function fee(amount, international) {\n    if (!international) {\n        return 0;\n    }\n    return amount > 1000 ? 4.90 : 2.50;\n}\""}}`

## OuiMarkdownView
Markdown (GitHub style) shown safely, built as DOM with no raw HTML: headings, paragraphs, bold, italic, strike, code, code blocks with language, highlighting and Copy, tables with alignment, nested and task lists, quotes, GitHub alerts, footnotes, rules, math ($x$, $$x$$), mermaid diagrams, downloadable files (a fence named file:NAME, or a paragraph that is only a link to a file) and links (http, https, mailto only). Blocks nest inside lists and quotes. Images are shown as links and never loaded. Put data-oui-sources on a parent to show source chips. Use it for AI answers.
- Inputs: *Markdown: Text
- Shown on: OuiCode
- Example: `{"type": "BlockInstance", "sourceBlock": "OuiMarkdownView", "args": {"Markdown": "\"### Release notes\n\nVersion **2.4** brings *faster* exports and a new `Statements` page.\n\n- Export to PDF and CSV\n- [x] Dark mode\n- [ ] Card freeze from the app\n\n> Questions? See the [help centre](https://example.com/help).\""}}`

## OuiJsonViewer
A JSON text as a tree you can open and close.
- Inputs: *Json: Text, ExpandDepth: Integer = 2
- Shown on: OuiCode
- Example: `{"type": "BlockInstance", "sourceBlock": "OuiJsonViewer", "args": {"Json": "\"{\"\"customer\"\": {\"\"name\"\": \"\"Ana Silva\"\", \"\"iban\"\": \"\"GE29NB0000000101904917\"\", \"\"active\"\": true, \"\"limits\"\": {\"\"daily\"\": 10000, \"\"monthly\"\": 50000}}, \"\"cards\"\": [{\"\"type\"\": \"\"debit\"\", \"\"last4\"\": \"\"4821\"\"}, {\"\"type\"\": \"\"credit\"\", \"\"last4\"\": \"\"1177\"\"}]`

## OuiHexViewer
Bytes of a text (UTF-8) as offset, hex and characters.
- Inputs: *Text: Text
- Shown on: OuiCode
- Example: `{"type": "BlockInstance", "sourceBlock": "OuiHexViewer", "args": {"Text": "\"IBAN GE29 NB00 0000 0101 9049 17\""}}`

## OuiLineChart
Lines over time with a hover guide; IsArea fills under them, IsSmooth curves them.
- Inputs: *Labels: Text, *Series: Text, IsArea: Boolean = False, IsSmooth: Boolean = True, Title: Text = "", Subtitle: Text = "", Height: Integer = 240
- Shown on: OuiCharts, OuiHome, OuiTplDashboard
- Example: `{"type": "BlockInstance", "sourceBlock": "OuiLineChart", "args": {"IsArea": "True", "IsSmooth": "True", "Height": "200", "Labels": "\"Mon,Tue,Wed,Thu,Fri,Sat,Sun\"", "Series": "\"Balance:1200,1340,1280,1510,1490,1620,1700\""}}`

## OuiBarChart
Grouped or stacked bars, vertical or horizontal.
- Inputs: *Labels: Text, *Series: Text, IsStacked: Boolean = False, IsHorizontal: Boolean = False, Title: Text = "", Subtitle: Text = "", Height: Integer = 240
- Shown on: OuiCharts
- Example: `{"type": "BlockInstance", "sourceBlock": "OuiBarChart", "args": {"Title": "\"Spending by category\"", "Labels": "\"Q1,Q2,Q3,Q4\"", "Series": "\"Rent:2700,2700,2800,2800|Food:1100,1250,1180,1320|Travel:300,900,1400,500\"", "IsStacked": "True"}}`

## OuiDonutChart
Parts of a whole, with a value in the middle. IsPie fills the centre.
- Inputs: *Labels: Text, *Values: Text, CenterValue: Text = "", CenterCaption: Text = "", IsPie: Boolean = False, Title: Text = "", Subtitle: Text = "", Height: Integer = 220
- Shown on: OuiCharts, OuiTplDashboard
- Example: `{"type": "BlockInstance", "sourceBlock": "OuiDonutChart", "args": {"Title": "\"Portfolio\"", "Labels": "\"Bonds,Equity,Cash,Gold\"", "Values": "\"42,35,15,8\"", "CenterValue": "\"EUR 48k\"", "CenterCaption": "\"total\""}}`

## OuiSparkline
A tiny trend line for tables and metrics.
- Inputs: *Values: Text, Title: Text = "", Subtitle: Text = "", Height: Integer = 36
- Shown on: OuiCharts
- Example: `{"type": "BlockInstance", "sourceBlock": "OuiSparkline", "args": {"Values": "\"2.91,2.93,2.92,2.95,2.94,2.97,2.96\""}}`

## OuiHeatmap
A grid of values; darker is more. Values are row by row.
- Inputs: *Rows: Text, *Columns: Text, *Values: Text, Title: Text = "", Subtitle: Text = "", Height: Integer = 240
- Shown on: OuiCharts
- Example: `{"type": "BlockInstance", "sourceBlock": "OuiHeatmap", "args": {"Title": "\"Transfers by hour\"", "Rows": "\"Mon,Tue,Wed,Thu,Fri\"", "Columns": "\"8,10,12,14,16,18\"", "Values": "\"17,13,21,28,20,22,18,4,20,26,17,9,14,21,24,23,8,30,10,14,9,17,10,18,11,1,26,14,19,11\""}}`

## OuiCalendarHeatmap
Activity per day over the last weeks. Data is date:value pairs.
- Inputs: *Data: Text, Weeks: Integer = 26, EndDate: Date, Title: Text = "", Subtitle: Text = "", Height: Integer = 140
- Shown on: OuiCharts
- Example: `{"type": "BlockInstance", "sourceBlock": "OuiCalendarHeatmap", "args": {"Title": "\"Card activity\"", "Data": "\"2025-05-01:0,2025-05-02:3,2025-05-03:3,2025-05-04:0,2025-05-05:1,2025-05-06:3,2025-05-07:2,2025-05-08:5,2025-05-09:3,2025-05-10:0,2025-05-11:3,2025-05-12:0,2025-05-13:8,2025-05-14:2,2025-05-15:1,2025-05-16:3,2025-05-17:0,2025-05-18:0,2025-05-19:5,2025-05-20:2,2025-05-21:3,2025-05-22:8,2`

## OuiTreemap
Rectangles sized by value, for shares of many parts.
- Inputs: *Labels: Text, *Values: Text, Title: Text = "", Subtitle: Text = "", Height: Integer = 240
- Shown on: OuiCharts
- Example: `{"type": "BlockInstance", "sourceBlock": "OuiTreemap", "args": {"Title": "\"Deposits by branch\"", "Labels": "\"Tbilisi,Batumi,Kutaisi,Rustavi,Zugdidi,Telavi\"", "Values": "\"48,21,12,9,6,4\""}}`

## OuiRadarChart
Several measures on one shape, to compare profiles.
- Inputs: *Axes: Text, *Series: Text, Title: Text = "", Subtitle: Text = "", Height: Integer = 300
- Shown on: OuiCharts
- Example: `{"type": "BlockInstance", "sourceBlock": "OuiRadarChart", "args": {"Title": "\"Customer profile\"", "Axes": "\"Savings,Cards,Loans,Digital,Investments,Insurance\"", "Series": "\"Ana:80,60,30,95,40,55|Average:55,50,45,60,35,40\""}}`

## OuiFunnel
Steps that lose people, with the conversion to the first step.
- Inputs: *Labels: Text, *Values: Text, Title: Text = "", Subtitle: Text = "", Height: Integer = 240
- Shown on: OuiCharts
- Example: `{"type": "BlockInstance", "sourceBlock": "OuiFunnel", "args": {"Title": "\"Loan applications\"", "Labels": "\"Started,Documents,Approved,Signed\"", "Values": "\"1200,780,420,355\""}}`

## OuiSankey
Flows from sources to targets. Links are Source>Target:value.
- Inputs: *Links: Text, Title: Text = "", Subtitle: Text = "", Height: Integer = 260
- Shown on: OuiCharts
- Example: `{"type": "BlockInstance", "sourceBlock": "OuiSankey", "args": {"Title": "\"Where the salary goes\"", "Links": "\"Salary>Rent:900,Salary>Food:420,Salary>Savings:600,Salary>Other:380,Freelance>Savings:300,Freelance>Travel:250\""}}`

## OuiScatterChart
Points by two values; a third value sizes bubbles. Points are x:y[:size[:series]].
- Inputs: *Points: Text, Title: Text = "", Subtitle: Text = "", Height: Integer = 240
- Shown on: OuiCharts
- Example: `{"type": "BlockInstance", "sourceBlock": "OuiScatterChart", "args": {"Title": "\"Risk and return\"", "Points": "\"4:2:3,6:3.5:5,9:5:8:1,12:7:4:1,15:6:9:2,3:1:2,8:4:6:2,11:8:7:1\""}}`

## OuiActivityRings
Concentric rings for goals in percent.
- Inputs: *Labels: Text, *Values: Text, Title: Text = "", Subtitle: Text = "", Height: Integer = 200
- Shown on: OuiCharts
- Example: `{"type": "BlockInstance", "sourceBlock": "OuiActivityRings", "args": {"Labels": "\"Savings,Budget,Steps\"", "Values": "\"82,64,45\"", "Height": "160"}}`

## OuiBarList
A ranked list with a bar behind each row.
- Inputs: *Labels: Text, *Values: Text, Title: Text = "", Subtitle: Text = "", Height: Integer = 240
- Shown on: OuiCharts
- Example: `{"type": "BlockInstance", "sourceBlock": "OuiBarList", "args": {"Title": "\"Logins by channel\"", "Labels": "\"Mobile app,Web,Branch,Phone\"", "Values": "\"8420,3110,640,210\""}}`

## OuiSplitBar
One bar split into parts, with a legend in percent.
- Inputs: *Labels: Text, *Values: Text, Title: Text = "", Subtitle: Text = "", Height: Integer = 240
- Shown on: OuiCharts
- Example: `{"type": "BlockInstance", "sourceBlock": "OuiSplitBar", "args": {"Title": "\"Account mix\"", "Labels": "\"Current,Savings,Deposits\"", "Values": "\"55,30,15\""}}`

## OuiWaffle
A 10 by 10 grid that shows one percentage.
- Inputs: *Value: Decimal, Title: Text = "", Subtitle: Text = "", Height: Integer = 240
- Shown on: OuiCharts
- Example: `{"type": "BlockInstance", "sourceBlock": "OuiWaffle", "args": {"Title": "\"Goal reached\"", "Value": "68"}}`

## OuiKanbanBoard
A board of OuiKanbanColumn blocks (in Columns). Drag a card to another column, or press Alt+Arrow on a focused card; OnMove gets the card and column keys.
- Inputs: none
- Events: OnMove(CardKey: Text, ColumnKey: Text)
- Placeholders: Columns
- Shown on: OuiPlanning
- Example: `{"type": "BlockInstance", "sourceBlock": "OuiKanbanBoard", "placeholders": {"Columns": [{"type": "BlockInstance", "sourceBlock": "OuiKanbanColumn", "args": {"Key": "\"1\"", "Title": "\"Backlog\"", "Tone": "\"neutral\""}, "placeholders": {"Cards": [{"type": "List", "name": "CardsBacklog", "source": "FetchTasks.List", "style": "\"oui-contents\"", "children": [{"type": "Container", "style": "\"oui-co`

## OuiKanbanColumn
One column of OuiKanbanBoard. Put OuiKanbanCard blocks in Cards.
- Inputs: *Key: Text, *Title: Text, Count: Text = "", Tone: Text = "neutral"
- Placeholders: Cards
- Shown on: OuiPlanning
- Example: `{"type": "BlockInstance", "sourceBlock": "OuiKanbanColumn", "args": {"Key": "\"1\"", "Title": "\"Backlog\"", "Tone": "\"neutral\""}, "placeholders": {"Cards": [{"type": "List", "name": "CardsBacklog", "source": "FetchTasks.List", "style": "\"oui-contents\"", "children": [{"type": "Container", "style": "\"oui-contents\"", "children": [{"type": "BlockInstance", "sourceBlock": "OuiKanbanCard", "args"`

## OuiKanbanCard
A card on the board: title, priority, due date, progress and assignee.
- Inputs: *Key: Text, *Title: Text, Priority: Text = "", Due: Text = "", Assignee: Text = "", Progress: Integer = -1
- Uses: OuiAvatar, OuiBadge
- Shown on: OuiPlanning
- Example: `{"type": "BlockInstance", "sourceBlock": "OuiKanbanCard", "args": {"Key": "LongIntegerToText(FetchTasks.List.Current.OuiDemoTask.Id)", "Title": "FetchTasks.List.Current.OuiDemoTask.Title", "Priority": "FetchTasks.List.Current.OuiDemoPriority.Label", "Due": "If(FetchTasks.List.Current.OuiDemoTask.DueDate = NullDate(), \"\", FormatDateTime(NewDateTime(Year(FetchTasks.List.Current.OuiDemoTask.DueDate`

## OuiWeekCalendar
A week grid with events and a now line. Events: Day(1-7)|Start|End|Title|Tone separated by ;.
- Inputs: *Events: Text, StartHour: Integer = 8, EndHour: Integer = 18, Days: Text = "Mon,Tue,Wed,Thu,Fri"
- Events: OnEventClick(Index: Integer)
- Shown on: OuiPlanning
- Example: `{"type": "BlockInstance", "sourceBlock": "OuiWeekCalendar", "args": {"Events": "\"1|09:00|09:30|Stand-up|accent;1|13:00|14:30|Credit committee|warning;2|10:00|11:00|Customer call: Northwind|info;3|09:00|09:30|Stand-up|accent;3|15:00|16:30|Release review|success;4|11:00|12:00|Risk workshop|danger;5|09:00|09:30|Stand-up|accent;5|14:00|15:00|Demo|success\""}, "events": {"OnEventClick": {"action": "Lo`

## OuiGantt
Tasks on a timeline with progress, weekends, today and dependencies. Tasks: Name|Start|End|Progress|DependsOn separated by ;.
- Inputs: *Tasks: Text
- Shown on: OuiPlanning
- Example: `{"type": "BlockInstance", "sourceBlock": "OuiGantt", "args": {"Tasks": "\"Discovery|2026-10-26|2026-11-04|100;Design|2026-11-02|2026-11-13|70|1;Build|2026-11-09|2026-11-27|35|2;Security review|2026-11-23|2026-11-30|0|3;Launch|2026-12-01|2026-12-03|0|4\""}}`

## OuiNodeEditor
Nodes you drag, wires you draw from an output to an input, pan and zoom. Nodes: id|label|x|y|kind; links: a>b.
- Inputs: *Nodes: Text, Links: Text = "", Height: Integer = 420
- Events: OnChange(Nodes: Text, Links: Text)
- Shown on: OuiHome, OuiPlanning
- Example: `{"type": "BlockInstance", "sourceBlock": "OuiNodeEditor", "args": {"Nodes": "\"t|Payment|16|24|trigger;c|Over 1,000?|16|124|condition;a|Approve|196|24|action;b|Book|196|124|action\"", "Links": "\"t>c;c>a;c>b\"", "Height": "220"}}`

## OuiChatThread
A scrolling list of messages. Put OuiChatMessage blocks in Messages; it follows new messages.
- Inputs: Height: Integer = 420
- Placeholders: Messages
- Shown on: OuiChat, OuiHome, OuiTplInbox
- Example: `{"type": "BlockInstance", "sourceBlock": "OuiChatThread", "args": {"Height": "220"}, "placeholders": {"Messages": [{"type": "BlockInstance", "sourceBlock": "OuiChatMessage", "args": {"Author": "\"Ana\"", "Text": "\"Did the transfer to Lisbon go through?\"", "Time": "\"09:12\""}}, {"type": "BlockInstance", "sourceBlock": "OuiChatMessage", "args": {"Author": "\"You\"", "Text": "\"Yes, booked this mo`

## OuiChatMessage
One chat bubble: mine on the right in the accent, theirs on the left with an avatar. Extra holds reactions or attachments.
- Inputs: *Author: Text, *Text: Text, Time: Text = "", IsMine: Boolean, Status: Text = ""
- Placeholders: Extra
- Uses: OuiAvatar
- Shown on: OuiChat, OuiHome, OuiTplInbox
- Example: `{"type": "BlockInstance", "sourceBlock": "OuiChatMessage", "args": {"Author": "\"Ana\"", "Text": "\"Did the transfer to Lisbon go through?\"", "Time": "\"09:12\""}}`

## OuiTypingIndicator
Three bouncing dots while the other side writes.
- Inputs: Label: Text = "Typing"
- Shown on: OuiChat
- Example: `{"type": "BlockInstance", "sourceBlock": "OuiTypingIndicator"}`

## OuiChatComposer
Message box: Enter sends, Shift+Enter adds a line, it grows with the text. Tools holds attach or model buttons.
- Inputs: Placeholder: Text = "Write a message", IsBusy: Boolean, Hint: Text = "Enter to send, Shift+Enter for a new line"
- Events: OnSend(Text: Text), OnStop()
- Placeholders: Tools
- Shown on: OuiChat, OuiHome, OuiTplInbox
- Example: `{"type": "BlockInstance", "sourceBlock": "OuiChatComposer", "args": {"Placeholder": "\"Reply\""}, "events": {"OnSend": {"action": "SendChat", "args": {"Text": "Text"}}}}`

## OuiComment
A comment with author, time, text, Actions (reply, like) and nested Replies.
- Inputs: *Author: Text, *Text: Text, Time: Text = ""
- Placeholders: Actions, Replies
- Uses: OuiAvatar
- Shown on: OuiChat
- Example: `{"type": "BlockInstance", "sourceBlock": "OuiComment", "args": {"Author": "\"Mei Chen\"", "Time": "\"2 hours ago\"", "Text": "\"The fee table on page 3 still shows last year's rates.\""}, "placeholders": {"Actions": [{"type": "BlockInstance", "sourceBlock": "OuiButton", "args": {"Variant": "\"ghost\"", "Size": "\"sm\""}, "placeholders": {"Label": [{"type": "Text", "text": "Reply"}]}}, {"type": "Bl`

## OuiPresence
Who is here: up to 4 avatars and a count of the rest. Names is comma separated.
- Inputs: *Names: Text, Label: Text = "viewing"
- Uses: OuiAvatar
- Shown on: OuiChat
- Example: `{"type": "BlockInstance", "sourceBlock": "OuiPresence", "args": {"Names": "\"Ana Silva,Mei Chen,Omar Haddad,Lena Novak,Lucas Martin,Nino Kapanadze\""}}`

## OuiCounter
A number with minus and plus, for quantities.
- Inputs: Value: Integer, Min: Integer = 0, Max: Integer = 99, Label: Text = "Quantity"
- Events: OnChange(Value: Integer)
- Shown on: OuiChat
- Example: `{"type": "BlockInstance", "sourceBlock": "OuiCounter", "args": {"Value": "Quantity", "Label": "\"Cards\""}, "events": {"OnChange": "SetQuantity"}}`

## OuiTicker
A gently scrolling line of items, for rates or news. It pauses on hover. Items are separated by |.
- Inputs: *Items: Text, Seconds: Integer = 30
- Shown on: OuiChat
- Example: `{"type": "BlockInstance", "sourceBlock": "OuiTicker", "args": {"Items": "\"EUR/GEL 2.94 +0.2%|USD/GEL 2.71 -0.1%|GBP/GEL 3.45 +0.4%|Gold 2,412 +0.8%|BTC 68,200 -1.2%|Brent 82.4 +0.3%\""}}`

## OuiReasoning
The model's thinking, folded by default, with a shimmer while it streams.
- Inputs: *Text: Text, Title: Text = "Thought for a few seconds", IsStreaming: Boolean, IsOpen: Boolean
- Shown on: OuiAssistant
- Example: `{"type": "BlockInstance", "sourceBlock": "OuiReasoning", "args": {"Text": "\"The user asks about the card limit. I need the current limit and this month's card payments. I will read the card settings and sum the payments since the 1st.\"", "IsStreaming": "AgentState = \"thinking\""}}`

## OuiToolCall
A tool the agent called: name, status, and the input and output on demand.
- Inputs: *Name: Text, Status: Text = "done", Input: Text = "", Output: Text = ""
- Uses: OuiBadge, OuiSpinner
- Shown on: OuiAssistant
- Example: `{"type": "BlockInstance", "sourceBlock": "OuiToolCall", "args": {"Name": "\"get_card_limits\"", "Input": "\"{ \"\"card\"\": \"\"4821\"\" }\"", "Output": "\"{ \"\"monthly\"\": 2500, \"\"used\"\": 1840 }\"", "Status": "If(AgentState = \"working\", \"running\", \"done\")"}}`

## OuiAgentPlan
The agent's plan: done, running, pending or failed steps. Steps: label|status separated by ;.
- Inputs: *Steps: Text
- Shown on: OuiAssistant
- Example: `{"type": "BlockInstance", "sourceBlock": "OuiAgentPlan", "args": {"Steps": "PlanSteps"}}`

## OuiAgentStatus
A small live status of an agent: idle, thinking, working, done or error.
- Inputs: Status: Text = "idle", Text: Text = ""
- Shown on: OuiAssistant
- Example: `{"type": "BlockInstance", "sourceBlock": "OuiAgentStatus", "args": {"Status": "AgentState"}}`

## OuiCitation
A small numbered source marker in a text; hover or focus shows the source.
- Inputs: *Number: Integer, *Title: Text, Url: Text = ""
- Uses: OuiTooltip
- Shown on: OuiAssistant
- Example: `{"type": "BlockInstance", "sourceBlock": "OuiCitation", "args": {"Number": "1", "Title": "\"Card settings, updated today\""}}`

## OuiSourceList
Sources as cards. Sources: title|url|domain separated by ;.
- Inputs: *Sources: Text
- Shown on: OuiAssistant
- Example: `{"type": "BlockInstance", "sourceBlock": "OuiSourceList", "args": {"Sources": "\"Card settings|https://example.com/cards|example.com;Card transactions, October|https://example.com/statements|example.com\""}}`

## OuiModelPicker
Pick a model from a short menu. Models is comma separated; Value is the current one.
- Inputs: *Models: Text, Value: Text = ""
- Events: OnChange(Model: Text)
- Shown on: OuiAssistant
- Example: `{"type": "BlockInstance", "sourceBlock": "OuiModelPicker", "args": {"Models": "\"Fast,Balanced,Deep\"", "Value": "Model"}, "events": {"OnChange": {"action": "SetModel", "args": {"Value": "Model"}}}}`

## OuiAttach
Attach files to a chat message: a paperclip button for the Tools placeholder of OuiChatComposer; files can also be dropped on the composer or pasted. Chips show above the text. The files stay in the browser until JavaScript calls OUI.attach.take(), which returns [{name, mime, kind (image, pdf or text), data}] and clears them.
- Inputs: Accept: Text = "image/*,.pdf,.txt,.md,.csv,.json,.xml,.log", MaxMb: Integer = 5, MaxFiles: Integer = 5

## OuiPromptSuggestions
Suggested prompts as buttons. Items is separated by |.
- Inputs: *Items: Text
- Events: OnPick(Prompt: Text)
- Shown on: OuiAssistant
- Example: `{"type": "BlockInstance", "sourceBlock": "OuiPromptSuggestions", "args": {"Items": "\"What did I spend on travel this year?|Why was my payment declined?|Set up a monthly savings transfer|Explain the new fees\""}, "events": {"OnPick": {"action": "SetPrompt", "args": {"Value": "Prompt"}}}}`

## OuiResponseActions
Copy, good, bad and regenerate under an answer.
- Inputs: *Text: Text, Rating: Integer
- Events: OnRate(Rating: Integer), OnRegenerate()
- Uses: OuiCopyButton
- Shown on: OuiAssistant
- Example: `{"type": "BlockInstance", "sourceBlock": "OuiResponseActions", "args": {"Text": "\"Your card limit is EUR 2,500 per month and you have used EUR 1,840 so far \"", "Rating": "Rating"}, "events": {"OnRate": {"action": "SetRating", "args": {"Value": "Rating"}}}}`

## OuiContextMeter
How much of the model's context is used: a small ring and the count.
- Inputs: Used: Integer, Max: Integer = 200000
- Shown on: OuiAssistant
- Example: `{"type": "BlockInstance", "sourceBlock": "OuiContextMeter", "args": {"Used": "48200"}}`

## OuiSelectionAsk
Select text anywhere in Scope and a small bar offers Send to AI and quick actions. OnSend gets the text, the action and TargetId: the nearest data-ai-target (else id) around the selection, so the answer can be written back there. Put it once on a screen or in the layout.
- Inputs: Scope: Text = "", Label: Text = "Ask AI", Actions: Text = "Explain,Rewrite,Summarize", MinLength: Integer = 2
- Events: OnSend(Text: Text, Action: Text, TargetId: Text)
- Shown on: OuiAssistant
- Example: `{"type": "BlockInstance", "sourceBlock": "OuiSelectionAsk", "args": {"Scope": "\".oui-page\"", "Actions": "\"Explain,Rewrite,Summarize\""}, "events": {"OnSend": {"action": "Asked", "args": {"Text": "Text", "Action": "Action", "TargetId": "TargetId"}}}}`

## OuiVoiceOrb
An animated orb for voice: idle, listening, thinking or speaking.
- Inputs: State: Text = "idle", Size: Integer = 120
- Shown on: OuiAssistant
- Example: `{"type": "BlockInstance", "sourceBlock": "OuiVoiceOrb", "args": {"State": "Voice"}}`

## OuiTypewriter
Text typed letter by letter with a blinking caret. Loop deletes and types again.
- Inputs: *Text: Text, Speed: Integer = 45, Loop: Boolean
- Shown on: OuiMotion
- Example: `{"type": "BlockInstance", "sourceBlock": "OuiTypewriter", "args": {"Text": "\"Banking that feels calm.\"", "Loop": "True"}}`

## OuiTextReveal
Words that fade and rise in one after another.
- Inputs: *Text: Text
- Shown on: OuiMotion
- Example: `{"type": "BlockInstance", "sourceBlock": "OuiTextReveal", "args": {"Text": "\"Every transfer is checked, encrypted and confirmed in real time, so you always know where your money is.\""}}`

## OuiWordRotate
One word that changes every few seconds, for headlines. Words is comma separated.
- Inputs: *Words: Text, Interval: Integer = 2200
- Shown on: OuiMotion
- Example: `{"type": "BlockInstance", "sourceBlock": "OuiWordRotate", "args": {"Words": "\"rent,friends,bills,abroad\""}}`

## OuiShimmerText
Text with a light that passes over it, for loading or thinking states.
- Inputs: *Text: Text
- Shown on: OuiMotion
- Example: `{"type": "BlockInstance", "sourceBlock": "OuiShimmerText", "args": {"Text": "\"Preparing your statement\""}}`

## OuiGradientText
Text filled with a slowly moving accent gradient, for one headline per page.
- Inputs: *Text: Text, IsDisplay: Boolean = True
- Shown on: OuiMotion
- Example: `{"type": "BlockInstance", "sourceBlock": "OuiGradientText", "args": {"Text": "\"Your money, beautifully organised\""}}`

## OuiRollingNumber
A number whose digits roll to the new value, for totals and counters. Value is formatted text.
- Inputs: *Value: Text
- Shown on: OuiMotion
- Example: `{"type": "BlockInstance", "sourceBlock": "OuiRollingNumber", "args": {"Value": "FormatDecimal(Balance, 2, \".\", \",\")"}}`

## OuiSpotlightCard
A card lit where the pointer is, with a glowing edge. Put content in Content.
- Inputs: none
- Placeholders: Content
- Shown on: OuiMotion
- Example: `{"type": "BlockInstance", "sourceBlock": "OuiSpotlightCard", "placeholders": {"Content": [{"type": "BlockInstance", "sourceBlock": "OuiMetric", "args": {"Label": "\"Savings\"", "Value": "\"EUR 18,240\"", "Caption": "\"+4.2% this year\""}}]}}`

## OuiDock
Icons that grow as the pointer passes, as in macOS. Put OuiDockItem blocks in Items.
- Inputs: none
- Placeholders: Items
- Shown on: OuiMotion
- Example: `{"type": "BlockInstance", "sourceBlock": "OuiDock", "placeholders": {"Items": [{"type": "BlockInstance", "sourceBlock": "OuiDockItem", "args": {"Icon": "\"home\"", "Label": "\"Home\"", "IsActive": "True"}, "events": {"OnClick": {"action": "Log", "args": {"Message": "\"Dock: Home\""}}}}, {"type": "BlockInstance", "sourceBlock": "OuiDockItem", "args": {"Icon": "\"send\"", "Label": "\"Pay\"", "IsActi`

## OuiDockItem
One icon of OuiDock.
- Inputs: *Icon: Text, *Label: Text, IsActive: Boolean
- Events: OnClick()
- Shown on: OuiMotion
- Example: `{"type": "BlockInstance", "sourceBlock": "OuiDockItem", "args": {"Icon": "\"home\"", "Label": "\"Home\"", "IsActive": "True"}, "events": {"OnClick": {"action": "Log", "args": {"Message": "\"Dock: Home\""}}}}`

## OuiControlTile
A big square on and off tile, as in a control centre.
- Inputs: *Label: Text, *Icon: Text, IsOn: Boolean, OnText: Text = "On", OffText: Text = "Off"
- Events: OnChange(IsOn: Boolean)
- Shown on: OuiMotion
- Example: `{"type": "BlockInstance", "sourceBlock": "OuiControlTile", "args": {"Label": "\"Card frozen\"", "Icon": "\"lock\"", "IsOn": "Frozen", "OnText": "\"Frozen\"", "OffText": "\"Active\""}, "events": {"OnChange": {"action": "SetFrozen", "args": {"Value": "IsOn"}}}}`

## OuiNowPlaying
A media player card: art, title, artist, progress and controls.
- Inputs: *Title: Text, Artist: Text = "", ArtUrl: Text = "", Progress: Decimal, IsPlaying: Boolean
- Events: OnCommand(Command: Text)
- Uses: OuiIconButton, OuiProgressBar
- Shown on: OuiMotion
- Example: `{"type": "BlockInstance", "sourceBlock": "OuiNowPlaying", "args": {"Title": "\"Market minute: October rates\"", "Artist": "\"Bank podcast\"", "Progress": "38", "IsPlaying": "Playing"}, "events": {"OnCommand": "PlayerCommand"}}`

## OuiWaveform
Audio bars: still or moving while playing. Values is comma separated 0 to 100.
- Inputs: Values: Text = "30,60,45,80,55,90,40,70,35,65,50,85,45,60,30", IsPlaying: Boolean
- Shown on: OuiMotion
- Example: `{"type": "BlockInstance", "sourceBlock": "OuiWaveform", "args": {"IsPlaying": "Playing"}}`

## OuiImage
A picture that fades in when loaded, with a ratio, radius and optional caption.
- Inputs: *Url: Text, *Alt: Text, Ratio: Text = "16 / 9", Caption: Text = ""
- Shown on: OuiMedia
- Example: `{"type": "BlockInstance", "sourceBlock": "OuiImage", "args": {"Url": "\"https://picsum.photos/seed/oui-a/800/500\"", "Alt": "\"A calm lake at sunrise\"", "Caption": "\"Branch of the year: Lake District\""}}`

## OuiCarousel
Swipe or use the arrows to move between pictures. Urls is comma separated.
- Inputs: *Urls: Text, Label: Text = "Pictures"
- Shown on: OuiMedia
- Example: `{"type": "BlockInstance", "sourceBlock": "OuiCarousel", "args": {"Urls": "\"https://picsum.photos/seed/oui-c/1200/600,https://picsum.photos/seed/oui-d/1200/600,https://picsum.photos/seed/oui-e/1200/600,https://picsum.photos/seed/oui-f/1200/600\"", "Label": "\"Offers\""}}`

## OuiImageCompare
Drag across two pictures to compare before and after.
- Inputs: *BeforeUrl: Text, *AfterUrl: Text, BeforeLabel: Text = "Before", AfterLabel: Text = "After", Ratio: Text = "16 / 9"
- Shown on: OuiMedia
- Example: `{"type": "BlockInstance", "sourceBlock": "OuiImageCompare", "args": {"BeforeUrl": "\"https://picsum.photos/seed/oui-g/1200/700\"", "AfterUrl": "\"https://picsum.photos/seed/oui-g/1200/700?grayscale\"", "BeforeLabel": "\"Colour\"", "AfterLabel": "\"Grey\""}}`

## OuiCardStack
Cards on a pile; swipe the top one left or right. Put OuiStackCard blocks in Cards.
- Inputs: none
- Events: OnSwipe(Direction: Text)
- Placeholders: Cards
- Shown on: OuiMedia
- Example: `{"type": "BlockInstance", "sourceBlock": "OuiCardStack", "placeholders": {"Cards": [{"type": "BlockInstance", "sourceBlock": "OuiStackCard", "args": {"Title": "\"Travel card\"", "Text": "\"No fees abroad.\"", "Tone": "\"c6\""}}, {"type": "BlockInstance", "sourceBlock": "OuiStackCard", "args": {"Title": "\"Savings goal\"", "Text": "\"EUR 3,000 for summer.\"", "Tone": "\"c3\""}}, {"type": "BlockInst`

## OuiStackCard
One card of OuiCardStack.
- Inputs: *Title: Text, Text: Text = "", Tone: Text = "accent"
- Shown on: OuiMedia
- Example: `{"type": "BlockInstance", "sourceBlock": "OuiStackCard", "args": {"Title": "\"Travel card\"", "Text": "\"No fees abroad.\"", "Tone": "\"c6\""}}`

## OuiDropzone
A drop area for files. Put an Upload widget in Input; dropped files go into it.
- Inputs: Title: Text = "Drop files here or click to choose", Hint: Text = "PDF, PNG or JPG, up to 10 MB"
- Placeholders: Input
- Shown on: OuiMedia
- Example: `{"type": "BlockInstance", "sourceBlock": "OuiDropzone", "placeholders": {"Input": [{"type": "Upload", "name": "DocUpload", "fileContent": "FileContent", "fileName": "FileName"}]}}`

## OuiAvatarUpload
An avatar that shows a camera on hover; put an Upload widget in Input.
- Inputs: *Name: Text, ImageUrl: Text = "", Size: Text = "xl"
- Placeholders: Input
- Uses: OuiAvatar
- Shown on: OuiMedia
- Example: `{"type": "BlockInstance", "sourceBlock": "OuiAvatarUpload", "args": {"Name": "\"Ana Silva\""}, "placeholders": {"Input": [{"type": "Upload", "name": "AvatarUpload", "fileContent": "AvatarContent", "fileName": "AvatarName"}]}}`

## OuiSignaturePad
Sign with a mouse, pen or finger. OnChange gets the picture as a data URL (empty after Clear).
- Inputs: Hint: Text = "Sign here", Height: Integer = 180
- Events: OnChange(DataUrl: Text)
- Shown on: OuiMedia
- Example: `{"type": "BlockInstance", "sourceBlock": "OuiSignaturePad", "events": {"OnChange": {"action": "Log", "args": {"Message": "If(DataUrl = \"\", \"Signature cleared\", \"Signed (\" + Length(DataUrl) + \" characters)\")"}}}}`

## OuiCurveEditor
Edit an easing curve by dragging two handles; the ball runs on it. Value is x1,y1,x2,y2.
- Inputs: Value: Text = ".25,.1,.25,1"
- Events: OnChange(Value: Text)
- Shown on: OuiTools
- Example: `{"type": "BlockInstance", "sourceBlock": "OuiCurveEditor", "args": {"Value": "Curve"}, "events": {"OnChange": {"action": "SetCurve", "args": {"Value": "Value"}}}}`

## OuiGradientEditor
Drag colour stops on a bar; click a stop to change its colour. Value is colour position pairs, comma separated.
- Inputs: Value: Text = "#6a4cf5 0,#e04a84 100"
- Events: OnChange(Value: Text)
- Shown on: OuiTools
- Example: `{"type": "BlockInstance", "sourceBlock": "OuiGradientEditor", "args": {"Value": "Gradient"}, "events": {"OnChange": {"action": "SetGradient", "args": {"Value": "Value"}}}}`

## OuiTimelineEditor
Tracks with clips you drag in time. Tracks: Name|Clip:start:duration,... separated by ;.
- Inputs: *Tracks: Text, Length: Integer = 30
- Events: OnChange(Tracks: Text)
- Shown on: OuiTools
- Example: `{"type": "BlockInstance", "sourceBlock": "OuiTimelineEditor", "args": {"Tracks": "\"Intro|Logo:0:4,Title:4:6;Voice|Welcome:2:9,Offer:14:8;Music|Theme:0:24\"", "Length": "30"}, "events": {"OnChange": {"action": "Log", "args": {"Message": "\"Timeline changed\""}}}}`

## OuiFlameGraph
Where time goes, as a flame graph. Stacks: a>b>c value separated by ;.
- Inputs: *Stacks: Text
- Events: OnPick(Frame: Text)
- Shown on: OuiTools
- Example: `{"type": "BlockInstance", "sourceBlock": "OuiFlameGraph", "args": {"Stacks": "\"api>auth>token 12;api>auth>roles 6;api>transfers>validate 18;api>transfers>fx>rates 22;api>transfers>book 30;api>notify>email 9;api>notify>push 4\""}, "events": {"OnPick": {"action": "Log", "args": {"Message": "\"Frame: \" + Frame"}}}}`

## OuiSplitPane
Two panes with a draggable divider (or arrow keys on it), for docking layouts.
- Inputs: IsVertical: Boolean, Start: Integer = 50, Height: Integer = 280
- Events: OnResize(Percent: Decimal)
- Placeholders: First, Second
- Shown on: OuiTools
- Example: `{"type": "BlockInstance", "sourceBlock": "OuiSplitPane", "args": {"Start": "35"}, "placeholders": {"First": [{"type": "BlockInstance", "sourceBlock": "OuiTreeItem", "args": {"Label": "\"Statements\"", "HasChildren": "True", "IsOpen": "True"}}, {"type": "BlockInstance", "sourceBlock": "OuiTreeItem", "args": {"Label": "\"October.pdf\"", "Depth": "1", "Icon": "\"file\"", "IsSelected": "True"}}], "Sec`

## OuiLayoutSide
App shell: a sidebar menu (Menu, MenuFooter) that folds to icons, a title bar (Title, Actions, HeaderActions), the content, and an optional right panel (Aside) for an assistant or details. Tablet folds the menu; phone makes it a drawer and the panel a sheet.
- Inputs: AppName: Text = "OUI", ShowAccentPicker: Boolean = False, ShowThemeToggle: Boolean = True, HasAside: Boolean, AsideTitle: Text = "Assistant", AsideStartsOpen: Boolean
- Placeholders: Menu, MenuFooter, Title, Actions, HeaderActions, MainContent, Aside
- Uses: OuiAccentPicker, OuiThemeToggle

## OuiLayoutBlank
A centred card for sign-in, sign-up and error pages: the brand on top, Title and MainContent in the card, Footer below.
- Inputs: AppName: Text = "OUI", Width: Integer = 420, ShowThemeToggle: Boolean = True
- Placeholders: Title, MainContent, Footer
- Uses: OuiThemeToggle

## OuiSplitView
List and detail side by side (ListHeader, List, DetailHeader, Detail). On a phone it shows one pane: the detail when IsDetailOpen, with a Back button that raises OnBack.
- Inputs: IsDetailOpen: Boolean, ListWidth: Integer = 340, Height: Integer = 560, BackLabel: Text = "Back"
- Events: OnBack()
- Placeholders: ListHeader, List, DetailHeader, Detail
- Shown on: OuiLayoutSideDemo, OuiTplInbox, OuiTplListDetail
- Example: `{"type": "BlockInstance", "sourceBlock": "OuiSplitView", "args": {"IsDetailOpen": "IsDetailOpen", "Height": "460"}, "placeholders": {"ListHeader": [{"type": "Expression", "value": "\"Customers\"", "style": "\"oui-splitview__title\""}], "List": [{"type": "Button", "name": "Person0", "onClick": "Pick", "args": {"Index": "0"}, "style": "\"oui-pick-row\" + If(Picked = 0, \" is-active\", \"\")", "child`

## OuiShowcaseNav
Links between the showcase screens.
- Inputs: *Active: Text
