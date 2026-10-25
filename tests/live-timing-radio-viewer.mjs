// Viewer race radio playback rules with a stubbed DOM, audio element and clock. Run: node tests/live-timing-radio-viewer.mjs
import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import vm from 'node:vm';
import assert from 'node:assert/strict';

const source = readFileSync(fileURLToPath(new URL('../src/OpenSkiTime.LiveTiming.Server/wwwroot/live-radio.js', import.meta.url)), 'utf8');
let now = Date.parse('2026-10-02T09:00:00Z');
const timers = [];
const played = [], musicPlayed = [], audios = [];
let unlocked = 0;
const listeners = {};
const control = name => ({ hidden: true, textContent: '', title: '', attributes: {}, setAttribute(n, v) { this.attributes[n] = String(v); }, addEventListener(t, f) { listeners[name + ':' + t] = f; } });
const button = control('button'), musicButton = control('music-button');
// The first element plays speech, the second background music.
class FakeAudio {
  constructor() { this.src = ''; this.paused = true; this.volume = 1; this.kind = audios.length ? 'music' : 'audio'; audios.push(this); }
  addEventListener(t, f) { listeners[this.kind + ':' + t] = f; }
  play() {
    this.paused = false;
    if (this.kind === 'music') musicPlayed.push(this.src.split('/').pop());
    else if (this.src.startsWith('blob:')) unlocked++; else played.push(this.src.split('/').pop());
    return Promise.resolve();
  }
  pause() { this.paused = true; }
  removeAttribute() { this.src = ''; this.paused = true; }
}
const context = {
  document: { getElementById: id => (id === 'radio-music' ? musicButton : button) }, Audio: FakeAudio, encodeURIComponent, Blob, URL,
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
assert.deepEqual(played.slice(-1), ['c1'], 'waits for the next line of an unfinished sentence');
// The worker may still be synthesizing and uploading the next line for up to 35 s.
await advance(30000);
assert.deepEqual(played.slice(-1), ['c1'], 'a slow but successful next line is still awaited');
await advance(8000);
// The conversation queued behind it for 43 s is no longer news and is skipped; a fresh one plays.
assert.deepEqual(played.slice(-1), ['c1'], 'a conversation that waited past the live edge is skipped');
radio.update('s', { available: true, clips: [clip('c1', 'b3', 0, 3, 3, 5, false), clip('d1', 'b5', 0, 1, 4, 7), clip('d3', 'b16', 0, 1, 4, 23)] });
await advance(1200);
assert.deepEqual(played.slice(-1), ['d3']);
// A conversation whose played line the server withdraws went stale: the next conversation follows at once.
await end(); await advance(1200);
radio.update('s', { available: true, clips: [clip('x1', 'b14', 0, 2, 3, 21, false)], serverTime: new Date(now).toISOString() });
await advance(1200);
assert.deepEqual(played.slice(-1), ['x1']);
await end();
radio.update('s', { available: true, clips: [clip('y1', 'b15', 0, 1, 3, 22)], serverTime: new Date(now).toISOString() });
await advance(1200);
assert.deepEqual(played.slice(-1), ['y1']);
// Expiry is judged on the server's clock: a browser five minutes ahead still plays a fresh two-minute clip.
await end(); await advance(1200);
const ahead = { ...clip('skew', 'b6', 0, 1, 3, 8), receivedAt: new Date(now - 300000).toISOString(), expiresAt: new Date(now - 300000 + 120000).toISOString() };
radio.update('s', { available: true, clips: [ahead], serverTime: new Date(now - 300000).toISOString() });
await advance(1200);
assert.deepEqual(played.slice(-1), ['skew']);
// A clip withdrawn while it plays (a finish moved the results on) is not cut mid-sentence; the rest of its conversation is dropped.
await end(); await advance(1200);
radio.update('s', { available: true, clips: [clip('w1', 'b7', 0, 2, 3, 9, false), clip('w2', 'b7', 1, 2, 3, 10), clip('n1', 'b8', 0, 1, 3, 11)] });
await advance(1200);
assert.deepEqual(played.slice(-1), ['w1']);
radio.update('s', { available: true, clips: [clip('n1', 'b8', 0, 1, 3, 11)] });
await advance(1200);
assert.deepEqual(played.slice(-1), ['w1'], 'the withdrawn line keeps playing');
await end(); await advance(1200);
assert.deepEqual(played.slice(-1), ['n1'], 'its reply is dropped; the next conversation follows');
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
// While the radio plays, speech never drifts behind the race: a result (priority 2) goes before a waiting start
// (priority 3), and a start that waited longer than the live edge behind a long conversation is skipped.
await advance(1200);
radio.update('s', { available: true, clips: [clip('long', 'b20', 0, 1, 3, 40)], serverTime: new Date(now).toISOString() });
await advance(1200);
assert.deepEqual(played.slice(-1), ['long']);
radio.update('s', { available: true, clips: [clip('long', 'b20', 0, 1, 3, 40), clip('start', 'b21', 0, 2, 3, 41), clip('gate', 'b21', 1, 2, 3, 42),
  clip('dnf', 'b22', 0, 1, 2, 43)], serverTime: new Date(now).toISOString() });
await advance(5000); await end(); await advance(1200);
assert.deepEqual(played.slice(-1), ['dnf'], 'the result goes first');
await advance(11000); await end(); await advance(1200);
assert.deepEqual(played.slice(-1), ['dnf'], 'the start that waited 17 s is skipped with its gate line');
radio.update('s', { available: true, clips: [clip('start', 'b21', 0, 2, 3, 41), clip('gate', 'b21', 1, 2, 3, 42), clip('next', 'b23', 0, 1, 3, 44)],
  serverTime: new Date(now).toISOString() });
await advance(1200);
assert.deepEqual(played.slice(-1), ['next']);
await end(); await advance(1200);
// The hosts' own talk (priority 5) gives way to race news at its next line, even inside a sentence pair.
radio.update('s', { available: true, clips: [clip('t0', 'b30', 0, 3, 5, 50, false), clip('t1', 'b30', 1, 3, 5, 51, false), clip('t2', 'b30', 2, 3, 5, 52)],
  serverTime: new Date(now).toISOString() });
await advance(1200);
assert.deepEqual(played.slice(-1), ['t0']);
radio.update('s', { available: true, clips: [clip('t0', 'b30', 0, 3, 5, 50, false), clip('t1', 'b30', 1, 3, 5, 51, false), clip('t2', 'b30', 2, 3, 5, 52),
  clip('fin', 'b31', 0, 1, 2, 53)], serverTime: new Date(now).toISOString() });
await end(); await advance(1200);
assert.deepEqual(played.slice(-1), ['fin'], 'the finish cuts the talk');
await end(); await advance(5000);
assert.deepEqual(played.slice(-1), ['fin'], 'the rest of the talk is dropped');
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
// Background music shows only with tracks and never autoplays.
const music = audios[1];
const at = () => new Date(now).toISOString();
radio.update('s', { available: true, clips: [], music: ['t1', 't2'], serverTime: at() });
assert.equal(musicButton.hidden, false);
assert.deepEqual(musicPlayed, []);
// It starts with the radio, dips under speech and returns after the conversation.
listeners['button:click']();
assert.deepEqual(musicPlayed, ['t1']);
assert.equal(music.volume, 0.35);
radio.update('s', { available: true, clips: [clip('m1', 'bm', 0, 1, 3, 30)], music: ['t1', 't2'], serverTime: at() });
await advance(1200);
assert.equal(played.at(-1), 'm1');
assert.equal(music.volume, 0.08);
await end(); await advance(1000);
assert.equal(music.volume, 0.08, 'another line could still follow');
await advance(600);
assert.equal(music.volume, 0.35);
// Tracks play in the operator's order and loop.
listeners['music:ended']();
assert.deepEqual(musicPlayed, ['t1', 't2']);
// The listener can turn the music off and on.
listeners['music-button:click']();
assert.equal(music.paused, true);
assert.equal(musicButton.attributes['aria-pressed'], 'false');
listeners['music-button:click']();
assert.equal(music.paused, false);
assert.deepEqual(musicPlayed, ['t1', 't2', 't1']);
// A new playlist without the playing track switches to it.
radio.update('s', { available: true, clips: [], music: ['t3'], serverTime: at() });
assert.equal(musicPlayed.at(-1), 't3');
// Pausing the radio stops the music; an unavailable radio hides its control.
listeners['button:click']();
assert.equal(music.paused, true);
radio.update('s', { available: false, clips: [] });
assert.equal(musicButton.hidden, true);
console.log('live-radio viewer checks passed');
