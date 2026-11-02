// A dedicated worker that hosts its own copy of the test runtime: the place FileSystemSyncAccessHandle is allowed.
import { boot, dispatch } from './main.js';

let exportsPromise = null;
onmessage = async (ev) => {
  const { id, op, args } = ev.data;
  let r;
  try {
    exportsPromise ??= boot();
    const ex = await exportsPromise;
    r = await dispatch(ex, op, args);
  } catch (e) {
    r = 'FAIL ' + (e && e.stack ? e.stack : e);
  }
  postMessage({ id, r });
};
postMessage({ id: 0, r: 'started' });
