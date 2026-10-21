# OutSystems UI patterns

The most useful OutSystems UI blocks: purpose, inputs, placeholders, events. Facts partly from the OutSystems `outsystems11-mcp` skill (MIT) and the OutSystems UI docs; names are for OutSystemsUI 2.x, so check them against your module.

## Rules

- Use a pattern block before building the same thing from Containers and CSS.
- Patterns live in the `OutSystemsUI` module (charts in `OutSystemsCharts`). If the block is not in the module's references, ask the user to add the dependency or give a producer export for `references`.
- In a spec: `{ "type": "BlockInstance", "sourceBlock": "Tag", "args": { "Color": "Entities.Color.Green" }, "placeholders": { "Tag": [ ... ] } }`. Every mandatory input (M below) needs an argument. Handle pattern events with the instance's `events` (`oml spec events`).
- Most patterns also have the optional input `ExtendedClass` (extra CSS classes). Many have `OptionalConfigs` (a structure) and an `Initialized` event.
- Prefer theme CSS to change the look. Pattern classes follow `osui-<pattern>__<part>` with `is-*` modifiers. A copy of a pattern block loses its upgrades.
- Pattern client actions (for example `AccordionExpandAll`, `NotificationOpen`) control a pattern by its widget Id.

## Patterns

| Block | Purpose | Key inputs | Placeholders | Events | Note |
|---|---|---|---|---|---|
| LayoutTopMenu | page with top menu | HasFixedHeader, EnableAccessibilityFeatures | Header, Breadcrumbs, Title, Actions, MainContent, Footer | | screen buttons go in Actions |
| LayoutSideMenu | page with side menu | same | Navigation, Header, Breadcrumbs, Title, MainContent, Footer | | |
| Columns2 to Columns6, ColumnsSmallLeft/Right, ColumnsMediumLeft/Right | responsive columns | GutterSize, TabletBehavior, PhoneBehavior | Column1 to ColumnN | | break modes: all, first, middle, last, none |
| Section | titled group | UsePadding | Title, Content | | groups form parts |
| Card | box | UsePadding | Content | | |
| CardSectioned | card with image and footer | UsePadding, IsVertical, ImagePadding | Image, Title, Content, Footer | | catalog item, chart frame |
| Counter | KPI tile | BackgroundColor, IsVertical, Height | Content | | KPI row = Columns3 of Counter; IsVertical for long labels |
| Tag | status chip | Color, Shape, Size, IsLight | Tag | | `Color: If(Late, Entities.Color.Red, Entities.Color.Green)` |
| Badge | number badge | Number, Color, Shape, Size | | | |
| BlankSlate | empty state | FullHeight | Icon, Content, Actions | | show when `IsDataFetched and List.Empty` |
| Alert | inline message | AlertType (M: Error, Info, Success, Warning) | MessageText | | |
| Gallery | card grid | RowItemsDesktop, RowItemsTablet, RowItemsPhone, ItemsGap | Content | | put a List of CardSectioned inside |
| MasterDetail | list and detail side by side | LeftPercentage (50), OpenedOnPhone, Height | LeftContent, RightContent | ItemSelected, DetailClose | right side: detail if selected, else BlankSlate |
| Pagination | page links | StartIndex, MaxRecords, TotalCount (all M), ShowGoToPage | Previous, Next | OnNavigate (NewStartIndex) | handler: StartIndex = NewStartIndex, then refreshData |
| Tabs, TabsHeaderItem, TabsContentItem | tabs | StartingTab (from 0), TabsOrientation, Height | header and content items | OnTabChange | a new Tabs has 3 items |
| Accordion, AccordionItem | collapsible rows | MultipleItems; item StartsExpanded, IsDisabled | Title, Content | item OnToggle | |
| Wizard, WizardItem | steps | IsVertical; item Status (M) | | | |
| Notification | toast | StartsOpen, Position, Width | Content | OnToggle | NotificationOpen/Close actions |
| Tooltip | hint on hover or click | Position, Trigger, StartsOpen | Content, Tooltip | OnToggle (M) | |
| Sidebar | side panel | StartsOpen, Direction, HasOverlay, Width | Header, Content | OnToggle | |
| BottomSheet | mobile sheet | Shape, ShowHandler | TopBar, Content | OnToggle | |
| ActionSheet | mobile action list | IsOpen (M) | 5 button placeholders | OnClose | bind IsOpen to a Boolean local; OnClose sets it False |
| DatePicker | date | DateFormat, ShowTodayButton, OptionalConfigs (InitialDate, MinDate, MaxDate) | | OnSelected (M) | each picker holds an Input: bind it to a variable |
| DatePickerRange | date range | DateFormat, OptionalConfigs (InitialStartDate, InitialEndDate, MinDate, MaxDate) | | OnSelected (M) | |
| MonthPicker | month | DateFormat, InitialMonth, MinMonth, MaxMonth | | OnSelected (M) | |
| TimePicker | time | TimeFormat, InitialTime, OptionalConfigs (MinTime, MaxTime) | | OnSelected (M) | |
| DropdownSearch | searchable dropdown | OptionsList (M: Value, Label, ...), StartingSelection, Prompt, OptionalConfigs (AllowMultipleSelection, IsDisabled) | | OnChanged (M) | build OptionsList in logic or with a mapped aggregate |
| DropdownTags | multi-select with tags | same as DropdownSearch; OptionalConfigs (IsDisabled) | | OnChanged (M) | |
| Search, InputWithIcon | search field, input with icon | InputWithIcon: AlignIconRight | Input; Icon, Input | | |
| RangeSlider, RangeSliderInterval | number or range | MinValue, MaxValue, StartingValue or StartingValueFrom/To (M) | | OnValueChange (M) | numeric filter |
| ProgressBar, ProgressCircle | progress | Progress (M, 0 to 100), ProgressColor, TrailColor, Thickness | | | |
| ButtonLoading | button with spinner | IsLoading (M), ShowLabelOnLoading | | | set IsLoading while the server call runs |
| Carousel | slides | Height, ItemsPerSlide, Navigation, OptionalConfigs (AutoPlay, Loop) | CarouselItems | OnSlideMoved | |
| Animate | entrance animation | AnimationType (M), Delay, Speed | Content | | stagger: `Delay = List.CurrentRowNumber * 60` |
| DisplayOnDevice | show per device | | OnDesktop, OnTablet, OnPhone | | |
| Rating | stars | RatingValue (M), RatingScale, IsEdit, Size | FilledState, HalfState, EmptyState | OnSelect | |
| ScrollableArea | scroll box | Orientation, Height, Width, ScrollbarType | Content | | |
| Separator | divider | Color, Space, IsVertical | | | |
| Submenu | menu group | | Menu, Items | | a new instance has a stray link in Items: remove it |
| UserAvatar, LightboxImage, Video | media | Image; ImageURL, Group; VideoURL (M) | | | |
| ColumnChart, BarChart, LineChart, AreaChart, PieChart, DonutChart | charts (OutSystemsCharts) | DataPointList (M, list of DataPoint), Height, StackingType, InnerSize (donut) | AddOns_Placeholder | | DataPoint: Value, Label, SeriesName, Color, Tooltip |

Avoid blocks named `DEPRECATED_*`.

## Values used in inputs

- Colors: `Entities.Color.` Neutral0 to Neutral10, Primary, Secondary, Blue, Green, Orange, Red, Violet, Teal, Yellow, Lime, Pink, Grape, Indigo, Cyan, Transparent. There is no `Success` or `Error` color.
- Column breaks: `Entities.BreakColumns.All`, `.First`, `.Middle`, `.Last`, `.None`.
- Animation: `Entities.AnimationType.FadeIn` and similar names. `FadeInUp` does not exist.
- Alert: `Entities.Alert.` records such as `Warning` and `Error`.

Check the record names in your module before you use them: a wrong record name is an expression error.

## Theme tokens

- Colors: `--color-primary`, `--color-secondary`, `--color-neutral-0` to `--color-neutral-10`, `--color-error`, `--color-warning`, `--color-success`, `--color-info`.
- Space: `--space-xs`, `--space-s`, `--space-base`, `--space-m`, `--space-l`, `--space-xl`, `--space-xxl` (4, 8, 16, 24, 32, 40, 48 px).
- Radius: `--border-radius-soft`, `--border-radius-rounded`, `--border-radius-circle`. Shadow: `--shadow-xs` to `--shadow-xl`.
- Font size: `--font-size-h1` to `--font-size-h6`, `--font-size-base`, `--font-size-s`, `--font-size-xs`.
- Some palette colors (orange, yellow, lime, green, light-transparent, light neutrals) fail contrast as text. Override the token or pick another color.

More: `oml doc ui-reactive`.
