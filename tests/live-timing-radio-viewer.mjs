// Viewer race radio playback rules with a stubbed DOM, audio element and clock. Run: node tests/live-timing-radio-viewer.mjs
import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import vm from 'node:vm';
import assert from 'node:assert/strict';

const source = readFileSync(fileURLToPath(new URL('../src/OpenSkiTime.LiveTiming.Server/wwwroot/live-radio.js', import.meta.url)), 'utf8');
let now = Date.parse('2026-10-02T09:00:00Z');
const timers = [];
const played = [];
const listeners = {};
const button = { hidden: true, textContent: '', title: '', attributes: {}, setAttribute(n, v) { this.attributes[n] = String(v); }, addEventListener(t, f) { listeners['button:' + t] = f; } };
class FakeAudio {
  constructor() { this.src = ''; }
  addEventListener(t, f) { listeners['audio:' + t] = f; }
  play() { played.push(this.src.split('/').pop()); return Promise.resolve(); }
  pause() {}
  removeAttribute() { this.src = ''; }
}
const context = {
  document: { getElementById: () => button }, Audio: FakeAudio, encodeURIComponent,
  Date: { now: () => now, parse: Date.parse },
  setTimeout: (f, ms) => { const t = { f, at: now + ms }; timers.push(t); return t; },
  clearTimeout: t => { const i = timers.indexOf(t); if (i >= 0) timers.splice(i, 1); }
};
vm.createContext(context);
vm.runInContext(source + '\nthis.liveRadio = liveRadio;', context);
const radio = context.liveRadio;
async function advance(ms) {
  const until = now + ms;
  for (;;) {
    timers.sort((a, b) => a.at - b.at);
    const next = timers[0];
    if (!next || next.at > until) break;
    timers.shift(); now = next.at; next.f();
    await Promise.resolve();
  }
  now = until;
}
const end = async () => { listeners['audio:ended'](); await Promise.resolve(); };
const clip = (id, batch, line, lineCount, priority, sequence, interruptible = line === lineCount - 1) =>
  ({ clipId: id, batchId: batch, line, lineCount, priority, sequence, interruptible, expiresAt: new Date(now + 120000).toISOString(), host: 'Aino', durationMs: 1000 });

// Hidden until a healthy radio is announced; paused by default and never autoplays.
radio.update('s', { available: false, clips: [] });
assert.equal(button.hidden, true);
const old = clip('old', 'b0', 0, 1, 3, 1);
radio.update('s', { available: true, clips: [old] });
assert.equal(button.hidden, false);
assert.deepEqual(played, []);
// Joining at the live edge skips clips announced more than a few seconds earlier.
await advance(20000);
listeners['button:click']();
assert.equal(button.attributes['aria-pressed'], 'true');
assert.deepEqual(played, []);
// A two-line conversation plays in order with a short pause.
radio.update('s', { available: true, clips: [old, clip('a1', 'b1', 0, 2, 3, 2, false), clip('a2', 'b1', 1, 2, 3, 3)] });
await Promise.resolve();
assert.deepEqual(played, ['a1']);
// Urgent news arriving mid-sentence waits for the conversation's natural break.
radio.update('s', { available: true, clips: [clip('a1', 'b1', 0, 2, 3, 2, false), clip('a2', 'b1', 1, 2, 3, 3), clip('u1', 'b2', 0, 1, 2, 4)] });
await end(); await advance(350);
assert.deepEqual(played, ['a1', 'a2']);
await end(); await advance(350);
assert.deepEqual(played, ['a1', 'a2'], 'a new conversation waits for the longer pause');
await advance(850);
assert.deepEqual(played, ['a1', 'a2', 'u1']);
await end();
// A clip the server no longer lists (stale) is never played; a batch whose next line never arrives is given up.
radio.update('s', { available: true, clips: [clip('c1', 'b3', 0, 3, 3, 5, false), clip('stale', 'b4', 0, 1, 3, 6)] });
await advance(1200);
assert.deepEqual(played.slice(-1), ['c1']);
radio.update('s', { available: true, clips: [clip('c1', 'b3', 0, 3, 3, 5, false), clip('d1', 'b5', 0, 1, 4, 7)] });
await end(); await advance(5000);
assert.deepEqual(played.slice(-1), ['c1'], 'waits briefly for the next line of an unfinished sentence');
await advance(6000);
assert.deepEqual(played.slice(-1), ['d1']);
// Pause stops at once; an unavailable radio hides the control and stops playback.
listeners['button:click']();
assert.equal(button.attributes['aria-pressed'], 'false');
radio.update('s', { available: false, clips: [] });
assert.equal(button.hidden, true);
console.log('live-radio viewer checks passed');
