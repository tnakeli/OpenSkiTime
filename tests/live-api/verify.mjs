// Verifies the published Live API (website/live-api) against a real live timing server: every message the server sends
// must be documented by the schema, and the JavaScript, .NET and Java example readers must report exactly the same race,
// including a lost message, a server restart and a deletion. All data is synthetic and everything runs on loopback.
//
//   dotnet build src/OpenSkiTime.LiveTiming.Server -c Release
//   npm ci --ignore-scripts --prefix tests/live-api
//   node tests/live-api/verify.mjs [--readers=js,dotnet,java]
//
// The Java reader needs a JDK (17 or later) and Maven on the path, the .NET reader the .NET 10 SDK.
import assert from 'node:assert/strict';
import crypto from 'node:crypto';
import net from 'node:net';
import os from 'node:os';
import path from 'node:path';
import { spawn, spawnSync } from 'node:child_process';
import { copyFileSync, mkdirSync, mkdtempSync, readFileSync, rmSync } from 'node:fs';
import { fileURLToPath, pathToFileURL } from 'node:url';
import { HubConnectionBuilder, LogLevel } from '@microsoft/signalr';
import { validate } from './schema.mjs';

const here = fileURLToPath(new URL('.', import.meta.url)), root = path.resolve(here, '../..');
const readers = (process.argv.find(x => x.startsWith('--readers='))?.slice(10) ?? 'js,dotnet,java').split(',');
for (const reader of readers) assert(['js', 'dotnet', 'java'].includes(reader), `Unknown reader ${reader}`);
const serverAssembly = path.join(root, 'src/OpenSkiTime.LiveTiming.Server/bin/Release/net10.0/OpenSkiTime.LiveTiming.Server.dll');
const schema = JSON.parse(readFileSync(path.join(root, 'website/live-api/live-api.schema.json'), 'utf8'));
// The JavaScript reader finds its package in tests/live-api/node_modules; the .NET one must be outside the repository,
// whose central package versions do not apply to the reader's users.
const work = path.join(here, 'artifacts'), outside = mkdtempSync(path.join(os.tmpdir(), 'live-reader-'));
mkdirSync(work, { recursive: true });
copyFileSync(path.join(root, 'website/live-api/live-reader.mjs'), path.join(work, 'live-reader.mjs'));
copyFileSync(path.join(root, 'website/live-api/live-reader.cs'), path.join(outside, 'live-reader.cs'));
const { RaceState, follow } = await import(pathToFileURL(path.join(work, 'live-reader.mjs')).href);

const wait = ms => new Promise(resolve => setTimeout(resolve, ms));
async function until(what, condition, seconds = 30) {
  for (const end = Date.now() + seconds * 1000; ;) {
    if (await condition()) return;
    if (Date.now() > end) throw new Error(`Timed out: ${what}`);
    await wait(50);
  }
}
const freePort = () => new Promise((resolve, reject) => {
  const probe = net.createServer();
  probe.on('error', reject).listen(0, '127.0.0.1', () => { const { port } = probe.address(); probe.close(() => resolve(port)); });
});

// --- Server ---
const port = await freePort(), origin = `http://127.0.0.1:${port}`;
const publisherKey = 'ost_pk_' + crypto.randomBytes(32).toString('hex');
const environment = { ...process.env, LiveTiming__SigningKey: crypto.randomBytes(32).toString('base64'), LiveTiming__PublicBaseUrl: origin,
  LiveTiming__PublisherKeys: 'verify:' + crypto.createHash('sha256').update(publisherKey).digest('hex'), LiveTiming__SyncBeaconSeconds: '2' };
let server;
async function startServer() {
  server = spawn('dotnet', [serverAssembly, '--urls', origin], { cwd: path.dirname(serverAssembly), env: environment, stdio: 'ignore' });
  await until('server start', async () => { try { return (await fetch(`${origin}/health`)).ok; } catch { return false; } });
}
async function stopServer() {
  const ended = new Promise(resolve => server.once('exit', resolve));
  server.kill('SIGKILL'); await ended;
}

// --- A proxy per reader that can lose one SignalR Delta, which the real server never does ---
const proxies = [];
async function startProxy() {
  const proxy = { drop: 0, dropped: 0 };
  proxy.server = net.createServer(client => {
    const upstream = net.connect(port, '127.0.0.1');
    let upgraded = false, pending = Buffer.alloc(0);
    client.on('data', data => upstream.write(data));
    upstream.on('data', data => {
      if (!upgraded) {
        if (!data.subarray(0, 12).equals(Buffer.from('HTTP/1.1 101'))) { client.write(data); return; }
        upgraded = true;
        const head = data.indexOf('\r\n\r\n') + 4;
        client.write(data.subarray(0, head)); data = data.subarray(head);
      }
      pending = Buffer.concat([pending, data]);
      for (;;) { // Complete unmasked WebSocket frames.
        if (pending.length < 2) return;
        let length = pending[1] & 0x7f, start = 2;
        if (length === 126) { if (pending.length < 4) return; length = pending.readUInt16BE(2); start = 4; }
        else if (length === 127) { if (pending.length < 10) return; length = Number(pending.readBigUInt64BE(2)); start = 10; }
        if (pending.length < start + length) return;
        const frame = pending.subarray(0, start + length);
        pending = pending.subarray(start + length);
        if (proxy.drop > 0 && frame.includes('"target":"Delta"')) { proxy.drop--; proxy.dropped++; } else client.write(frame);
      }
    });
    for (const [one, other] of [[client, upstream], [upstream, client]]) { one.on('error', () => other.destroy()); one.on('close', () => other.destroy()); }
  });
  await new Promise(resolve => proxy.server.listen(0, '127.0.0.1', resolve));
  proxy.origin = `http://127.0.0.1:${proxy.server.address().port}`;
  proxies.push(proxy);
  return proxy;
}

// --- Readers ---
let classpath;
function command(reader, args) {
  if (reader === 'js') return ['node', [path.join(work, 'live-reader.mjs'), ...args], work];
  if (reader === 'dotnet') return ['dotnet', ['run', 'live-reader.cs', '--', ...args], outside];
  if (!classpath) {
    const file = path.join(work, 'classpath.txt');
    const maven = spawnSync('mvn', ['-q', '-B', '-f', path.join(here, 'pom.xml'), 'dependency:build-classpath', `-Dmdep.outputFile=${file}`],
      { encoding: 'utf8', shell: process.platform === 'win32' });
    assert.equal(maven.status, 0, `Maven failed: ${maven.error ?? maven.stdout + maven.stderr}`);
    classpath = readFileSync(file, 'utf8').trim();
  }
  return ['java', ['-cp', classpath, path.join(root, 'website/live-api/LiveReader.java'), ...args], work];
}
const running = [];
function startReader(reader, args) {
  const [file, parameters, cwd] = command(reader, args);
  const process_ = spawn(file, parameters, { cwd, stdio: ['ignore', 'pipe', 'pipe'] });
  const result = { reader, process: process_, output: '', errors: '', code: undefined };
  process_.stdout.setEncoding('utf8').on('data', data => { result.output += data; });
  process_.stderr.setEncoding('utf8').on('data', data => { result.errors += data; });
  process_.on('error', error => { result.errors += error; result.code = -1; });
  process_.on('exit', code => { result.code = code; });
  result.lines = () => result.output.split(/\r?\n/).filter(Boolean);
  running.push(result);
  return result;
}
async function list(reader) {
  const result = startReader(reader, [origin]);
  await until(`${reader} list`, () => result.code !== undefined, 180);
  assert.equal(result.code, 0, `${reader} list failed: ${result.errors}`);
  return result.lines();
}

// --- The race ---
const at = second => new Date(Date.UTC(2026, 10, 24, 8, 0, second)).toISOString().replace('.000Z', '+00:00');
const blank = bib => ({ bib, status: 'Ready', hundredths: null, rank: null, difference: null, at: at(0) });
const bibs = Array.from({ length: 40 }, (_, i) => i + 1);
const state = { version: 1, competition: { name: 'Synthetic slalom', place: 'Testville', discipline: 'SL', date: '2026-11-24', isFis: false, codex: '',
  gender: 'L', category: 'Club', intermediateCount: 1, slope: 'Test', runCount: 2 },
  competitors: bibs.map(bib => ({ bib, lastName: bib === 2 ? 'ÄÄKKÖNEN' : `TEST${bib}`, firstName: 'Åke', nation: 'FIN', club: 'Test Club', fisCode: String(1000 + bib) })),
  currentRun: 1, runs: [{ number: 1, listCreatedAt: at(0), startOrder: bibs, results: bibs.map(blank),
    forerunners: [{ letter: 'A', lastName: 'FORE', firstName: 'Runner', nation: 'FIN', status: 'Ready' }] }], updatedAt: at(0) };
let session;
async function call(method, route, body, expected = 200) {
  const response = await fetch(`${origin}/api/sessions/${session.sessionId}${route}`, { method, body: body && JSON.stringify(body),
    headers: { Authorization: `Bearer ${session.publisherToken}`, ...(body ? { 'Content-Type': 'application/json' } : {}) } });
  assert.equal(response.status, expected, `${method} ${route}: ${response.status}`);
  return response;
}
const row = (bib, run = 1) => state.runs.find(x => x.number === run).results.find(x => x.bib === bib);
const name = bib => { const x = state.competitors.find(c => c.bib === bib); return `${x.lastName} ${x.firstName}`; };
// One row as an event, or any change as the complete state.
async function publishRow(bib, values, run = 1) {
  Object.assign(row(bib, run), values);
  state.version++;
  await call('POST', '/events', { version: state.version, run, kind: 'ResultUpdated', result: row(bib, run), at: at(1) });
}
async function publishState() { state.version++; await call('PUT', '/state', state); }

const expected = [], watching = [];
let mirror, mirrorError, mirrorDone, raw;
const received = [];
async function expect(...lines) {
  expected.push(...lines);
  for (const reader of watching) {
    await until(`${reader.reader}: ${expected.at(-1)}\n${reader.output}\n${reader.errors}`, () => reader.lines().length >= expected.length || reader.code !== undefined, 60);
    assert.deepEqual(reader.lines(), expected, `${reader.reader}: ${reader.errors}`);
  }
}
async function settled() {
  const current = await (await fetch(`${origin}/api/sessions/${session.sessionId}/state`)).json();
  await until('the reader library shows the state of the server', () => JSON.stringify(mirror) === JSON.stringify(current));
}

try {
  await startServer();
  const nothing = await fetch(`${origin}/api/sessions`);
  assert.deepEqual(await nothing.json(), []);
  for (const reader of readers) assert.deepEqual(await list(reader), ['No race is published now.'], reader);
  const creation = await fetch(`${origin}/api/sessions`, { method: 'POST', headers: { Authorization: `Bearer ${publisherKey}` } });
  session = await creation.json();
  assert.equal((await fetch(`${origin}/api/sessions/${session.sessionId}/state`)).status, 404);

  // Every reader, the reader library and a raw connection subscribe before the first publish.
  for (const reader of readers) watching.push(startReader(reader, [(await startProxy()).origin, session.sessionId]));
  mirrorDone = follow((await startProxy()).origin, session.sessionId, value => { mirror = value; }, () => {}).catch(error => { mirrorError = error; });
  const connectRaw = async () => {
    raw = new HubConnectionBuilder().withUrl(`${origin}/live`).configureLogging(LogLevel.None).build();
    for (const [target, type] of [['Sync', 'SyncMessage'], ['Delta', 'DeltaMessage'], ['Beacon', 'BeaconMessage']])
      raw.on(target, message => received.push([type, message]));
    await raw.start();
    await raw.invoke('WatchSync', session.sessionId, 1);
  };
  await connectRaw();
  const viewers = readers.length + 2;
  const everyoneWatches = () => until('everyone is watching\n' + watching.map(x => x.reader + x.errors).join('\n'),
    async () => (await (await call('GET', '/stats')).json()).viewers.current === viewers, 180);
  await everyoneWatches();
  await expect('Waiting for the publisher');

  await publishState();
  await expect('Race | Synthetic slalom | 40 competitors');
  for (const reader of readers) assert.deepEqual(await list(reader), [`${session.sessionId} | 2026-11-24 | SL | Synthetic slalom`], reader);

  const split = hundredths => ({ intermediates: [{ number: 1, hundredths, at: at(2), sourceTicks: '639000000000000000' }] });
  await publishRow(1, { status: 'OnCourse', startedAt: at(1), startSourceTicks: '639000000000000000' });
  await expect(`Run 1 | 1 ${name(1)} | OnCourse`);
  await publishRow(1, split(2501));
  await expect(`Run 1 | 1 ${name(1)} | Intermediate 1 25.01`);
  await publishRow(1, { status: 'Finished', hundredths: 5012, rank: 1, difference: 0, finishSourceTicks: '639000000050120000' });
  await expect(`Run 1 | 1 ${name(1)} | Finished 50.12, rank 1`);
  // A new leader changes another row's rank, which is not an event of its own; the changes arrive as one state.
  await publishRow(2, { status: 'OnCourse', startedAt: at(3) });
  Object.assign(row(2), split(2490), { status: 'Finished', hundredths: 4987, rank: 1, difference: 0 });
  Object.assign(row(1), { rank: 2, difference: 25 });
  await publishState();
  await expect(`Run 1 | 2 ${name(2)} | OnCourse`, `Run 1 | 2 ${name(2)} | Intermediate 1 24.90`, `Run 1 | 2 ${name(2)} | Finished 49.87, rank 1`);
  await publishRow(3, { status: 'DNS' });
  await publishRow(4, { status: 'OnCourse', startedAt: at(4) });
  await publishRow(4, { status: 'DNF' });
  await publishRow(5, { status: 'Review' });
  await publishRow(5, { status: 'Finished', hundredths: 6203, rank: 3, difference: 1216 });
  await publishRow(8, { status: 'NPS' });
  await expect(`Run 1 | 3 ${name(3)} | DNS`, `Run 1 | 4 ${name(4)} | OnCourse`, `Run 1 | 4 ${name(4)} | DNF`,
    `Run 1 | 5 ${name(5)} | Review`, `Run 1 | 5 ${name(5)} | Finished 1:02.03, rank 3`, `Run 1 | 8 ${name(8)} | NPS`);
  // Corrections: a time, an intermediate time, a disqualification that reorders the rows.
  await publishRow(2, { hundredths: 4990 });
  await publishRow(2, { intermediates: null });
  Object.assign(row(1), { status: 'DSQ', hundredths: null, rank: null, difference: null });
  state.runs[0].results.reverse();
  await publishState();
  await expect(`Run 1 | 2 ${name(2)} | Time changed 49.90, rank 1`, `Run 1 | 2 ${name(2)} | Intermediate 1 removed`, `Run 1 | 1 ${name(1)} | DSQ`);
  assert(received.some(([type, x]) => type === 'DeltaMessage' && x.runs[0]?.order), 'A delta with a new row order');
  state.runs[0].forerunners[0].status = 'OnCourse';
  await publishState();
  await settled();
  assert(received.some(([type, x]) => type === 'DeltaMessage' && x.runs[0]?.forerunners), 'A delta with forerunners');

  // A lost message followed by others: the gap asks for the complete state, which has the lost start of bib 9 and joins
  // the two changes of bib 6. The server orders the rows by bib when it applies an event.
  for (const proxy of proxies) proxy.drop = 1;
  await publishRow(9, { status: 'OnCourse', startedAt: at(5) });
  await until('every reader loses a message', () => proxies.every(x => x.dropped === 1));
  await publishRow(6, { status: 'OnCourse', startedAt: at(5) });
  await publishRow(6, { status: 'DNF' });
  await expect(`Run 1 | 6 ${name(6)} | DNF`, `Run 1 | 9 ${name(9)} | OnCourse`);
  await settled();
  // A lost message and then nothing: the beacon shows it.
  await wait(3500);
  for (const proxy of proxies) proxy.drop = 1;
  await publishRow(7, { status: 'OnCourse', startedAt: at(6) });
  await until('every reader loses another message', () => proxies.every(x => x.dropped === 2));
  await expect(`Run 1 | 7 ${name(7)} | OnCourse`);
  await settled();

  // A restarted server has no state until the publisher restores it; the readers keep theirs and compare.
  await raw.stop();
  await stopServer();
  await startServer();
  await connectRaw();
  await everyoneWatches();
  await expect('Waiting for the publisher');
  Object.assign(row(7), { status: 'Finished', hundredths: 5500, rank: 2, difference: 510 });
  await publishState();
  await expect(`Run 1 | 7 ${name(7)} | Finished 55.00, rank 2`);

  // A new run is not an event; its results are.
  state.runs.push({ number: 2, listCreatedAt: at(10), startOrder: [2, 7, 5], results: [2, 7, 5].map(blank), forerunners: null });
  state.currentRun = 2;
  await publishState();
  await publishRow(2, { status: 'OnCourse', startedAt: at(11) }, 2);
  await publishRow(2, { status: 'Finished', hundredths: 5100, rank: 1, difference: 0, totalHundredths: 10090, totalRank: 1, totalDifference: 0 }, 2);
  await expect(`Run 2 | 2 ${name(2)} | OnCourse`, `Run 2 | 2 ${name(2)} | Finished 51.00, rank 1`);
  await call('POST', '/pause');
  await settled();
  assert.equal(mirror.paused, true);

  // Everything the server sent is documented.
  received.push(['State', await (await fetch(`${origin}/api/sessions/${session.sessionId}/state`)).json()]);
  for (const race of await (await fetch(`${origin}/api/sessions`)).json()) received.push(['Race', race]);
  await until('a beacon', () => received.some(([type, x]) => type === 'BeaconMessage' && x.status === 'Live'));
  await call('DELETE', '', undefined, 204);
  await until('the deletion', () => received.some(([type, x]) => type === 'SyncMessage' && x.status === 'Deleted'));
  const kinds = new Set(received.map(([type, x]) => type + (x.status ? ` ${x.status}` : '')));
  for (const kind of ['SyncMessage Live', 'SyncMessage Awaiting', 'SyncMessage Deleted', 'DeltaMessage', 'BeaconMessage Live', 'State', 'Race'])
    assert(kinds.has(kind), `No ${kind} was validated.`);
  for (const [type, message] of received) assert.deepEqual(validate(schema, type, message), [], JSON.stringify(message).slice(0, 2000));

  await expect('Deleted');
  for (const reader of watching) {
    await until(`${reader.reader} ends`, () => reader.code !== undefined);
    assert.equal(reader.code, 0, `${reader.reader}: ${reader.errors}`);
  }
  await mirrorDone;
  assert.equal(mirrorError, undefined);
  assert.equal(new RaceState().phase, 'subscribing');
  // An unsupported subscription version is refused, not silently ignored.
  await assert.rejects(raw.invoke('WatchSync', session.sessionId, 99));
  console.log(`Live API verified with the ${readers.join(', ')} readers: ${expected.length} lines each, ${received.length} messages match the schema.`);
} finally {
  await raw?.stop().catch(() => {});
  for (const reader of running) if (reader.code === undefined) reader.process.kill();
  for (const proxy of proxies) proxy.server.close();
  server?.kill('SIGKILL');
  try { rmSync(outside, { recursive: true, force: true }); } catch { /* A process may still hold a file on Windows. */ }
}
process.exit(0);
