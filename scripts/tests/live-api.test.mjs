import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import vm from 'node:vm';
import { RaceState, changes, time } from '../../website/live-api/live-reader.mjs';
import { validate } from '../../tests/live-api/schema.mjs';

const read = path => readFileSync(new URL(`../../${path}`, import.meta.url), 'utf8');
const schema = JSON.parse(read('website/live-api/live-api.schema.json'));
const page = read('website/pages/live-api.html');

// --- The published contract and the server's contract ---
test('every schema reference and required property exists', () => {
  for (const [name, definition] of Object.entries(schema.$defs))
    for (const key of definition.required ?? []) assert(definition.properties[key], `${name}.${key}`);
  for (const [, ref] of JSON.stringify(schema).matchAll(/"\$ref":"#\/\$defs\/([^"]+)"/g)) assert(schema.$defs[ref], ref);
});
test('the documented statuses and versions are those of the server', () => {
  const enumeration = (file, name) => read(file).match(new RegExp(`public enum ${name} \\{ ([^}]+) \\}`))[1].trim().split(', ');
  const contracts = 'src/OpenSkiTime.LiveTiming.Contracts/';
  assert.deepEqual(schema.$defs.Status.enum, enumeration(`${contracts}LiveContracts.cs`, 'LiveStatus'));
  assert.deepEqual(schema.$defs.SessionStatus.enum, enumeration(`${contracts}LiveSync.cs`, 'LiveSessionStatus'));
  for (const status of schema.$defs.Status.enum) assert(page.includes(`<tr><td><code>${status}</code></td>`), status);
  const version = read(`${contracts}LiveSync.cs`).match(/const int Version = (\d+);/)[1];
  assert(page.includes(`WatchSync(sessionId, ${version})`), 'The page must tell the version of viewer synchronization.');
  for (const [file, call] of [['live-reader.mjs', `'WatchSync', sessionId, ${version}`], ['live-reader.cs', `"WatchSync", sessionId, ${version}`], ['LiveReader.java', `"WatchSync", sessionId, ${version}`]])
    assert(read(`website/live-api/${file}`).includes(call), file);
});
test('the Java reader, the page and the verification use the same SignalR client', () => {
  const version = read('tests/live-api/pom.xml').match(/<artifactId>signalr<\/artifactId>\s*<version>([^<]+)</)[1];
  for (const text of [read('website/live-api/LiveReader.java'), page]) assert(text.includes(`com.microsoft.signalr:signalr:${version}`) || text.includes(`com.microsoft.signalr:signalr</a> ${version}`));
});
test('the JSON samples of the page match the schema', () => {
  const samples = [...page.matchAll(/<code data-schema="(\w+)">([\s\S]*?)<\/code>/g)];
  assert.deepEqual(samples.map(x => x[1]), ['Race', 'Result', 'DeltaMessage', 'BeaconMessage']);
  for (const [, name, json] of samples) assert.deepEqual(validate(schema, name, JSON.parse(json)), []);
});
test('the validator finds what it must find', () => {
  const beacon = { status: 'Live', generation: 'g', revision: 1 };
  assert.deepEqual(validate(schema, 'BeaconMessage', beacon), []);
  for (const wrong of [{ ...beacon, status: 'Gone' }, { ...beacon, revision: '1' }, { ...beacon, revision: 1.5 }, { ...beacon, extra: 1 }, { status: 'Live', revision: 1 }])
    assert.equal(validate(schema, 'BeaconMessage', wrong).length, 1, JSON.stringify(wrong));
  assert.deepEqual(validate(schema, 'SyncMessage', { ...beacon, state: null }), []);
  assert(validate(schema, 'SyncMessage', { ...beacon, state: {} }).length > 0);
  assert.equal(validate(schema, 'Intermediate', { number: 0, hundredths: 1, at: 'x', sourceTicks: '1.5' }).length, 2);
  assert.throws(() => validate({ $defs: { X: { maximum: 1 } } }, 'X', 1), /Unsupported/);
});

// --- The reader against the browser's implementation of the same rules ---
const browser = {};
vm.runInNewContext(read('src/OpenSkiTime.LiveTiming.Server/wwwroot/live-sync.js'), browser);
const plain = value => JSON.parse(JSON.stringify(value));
const row = (bib, status = 'Ready', extra = {}) => ({ bib, status, hundredths: null, rank: null, intermediates: null, ...extra });
const race = (...runs) => ({ version: 1, competitors: [], currentRun: 1, updatedAt: 'u', paused: false,
  runs: runs.map((results, i) => ({ number: i + 1, startOrder: [], results, forerunners: null })) });

test('the reader follows the same rules as the race page in random message sequences', () => {
  let seed = 20261124;
  const random = n => { seed = (Math.imul(seed, 1103515245) + 12345) >>> 0; return (seed >>> 8) % n; };
  const outcomes = { changed: 0, resync: 0, none: 0 };
  for (let sequence = 0; sequence < 300; sequence++) {
    const reader = new RaceState();
    let page = browser.liveSyncConnect(null, 1), connection = 1, now = 0;
    for (let i = 0; i < 40; i++) {
      now += [100, 5000, 31000][random(3)];
      const generation = ['g', 'g', 'g', 'h'][random(4)], revision = random(6), bibs = [1, 2, 3].slice(0, 1 + random(3));
      const kind = random(12);
      let step, outcome;
      if (kind === 0) {
        page = browser.liveSyncConnect(page, ++connection); reader.connected();
        continue;
      }
      if (kind <= 3) {
        const status = ['Live', 'Live', 'Live', 'Awaiting', 'Deleted'][random(5)];
        const message = status === 'Live' ? { status, generation, revision, state: race(bibs.map(bib => row(bib))) } : { status, generation: null, revision: 0, state: null };
        step = browser.liveSyncFull(page, connection, message); outcome = reader.sync(message);
      } else if (kind <= 9) {
        // Mostly the next change; sometimes a gap, a duplicate, an unknown run or an order that does not fit.
        const base = random(3) ? reader.revision : revision;
        const message = { generation: random(5) ? reader.generation ?? generation : generation, baseRevision: base, revision: base + (random(8) ? 1 : 0), version: i, updatedAt: `u${i}`,
          paused: random(2) === 0, runs: [{ number: random(8) ? 1 : 9, results: bibs.map(bib => row(bib, ['OnCourse', 'DNF'][random(2)])),
            ...(random(3) ? {} : { order: [[3, 2, 1], [2, 1], [1, 1], [1]][random(4)] }), ...(random(4) ? {} : { forerunners: [{ letter: 'A' }] }) }] };
        step = browser.liveSyncDelta(page, connection, message, now); outcome = reader.delta(message, now);
      } else {
        const status = ['Live', 'Live', 'Awaiting', 'Deleted'][random(4)];
        const message = { status, generation: status === 'Live' ? generation : null, revision: status === 'Live' ? (random(2) ? reader.revision : revision) : 0 };
        step = browser.liveSyncBeacon(page, connection, message, now); outcome = reader.beacon(message, now);
      }
      page = step.sync;
      outcomes[outcome]++;
      assert.equal(outcome, step.resync ? 'resync' : step.show ? 'changed' : 'none');
      for (const key of ['phase', 'generation', 'revision', 'resyncSince']) assert.equal(reader[key], page[key], key);
      assert.deepEqual(plain(reader.state), plain(page.state));
    }
  }
  for (const [outcome, count] of Object.entries(outcomes)) assert(count > 500, `${outcome}: ${count}`);
});

// --- From changes to events ---
const summary = list => list.map(x => `${x.run}:${x.row.bib} ${x.kind}${x.kind === 'status' ? ` ${x.from}-${x.row.status}` : x.kind === 'intermediate' ? ` ${x.number}=${x.hundredths}` : ''}`);
const split = (...times) => ({ intermediates: times.map((hundredths, i) => ({ number: i + 1, hundredths })) });
test('changes between two states', () => {
  const finished = row(1, 'Finished', { hundredths: 5012, rank: 1, ...split(2501) });
  const between = (before, after) => summary(changes(race([before]), race([after])));
  assert.deepEqual(between(row(1), row(1, 'OnCourse')), ['1:1 status Ready-OnCourse']);
  assert.deepEqual(between(row(1, 'OnCourse'), row(1, 'OnCourse', split(2501))), ['1:1 intermediate 1=2501']);
  assert.deepEqual(between(row(1, 'OnCourse', split(2501)), finished), ['1:1 status OnCourse-Finished']);
  assert.deepEqual(between(finished, { ...finished, rank: 2 }), [], 'A rank moved by another result is not an event.');
  assert.deepEqual(between(finished, { ...finished, hundredths: 5013 }), ['1:1 time']);
  assert.deepEqual(between(finished, row(1, 'DSQ', split(2501))), ['1:1 status Finished-DSQ']);
  assert.deepEqual(between(finished, { ...finished, intermediates: null }), ['1:1 intermediate 1=null']);
  assert.deepEqual(between(finished, row(1, 'OnCourse')), ['1:1 status Finished-OnCourse', '1:1 intermediate 1=null'], 'A re-run');
  // Joined changes keep the order of the course, and a row or run that was not there counts as Ready.
  assert.deepEqual(between(row(1), finished), ['1:1 intermediate 1=2501', '1:1 status Ready-Finished']);
  assert.deepEqual(between(row(1), row(1, 'OnCourse', split(2501))), ['1:1 status Ready-OnCourse', '1:1 intermediate 1=2501']);
  assert.deepEqual(summary(changes(race([]), race([row(1)], [row(1), row(2, 'DNS')]))), ['2:2 status Ready-DNS']);
});
test('times are shown with integer arithmetic', () => {
  assert.deepEqual([0, 5, 99, 5012, 5999, 6000, 6203, 36000].map(time), ['0.00', '0.05', '0.99', '50.12', '59.99', '1:00.00', '1:02.03', '6:00.00']);
});
