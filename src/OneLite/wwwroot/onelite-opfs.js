// OneLite browser storage: the thin JavaScript half of the OPFS and Web Locks file systems.
//
// Everything here follows two web platform documents and nothing else:
//   * the File System Standard (https://fs.spec.whatwg.org/): FileSystemFileHandle.getFile / createWritable,
//     FileSystemSyncAccessHandle (dedicated workers only), FileSystemDirectoryHandle.getFileHandle / removeEntry;
//   * the Web Locks API (https://www.w3.org/TR/web-locks/): navigator.locks.request with { mode, ifAvailable }.
//
// The module has no state the .NET side cannot recreate and depends on nothing. Failures are thrown as Error("OL:<code>:<text>") where <code> is
// one of the codes below; the .NET side turns them into VfsException with the SQLite extended result code.

const E_NOTFOUND = 1, E_EXISTS = 2, E_BUSY = 3, E_IO = 4, E_FULL = 5, E_ISDIR = 6, E_UNSUPPORTED = 7;

function fail(e) {
  if (e && typeof e.message === 'string' && e.message.startsWith('OL:')) return e;
  const name = e && e.name;
  let code = E_IO;
  if (name === 'NotFoundError') code = E_NOTFOUND;
  // A second createSyncAccessHandle on a file, or a write to a file another context holds a sync handle on, fails like this (File System Standard).
  else if (name === 'NoModificationAllowedError' || name === 'InvalidStateError') code = E_BUSY;
  else if (name === 'QuotaExceededError') code = E_FULL;
  else if (name === 'TypeMismatchError') code = E_ISDIR;
  else if (name === 'NotSupportedError' || name === 'SecurityError' || name === 'TypeError') code = E_UNSUPPORTED;
  return new Error('OL:' + code + ':' + (name || 'Error') + ': ' + ((e && e.message) || String(e)));
}

function err(code, text) { return new Error('OL:' + code + ':' + text); }

/** Reports which pieces of the platform this context has. */
export function capabilities() {
  const inWorker = typeof WorkerGlobalScope !== 'undefined' && self instanceof WorkerGlobalScope;
  return {
    opfs: typeof navigator !== 'undefined' && !!navigator.storage && typeof navigator.storage.getDirectory === 'function',
    syncAccessHandle: typeof FileSystemFileHandle !== 'undefined' && typeof FileSystemFileHandle.prototype.createSyncAccessHandle === 'function',
    webLocks: typeof navigator !== 'undefined' && !!navigator.locks,
    worker: inWorker,
    crossOriginIsolated: typeof crossOriginIsolated !== 'undefined' && crossOriginIsolated,
  };
}

export function capabilitiesText() {
  const c = capabilities();
  return Object.keys(c).map(k => k + '=' + c[k]).join(' ');
}

// ---- paths -----------------------------------------------------------------------------------------------------------------------------

async function locate(path, create) {
  const parts = path.split('/').filter(p => p && p !== '.');
  const name = parts.pop();
  if (!name) throw err(E_NOTFOUND, 'empty path');
  let dir = await navigator.storage.getDirectory();
  for (const p of parts) dir = await dir.getDirectoryHandle(p, { create });
  return { dir, name };
}

export async function exists(path) {
  try {
    const { dir, name } = await locate(path, false);
    await dir.getFileHandle(name);
    return true;
  } catch (e) {
    if (e && e.name === 'NotFoundError') return false;
    if (e && e.name === 'TypeMismatchError') return false;
    throw fail(e);
  }
}

/** True if the file exists and is not empty. The worker file system keeps empty files around for names it has prepared, and counts an empty file as absent. */
export async function existsNonEmpty(path) {
  try {
    const { dir, name } = await locate(path, false);
    return (await (await dir.getFileHandle(name)).getFile()).size > 0;
  } catch (e) {
    if (e && (e.name === 'NotFoundError' || e.name === 'TypeMismatchError')) return false;
    throw fail(e);
  }
}

export async function removeFile(path) {
  try {
    const { dir, name } = await locate(path, false);
    await dir.removeEntry(name);
  } catch (e) { throw fail(e); }
}

/** Lists the file names (with sizes) directly inside a directory path ('' is the root). Used by the tests and by tools; the engine never calls it. */
export async function listFiles(path) {
  try {
    let dir = await navigator.storage.getDirectory();
    for (const p of path.split('/').filter(p => p && p !== '.')) dir = await dir.getDirectoryHandle(p);
    const out = [];
    for await (const [name, h] of dir.entries()) {
      if (h.kind === 'file') out.push(name + ':' + (await h.getFile()).size);
    }
    return out.sort().join('\n');
  } catch (e) { throw fail(e); }
}

/** Removes every file and directory in the origin's private file system. Test hygiene only. */
export async function wipe() {
  try {
    const root = await navigator.storage.getDirectory();
    for await (const name of root.keys()) await root.removeEntry(name, { recursive: true });
  } catch (e) { throw fail(e); }
}

// ---- files for contexts that may not block (FileSystemFileHandle.createWritable) ----------------------------------------------------------

const files = new Map();
let nextFile = 1;

function file(id) {
  const f = files.get(id);
  if (!f) throw err(E_IO, 'unknown file handle ' + id);
  return f;
}

/**
 * Opens a file. A writable stream's data reaches the file only when the stream is closed (File System Standard, FileSystemWritableFileStream:
 * writes go to a swap file that close() moves over the original), so the file object keeps one open stream while there are unflushed writes and
 * closes it before any read and at every sync.
 */
export async function aOpen(path, create, exclusive) {
  try {
    const { dir, name } = await locate(path, create);
    if (exclusive) {
      let present = true;
      try { await dir.getFileHandle(name); } catch (e) { if (e.name === 'NotFoundError') present = false; else throw e; }
      if (present) throw err(E_EXISTS, 'already exists');
    }
    const fh = await dir.getFileHandle(name, { create });
    const blob = await fh.getFile();
    const id = nextFile++;
    files.set(id, { fh, blob, w: null, size: blob.size });
    return id;
  } catch (e) { throw fail(e); }
}

async function commit(f) {
  if (f.w) {
    const w = f.w;
    f.w = null;
    f.blob = null;
    await w.close();
  }
}

async function writer(f) {
  if (!f.w) {
    f.w = await f.fh.createWritable({ keepExistingData: true });
    f.blob = null;
  }
  return f.w;
}

/**
 * Reads up to `length` bytes at `offset` and resolves with how many there are (fewer at the end of the file). The bytes wait in the file object
 * until aTake copies them into .NET memory, because a window onto that memory is only valid during a synchronous call.
 */
export async function aRead(id, offset, length) {
  try {
    const f = file(id);
    await commit(f);
    if (!f.blob) { f.blob = await f.fh.getFile(); f.size = f.blob.size; }
    const end = Math.min(offset + length, f.blob.size);
    if (offset >= end) { f.out = null; return 0; }
    f.out = new Uint8Array(await f.blob.slice(offset, end).arrayBuffer());
    return f.out.byteLength;
  } catch (e) { throw fail(e); }
}

/** Copies the bytes the last aRead of this file produced into `view`. */
export function aTake(id, view) {
  const f = file(id);
  if (f.out) { view.set(f.out); f.out = null; }
}

export async function aWrite(id, offset, view) {
  // The view is a window onto .NET memory that is valid only until this function first awaits: copy it now.
  const data = view.slice();
  try {
    const f = file(id);
    const w = await writer(f);
    await w.write({ type: 'write', position: offset, data });
    f.size = Math.max(f.size, offset + data.byteLength);
  } catch (e) { throw fail(e); }
}

export async function aTruncate(id, size) {
  try {
    const f = file(id);
    const w = await writer(f);
    await w.truncate(size);
    f.size = size;
  } catch (e) { throw fail(e); }
}

export async function aSize(id) {
  try {
    const f = file(id);
    if (f.w) return f.size;
    if (!f.blob) f.blob = await f.fh.getFile();
    f.size = f.blob.size;
    return f.size;
  } catch (e) { throw fail(e); }
}

/** The closest thing to fsync this API has: close the stream, which moves its swap file over the original. */
export async function aFlush(id) {
  try { await commit(file(id)); } catch (e) { throw fail(e); }
}

export async function aClose(id) {
  const f = files.get(id);
  if (!f) return;
  files.delete(id);
  try { await commit(f); } catch (e) { throw fail(e); }
}

// ---- files for dedicated workers (FileSystemSyncAccessHandle) ----------------------------------------------------------------------------

const handles = new Map();
let scratch = new Uint8Array(65536);

function handle(id) {
  const h = handles.get(id);
  if (!h) throw err(E_IO, 'unknown sync handle ' + id);
  return h;
}

function scratchOf(n) {
  if (scratch.byteLength < n) scratch = new Uint8Array(Math.max(n, scratch.byteLength * 2));
  return scratch;
}

/**
 * Opens a sync access handle (only a dedicated worker may; the call is the one asynchronous step). The platform lets one context hold a
 * file's handle at a time: a second open fails with NoModificationAllowedError, which is reported as E_BUSY.
 */
export async function sOpen(path, create) {
  try {
    const { dir, name } = await locate(path, create);
    const fh = await dir.getFileHandle(name, { create });
    const h = await fh.createSyncAccessHandle();
    const id = nextFile++;
    handles.set(id, h);
    return id;
  } catch (e) { throw fail(e); }
}

export function sRead(id, offset, view) {
  try {
    const h = handle(id);
    const n = view.byteLength;
    const tmp = scratchOf(n);
    const got = h.read(tmp.subarray(0, n), { at: offset });
    if (got > 0) view.set(tmp.subarray(0, got));
    return got;
  } catch (e) { throw fail(e); }
}

export function sWrite(id, offset, view) {
  try {
    const h = handle(id);
    const n = view.byteLength;
    const tmp = scratchOf(n);
    view.copyTo(tmp.subarray(0, n));
    const wrote = h.write(tmp.subarray(0, n), { at: offset });
    if (wrote !== n) throw err(E_FULL, 'short write: ' + wrote + ' of ' + n);
  } catch (e) { throw fail(e); }
}

export function sTruncate(id, size) {
  try { handle(id).truncate(size); } catch (e) { throw fail(e); }
}

export function sSize(id) {
  try { return handle(id).getSize(); } catch (e) { throw fail(e); }
}

export function sFlush(id) {
  try { handle(id).flush(); } catch (e) { throw fail(e); }
}

export function sClose(id) {
  const h = handles.get(id);
  if (!h) return;
  handles.delete(id);
  try { h.close(); } catch (e) { throw fail(e); }
}

// ---- Web Locks ---------------------------------------------------------------------------------------------------------------------------

const held = new Map();
let nextLock = 1;

/**
 * Tries to take the named lock without waiting (ifAvailable). Resolves with a positive handle when granted, 0 when another holder has it.
 * A Web Lock is held for as long as the promise returned by the request callback is pending, so a granted lock parks one.
 */
export function lockTry(name, exclusive) {
  return new Promise((resolve, reject) => {
    let done;
    // The callback runs after request() has returned, so `done` is set by then.
    done = navigator.locks.request(name, { mode: exclusive ? 'exclusive' : 'shared', ifAvailable: true }, lock => {
      if (!lock) return undefined;
      const id = nextLock++;
      return new Promise(release => { held.set(id, { release, done }); resolve(id); });
    });
    // Not granted: the request settles without the callback parking a lock. Granted: resolve(id) already ran and this one is ignored.
    done.then(() => resolve(0), reject);
  });
}

/** Releases a lock taken with lockTry. Resolves once the lock manager has dropped it. */
export async function lockRelease(id) {
  const h = held.get(id);
  if (!h) return;
  held.delete(id);
  h.release();
  if (h.done) await h.done;
}
