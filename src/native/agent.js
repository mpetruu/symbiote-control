'use strict';

const ALLOWED = new Set(['b6c5c279a896d94b', 'a8e14599ded2e3fd', '96cf9952f3b72010', '9118f67431e68a1d']);
const base = Process.getModuleByName('Spider-Man2.exe').base;
const records = new Map(), leases = new Map(), tableLeases = new Map();
let pending = null, hook = null, running = false, accepting = true;
let helperNativeDepth = 0, helperNativeThread = null;
let activeUpdateFrames = 0, activeAddFrames = 0, quiescing = false, addObserver = null;

function equalCode(rva, hex) {
  const bytes = new Uint8Array(base.add(rva).readByteArray(hex.length / 2));
  return Array.from(bytes, byte => byte.toString(16).padStart(2, '0')).join('') === hex.toLowerCase();
}
for (const [rva, hex] of [
  [0x113c2b0, 'C681AC090000018891AD090000448881AE090000C3'],
  [0x10d2e60, '0FB68190000000A80174080C08888190000000C3'],
  [0x34bcd00, '48895C241055565741564157488DAC2420F8FFFF4881ECE0'],
  [0x34bd4c0, '48895C240848896C241844894C2420488954241056574156'],
  [0x34c9880, '40534883EC60488B014C8BC2488D542470488BD9FF903802']
]) if (!equalCode(rva, hex)) throw new Error(`Unsupported live native code at RVA 0x${rva.toString(16)}.`);

// ABI and addresses supplied by the root's independent REA investigation.
const addItem = new NativeFunction(base.add(0x34bcd00), 'uint16', ['pointer', 'uint64', 'int']);
const findItem = new NativeFunction(base.add(0x34c9880), 'pointer', ['pointer', 'uint64']);
const quantity = new NativeFunction(base.add(0x34c9990), 'int', ['pointer', 'uint64']);
const removeItem = new NativeFunction(base.add(0x34bd4c0), 'pointer', ['pointer', 'pointer', 'uint64', 'int']);
const destroyScratch = new NativeFunction(base.add(0x34b9830), 'void', ['pointer']);
const gearConfig = new NativeFunction(base.add(0x34ba770), 'pointer', ['pointer']);

function selectedHero(rage, handle) {
  if (rage.isNull() || !rage.readPointer().equals(base.add(0x5439440))) throw new Error('Rage receiver vtable differs.');
  const index = handle & 0xfffff, generation = (handle >>> 20) & 0x7ff;
  const count = base.add(0xc2d3dac).readU32();
  if (index >= count || count > 0x100000) throw new Error('Selected actor slot is unavailable.');
  const actor = base.add(0xc2d3d90).readPointer().add(index * 0xc0);
  if (actor.add(8).readU16() !== generation || !rage.add(0x10).readPointer().equals(actor)) throw new Error('Selected actor generation differs.');
  const context = rage.add(0x48).readPointer();
  if (context.isNull() || !context.add(0x220).readPointer().equals(rage)) throw new Error('Rage context backpointer differs.');
  const character = context.add(0x78).readPointer();
  if (character.isNull() || !character.readPointer().equals(base.add(0x5435c08)) ||
      !character.add(0x10).readPointer().equals(actor) || !character.add(0x58).readPointer().equals(context) ||
      character.add(0x98).readU32() !== 0) throw new Error('Selected actor is not the verified Peter instance.');
  if (base.add(0x9bd1134).readU32() !== handle) throw new Error('Selected Peter is no longer active.');
  return { handle, actor, context, rage, character };
}

function equipmentInventory(hero) {
  const manager = hero.context.add(0xc8).readPointer();
  if (manager.isNull()) throw new Error('Game inventory manager is unavailable.');
  const handle = manager.add(0x2118).readU32();
  const index = handle & 0xfffff, generation = handle >>> 20;
  const count = base.add(0xc2d3d40).readU32();
  if (index >= count || count > 0x100000) throw new Error('Equipment inventory slot is unavailable.');
  const slot = base.add(0xc2d3d20).readPointer().add(index * 16);
  if (slot.add(8).readU32() !== generation) throw new Error('Equipment inventory generation differs.');
  const inventory = slot.readPointer();
  if (inventory.isNull() || !inventory.readPointer().equals(base.add(0x5484908))) throw new Error('Equipment inventory vtable differs.');
  if (manager.add(0x2118).readU32() !== handle || slot.add(8).readU32() !== generation) throw new Error('Equipment inventory changed during resolution.');
  return { inventory, inventoryHandle: handle };
}

function inspect(inventory, id) {
  const asset = uint64(`0x${id}`);
  const item = findItem(inventory, asset);
  const count = quantity(inventory, asset);
  if (count < 0 || count > 1 || (item.isNull() && count !== 0)) throw new Error(`Unexpected native inventory state for ${id}.`);
  const installed = !item.isNull() && count > 0;
  let subtype = null, defaultSlot = null;
  if (installed) {
    const propertiesFunction = new NativeFunction(item.readPointer().add(0x120).readPointer(), 'pointer', ['pointer']);
    const properties = propertiesFunction(item);
    const config = gearConfig(item);
    if (properties.isNull() || config.isNull()) throw new Error(`Native gear metadata is unavailable for ${id}.`);
    subtype = properties.add(0x3c).readU16();
    defaultSlot = config.add(0x268).readU32();
  }
  return { id, item: item.toString(), quantity: count, installed, subtype, defaultSlot };
}

function nativeMutation(action) {
  const previousThread = helperNativeThread;
  helperNativeThread = Process.getCurrentThreadId();
  helperNativeDepth++;
  try { return action(); }
  finally { helperNativeDepth--; helperNativeThread = previousThread; }
}

function dirtyAbilities(hero) {
  const sam = hero.context.add(0x180).readPointer();
  if (sam.isNull() || !sam.readPointer().equals(base.add(0x543a578)) || !sam.add(0x10).readPointer().equals(hero.actor))
    throw new Error('Ability manager identity differs.');
  sam.add(0x7f4).writeU8(sam.add(0x7f4).readU8() | 0x10);
}

function inventoryKey(resolved) { return `${resolved.inventoryHandle}:${resolved.inventory.toString()}`; }
function rawTable(address) { return Array.from(new Uint8Array(address.readByteArray(8))); }
function sameBytes(left, right) { return left.length === right.length && left.every((value, index) => value === right[index]); }
function words(bytes) { return [0, 1, 2, 3].map(index => bytes[index * 2] | (bytes[index * 2 + 1] << 8)); }
function wordBytes(values) { return values.flatMap(value => [value & 0xff, value >>> 8]); }
function unknownOutcome(message, cause) {
  const error = new Error(cause ? `${message}: ${cause}` : message);
  error.unknownOutcome = true;
  return error;
}
function writeTable(address, bytes, label) {
  try {
    address.writeByteArray(bytes);
    if (!sameBytes(rawTable(address), bytes)) throw new Error('Readback differs.');
  } catch (error) { throw unknownOutcome(`${label} could not be confirmed`, error); }
}

function prepareTableLease(hero) {
  const resolved = equipmentInventory(hero), inventory = resolved.inventory;
  const key = inventoryKey(resolved);
  const existing = tableLeases.get(key);
  if (existing?.owned) return existing;
  const group = inventory.add(0x2408).readU32();
  if (group > 4) throw new Error('Equipment group is outside the verified table range.');
  const address = inventory.add(0x23dc + group * 8);
  const originalRaw = rawTable(address), originalIds = words(originalRaw);
  const sam = hero.context.add(0x180).readPointer();
  if (sam.isNull() || !sam.readPointer().equals(base.add(0x543a578)) || !sam.add(0x10).readPointer().equals(hero.actor))
    throw new Error('Ability cache identity differs while snapshotting equipment.');
  const count = sam.add(0x658).readU32();
  if (count > 12) throw new Error('Ability cache count is invalid.');
  const originalAssetIds = originalIds.map(subtype => {
    if (subtype === 0) return null;
    for (let index = 0; index < count; index++) {
      const entry = sam.add(0x58 + index * 0x80);
      if (entry.add(0x24).readU16() !== subtype) continue;
      const id = entry.add(0x6c).readU64().toString(16).padStart(16, '0').toLowerCase();
      // A stale slot naming a removed power must remain empty on restoration;
      // it must never be rebound to a temporary power added by this bridge.
      return inspect(inventory, id).installed ? id : null;
    }
    return null;
  });
  const lease = { handle: hero.handle, inventory: inventory.toString(), inventoryHandle: resolved.inventoryHandle,
    group, originalRaw, originalIds, originalAssetIds, lastWrittenRaw: null, owned: false, state: 'snapshotted' };
  tableLeases.set(key, lease);
  return lease;
}

function equipSymbiote(hero, metadata) {
  const resolved = equipmentInventory(hero), lease = tableLeases.get(inventoryKey(resolved));
  if (!lease) throw new Error('Equipment baseline snapshot is missing.');
  if (resolved.inventory.add(0x2408).readU32() !== lease.group) throw new Error('Equipment group changed before writing.');
  const address = resolved.inventory.add(0x23dc + lease.group * 8);
  const current = rawTable(address);
  const expected = lease.owned ? lease.lastWrittenRaw : lease.originalRaw;
  if (!sameBytes(current, expected)) throw new Error('Equipment choices changed externally before assignment; write refused.');
  const selected = [0, 0, 0, 0];
  for (const item of metadata) {
    if (!item.installed || !Number.isInteger(item.subtype) || item.subtype <= 0 || item.subtype >= 0xffff ||
        !Number.isInteger(item.defaultSlot) || item.defaultSlot < 0 || item.defaultSlot > 3 || selected[item.defaultSlot] !== 0)
      throw new Error('Four installed powers with distinct verified logical slots are required.');
    selected[item.defaultSlot] = item.subtype;
  }
  if (selected.some(id => id === 0)) throw new Error('Canonical symbiote equipment mapping is incomplete.');
  selectedHero(hero.rage, hero.handle);
  const bytes = wordBytes(selected);
  writeTable(address, bytes, 'Equipment assignment');
  lease.lastWrittenRaw = bytes;
  lease.owned = true;
  lease.state = 'equipped';
  dirtyAbilities(hero);
  return lease;
}

function restoreEquipment(hero) {
  const resolved = equipmentInventory(hero), lease = tableLeases.get(inventoryKey(resolved));
  if (!lease?.owned) return lease ?? null;
  const address = resolved.inventory.add(0x23dc + lease.group * 8);
  const current = rawTable(address);
  if (resolved.inventory.add(0x2408).readU32() !== lease.group || !sameBytes(current, lease.lastWrittenRaw)) {
    lease.owned = false;
    lease.state = 'external-selection-preserved';
    lease.observedAtRestore = current;
    return lease;
  }
  const restored = lease.originalAssetIds.map(id => {
    if (id === null) return 0;
    const original = inspect(resolved.inventory, id);
    return original.installed ? original.subtype : 0;
  });
  const bytes = wordBytes(restored);
  selectedHero(hero.rage, hero.handle);
  writeTable(address, bytes, 'Equipment restoration');
  lease.owned = false;
  lease.state = 'restored-logical-choices';
  lease.restoredIds = restored;
  dirtyAbilities(hero);
  return lease;
}

function execute(command, hero, id) {
  const resolved = equipmentInventory(hero);
  const inventory = resolved.inventory;
  const before = inspect(inventory, id);
  const key = `${resolved.inventoryHandle}:${inventory.toString()}:${id}`;
  if (command === 'probe') return { ...before, changed: false, inventory: inventory.toString(), inventoryHandle: resolved.inventoryHandle };
  if (command === 'grant') {
    if (before.installed) return { ...before, changed: false, outcome: 'already-owned' };
    const lease = { handle: hero.handle, id, inventory: inventory.toString(), inventoryHandle: resolved.inventoryHandle,
      owned: true, state: 'grant-in-progress', original: before };
    leases.set(key, lease);
    ensureAddObserver();
    let returnedItemIdentity, after;
    try {
      returnedItemIdentity = nativeMutation(() => addItem(inventory, uint64(`0x${id}`), 1));
      after = inspect(inventory, id);
    } catch (error) { throw unknownOutcome(`Native Add outcome is unknown for ${id}`, error); }
    Object.assign(lease, { returnedItemIdentity, item: after.item, quantity: after.quantity, subtype: after.subtype, defaultSlot: after.defaultSlot });
    if (returnedItemIdentity === 0xffff || !after.installed || after.quantity !== 1) throw unknownOutcome(`Native Add postcondition is unknown for ${id}.`);
    lease.state = 'granted';
    const result = { ...after, changed: true, inventory: inventory.toString(), returnedItemIdentity, cleanupOwned: lease.owned };
    try { dirtyAbilities(hero); }
    catch (error) { result.error = `Grant postcondition is known; ability refresh failed: ${error}`; }
    return result;
  }
  const lease = leases.get(key);
  if (!lease || !lease.owned) return { ...before, changed: false, outcome: 'not-owned-by-bridge' };
  if (lease.inventory !== inventory.toString() || lease.inventoryHandle !== resolved.inventoryHandle ||
      lease.state !== 'granted' || lease.item !== before.item || lease.quantity !== before.quantity || before.quantity !== 1)
    throw new Error(`Cleanup identity or quantity changed for ${id}; removal refused.`);
  const group = inventory.add(0x2408).readU32();
  if (group > 4) throw new Error('Equipment group is invalid during cleanup.');
  if (words(rawTable(inventory.add(0x23dc + group * 8))).includes(lease.subtype))
    throw new Error(`The player or game selected temporary power ${id}; removal refused to preserve the current equipment choice.`);
  const scratch = Memory.alloc(8);
  scratch.writeByteArray([0xff, 0xff, 0, 0, 0, 0, 0, 0]);
  let after;
  try {
    nativeMutation(() => {
      try { removeItem(inventory, scratch, uint64(`0x${id}`), 1); }
      finally { destroyScratch(scratch); }
    });
    after = inspect(inventory, id);
  } catch (error) { throw unknownOutcome(`Native Remove outcome is unknown for ${id}`, error); }
  if (after.quantity !== 0) throw unknownOutcome(`Native Remove postcondition is unknown for ${id}.`);
  lease.owned = false;
  lease.state = 'removed';
  const result = { ...after, changed: true, outcome: 'removed' };
  try { dirtyAbilities(hero); }
  catch (error) { result.error = `Removal postcondition is known; ability refresh failed: ${error}`; }
  return result;
}

// Capped story awards can leave quantity unchanged. Every outside Add therefore
// relinquishes ownership, preserving a legitimately earned power on toggle off.
function ensureAddObserver() {
  if (addObserver !== null) return;
  addObserver = Interceptor.attach(base.add(0x34bcd00), {
  onEnter(args) {
    activeAddFrames++;
    if (helperNativeDepth > 0 && helperNativeThread === Process.getCurrentThreadId()) return;
    const inventory = args[0].toString();
    const id = args[1].toString().replace(/^0x/, '').padStart(16, '0').toLowerCase();
    if (!ALLOWED.has(id)) return;
    for (const lease of leases.values())
      if (lease.owned && lease.inventory === inventory && lease.id === id) {
        lease.owned = false;
        lease.state = 'relinquished-after-external-native-add';
      }
  },
  onLeave() {
    activeAddFrames--;
  }
  });
}

function finish(record, result) {
  Object.assign(record, result);
  const resolve = record.resolve;
  delete record.resolve;
  if (resolve) setImmediate(() => resolve(publicRecord(record)));
}
function publicRecord(record) { const { resolve, ...value } = record; return value; }

function reconcileLeases() {
  for (const lease of [...leases.values(), ...tableLeases.values()]) {
    if (!lease.owned) continue;
    try {
      const index = lease.inventoryHandle & 0xfffff;
      const expectedGeneration = lease.inventoryHandle >>> 20;
      const count = base.add(0xc2d3d40).readU32();
      if (index >= count || count > 0x100000) {
        lease.reconciliation = 'Original pool slot is outside the current readable count; ownership retained.';
        continue;
      }
      const pool = base.add(0xc2d3d20).readPointer();
      if (pool.isNull()) { lease.reconciliation = 'Pool unavailable; ownership retained.'; continue; }
      const observedGeneration = pool.add(index * 16 + 8).readU32();
      if (observedGeneration !== expectedGeneration) {
        lease.owned = false;
        lease.state = 'retired-inventory-generation-destroyed';
        lease.retired = { expectedGeneration, observedGeneration, physicalValuesRestored: false,
          reason: 'Original equipment inventory component generation no longer exists.' };
      } else lease.reconciliation = 'Original pool generation remains alive.';
    } catch (error) { lease.reconciliation = `Identity read failed; ownership retained: ${error}`; }
  }
}

hook = Interceptor.attach(base.add(0x10d37c0), {
  onEnter(args) {
    this.rage = args[0];
    activeUpdateFrames++;
  },
  onLeave() {
    try {
    if (!pending || running || quiescing) return;
    const record = pending;
    let hero;
    try { hero = selectedHero(this.rage, record.handle); }
    catch (error) { record.lastValidationError = String(error.message ?? error); return; }
    pending = null;
    running = true;
    record.state = 'running';
    record.threadId = Process.getCurrentThreadId();
    record.results = [];
    try {
      if (record.command === 'grant') prepareTableLease(hero);
      if (record.command === 'remove') record.equipmentLease = restoreEquipment(hero);
      for (const id of record.ids) record.results.push(execute(record.command, hero, id));
      if (record.command === 'grant') {
        const changes = new Map(record.results.map(result => [result.id, result]));
        const inventory = equipmentInventory(hero).inventory;
        record.results = [...ALLOWED].map(id => ({ ...inspect(inventory, id), changed: false, ...changes.get(id) }));
        record.equipmentLease = equipSymbiote(hero, record.results);
      }
      const ok = record.results.every(result => !result.error);
      finish(record, { ok, state: 'completed', outcome: !ok ? 'known-postconditions-with-errors' : record.command === 'probe' ? 'observed' : 'applied',
        missing: record.results.filter(result => !result.installed).map(result => result.id), grantLedger: [...leases.values()], tableLeases: [...tableLeases.values()], equipmentLeases: [...tableLeases.values()] });
    } catch (error) {
      finish(record, { ok: false, state: record.command === 'probe' ? 'rejected' : error.unknownOutcome ? 'unknown' : 'completed',
        outcome: record.command === 'probe' ? 'probe-failed' : error.unknownOutcome ? 'native-outcome-unknown' : 'known-partial-failure',
        error: String(error.stack ?? error), grantLedger: [...leases.values()], equipmentLeases: [...tableLeases.values()] });
    } finally { running = false; }
    } finally { activeUpdateFrames--; }
  }
});

rpc.exports = {
  submit(command) {
    if (!accepting) return { ok: false, state: 'rejected', outcome: 'cleanup-in-progress' };
    if (!command || !['grant', 'remove', 'probe'].includes(command.command) || !Number.isInteger(command.handle) || command.handle < 0 || command.handle > 0xffffffff)
      return { ok: false, state: 'rejected', outcome: 'invalid-command' };
    if (!Array.isArray(command.ids) || command.ids.length < 1 || command.ids.length > 4 || new Set(command.ids).size !== command.ids.length || command.ids.some(id => !ALLOWED.has(id)))
      return { ok: false, state: 'rejected', outcome: 'unapproved-asset' };
    if (records.has(command.id)) return publicRecord(records.get(command.id));
    if (pending || running || [...records.values()].some(record => record.state === 'unknown'))
      return { ok: false, state: 'rejected', outcome: 'previous-command-unresolved' };
    const record = { id: command.id, command: command.command, handle: command.handle, ids: command.ids.slice(), ok: false, state: 'queued', outcome: 'queued' };
    records.set(record.id, record);
    return new Promise(resolve => { record.resolve = resolve; pending = record; });
  },
  status(commandId) {
    reconcileLeases();
    return { bindingEnabled: true, hookInstalled: hook !== null, observerInstalled: addObserver !== null,
      activeUpdateFrames, activeAddFrames, quiescing,
      commands: [...records.values()].filter(record => !commandId || record.id === commandId).map(publicRecord),
      leases: [...leases.values()], tableLeases: [...tableLeases.values()], equipmentLeases: [...tableLeases.values()], missing: [...leases.values()].filter(lease => lease.original?.quantity === 0).map(lease => lease.id),
      running, queuedId: pending?.id ?? null };
  },
  async cleanup() {
    reconcileLeases();
    if (pending?.command === 'probe' && !running) {
      const cancelled = pending;
      pending = null;
      finish(cancelled, { ok: false, state: 'rejected', outcome: 'read-only-probe-cancelled-before-execution' });
    }
    const unresolved = pending || running || [...records.values()].some(record => record.state === 'unknown');
    const owned = [...leases.values(), ...tableLeases.values()].filter(lease => lease.owned);
    let acknowledged = !unresolved && owned.length === 0;
    let hooksDetached = hook === null && addObserver === null;
    if (acknowledged) {
      accepting = false;
      quiescing = true;
      // Drain complete native invocations before removing their listeners.
      // Command completion itself is deferred until after RM onLeave, but
      // unrelated actor updates or external Add calls may still be in flight.
      const deadline = Date.now() + 5000;
      while ((activeUpdateFrames !== 0 || activeAddFrames !== 0) && Date.now() < deadline)
        await new Promise(resolve => setTimeout(resolve, 5));
      if (activeUpdateFrames === 0 && activeAddFrames === 0) {
        if (hook !== null) { hook.detach(); hook = null; }
        if (addObserver !== null) { addObserver.detach(); addObserver = null; }
        Interceptor.flush();
        // Leave the JavaScript runtime for multiple turns before permitting
        // script unload, letting any already-entered return trampoline finish.
        await new Promise(resolve => setTimeout(resolve, 100));
        hooksDetached = hook === null && addObserver === null;
      }
      acknowledged = hooksDetached && activeUpdateFrames === 0 && activeAddFrames === 0;
    }
    return { ok: acknowledged, state: acknowledged ? 'completed' : 'rejected', outcome: acknowledged ? 'clean' : 'cleanup-required',
      cleanupAcknowledged: acknowledged, unresolved: !!unresolved, hooksDetached, activeUpdateFrames, activeAddFrames,
      nativeExecutionBoundary: 'RM onLeave', leases: [...leases.values()], tableLeases: [...tableLeases.values()], equipmentLeases: [...tableLeases.values()] };
  }
};
