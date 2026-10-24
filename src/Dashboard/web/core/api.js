import { store } from './store.js';
import { state } from './state.js';

export const KEY = new URLSearchParams(location.search).get('key') || '';

export class ApiError extends Error {
  constructor(message, data) { super(message); this.data = data; }
}

// The latest run of each CLI line: time taken and --timings, read once by cli().
export const runs = new Map();

function timingsOf(data) {
  const out = data.output;
  if (out && typeof out === 'object' && !Array.isArray(out) && out.timings) return out.timings;
  const line = (data.stderr || '').split(/\r?\n/).find(l => l.startsWith('{"timings"'));
  try { return line ? JSON.parse(line).timings : null; } catch { return null; }
}

export async function api(path, body) {
  const started = Date.now();
  if (body && typeof body === 'object' && store.get('timings', false)) body = { ...body, timings: true };
  let res;
  try {
    res = await fetch('/api/' + path, {
      method: body === undefined ? 'GET' : 'POST',
      headers: { 'X-Oml-Key': KEY, 'Content-Type': 'application/json' },
      body: body === undefined ? undefined : JSON.stringify(body),
      cache: 'no-store',
    });
  } catch {
    throw new ApiError('The dashboard is not answering. Is the oml dashboard window still open?', null);
  }
  let data;
  try { data = await res.json(); } catch { data = { error: 'The dashboard sent an answer that is not JSON (HTTP ' + res.status + ').' }; }
  if (data && data.cli) runs.set(data.cli, { ms: Date.now() - started, ok: res.ok && (typeof data.code !== 'number' || data.code === 0), timings: timingsOf(data) });
  if (!res.ok) throw new ApiError(data.error || ('HTTP ' + res.status), data);
  return data;
}

export function need(data, okCodes) {
  if (typeof data.code === 'number' && !(okCodes || [0]).includes(data.code)) throw new ApiError(problem(data), data);
  return data;
}

export function problem(data) {
  const out = data.output;
  const first = v => {
    if (!v) return null;
    if (typeof v === 'string') return v;
    if (Array.isArray(v)) { for (const x of v) { const f = first(x); if (f) return f; } return null; }
    if (typeof v === 'object') {
      for (const k of ['error', 'message', 'problems', 'errors']) { const f = first(v[k]); if (f) return f; }
    }
    return null;
  };
  return first(out) || (data.stderr || '').split('\n')[0] || ('The command ended with exit code ' + data.code + '.');
}

// Read a module once and keep the answer for the next page (explorer, previews, openapi).
export async function inspect(path, fresh) {
  if (!fresh && state.inspect[path]) return state.inspect[path];
  const data = need(await api('inspect', { path, history: store.get('history', false) || undefined }));
  state.inspect[path] = data;
  return data;
}
