import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import vm from 'node:vm';

const context = {};
for (const file of ['live-nations.js', 'live-filter.js', 'live-favorites.js'])
  vm.runInNewContext(readFileSync(new URL(`../../src/OpenSkiTime.LiveTiming.Server/wwwroot/${file}`, import.meta.url), 'utf8'), context);
const plain = value => JSON.parse(JSON.stringify(value));
const racer = (bib, lastName, firstName, extra = {}) => ({ bib, lastName, firstName, nation: 'FIN', club: 'Test Club', fisCode: '', ...extra });

const chip = (kind, value) => ({ kind, value: context.liveFilterValue(kind, value) });

test('the search ignores case, accents and spacing; every word must occur in name, club or nation; digits alone find a bib', () => {
  const r = racer(12, 'MÄKI-KORPELA', 'Synthetic Anna', { nation: 'SWE', club: 'Valley Racers' });
  const match = text => context.liveFilterMatch(r, { text, chips: [] });
  assert.equal(match('maki'), true);
  assert.equal(match('  Anna   mäki '), true);
  assert.equal(match('korpela synth'), true);
  assert.equal(match('valley'), true);
  assert.equal(match('swe'), true);
  assert.equal(match('Sweden anna'), true);
  assert.equal(match('finland'), false);
  assert.equal(match('anna berg'), false);
  assert.equal(match('12'), true);
  assert.equal(match('1'), false);
  assert.equal(match(''), true);
});

test('chips of one kind are alternatives; the typed text, every kind and favorites must all match', () => {
  const swe = racer(3, 'TEST', 'Synthetic', { nation: 'swe ', club: ' Alpine Club' });
  const nor = racer(4, 'DEMO', 'Synthetic', { nation: 'NOR', club: 'Slope Team' });
  const fin = racer(5, 'MOCK', 'Synthetic', { nation: 'FIN', club: 'Alpine Club' });
  const match = (r, filter, favorite) => context.liveFilterMatch(r, { text: '', chips: [], ...filter }, favorite);
  const twoNations = [chip('nation', 'swe'), chip('nation', 'NOR')];
  assert.deepEqual([swe, nor, fin].map(r => match(r, { chips: twoNations })), [true, true, false]);
  const nationsAndClub = [...twoNations, chip('club', 'Alpine Club ')];
  assert.deepEqual([swe, nor, fin].map(r => match(r, { chips: nationsAndClub })), [true, false, false]);
  const searches = [chip('text', 'test'), chip('text', 'MOCK')];
  assert.deepEqual([swe, nor, fin].map(r => match(r, { chips: searches })), [true, false, true]);
  assert.deepEqual([swe, nor, fin].map(r => match(r, { chips: searches, text: 'alpine fin' })), [false, false, true]);
  assert.equal(match(swe, { favorites: true }, false), false);
  assert.equal(match(swe, { favorites: true, chips: twoNations }, true), true);
  assert.equal(context.liveFilterActive({ text: '  ', chips: [] }), false);
  assert.equal(context.liveFilterActive({ text: '', chips: twoNations }), true);
});

test('toggling a chip adds it once and removes it again; a blank value adds nothing', () => {
  let chips = context.liveFilterToggle([], 'nation', ' swe');
  chips = context.liveFilterToggle(chips, 'text', '  Mäki  Anna ');
  assert.deepEqual(plain(chips), [{ kind: 'nation', value: 'SWE' }, { kind: 'text', value: 'maki anna' }]);
  assert.deepEqual(plain(context.liveFilterToggle(chips, 'nation', 'SWE')), [{ kind: 'text', value: 'maki anna' }]);
  assert.equal(context.liveFilterToggle(chips, 'text', '   '), chips);
});

test('a favorite is found by FIS code when both have one, otherwise by name', () => {
  const coded = context.liveFavoriteOf(racer(1, 'TEST', 'Synthetic', { fisCode: '9990001' }));
  assert.equal(context.liveIsFavorite(coded, racer(40, 'Other', 'Name', { fisCode: '9990001' })), true);
  assert.equal(context.liveIsFavorite(coded, racer(1, 'TEST', 'Synthetic', { fisCode: '9990002' })), false);
  assert.equal(context.liveIsFavorite(coded, racer(7, ' test ', 'synthetic')), true);
  const named = context.liveFavoriteOf(racer(2, 'MÄKI', 'Anna  Maria'));
  assert.equal(context.liveIsFavorite(named, racer(9, 'mäki', 'anna maria', { fisCode: '9990003' })), true);
  assert.equal(context.liveIsFavorite(named, racer(9, 'MAKI', 'Anna Maria')), false);
});

test('toggling adds and removes a favorite; the stored list is validated, deduplicated and bounded', () => {
  const r = racer(1, 'TEST', 'Synthetic', { fisCode: '9990001' });
  const added = context.liveFavoritesToggle([], r);
  assert.deepEqual(plain(added), [{ code: '9990001', lastName: 'TEST', firstName: 'Synthetic', nation: 'FIN', club: 'Test Club' }]);
  assert.deepEqual(plain(context.liveFavoritesToggle(added, racer(5, 'Renamed', 'Racer', { fisCode: '9990001' }))), []);
  assert.deepEqual(plain(context.liveFavoritesParse(JSON.stringify(added))), plain(added));
  for (const damaged of [null, '', 'not json', '{"a":1}', '42']) assert.deepEqual(plain(context.liveFavoritesParse(damaged)), []);
  assert.deepEqual(plain(context.liveFavoritesParse(JSON.stringify([null, 5, { lastName: 7 }, { lastName: 'A', firstName: 'B', code: 3 }, { lastName: 'a', firstName: 'b' }]))),
    [{ code: '', lastName: 'A', firstName: 'B', nation: '', club: '' }]);
  const many = Array.from({ length: 250 }, (_, i) => ({ lastName: `TEST${i}`, firstName: 'Synthetic' }));
  const bounded = context.liveFavoritesParse(JSON.stringify(many));
  assert.equal(bounded.length, context.liveFavoriteLimit);
  assert.equal(context.liveFavoritesToggle(bounded, racer(1, 'NEW', 'Racer')).length, context.liveFavoriteLimit);
});

test('a favorite\'s race results: each run in order and the latest combined time', () => {
  const snapshot = {
    competitors: [racer(1, 'TEST1', 'Synthetic'), racer(2, 'TEST2', 'Synthetic', { fisCode: '9990002' })],
    runs: [
      { number: 2, startOrder: [2, 1], results: [{ bib: 2, status: 'Finished', hundredths: 6100, rank: 1, totalHundredths: 12100, totalRank: 1 }, { bib: 1, status: 'OnCourse' }] },
      { number: 1, startOrder: [1, 2], results: [{ bib: 1, status: 'Finished', hundredths: 5900, rank: 1 }, { bib: 2, status: 'Finished', hundredths: 6000, rank: 2 }] }
    ]
  };
  assert.deepEqual(plain(context.liveFavoriteResults(snapshot, { code: '9990002', lastName: 'X', firstName: 'Y' })), {
    competitor: snapshot.competitors[1],
    runs: [{ number: 1, status: 'Finished', hundredths: 6000, rank: 2 }, { number: 2, status: 'Finished', hundredths: 6100, rank: 1 }],
    total: { run: 2, hundredths: 12100, rank: 1 }
  });
  const first = context.liveFavoriteResults(snapshot, { code: '', lastName: 'TEST1', firstName: 'Synthetic' });
  assert.deepEqual(plain(first.runs.map(r => r.status)), ['Finished', 'OnCourse']);
  assert.equal(first.total, null);
  assert.equal(context.liveFavoriteResults(snapshot, { code: '', lastName: 'NOBODY', firstName: 'Here' }), null);
  assert.equal(context.liveFavoriteResults(null, { code: '', lastName: 'TEST1', firstName: 'Synthetic' }), null);
});
