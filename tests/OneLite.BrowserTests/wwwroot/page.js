// The page side of the test host. The runner (tools/scripts/browser_tests.py) drives window.ol.
import { boot, dispatch } from './main.js';

let exportsPromise = null;
let worker = null;
let nextId = 1;
const waiting = new Map();

function startWorker() {
  worker = new Worker('./worker.js', { type: 'module' });
  worker.onmessage = (ev) => {
    const { id, r } = ev.data;
    const w = waiting.get(id);
    if (w) { waiting.delete(id); w(r); }
  };
  worker.onerror = (ev) => { for (const w of waiting.values()) w('FAIL worker error: ' + ev.message); waiting.clear(); };
}

function toWorker(op, args) {
  if (!worker) startWorker();
  return new Promise(resolve => { const id = nextId++; waiting.set(id, resolve); worker.postMessage({ id, op, args }); });
}

async function here(op, args) {
  exportsPromise ??= boot();
  return dispatch(await exportsPromise, op, args);
}

/** Where: 'page' for this thread, 'worker' for the dedicated worker. */
window.ol = {
  call: (where, op, ...args) => (where === 'worker' ? toWorker(op, args) : here(op, args)),
  stopWorker: () => { if (worker) { worker.terminate(); worker = null; } },
  // Fetches a file from the host's /corpus/ and hands its bytes to a command as base64-free binary through the page-side helper.
  fetchBytes: async (url) => new Uint8Array(await (await fetch(url)).arrayBuffer()),
  upload: async (name, bytes) => { const r = await fetch('/upload/' + encodeURIComponent(name), { method: 'POST', body: bytes }); return r.status; },
};
window.olReady = true;
