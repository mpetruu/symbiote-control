// Executes the actual agent source against a mock Frida/memory surface.
// No Frida import, process attachment, or game access occurs.
import fs from 'node:fs/promises';
import vm from 'node:vm';
import assert from 'node:assert/strict';

const base = 0x140000000n, actorPool = 0x20000000n, actor = actorPool + 7n * 0xc0n;
const handle = (2 << 20) | 7, context = 0x30000000n, rage = 0x30010000n, character = 0x30020000n;
const manager = 0x30030000n, inventory = 0x30040000n, componentPool = 0x40000000n;
const inventoryHandle = (2 << 20) | 3, hooks = new Map(), memory = new Map();
const calls = [], checks = [];
let phase = 'idle', flushes = 0;
const codes = new Map([
  [0x113c2b0, 'C681AC090000018891AD090000448881AE090000C3'],
  [0x10d2e60, '0FB68190000000A80174080C08888190000000C3'],
  [0x34bcd00, '48895C241055565741564157488DAC2420F8FFFF4881ECE0'],
  [0x34bd4c0, '48895C240848896C241844894C2420488954241056574156'],
  [0x34c9880, '40534883EC60488B014C8BC2488D542470488BD9FF903802']
].map(([offset, hex]) => [(base + BigInt(offset)).toString(), Buffer.from(hex, 'hex')]));
class Pointer {
  constructor(value) { this.value = BigInt(value); }
  add(value) { return new Pointer(this.value + BigInt(value)); }
  readPointer() { return new Pointer(memory.get(this.value.toString()) ?? 0n); }
  readU16() { return Number(memory.get(this.value.toString()) ?? 0); }
  readU32() { return this.readU16(); }
  readByteArray(length) { const bytes = codes.get(this.value.toString()); assert(bytes && bytes.length === length); return Uint8Array.from(bytes).buffer; }
  isNull() { return this.value === 0n; }
  equals(other) { return this.value === other.value; }
  toString() { return `0x${this.value.toString(16)}`; }
}
const put = (address, value) => memory.set(address.toString(), value);
put(base + 0x9bd1134n, handle); put(base + 0xc2d3dacn, 16); put(base + 0xc2d3d90n, actorPool);
put(actor + 8n, 2); put(rage, base + 0x5439440n); put(rage + 0x10n, actor); put(rage + 0x48n, context);
put(context + 0x220n, rage); put(context + 0x78n, character); put(character, base + 0x5435c08n);
put(character + 0x10n, actor); put(character + 0x58n, context); put(character + 0x98n, 0);
put(context + 0xc8n, manager); put(manager + 0x2118n, inventoryHandle); put(base + 0xc2d3d40n, 16);
put(base + 0xc2d3d20n, componentPool); put(componentPool + 3n * 16n + 8n, 2); put(componentPool + 3n * 16n, inventory);
put(inventory, base + 0x5484908n);
const ids = ['b6c5c279a896d94b', 'a8e14599ded2e3fd', '96cf9952f3b72010', '9118f67431e68a1d'];
const items = ids.map((id, index) => {
  const item = 0x50000000n + BigInt(index) * 0x1000n;
  const vtable = 0x60000000n + BigInt(index) * 0x1000n;
  const properties = 0x70000000n + BigInt(index) * 0x1000n;
  const config = 0x80000000n + BigInt(index) * 0x1000n;
  const getter = 0x90000000n + BigInt(index) * 0x1000n;
  put(item, vtable); put(vtable + 0x120n, getter); put(properties + 0x3cn, 40000 + index); put(config + 0x268n, index);
  return { id, item, properties, config, getter };
});
function NativeFunction(address) {
  return (...args) => {
    assert.equal(phase, 'leave', 'Every native call must execute on the RM onLeave boundary');
    calls.push(address.toString());
    if (address.value === base + 0x34c9880n) return new Pointer(items.find(item => BigInt(`0x${item.id}`) === args[1]).item);
    if (address.value === base + 0x34c9990n) return 1;
    if (address.value === base + 0x34ba770n) return new Pointer(items.find(item => item.item === args[0].value).config);
    const item = items.find(item => item.getter === address.value);
    if (item) return new Pointer(item.properties);
    throw new Error(`Unexpected mutating native function: ${address}`);
  };
}
const rpc = { exports: {} };
const sandbox = {
  rpc, Uint8Array, Map, Set, Date, Promise, Number, Error, String, Array,
  setImmediate, setTimeout, uint64: value => BigInt(value), NativeFunction,
  Process: { getModuleByName: () => ({ base: new Pointer(base) }), getCurrentThreadId: () => 99 },
  Interceptor: {
    attach(address, callbacks) {
      const listener = { callbacks, detached: false, detach() { this.detached = true; } };
      hooks.set(address.toString(), listener);
      return listener;
    },
    flush() { flushes++; }
  }
};
vm.runInNewContext(await fs.readFile(new URL('../agent.js', import.meta.url), 'utf8'), sandbox);
assert.equal(hooks.size, 1); checks.push('Read-only startup installs only the RM listener, not the Add observer');
const hook = hooks.get(new Pointer(base + 0x10d37c0n).toString());
const response = rpc.exports.submit({ id: 'probe', command: 'probe', handle, ids });
const invocation = {};
phase = 'enter'; hook.callbacks.onEnter.call(invocation, [new Pointer(rage)]);
assert.equal(calls.length, 0); checks.push('RM onEnter captures the receiver without native calls');
phase = 'leave'; hook.callbacks.onLeave.call(invocation);
phase = 'idle';
const result = await response;
assert.equal(result.ok, true); assert.equal(result.results.length, 4); assert.equal(calls.length, 16);
checks.push('Probe native calls execute onLeave and acknowledge asynchronously');
const flight = {};
phase = 'enter'; hook.callbacks.onEnter.call(flight, [new Pointer(rage)]); phase = 'idle';
let finished = false;
const cleanup = rpc.exports.cleanup().then(value => { finished = true; return value; });
await new Promise(resolve => setTimeout(resolve, 20));
assert.equal(finished, false); assert.equal(hook.detached, false);
checks.push('Cleanup refuses early acknowledgement while a native update frame remains active');
phase = 'leave'; hook.callbacks.onLeave.call(flight); phase = 'idle';
const clean = await cleanup;
assert.equal(clean.cleanupAcknowledged, true); assert.equal(clean.hooksDetached, true);
assert.equal(clean.activeUpdateFrames, 0); assert.equal(clean.activeAddFrames, 0); assert.equal(flushes, 1);
assert.equal(hook.detached, true);
checks.push('Cleanup drains, detaches, flushes, and acknowledges zero active frames');
console.log(JSON.stringify({ passed: true, scope: 'Mock Frida only; no process attachment', checks }, null, 2));
