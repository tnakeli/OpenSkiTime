// OpenSkiTime Live API reader for Node.js 22 or later. MIT license. https://openskiti.me/live-api/
//
//   npm install @microsoft/signalr@10
//   node live-reader.mjs                                        lists the races published now
//   node live-reader.mjs https://live.openskiti.me <sessionId>  follows one race
import { pathToFileURL } from 'node:url';

/** The state of one race, kept up to date from the messages of the server. */
export class RaceState {
  phase = 'subscribing'; // subscribing, live, awaiting or deleted
  generation = null;
  revision = 0;
  state = null;
  resyncSince = null; // When the complete state was asked for, or null.

  /** A new connection: the previous state stays until the subscription is answered. */
  connected() { Object.assign(this, { phase: 'subscribing', generation: null, revision: 0, resyncSince: null }); }

  /** The complete state, or the reason why there is none. Always the newest on this connection. */
  sync(message) {
    const live = message.status === 'Live';
    this.resyncSince = null;
    this.phase = live ? 'live' : message.status === 'Deleted' ? 'deleted' : 'awaiting';
    this.generation = live ? message.generation : null;
    this.revision = live ? message.revision : 0;
    if (live) this.state = message.state;
    else if (this.phase === 'deleted') this.state = null;
    return 'changed';
  }

  /** One change. It applies only to the revision it names. */
  delta(message, now = Date.now()) {
    if (this.phase === 'subscribing' || this.resyncSince !== null) return 'none';
    if (this.phase !== 'live' || message.generation !== this.generation) return this.#resync(now);
    if (message.revision <= this.revision) return 'none';
    if (message.baseRevision !== this.revision) return this.#resync(now);
    const next = merge(this.state, message);
    if (!next) return this.#resync(now);
    this.revision = message.revision;
    this.state = next;
    return 'changed';
  }

  /** What the server has now: confirms the state, or shows that a message was missed. */
  beacon(message, now = Date.now()) {
    if (this.phase === 'subscribing') return 'none';
    if (this.resyncSince !== null) return now - this.resyncSince >= 30000 ? this.#resync(now) : 'none';
    const agrees = message.status === 'Live'
      ? this.phase === 'live' && message.generation === this.generation && message.revision === this.revision
      : this.phase === (message.status === 'Deleted' ? 'deleted' : 'awaiting');
    return agrees ? 'none' : this.#resync(now);
  }

  #resync(now) { this.resyncSince = now; return 'resync'; }
}

/** The state after a delta, or null when the delta does not fit it. */
export function merge(state, delta) {
  if (delta.runs.some(change => !state.runs.some(run => run.number === change.number))) return null;
  const runs = [];
  for (const run of state.runs) {
    const change = delta.runs.find(x => x.number === run.number);
    if (!change) { runs.push(run); continue; }
    const rows = new Map(run.results.map(row => [row.bib, row]));
    for (const row of change.results) rows.set(row.bib, row); // A Map keeps known rows in place and appends new ones.
    const order = change.order ?? [...rows.keys()];
    if (order.length !== rows.size || new Set(order).size !== order.length || order.some(bib => !rows.has(bib))) return null;
    runs.push({ ...run, results: order.map(bib => rows.get(bib)), forerunners: change.forerunners ?? run.forerunners });
  }
  return { ...state, version: delta.version, updatedAt: delta.updatedAt, paused: delta.paused, runs };
}

/** What happened between two states: status changes, intermediate times and corrected run times. */
export function changes(before, after) {
  const result = [];
  for (const run of after.runs) {
    const rows = new Map((before.runs.find(x => x.number === run.number)?.results ?? []).map(row => [row.bib, row]));
    for (const row of run.results) {
      const old = rows.get(row.bib) ?? { status: 'Ready', hundredths: null, intermediates: null };
      const at = { run: run.number, row };
      const status = row.status !== old.status ? [{ ...at, kind: 'status', from: old.status }]
        : row.status === 'Finished' && row.hundredths !== old.hundredths ? [{ ...at, kind: 'time' }] : [];
      const times = new Map((row.intermediates ?? []).map(x => [x.number, x.hundredths]));
      const oldTimes = new Map((old.intermediates ?? []).map(x => [x.number, x.hundredths]));
      const intermediates = [...new Set([...oldTimes.keys(), ...times.keys()])].sort((a, b) => a - b)
        .filter(number => times.get(number) !== oldTimes.get(number))
        .map(number => ({ ...at, kind: 'intermediate', number, hundredths: times.get(number) ?? null }));
      // When a break in the connection joined several changes, a start comes before and a finish after the intermediates.
      result.push(...(row.status === 'OnCourse' ? [...status, ...intermediates] : [...intermediates, ...status]));
    }
  }
  return result;
}

/** Follows one race until it is deleted. Calls onState(state, changes) after every change and onStatus(phase) without state. */
export async function follow(server, sessionId, onState, onStatus) {
  const { HubConnectionBuilder, LogLevel } = await import('@microsoft/signalr');
  const race = new RaceState();
  const wait = ms => new Promise(resolve => setTimeout(resolve, ms));
  for (let attempt = 0; ;) {
    const hub = new HubConnectionBuilder().withUrl(`${server}/live`).configureLogging(LogLevel.None).build();
    const closed = new Promise(resolve => hub.onclose(resolve));
    let lastResync = 0;
    const handle = step => message => {
      const before = race.state, outcome = step(message);
      if (outcome === 'changed') {
        if (race.phase === 'live') onState(race.state, before ? changes(before, race.state) : []);
        else onStatus(race.phase);
        if (race.phase === 'deleted') hub.stop();
      } else if (outcome === 'resync') {
        const delay = Math.max(500 + Math.random() * 4500, lastResync + 3000 - Date.now());
        setTimeout(() => {
          if (race.resyncSince === null || hub.state !== 'Connected') return;
          lastResync = Date.now();
          hub.send('Resync').catch(() => { /* The connection broke; the loop connects again. */ });
        }, delay);
      }
    };
    hub.on('Sync', handle(message => race.sync(message)));
    hub.on('Delta', handle(message => race.delta(message)));
    hub.on('Beacon', handle(message => race.beacon(message)));
    race.connected();
    let connected = false;
    try {
      await hub.start();
      connected = true;
      await hub.invoke('WatchSync', sessionId, 1);
      attempt = 0;
      await closed;
    } catch (error) {
      if (connected && hub.state === 'Connected') throw error; // The server refused the subscription.
    }
    if (race.phase === 'deleted') return;
    await wait(Math.min(15000, 1000 * 2 ** attempt++) * (0.5 + Math.random() / 2));
  }
}

/** Hundredths as 50.12 or 1:02.03, with integer arithmetic. */
export function time(hundredths) {
  const minutes = Math.floor(hundredths / 6000), seconds = Math.floor(hundredths / 100) % 60;
  const rest = String(hundredths % 100).padStart(2, '0');
  return minutes > 0 ? `${minutes}:${String(seconds).padStart(2, '0')}.${rest}` : `${seconds}.${rest}`;
}

function describe(change) {
  const { row } = change;
  if (change.kind === 'intermediate')
    return `Intermediate ${change.number} ${change.hundredths === null ? 'removed' : time(change.hundredths)}`;
  if (row.status === 'Finished') return `${change.kind === 'time' ? 'Time changed' : 'Finished'} ${time(row.hundredths)}, rank ${row.rank}`;
  return row.status;
}

if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href) {
  const [server = 'https://live.openskiti.me', sessionId] = process.argv.slice(2);
  if (!sessionId) {
    // The hosted service sleeps between races: the first answer can take a couple of minutes.
    const response = await fetch(`${server}/api/sessions`, { signal: AbortSignal.timeout(120000) });
    if (!response.ok) throw new Error(`HTTP ${response.status}`);
    const races = await response.json();
    if (races.length === 0) console.log('No race is published now.');
    for (const race of races) console.log(`${race.sessionId} | ${race.date} | ${race.discipline} | ${race.name}`);
  } else {
    let named = false;
    await follow(server, sessionId, (state, list) => {
      if (!named) console.log(`Race | ${state.competition.name} | ${state.competitors.length} competitors`);
      named = true;
      for (const change of list) {
        const who = state.competitors.find(x => x.bib === change.row.bib);
        console.log(`Run ${change.run} | ${change.row.bib} ${who?.lastName ?? ''} ${who?.firstName ?? ''} | ${describe(change)}`);
      }
    }, phase => console.log(phase === 'deleted' ? 'Deleted' : 'Waiting for the publisher'));
  }
}
