import { store } from './store.js';

// The module the person works on and the cached inspect answers. Shared by every page; kept in one place.
export const state = {
  module: store.get('module', ''),
  inspect: {},
  info: null,
};

export function remember(path) {
  const recent = [path, ...store.get('recent', []).filter(p => p.toLowerCase() !== path.toLowerCase())].slice(0, 12);
  store.set('recent', recent);
}

export function setModule(path) {
  state.module = path;
  store.set('module', path);
  remember(path);
}

// Values after ? in the page address: #/review?before=...&after=..., #/docs?q=...
export const hashParams = () => new URLSearchParams(location.hash.split('?')[1] || '');
