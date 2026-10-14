import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import vm from 'node:vm';

const context = {};
vm.runInNewContext(readFileSync(new URL('../../src/OpenSkiTime.LiveTiming.Server/wwwroot/live-order.js', import.meta.url), 'utf8'), context);
const order = (rows, startOrder) => context.liveResultOrder(rows, startOrder).map(row => row.bib);

test('equal ranks are listed with the higher bib first, independent of start order', () => {
  const startOrder = [1, 2, 3, 4, 5, 6, 7];
  const rows = [
    { bib: 1, rank: 1 }, { bib: 2, rank: 4 }, { bib: 3, rank: 1 }, { bib: 4, rank: 4 },
    { bib: 5, rank: 4 }, { bib: 6, rank: 7 }, { bib: 7, rank: 1 }
  ];
  assert.deepEqual(order(rows, startOrder), [7, 3, 1, 5, 4, 2, 6]);
  assert.deepEqual(order(rows.slice().reverse(), startOrder.slice().reverse()), [7, 3, 1, 5, 4, 2, 6]);
});

test('from Run 2 on the combined rank orders the list, ex aequo with the higher bib first', () => {
  const startOrder = [4, 3, 2, 1, 5];
  // Bib 4 has the fastest Run 2 time but no combined time; bibs 1 and 2 share the best total.
  const rows = [
    { bib: 1, rank: 3, totalRank: 1 }, { bib: 2, rank: 2, totalRank: 1 }, { bib: 3, rank: 4, totalRank: 3 },
    { bib: 4, rank: 1, totalRank: null }, { bib: 5 }
  ];
  assert.deepEqual(context.liveResultOrder(rows, startOrder, row => row.totalRank).map(row => row.bib), [2, 1, 3, 4, 5]);
  assert.deepEqual(order(rows, startOrder), [4, 2, 1, 3, 5]);
});

test('rows without a rank follow the ranked rows in start order and nothing is dropped', () => {
  const startOrder = [30, 10, 20, 40, 50];
  const rows = [{ bib: 10 }, { bib: 50, rank: 2 }, { bib: 20 }, { bib: 40, rank: 1 }, { bib: 30 }];
  assert.deepEqual(order(rows, startOrder), [40, 50, 30, 10, 20]);
  assert.equal(context.liveResultOrder(rows, startOrder).length, rows.length);
});

test('the latest finisher is chosen by the full-precision device finish timestamp', () => {
  const latest = results => context.liveLatestFinish(results)?.bib ?? null;
  // 100 ns ticks beyond 2^53: a Number comparison would treat these two as equal.
  const rows = [
    { bib: 1, status: 'Finished', finishSourceTicks: '639326664491513001', at: '2026-12-12T10:05:00+00:00' },
    { bib: 2, status: 'Finished', finishSourceTicks: '639326664491513002', at: '2026-12-12T10:00:49+00:00' },
    { bib: 3, status: 'OnCourse', finishSourceTicks: null, at: '2026-12-12T10:06:00+00:00' },
    { bib: 4, status: 'DNF', finishSourceTicks: null, at: '2026-12-12T10:07:00+00:00' }
  ];
  assert.equal(latest(rows), 2);
  assert.equal(latest(rows.slice().reverse()), 2);
  // A later correction time does not make an earlier finisher the latest one.
  assert.equal(latest([{ bib: 5, status: 'Finished', finishSourceTicks: '100', at: '2026-12-12T12:00:00+00:00' },
    { bib: 6, status: 'Finished', finishSourceTicks: '200', at: '2026-12-12T10:00:00+00:00' }]), 6);
  // Without device timestamps the publish time decides; no finisher gives null.
  assert.equal(latest([{ bib: 7, status: 'Finished', at: '2026-12-12T10:00:00+00:00' }, { bib: 8, status: 'Finished', at: '2026-12-12T10:01:00+00:00' }]), 8);
  assert.equal(latest(rows.slice(2)), null);
  // Mixed sources: a later manually timed finish (no device timestamp) is still the latest.
  const mixed = [{ bib: 9, status: 'Finished', finishSourceTicks: '100', at: '2026-12-12T10:00:00+00:00' },
    { bib: 10, status: 'Finished', at: '2026-12-12T10:02:00+00:00' }];
  assert.equal(latest(mixed), 10);
  assert.equal(latest(mixed.slice().reverse()), 10);
  // With mixed sources the choice does not depend on the order of the results.
  const three = [{ bib: 11, status: 'Finished', finishSourceTicks: '100', at: '2026-12-12T12:00:00+00:00' },
    { bib: 12, status: 'Finished', at: '2026-12-12T11:00:00+00:00' },
    { bib: 13, status: 'Finished', finishSourceTicks: '200', at: '2026-12-12T10:02:00+00:00' }];
  for (const order of [[0, 1, 2], [0, 2, 1], [1, 0, 2], [1, 2, 0], [2, 0, 1], [2, 1, 0]])
    assert.equal(latest(order.map(i => three[i])), 11);
});

test('the latest outcome can be a DNF or DSQ, chosen by publish time', () => {
  const results = [{ bib: 1, status: 'Finished', finishSourceTicks: '100', at: '2026-12-12T10:00:00+00:00' },
    { bib: 2, status: 'DNF', at: '2026-12-12T10:01:00+00:00' }, { bib: 3, status: 'DNS', at: '2026-12-12T10:02:00+00:00' }];
  assert.equal(context.liveLatestFinish(results).bib, 1);
  assert.equal(context.liveLatestFinish(results, ['Finished', 'DNF', 'DSQ']).bib, 2);
});

test('running time counts tenths from the start instant and shows no time for a start far in the future', () => {
  const start = '2026-12-12T10:00:00Z', at = ms => Date.parse(start) + ms;
  assert.equal(context.liveRunningTime(start, at(0)), '0:00.0');
  assert.equal(context.liveRunningTime(start, at(65_678)), '1:05.6');
  assert.equal(context.liveRunningTime(start, at(-2_000)), '0:00.0');
  assert.equal(context.liveRunningTime(start, at(-60_000)), '—');
});

test('a later correction of an earlier finisher does not make it the latest outcome', () => {
  // Bib 1 finished at 10:01 and was corrected at 12:00; bib 2 did not finish at 10:05.
  const results = [{ bib: 1, status: 'Finished', finishSourceTicks: '100', startedAt: '2026-12-12T10:00:00Z', hundredths: 6000, at: '2026-12-12T12:00:00Z' },
    { bib: 2, status: 'DNF', at: '2026-12-12T10:05:00Z' }];
  assert.equal(context.liveLatestFinish(results, ['Finished', 'DNF', 'DSQ']).bib, 2);
  assert.equal(context.liveLatestFinish(results.slice().reverse(), ['Finished', 'DNF', 'DSQ']).bib, 2);
});

test('a corrected run time does not move a finisher whose device impulses are published', () => {
  // Bib 1 started 10:00:00 and finished 10:01:00 by its impulses (60 s = 600,000,000 ticks); its time was corrected to
  // 10 minutes. Bib 2 did not finish at 10:05, so it stays the latest outcome.
  const results = [{ bib: 1, status: 'Finished', startSourceTicks: '1000000000', finishSourceTicks: '1600000000',
    startedAt: '2026-12-12T10:00:00Z', hundredths: 60000, at: '2026-12-12T12:00:00Z' }, { bib: 2, status: 'DNF', at: '2026-12-12T10:05:00Z' }];
  assert.equal(context.liveLatestFinish(results, ['Finished', 'DNF', 'DSQ']).bib, 2);
});
