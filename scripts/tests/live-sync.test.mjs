import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import vm from 'node:vm';

const context = {};
vm.runInNewContext(readFileSync(new URL('../../src/OpenSkiTime.LiveTiming.Server/wwwroot/live-sync.js', import.meta.url), 'utf8'), context);
const { liveSyncConnect, liveSyncFull, liveSyncDelta, liveSyncBeacon, liveSyncLegacy, liveSyncMerge } = context;
const plain = value => JSON.parse(JSON.stringify(value));

// Synthetic race states as the server serializes them: every field present, source ticks as decimal strings.
const row = (bib, status = 'Ready', extra = {}) => ({bib, status, hundredths: null, rank: null, difference: null, at: '2026-10-02T09:00:00+00:00',
  startedAt: null, startSourceTicks: null, finishSourceTicks: null, intermediates: null, splits: [], totalHundredths: null, totalRank: null, totalDifference: null, ...extra});
const finished = (bib, hundredths, rank, difference) => row(bib, 'Finished', {hundredths, rank, difference,
  startSourceTicks: String(639000000000000000n + BigInt(bib)), finishSourceTicks: String(639000000000000000n + BigInt(bib) + BigInt(hundredths) * 100000n)});
const race = (results, extra = {}) => ({version: 1, competition: {name: 'Synthetic', place: 'Test', discipline: 'SL', date: '2026-10-02', isFis: false,
  codex: '', gender: 'M', category: 'Club', intermediateCount: 1, slope: '', runCount: 0},
  competitors: [1, 2, 3, 4].map(bib => ({bib, lastName: `TEST${bib}`, firstName: 'Synthetic', nation: 'FIN', club: '', fisCode: ''})),
  currentRun: 1, runs: [{number: 1, listCreatedAt: '2026-10-02T08:00:00+00:00', startOrder: [1, 2, 3, 4], results, forerunners: null}],
  updatedAt: '2026-10-02T09:00:00+00:00', paused: false, ...extra});
const initial = race([1, 2, 3, 4].map(bib => row(bib)));
const live = (state, generation = 'g1', revision = 1) => ({status: 'Live', generation, revision, state});
const delta = (baseRevision, revision, runs, extra = {}) => ({generation: 'g1', baseRevision, revision, version: revision,
  updatedAt: `2026-10-02T09:00:${String(revision).padStart(2, '0')}+00:00`, paused: false, runs, ...extra});
const connected = (connection = 'c1', previous = null) => liveSyncFull(liveSyncConnect(previous, connection), connection, live(initial)).sync;

test('the subscription reply is shown, then deltas in revision order', () => {
  const sync = liveSyncConnect(null, 'c1');
  assert.equal(sync.phase, 'subscribing');
  let step = liveSyncFull(sync, 'c1', live(initial));
  assert.equal(step.show, true); assert.equal(step.resync, false);
  assert.equal(step.sync.phase, 'live'); assert.equal(step.sync.revision, 1);
  step = liveSyncDelta(step.sync, 'c1', delta(1, 2, [{number: 1, results: [row(1, 'OnCourse', {startedAt: '2026-10-02T09:00:02+00:00'})]}]), 0);
  assert.equal(step.show, true); assert.equal(step.sync.revision, 2);
  assert.equal(step.sync.state.runs[0].results[0].status, 'OnCourse');
  assert.equal(step.sync.state.version, 2); assert.equal(step.sync.state.updatedAt, '2026-10-02T09:00:02+00:00');
  // Other rows and runs are kept as they were.
  assert.deepEqual(plain(step.sync.state.runs[0].results.slice(1)), plain(initial.runs[0].results.slice(1)));
});

test('a duplicate or older delta is ignored; a missing one asks for the complete state', () => {
  let sync = connected();
  sync = liveSyncDelta(sync, 'c1', delta(1, 2, [{number: 1, results: [row(1, 'OnCourse')]}]), 0).sync;
  for (const old of [delta(1, 2, [{number: 1, results: [row(1, 'DNS')]}]), delta(0, 1, [])]) {
    const step = liveSyncDelta(sync, 'c1', old, 0);
    assert.equal(step.show, false); assert.equal(step.resync, false); assert.equal(step.sync, sync);
  }
  // Revision 3 was lost: revision 4 cannot be applied to revision 2.
  const gap = liveSyncDelta(sync, 'c1', delta(3, 4, [{number: 1, results: [row(2, 'OnCourse')]}]), 1000);
  assert.equal(gap.show, false); assert.equal(gap.resync, true); assert.equal(gap.sync.resyncSince, 1000);
  assert.equal(gap.sync.state.runs[0].results[1].status, 'Ready');
});

test('out of order deltas never apply partly; the complete state repairs them', () => {
  let sync = connected();
  const third = delta(2, 3, [{number: 1, results: [row(1, 'OnCourse', {intermediates: [{number: 1, hundredths: 2134, at: '2026-10-02T09:00:03+00:00', sourceTicks: '639000000000000001'}]})]}]);
  const second = delta(1, 2, [{number: 1, results: [row(1, 'OnCourse')]}]);
  let step = liveSyncDelta(sync, 'c1', third, 5);
  assert.equal(step.resync, true); sync = step.sync;
  // While the resync is pending, every delta is dropped, also one that would fit.
  step = liveSyncDelta(sync, 'c1', second, 6);
  assert.equal(step.show, false); assert.equal(step.resync, false); assert.equal(step.sync.revision, 1);
  const authoritative = liveSyncMerge(liveSyncMerge(initial, second), third);
  step = liveSyncFull(step.sync, 'c1', live(authoritative, 'g1', 3));
  assert.equal(step.show, true); assert.equal(step.sync.resyncSince, null); assert.equal(step.sync.revision, 3);
  assert.deepEqual(plain(step.sync.state), plain(authoritative));
});

test('a lost last delta is found by the beacon; a matching beacon costs nothing', () => {
  let sync = connected();
  sync = liveSyncDelta(sync, 'c1', delta(1, 2, [{number: 1, results: [row(1, 'OnCourse')]}]), 0).sync;
  let step = liveSyncBeacon(sync, 'c1', {status: 'Live', generation: 'g1', revision: 2}, 30000);
  assert.equal(step.show, false); assert.equal(step.resync, false); assert.equal(step.sync, sync);
  // The finish (revision 3) never arrived and the race is quiet: only the beacon tells.
  step = liveSyncBeacon(sync, 'c1', {status: 'Live', generation: 'g1', revision: 3}, 60000);
  assert.equal(step.resync, true); assert.equal(step.sync.resyncSince, 60000);
  // A pending resync is not asked for again at every beacon, only once its reply is overdue.
  assert.equal(liveSyncBeacon(step.sync, 'c1', {status: 'Live', generation: 'g1', revision: 3}, 75000).resync, false);
  const again = liveSyncBeacon(step.sync, 'c1', {status: 'Live', generation: 'g1', revision: 3}, 90000);
  assert.equal(again.resync, true); assert.equal(again.sync.resyncSince, 90000);
});

test('an unknown generation in a broadcast asks for the complete state at once', () => {
  const sync = connected();
  // The publisher restored the session on a restarted server; this delta belongs to the new generation.
  const step = liveSyncDelta(sync, 'c1', {...delta(1, 2, [{number: 1, results: [row(1, 'OnCourse')]}]), generation: 'g2'}, 7);
  assert.equal(step.resync, true); assert.equal(step.show, false); assert.equal(step.sync.state, initial);
  const beacon = liveSyncBeacon(sync, 'c1', {status: 'Live', generation: 'g2', revision: 1}, 8);
  assert.equal(beacon.resync, true);
});

test('a complete state replaces a newer one only on the same connection, in the order received, whatever its generation', () => {
  let sync = connected();
  sync = liveSyncDelta(sync, 'c1', delta(1, 2, [{number: 1, results: [row(1, 'OnCourse')]}]), 0).sync;
  const restored = race([1, 2, 3, 4].map(bib => row(bib, 'DNS')));
  const step = liveSyncFull(sync, 'c1', live(restored, 'g2', 1));
  assert.equal(step.show, true); assert.equal(step.sync.generation, 'g2'); assert.equal(step.sync.revision, 1);
  assert.deepEqual(plain(step.sync.state), plain(restored));
  // Deltas of the old generation no longer apply.
  assert.equal(liveSyncDelta(step.sync, 'c1', delta(2, 3, [{number: 1, results: [row(2, 'OnCourse')]}]), 0).resync, true);
});

test('messages of an abandoned connection never apply', () => {
  const first = connected('c1');
  const second = liveSyncConnect(first, 'c2');
  // The new connection shows the last state until its own reply arrives, and nothing from the old one applies.
  assert.equal(second.state, initial); assert.equal(second.phase, 'subscribing');
  for (const step of [liveSyncFull(second, 'c1', live(race([])))
    , liveSyncDelta(second, 'c1', delta(1, 2, [{number: 1, results: [row(1, 'DNS')]}]), 0)
    , liveSyncBeacon(second, 'c1', {status: 'Live', generation: 'g9', revision: 9}, 0)
    , liveSyncLegacy(second, 'c1', null)]) {
    assert.equal(step.sync, second); assert.equal(step.show, false); assert.equal(step.resync, false);
  }
  // Before its subscription reply, the new connection applies no delta or beacon either.
  assert.equal(liveSyncDelta(second, 'c2', delta(1, 2, []), 0).resync, false);
  assert.equal(liveSyncBeacon(second, 'c2', {status: 'Live', generation: 'g1', revision: 1}, 0).resync, false);
});

test('deletion clears the state; awaiting keeps the last results', () => {
  const sync = connected();
  const awaiting = liveSyncFull(liveSyncConnect(sync, 'c2'), 'c2', {status: 'Awaiting', generation: null, revision: 0, state: null});
  assert.equal(awaiting.show, true); assert.equal(awaiting.sync.phase, 'awaiting'); assert.equal(awaiting.sync.state, initial);
  // An awaiting beacon keeps the retained results and asks for nothing.
  const beacon = liveSyncBeacon(awaiting.sync, 'c2', {status: 'Awaiting', generation: null, revision: 0}, 0);
  assert.equal(beacon.resync, false); assert.equal(beacon.sync.state, initial);
  // A deletion while the page was disconnected is told on subscribing.
  const deleted = liveSyncFull(liveSyncConnect(sync, 'c3'), 'c3', {status: 'Deleted', generation: null, revision: 0, state: null});
  assert.equal(deleted.show, true); assert.equal(deleted.sync.phase, 'deleted'); assert.equal(deleted.sync.state, null);
  // A beacon that disagrees with the shown status asks for the state rather than changing it itself.
  assert.equal(liveSyncBeacon(sync, 'c1', {status: 'Deleted', generation: null, revision: 0}, 0).resync, true);
  assert.equal(liveSyncBeacon(sync, 'c1', {status: 'Awaiting', generation: null, revision: 0}, 0).resync, true);
  assert.equal(liveSyncBeacon(deleted.sync, 'c3', {status: 'Deleted', generation: null, revision: 0}, 0).resync, false);
});

test('a correction that moves several ranks is applied as one update with the published values', () => {
  const before = race([finished(1, 5000, 1, 0), finished(2, 5100, 2, 100), finished(3, 5200, 3, 200), row(4, 'OnCourse')]);
  let sync = liveSyncFull(liveSyncConnect(null, 'c1'), 'c1', live(before, 'g1', 7)).sync;
  // Bib 4 finishes fastest: every finisher's rank and difference move with it, as one delta of four complete rows.
  const after = race([finished(1, 5000, 2, 10), finished(2, 5100, 3, 110), finished(3, 5200, 4, 210), finished(4, 4990, 1, 0)], {version: 8});
  const step = liveSyncDelta(sync, 'c1', delta(7, 8, [{number: 1, results: after.runs[0].results}], {updatedAt: after.updatedAt}), 0);
  assert.equal(step.show, true);
  assert.deepEqual(plain(step.sync.state), plain(after));
  assert.deepEqual(plain(step.sync.state.runs[0].results.map(r => [r.bib, r.rank, r.difference])), [[1, 2, 10], [2, 3, 110], [3, 4, 210], [4, 1, 0]]);
  assert.equal(step.sync.state.runs[0].results[3].finishSourceTicks, after.runs[0].results[3].finishSourceTicks);
});

test('merge keeps row order, appends new rows, follows an explicit order and replaces forerunners', () => {
  const state = race([row(2), row(1)]);
  const appended = liveSyncMerge(state, delta(1, 2, [{number: 1, results: [row(1, 'DNS'), row(4)]}]));
  assert.deepEqual(plain(appended.runs[0].results.map(r => [r.bib, r.status])), [[2, 'Ready'], [1, 'DNS'], [4, 'Ready']]);
  const ordered = liveSyncMerge(state, delta(1, 2, [{number: 1, results: [row(3)], order: [1, 2, 3]}]));
  assert.deepEqual(plain(ordered.runs[0].results.map(r => r.bib)), plain([1, 2, 3]));
  const forerunners = [{letter: 'A', lastName: 'FORERUNNER', firstName: 'Synthetic', nation: 'FIN', status: 'OnCourse'}];
  const runners = liveSyncMerge(state, delta(1, 2, [{number: 1, results: [], forerunners}]));
  assert.deepEqual(plain(runners.runs[0].forerunners), plain(forerunners));
  assert.deepEqual(plain(runners.runs[0].results), plain(state.runs[0].results));
  assert.equal(liveSyncMerge(runners, delta(2, 3, [{number: 1, results: [row(1, 'DNS')]}])).runs[0].forerunners, forerunners);
  // A delta that does not fit (unknown run, an order missing or repeating a row) is refused as a whole.
  assert.equal(liveSyncMerge(state, delta(1, 2, [{number: 1, results: [row(1, 'DNS')]}, {number: 2, results: []}])), null);
  assert.equal(liveSyncMerge(state, delta(1, 2, [{number: 1, results: [], order: [1]}])), null);
  assert.equal(liveSyncMerge(state, delta(1, 2, [{number: 1, results: [], order: [1, 1]}])), null);
  // The refused delta asks for the complete state and leaves the shown one unchanged.
  const sync = liveSyncFull(liveSyncConnect(null, 'c1'), 'c1', live(state)).sync;
  const step = liveSyncDelta(sync, 'c1', delta(1, 2, [{number: 9, results: []}]), 0);
  assert.equal(step.resync, true); assert.equal(step.sync.state, state);
});

test('a paused flag and the update time come with the delta', () => {
  const sync = connected();
  const step = liveSyncDelta(sync, 'c1', delta(1, 2, [], {paused: true}), 0);
  assert.equal(step.show, true); assert.equal(step.sync.state.paused, true);
  // Runs without changes are kept as they are.
  assert.equal(step.sync.state.runs[0], sync.state.runs[0]);
});

test('the final state after a whole race of deltas equals the authoritative state', () => {
  // Every start, intermediate and finish of four racers; each finish re-ranks every finisher behind it.
  let authoritative = initial, revision = 1, sync = connected();
  const times = {1: 5200, 2: 5000, 3: 5300, 4: 4900};
  for (const bib of [1, 2, 3, 4]) {
    for (const kind of ['start', 'split', 'finish']) {
      const results = authoritative.runs[0].results.map(r => {
        if (r.bib !== bib) return r;
        if (kind === 'start') return row(bib, 'OnCourse', {startedAt: '2026-10-02T09:01:00+00:00', startSourceTicks: String(639000000000000000n + BigInt(bib))});
        if (kind === 'split') return {...r, intermediates: [{number: 1, hundredths: times[bib] / 2, at: '2026-10-02T09:01:30+00:00', sourceTicks: String(639000000000000500n + BigInt(bib))}]};
        return {...r, status: 'Finished', hundredths: times[bib], finishSourceTicks: String(639000000000009000n + BigInt(bib))};
      });
      const done = results.filter(r => r.status === 'Finished'), best = Math.min(...done.map(r => r.hundredths));
      const ranked = results.map(r => r.status !== 'Finished' ? r : {...r, rank: done.filter(x => x.hundredths < r.hundredths).length + 1, difference: r.hundredths - best});
      const changed = ranked.filter((r, i) => JSON.stringify(r) !== JSON.stringify(authoritative.runs[0].results[i]));
      revision++;
      authoritative = {...authoritative, version: revision, updatedAt: `2026-10-02T09:02:${String(revision).padStart(2, '0')}+00:00`,
        runs: [{...authoritative.runs[0], results: ranked}]};
      const step = liveSyncDelta(sync, 'c1', delta(revision - 1, revision, [{number: 1, results: changed}], {updatedAt: authoritative.updatedAt}), 0);
      assert.equal(step.show, true); assert.equal(step.resync, false);
      sync = step.sync;
    }
  }
  assert.deepEqual(plain(sync.state), plain(authoritative));
  assert.deepEqual(plain(sync.state.runs[0].results.map(r => r.rank)), plain([3, 2, 4, 1]));
});

test('an older server without synchronization: complete states, null as deletion, or as awaiting on subscribing', () => {
  const first = liveSyncLegacy(liveSyncConnect(null, 'c1'), 'c1', initial);
  assert.equal(first.show, true); assert.equal(first.sync.phase, 'live'); assert.equal(first.sync.state, initial);
  const restarted = liveSyncLegacy(liveSyncConnect(first.sync, 'c2'), 'c2', null);
  assert.equal(restarted.sync.phase, 'awaiting'); assert.equal(restarted.sync.state, initial);
  const deleted = liveSyncLegacy(first.sync, 'c1', null);
  assert.equal(deleted.sync.phase, 'deleted'); assert.equal(deleted.sync.state, null);
});
