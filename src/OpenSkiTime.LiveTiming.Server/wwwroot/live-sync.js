/* Viewer synchronization of the race page: which state to show, from the server's messages on one connection. No DOM
   access; tested with Node (scripts/tests/live-sync.test.mjs).
   The server sends every message of a session in revision order: "Sync" (the complete state with its generation and
   revision, or the status Deleted or Awaiting), "Delta" (one change, applicable only to the revision it names) and
   "Beacon" (the current generation and revision, about every 30 s). The page keeps the generation and revision of what it
   shows. A gap, an unknown generation or a beacon that disagrees asks for the complete state again (Resync); deltas are
   dropped until it arrives. A delta applies entirely or not at all, and values are shown exactly as published: nothing is
   recalculated here.
   Each step returns {sync, show, resync}: show means draw sync.state again, resync means ask for the complete state. */
globalThis.liveSyncVersion = 1;
// A resync whose reply has not come by the next beacon after this long is asked for again.
globalThis.liveSyncResyncTimeout = 30000;
// A new connection shows the previous connection's state until the subscription reply arrives; nothing else applies before it.
globalThis.liveSyncConnect = (previous, connection) =>
  ({connection, phase: 'subscribing', generation: null, revision: 0, state: previous ? previous.state : null, resyncSince: null});
{
  const step = (sync, show = false, resync = false) => ({sync, show, resync});
  const request = (sync, now) => step({...sync, resyncSince: now}, false, true);
  // Messages of an abandoned connection, or before any connection, never apply.
  const foreign = (sync, connection) => !sync || sync.connection !== connection;
  // A complete state is the newest on this connection whatever its generation, because the server sends in order.
  globalThis.liveSyncFull = (sync, connection, message) => {
    if (foreign(sync, connection) || !message) return step(sync);
    const base = {...sync, resyncSince: null};
    if (message.status === 'Live' && message.state)
      return step({...base, phase: 'live', generation: message.generation, revision: message.revision, state: message.state}, true);
    if (message.status === 'Deleted') return step({...base, phase: 'deleted', generation: null, revision: 0, state: null}, true);
    // Awaiting: the server holds no state now (not yet restored after a restart, or not yet published); the last results stay.
    return step({...base, phase: 'awaiting', generation: null, revision: 0}, true);
  };
  globalThis.liveSyncDelta = (sync, connection, delta, now) => {
    if (foreign(sync, connection) || !delta || sync.phase === 'subscribing' || sync.resyncSince != null) return step(sync);
    if (sync.phase !== 'live' || delta.generation !== sync.generation) return request(sync, now);
    if (delta.revision <= sync.revision) return step(sync); // A duplicate or older change.
    if (delta.baseRevision !== sync.revision) return request(sync, now);
    const next = liveSyncMerge(sync.state, delta);
    return next ? step({...sync, revision: delta.revision, state: next}, true) : request(sync, now);
  };
  // A beacon never changes what is shown: it confirms it, or asks for the complete state.
  globalThis.liveSyncBeacon = (sync, connection, beacon, now) => {
    if (foreign(sync, connection) || !beacon || sync.phase === 'subscribing') return step(sync);
    if (sync.resyncSince != null) return now - sync.resyncSince >= liveSyncResyncTimeout ? request(sync, now) : step(sync);
    const agrees = beacon.status === 'Live' ? sync.phase === 'live' && beacon.generation === sync.generation && beacon.revision === sync.revision
      : beacon.status === 'Deleted' ? sync.phase === 'deleted' : sync.phase === 'awaiting';
    return agrees ? step(sync) : request(sync, now);
  };
  // A server without synchronization (an older release) sends the complete state after every change, and null both for a
  // deletion and, on subscribing, for a session it does not hold yet.
  globalThis.liveSyncLegacy = (sync, connection, state) => {
    if (foreign(sync, connection)) return step(sync);
    if (state) return step({...sync, phase: 'live', generation: null, revision: 0, state, resyncSince: null}, true);
    return sync.phase === 'subscribing' && sync.state ? step({...sync, phase: 'awaiting'}, true) : step({...sync, phase: 'deleted', state: null}, true);
  };
}
/* The state after a delta, or null when the delta does not fit it (an unknown run, or an order that does not list every row
   exactly once). Each listed row replaces the row with its bib or is new; without an order, rows keep their places and new
   rows follow. Forerunners are replaced when listed. Unchanged runs and rows are shared with the previous state. */
globalThis.liveSyncMerge = (state, delta) => {
  if (!state || !Array.isArray(delta.runs) || delta.runs.some(change => !state.runs.some(run => run.number === change.number))) return null;
  const runs = [];
  for (const run of state.runs) {
    const change = delta.runs.find(x => x.number === run.number);
    if (!change) { runs.push(run); continue; }
    const rows = new Map(run.results.map(r => [r.bib, r])), added = [];
    for (const row of change.results) { if (!rows.has(row.bib)) added.push(row.bib); rows.set(row.bib, row); }
    const order = change.order ?? [...run.results.map(r => r.bib), ...added];
    if (order.length !== rows.size || new Set(order).size !== order.length || order.some(bib => !rows.has(bib))) return null;
    const merged = {...run, results: order.map(bib => rows.get(bib))};
    if (change.forerunners != null) merged.forerunners = change.forerunners;
    runs.push(merged);
  }
  return {...state, version: delta.version, updatedAt: delta.updatedAt, paused: delta.paused, runs};
};
