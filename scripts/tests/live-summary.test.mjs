import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import vm from 'node:vm';

const context = {};
for (const file of ['live-summary.js', 'live-nations.js'])
  vm.runInNewContext(readFileSync(new URL(`../../src/OpenSkiTime.LiveTiming.Server/wwwroot/${file}`, import.meta.url), 'utf8'), context);
const plain = value => JSON.parse(JSON.stringify(value));

test('a run is counted from its start order: a racer without a result is still to start', () => {
  const run = { startOrder: [1, 2, 3, 4, 5, 6, 7, 8], results: [
    { bib: 1, status: 'Finished' }, { bib: 2, status: 'DNF' }, { bib: 3, status: 'DNS' }, { bib: 4, status: 'DSQ' },
    { bib: 5, status: 'OnCourse' }, { bib: 6, status: 'Review' }, { bib: 7, status: 'NPS' }] };
  assert.deepEqual(plain(context.liveRunCounts(run)),
    { racers: 8, finished: 1, onCourse: 1, waiting: 1, DNS: 1, DNF: 1, DSQ: 1, NPS: 1, review: 1 });
});

test('racers per nation or club: most racers first, then alphabetically, a blank value last', () => {
  const racers = [{ nation: 'SWE', club: 'B' }, { nation: 'FIN', club: '' }, { nation: 'FIN', club: 'A' }, { nation: 'AUT', club: 'A' }, { nation: ' ', club: 'C' }];
  assert.deepEqual(plain(context.liveGroupCounts(racers, 'nation')), [{ key: 'FIN', count: 2 }, { key: 'AUT', count: 1 }, { key: 'SWE', count: 1 }, { key: '', count: 1 }]);
  assert.deepEqual(plain(context.liveGroupCounts(racers, 'club')).map(x => x.key), ['A', 'B', 'C', '']);
});

test('a FIS nation code becomes the flag of its ISO country and an English name; an unknown code stays a code', () => {
  assert.deepEqual(plain(context.liveNation('GER')), { code: 'GER', flag: '\u{1F1E9}\u{1F1EA}', name: 'Germany' });
  assert.deepEqual(plain(context.liveNation(' sui ')), { code: 'SUI', flag: '\u{1F1E8}\u{1F1ED}', name: 'Switzerland' });
  assert.equal(context.liveNation('GBR').flag, '\u{1F1EC}\u{1F1E7}');
  assert.deepEqual(plain(context.liveNation('XYZ')), { code: 'XYZ', flag: '', name: 'XYZ' });
  assert.deepEqual(plain(context.liveNation(null)), { code: '', flag: '', name: '' });
});
