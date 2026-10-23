// Viewer race radio playback rules with a stubbed DOM, audio element and clock. Run: node tests/live-timing-radio-viewer.mjs
import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import vm from 'node:vm';
import assert from 'node:assert/strict';

const source = readFileSync(fileURLToPath(new URL('../src/OpenSkiTime.LiveTiming.Server/wwwroot/live-radio.js', import.meta.url)), 'utf8');
let now = Date.parse('2026-10-02T09:00:00Z');
const timers = [];
const played = [];
let unlocked = 0;
const listeners = {};
const button = { hidden: true, textContent: '', title: '', attributes: {}, setAttribute(n, v) { this.attributes[n] = String(v); }, addEventListener(t, f) { listeners['button:' + t] = f; } };
class FakeAudio {
  constructor() { this.src = ''; }
  addEventListener(t, f) { listeners['audio:' + t] = f; }
  play() { if (this.src.startsWith('blob:')) unlocked++; else played.push(this.src.split('/').pop()); return Promise.resolve(); }
  pause() {}
  removeAttribute() { this.src = ''; }
}
const context = {
  document: { getElementById: () => button }, Audio: FakeAudio, encodeURIComponent, Blob, URL,
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
const clip = (id, batch, line, lineCount, priority, sequence, interruptible = line === lineCount - 1, age = 0) =>
  ({ clipId: id, batchId: batch, line, lineCount, priority, sequence, interruptible, receivedAt: new Date(now - age).toISOString(),
     expiresAt: new Date(now + 120000).toISOString(), host: 'Aino', durationMs: 1000 });
const serverTime = () => new Date(now + 3600000).toISOString(); // The server clock runs an hour ahead of this browser.

// Hidden until a healthy radio is announced; paused by default and never autoplays.
radio.update('s', { available: false, clips: [] });
assert.equal(button.hidden, true);
// A clip already 30 s old on the server when this page loads is not news, even though the page sees it for the first time.
const backlog = { ...clip('backlog', 'bx', 0, 1, 3, 0), receivedAt: new Date(now + 3600000 - 30000).toISOString() };
radio.update('s', { available: true, clips: [backlog], serverTime: serverTime() });
listeners['button:click']();
assert.deepEqual(played, []);
listeners['button:click']();
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
// Expiry is judged on the server's clock: a browser five minutes ahead still plays a fresh two-minute clip.
await end(); await advance(1200);
const ahead = { ...clip('skew', 'b6', 0, 1, 3, 8), receivedAt: new Date(now - 300000).toISOString(), expiresAt: new Date(now - 300000 + 120000).toISOString() };
radio.update('s', { available: true, clips: [ahead], serverTime: new Date(now - 300000).toISOString() });
await advance(1200);
assert.deepEqual(played.slice(-1), ['skew']);
// A clip withdrawn while it plays stops at once, and the rest of its conversation is dropped.
await end(); await advance(1200);
radio.update('s', { available: true, clips: [clip('w1', 'b7', 0, 2, 3, 9, false), clip('w2', 'b7', 1, 2, 3, 10), clip('n1', 'b8', 0, 1, 3, 11)] });
await advance(1200);
assert.deepEqual(played.slice(-1), ['w1']);
radio.update('s', { available: true, clips: [clip('n1', 'b8', 0, 1, 3, 11)] });
await advance(1200);
assert.deepEqual(played.slice(-1), ['n1']);
// After a reconnect, clips that were already old when they arrived are skipped.
await end(); await advance(1200);
radio.update('s', { available: true, clips: [clip('stale-backlog', 'b9', 0, 1, 3, 12, true, 60000)], serverTime: new Date(now).toISOString() });
await advance(1200);
assert.deepEqual(played.slice(-1), ['n1']);
// An old opening line takes its fresh reply with it: no result is heard without the racer's name.
radio.update('s', { available: true, clips: [clip('open', 'b10', 0, 2, 3, 13, false, 20000), clip('reply', 'b10', 1, 2, 3, 14)], serverTime: new Date(now).toISOString() });
await advance(1200);
assert.deepEqual(played.slice(-1), ['n1']);
// A reply whose opening line never arrived is not played.
radio.update('s', { available: true, clips: [clip('orphan', 'b11', 1, 2, 3, 15)], serverTime: new Date(now).toISOString() });
await advance(1500);
assert.deepEqual(played.slice(-1), ['n1']);
// Pressing Radio with nothing queued plays a short silent sound inside the click (iOS Safari audio unlock).
const before = unlocked;
listeners['button:click'](); listeners['button:click']();
assert.equal(unlocked, before + 1);
// A missing middle line is never skipped over: after line 0, line 2 alone does not play.
radio.update('s', { available: true, clips: [clip('g0', 'b12', 0, 3, 3, 16, true), clip('g2', 'b12', 2, 3, 3, 18)], serverTime: new Date(now).toISOString() });
await advance(1500);
assert.deepEqual(played.slice(-1), ['g0']);
await end(); await advance(10000);
assert.deepEqual(played.slice(-1), ['g0']);
// An opening that fails to load drops its conversation: the reply is not played without it.
radio.update('s', { available: true, clips: [clip('f0', 'b13', 0, 2, 3, 19, false), clip('f1', 'b13', 1, 2, 3, 20)], serverTime: new Date(now).toISOString() });
await advance(1500);
assert.deepEqual(played.slice(-1), ['f0']);
listeners['audio:error'](); await advance(10000);
assert.deepEqual(played.slice(-1), ['f0']);
// Pause stops at once; an unavailable radio hides the control and stops playback.
listeners['button:click']();
assert.equal(button.attributes['aria-pressed'], 'false');
radio.update('s', { available: false, clips: [] });
assert.equal(button.hidden, true);
console.log('live-radio viewer checks passed');
