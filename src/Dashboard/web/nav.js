// The sidebar: the only place that decides the groups and their order, and which pages appear in each group.
// Remove an id from this list to hide the page; a page not listed here is still reachable by its URL.
// The order here is the order of the sidebar on screen (tests/dashboard-check.ps1 compares them).
export const NAV = [
  { group: 'Start', title: 'Start', ids: ['home', 'modules'] },
  { group: 'Module', title: 'Your module', ids: ['explorer', 'previews', 'openapi', 'architecture', 'check', 'review'] },
  { group: 'Agents', title: 'Work with agents', ids: ['prompt', 'recipes'] },
  { group: 'Assets', title: 'Assets', ids: ['assets'] },
  { group: 'Team', title: 'Team and agents', ids: ['lifetime', 'activity'] },
  { group: 'Learn', title: 'Learn', ids: ['cookbook', 'docs', 'rules'] },
  { group: 'Tools', title: 'Tools', ids: ['tools'] },
];
