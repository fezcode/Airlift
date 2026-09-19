import test from 'node:test';
import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import { nativePng } from '../scripts/icon-assets.mjs';

test('every shipped native icon is the largest PNG frame of its matching catalog icon', async () => {
  const { apps } = JSON.parse(await readFile(new URL('../src/data/catalog.json', import.meta.url), 'utf8'));
  for (const app of apps) {
    const source = await readFile(new URL('../public' + app.icon, import.meta.url));
    const native = await readFile(new URL(`../native/Airlift.Desktop/Assets/Icons/${app.id}.png`, import.meta.url));
    assert.deepEqual(native, nativePng(source), app.name);
    assert.ok(native.readUInt32BE(16) >= 128, `${app.name} needs a high-resolution native icon`);
  }
});

test('invalid ICO bounds fail rather than exporting truncated native icons', () => {
  const ico = Buffer.alloc(22); ico.writeUInt16LE(1, 2); ico.writeUInt16LE(1, 4);
  ico.writeUInt32LE(100, 14); ico.writeUInt32LE(22, 18);
  assert.throws(() => nativePng(ico), /bounds/);
  assert.throws(() => nativePng(Buffer.from('not an icon')), /PNG or Windows ICO/);
});
