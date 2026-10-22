import fs from 'node:fs/promises';
import path from 'node:path';
import { pathToFileURL, fileURLToPath } from 'node:url';
import readline from 'node:readline';

const options = new Map();
for (let index = 2; index < process.argv.length; index += 2) options.set(process.argv[index], process.argv[index + 1]);
const pid = Number(options.get('--pid'));
const packagePath = options.get('--frida');
const send = value => process.stdout.write(`${JSON.stringify(value)}\n`);
const commands = new Map();
let session, script, cleanupAcknowledged = false, shuttingDown = false, startupCleaned = false, targetExited = false;
const keepAlive = setInterval(() => {}, 1000);

try {
  if (!Number.isSafeInteger(pid) || pid <= 0 || !packagePath) throw new Error('Explicit --pid and --frida paths are required.');
  const manifest = JSON.parse(await fs.readFile(path.join(packagePath, 'package.json'), 'utf8'));
  if (manifest.name !== 'frida' || manifest.version !== '17.23.2') throw new Error('Only the reviewed Frida 17.23.2 runtime is supported.');
  const frida = await import(pathToFileURL(path.join(packagePath, 'build', 'src', 'frida.js')).href);
  session = await frida.attach(pid);
  session.detached.connect(reason => { if (reason === 'process-terminated') targetExited = true; });
  const source = await fs.readFile(new URL('./agent.js', import.meta.url), 'utf8');
  script = await session.createScript(source, { name: 'SymbioteControlNative' });
  script.message.connect(message => { if (message.type === 'error') process.stderr.write(`${message.stack ?? message.description}\n`); });
  await script.load();
  const status = await script.exports.status(null);
  send({ id: 'startup', ok: true, state: 'completed', outcome: 'ready', pid, ...status });
} catch (error) {
  // No commands can have been queued before startup acknowledges readiness, so
  // unloading a failed initialization cannot discard an ability mutation.
  const cleanupErrors = [];
  if (script) { try { await script.unload(); script = null; } catch (failure) { cleanupErrors.push(String(failure)); } }
  if (session) { try { await session.detach(); session = null; } catch (failure) { cleanupErrors.push(String(failure)); } }
  startupCleaned = cleanupErrors.length === 0;
  send({ id: 'startup', ok: false, state: startupCleaned ? 'rejected' : 'unknown', outcome: 'startup-failed',
    error: String(error.stack ?? error), cleaned: startupCleaned, attached: !!session, cleanupErrors });
  if (startupCleaned) { clearInterval(keepAlive); process.exitCode = 1; }
}

async function handle(request) {
  const id = request.id;
  if (typeof id !== 'string' || !/^[a-zA-Z0-9_-]{1,80}$/.test(id)) throw new Error('A unique bounded command ID is required.');
  if (!['grant', 'remove', 'probe', 'status', 'cleanup', 'shutdown', 'target-exited'].includes(request.command)) throw new Error('Unsupported fixed command.');
  if (shuttingDown) throw new Error('Helper is shutting down.');
  if (request.command === 'status') {
    const status = script ? await script.exports.status(request.queryId ?? null) : { bindingEnabled: false, commands: [], startupIncomplete: true };
    return { id, ok: true, state: 'completed', outcome: 'status', ...status };
  }
  if (request.command === 'target-exited') {
    if (!targetExited) return { id, ok: false, state: 'rejected', outcome: 'target-exit-not-confirmed', detached: false };
    // The process-terminated signal proves no live inventory remains to write.
    // This releases host resources and expressly makes no restoration claim.
    shuttingDown = true;
    if (script) { try { await script.unload(); } catch (_) { /* destroyed with target */ } script = null; }
    if (session && !session.isDetached()) await session.detach();
    session = null;
    clearInterval(keepAlive);
    return { id, ok: true, state: 'completed', outcome: 'target-exited', detached: true, targetExited: true,
      cleanupAcknowledged: false, inventoryRestoration: 'not-claimed; target process exited' };
  }
  if (request.command === 'cleanup') {
    if (!script) {
      cleanupAcknowledged = !session;
      return { id, ok: cleanupAcknowledged, state: cleanupAcknowledged ? 'completed' : 'unknown', outcome: 'cleanup', cleanupAcknowledged };
    }
    const status = await script.exports.cleanup();
    cleanupAcknowledged = status.cleanupAcknowledged === true;
    return { id, ...status };
  }
  if (request.command === 'shutdown') {
    if (!cleanupAcknowledged) return { id, ok: false, state: 'rejected', outcome: 'cleanup-required', detached: false };
    shuttingDown = true;
    if (script) await script.unload();
    if (session) await session.detach();
    clearInterval(keepAlive);
    return { id, ok: true, state: 'completed', outcome: 'shutdown', detached: true };
  }
  if (!script) return { id, ok: false, state: 'rejected', outcome: 'not-attached' };
  cleanupAcknowledged = false;
  return { id, ...await script.exports.submit({ id, command: request.command, handle: request.handle, ids: request.ids }) };
}

const input = readline.createInterface({ input: process.stdin, crlfDelay: Infinity });
if (startupCleaned) input.close();
input.on('line', line => {
  let request;
  try { request = JSON.parse(line); } catch (error) { process.stderr.write(`Invalid JSON: ${error}\n`); return; }
  const fingerprint = JSON.stringify(request);
  const existing = commands.get(request.id);
  if (existing) {
    if (existing.fingerprint !== fingerprint) send({ id: request.id, ok: false, state: 'rejected', outcome: 'id-reused-with-different-command' });
    else existing.promise.then(send);
    return;
  }
  const promise = handle(request).catch(error => ({ id: request.id, ok: false, state: 'unknown', outcome: 'helper-error', error: String(error.stack ?? error) }));
  commands.set(request.id, { fingerprint, promise });
  promise.then(result => {
    send(result);
    if (result.detached === true) { input.close(); process.exitCode = 0; }
  });
});
input.on('close', () => {
  if (!shuttingDown) process.stderr.write('Input closed before acknowledged cleanup. Helper retained; native state must be reconciled before detaching.\n');
});
