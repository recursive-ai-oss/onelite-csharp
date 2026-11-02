// Boots the .NET runtime that carries the browser tests and returns its exported functions. Shared by the page and the worker.
import { dotnet } from './_framework/dotnet.js';

export async function boot() {
  const { getAssemblyExports, getConfig, runMain } = await dotnet.create();
  const config = getConfig();
  const exports = await getAssemblyExports(config.mainAssemblyName);
  await runMain();
  return exports.RecursiveAI.OneLite.BrowserTests.Exports;
}

/** Runs one operation against the exports. `op` is init | list | run | cmd. */
export async function dispatch(ex, op, args) {
  switch (op) {
    case 'init': return await ex.Init(args[0] || '');
    case 'list': return ex.List(args[0]);
    case 'run': return await ex.Run(args[0]);
    case 'cmd': return await ex.Command(args[0], args[1] || '');
    case 'import-url': {
      // Fetches a database from the host and imports it into the named backend, then checks it. See Exports.ImportVerify.
      const buf = new Uint8Array(await (await fetch(args[2])).arrayBuffer());
      return await ex.ImportVerify(args[0], args[1], buf);
    }
    case 'export-upload': {
      // Exports a database and posts the bytes to the host, which checks them with sqlite3.
      const n = await ex.ExportPrepare(args[0], args[1]);
      if (n.startsWith('FAIL')) return n;
      const bytes = ex.ExportTake();
      const r = await fetch('/upload/' + encodeURIComponent(args[2]), { method: 'POST', body: bytes });
      return r.ok ? 'ok ' + bytes.length : 'FAIL upload status ' + r.status;
    }
    default: return 'FAIL unknown op ' + op;
  }
}
