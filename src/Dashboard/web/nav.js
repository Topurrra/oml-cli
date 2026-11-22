// The sidebar: the only place that decides the groups and their order, and which pages appear in each group.
// Remove an id from this list to hide the page; a page not listed here is still reachable by its URL.
// The order here is the order of the sidebar on screen (tests/dashboard-check.ps1 compares them).
export const NAV = [
  { group: 'Start', title: 'Start', ids: ['home', 'modules'] },
  { group: 'Module', title: 'Your module', ids: ['explorer', 'openapi', 'architecture', 'check', 'review'] },
  { group: 'Agents', title: 'Agents', ids: ['agents', 'activity', 'learned'] },
  { group: 'Environment', title: 'Environment', ids: ['server', 'lifetime'] },
  { group: 'Assets', title: 'Assets', ids: ['assets'] },
  { group: 'Tools', title: 'Tools', ids: ['tools'] },
];
