import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import vm from 'node:vm';

const context = {};
vm.runInNewContext(readFileSync(new URL('../../src/OpenSkiTime.LiveTiming.Server/wwwroot/live-columns.js', import.meta.url), 'utf8'), context);
const split = (number, hundredths) => ({ number, hundredths });
// Values cross the vm boundary; compare them as plain JSON.
const plain = value => JSON.parse(JSON.stringify(value));

test('the column count follows the highest intermediate in the run, and no splits means no split columns', () => {
  assert.equal(context.liveSplitCount([{ intermediates: [split(1, 2000)] }, { intermediates: [split(1, 2100), split(2, 4100)] }, {}]), 2);
  assert.equal(context.liveSplitCount([{ intermediates: [] }, {}]), 0);
  assert.deepEqual(plain(context.liveSplitHeaders(0, 'sector')), []);
  assert.deepEqual(plain(context.liveSplitHeaders(2, 'intermediate')), ['Intermediate 1', 'Intermediate 2']);
  // One sector more than intermediates: the last one ends at the finish.
  assert.deepEqual(plain(context.liveSplitHeaders(2, 'sector')), ['Sector 1', 'Sector 2', 'Sector 3']);
});

test('intermediate times are the published cumulative times from the start, each in its own column', () => {
  const finished = { status: 'Finished', hundredths: 6012, intermediates: [split(2, 4134), split(1, 2134)] };
  assert.deepEqual(plain(context.liveSplitValues(finished, 2, 'intermediate')), [2134, 4134]);
  assert.deepEqual(plain(context.liveSplitValues({ status: 'OnCourse', intermediates: [split(1, 2134)] }, 2, 'intermediate')), [2134, null]);
});

test('sector times run between consecutive timing points and the last sector ends at the finish', () => {
  const finished = { status: 'Finished', hundredths: 6012, intermediates: [split(1, 2134), split(2, 4134)] };
  assert.deepEqual(plain(context.liveSplitValues(finished, 2, 'sector')), [2134, 2000, 1878]);
  // On course: no finish yet, so the last sector is blank.
  assert.deepEqual(plain(context.liveSplitValues({ status: 'OnCourse', hundredths: null, intermediates: [split(1, 2134), split(2, 4134)] }, 2, 'sector')),
    [2134, 2000, null]);
  // A missed intermediate blanks both sectors that touch it rather than merging them.
  assert.deepEqual(plain(context.liveSplitValues({ status: 'Finished', hundredths: 6012, intermediates: [split(2, 4134)] }, 2, 'sector')),
    [null, null, 1878]);
  // A finish time does not make a sector for a racer who did not finish.
  assert.deepEqual(plain(context.liveSplitValues({ status: 'DNF', hundredths: null, intermediates: [split(1, 2134)] }, 1, 'sector')), [2134, null]);
});

test('from Run 2 on each racer carries the earlier run results in run order', () => {
  const snapshot = { runs: [
    { number: 2, results: [{ bib: 1, status: 'Finished', hundredths: 5000 }] },
    { number: 1, results: [{ bib: 1, status: 'Finished', hundredths: 5100 }, { bib: 2, status: 'DNF' }] }
  ] };
  assert.deepEqual(plain(context.liveEarlierRuns(snapshot, 1, 1)), []);
  assert.deepEqual(plain(context.liveEarlierRuns(snapshot, 2, 1)), [{ number: 1, result: { bib: 1, status: 'Finished', hundredths: 5100 } }]);
  assert.deepEqual(plain(context.liveEarlierRuns(snapshot, 2, 3)), [{ number: 1, result: null }]);
});
