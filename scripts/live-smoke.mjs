import assert from 'node:assert/strict';
import { pathToFileURL } from 'node:url';

export function syntheticSnapshot() {
  const at = '2026-10-03T09:00:00+00:00';
  return {
    version: 1,
    competition: { name:'OpenSkiTime deployment check', place:'Synthetic test', discipline:'SL', date:'2026-10-03', isFis:false, codex:'', gender:'M', category:'Club', intermediateCount:0, slope:'' },
    competitors: [{bib:1,lastName:'TEST',firstName:'Synthetic',nation:'FIN',club:'Test Club',fisCode:''}],
    currentRun:1,
    runs:[{number:1,listCreatedAt:at,startOrder:[1],results:[{bib:1,status:'Ready',hundredths:null,rank:null,difference:null,at}]}],
    updatedAt:at, paused:false
  };
}

async function request(origin, route, method = 'GET', body, token) {
  try {
    const response = await fetch(new URL(route, origin), {
      method, redirect:'error', signal:AbortSignal.timeout(60000),
      headers:{ ...(body ? {'Content-Type':'application/json'} : {}), ...(token ? {Authorization:`Bearer ${token}`} : {}) },
      ...(body ? {body:JSON.stringify(body)} : {})
    });
    return response;
  } catch { throw new Error(`Live check request failed: ${method} ${route.split('/')[0]}`); }
}

export async function smoke(origin, {websocket = true, publisherKey = process.env.LIVE_PUBLISHER_KEY, requirePublisherKey = false} = {}) {
  const url = new URL(origin);
  if (url.protocol !== 'https:' && !(url.protocol === 'http:' && ['localhost','127.0.0.1'].includes(url.hostname))) {
    throw new Error('Live check requires HTTPS, except on loopback.');
  }
  assert.equal(url.pathname, '/', 'Use an origin without a path.');
  let healthy = false;
  const readinessDeadline = Date.now() + 90000;
  do {
    try { healthy = (await request(url, 'health')).status === 200; } catch { /* Cold ingress can take time to become ready. */ }
    if (healthy) break;
    await new Promise(resolve => setTimeout(resolve, 2000));
  } while (Date.now() < readinessDeadline);
  assert(healthy, 'Health endpoint did not become ready');
  if (requirePublisherKey) {
    assert(publisherKey, 'A publisher key is required for this check (LIVE_PUBLISHER_KEY).');
    assert.equal((await request(url, 'api/sessions', 'POST')).status, 401, 'Reject anonymous session creation');
  }
  const creation = await request(url, 'api/sessions', 'POST', undefined, publisherKey);
  assert.equal(creation.status, 200, 'Session creation');
  const session = await creation.json();
  assert.match(session.sessionId, /^[0-9a-f-]{36}$/i);
  const route = `api/sessions/${session.sessionId}`;
  let socket, observedVersion = 0, onVersion;
  try {
    const state = syntheticSnapshot();
    assert.equal((await request(url, `${route}/state`, 'PUT', state, 'invalid')).status, 401, 'Reject invalid credentials');
    assert.equal((await request(url, `${route}/state`, 'PUT', state, session.publisherToken)).status, 200, 'Full state publication');
    assert.equal((await (await request(url, `${route}/state`)).json()).competitors[0].lastName, 'TEST');
    if (websocket) {
      const negotiate = await request(url, 'live/negotiate?negotiateVersion=1', 'POST');
      assert.equal(negotiate.status, 200, 'SignalR negotiation');
      const negotiation = await negotiate.json();
      const wsUrl = new URL('live', url);
      wsUrl.protocol = url.protocol === 'https:' ? 'wss:' : 'ws:';
      wsUrl.searchParams.set('id', negotiation.connectionToken);
      socket = new WebSocket(wsUrl);
      await new Promise((resolve, reject) => {
        const timer = setTimeout(() => reject(new Error('SignalR state timeout')), 15000);
        let pending = '', handshake = false;
        const fail = () => { clearTimeout(timer); reject(new Error('SignalR connection failed')); };
        socket.addEventListener('error', fail, {once:true});
        socket.addEventListener('open', () => socket.send('{"protocol":"json","version":1}\u001e'), {once:true});
        socket.addEventListener('message', event => {
          try {
            pending += event.data;
            const parts = pending.split('\u001e'); pending = parts.pop();
            for (const part of parts) {
              if (!part) continue;
              const message = JSON.parse(part);
              if (!handshake) {
                assert(!message.error, 'SignalR handshake'); handshake = true;
                socket.send(JSON.stringify({type:1,target:'Watch',arguments:[session.sessionId],invocationId:'1'})+'\u001e');
              } else if (message.target === 'State') {
                observedVersion = message.arguments[0]?.version ?? 0;
                onVersion?.(observedVersion);
                if (observedVersion !== 1) continue;
                clearTimeout(timer); resolve();
              }
            }
          } catch { fail(); }
        });
      });
    }
    const result = {...state.runs[0].results[0],status:'Finished',hundredths:5458,rank:1,difference:0};
    const event = {version:2,run:1,kind:'CompetitorFinished',result,at:state.updatedAt};
    assert.equal((await request(url, `${route}/events`, 'POST', event, session.publisherToken)).status, 200, 'Result event');
    if (websocket && observedVersion !== 2) {
      await new Promise((resolve, reject) => {
        const timer = setTimeout(() => reject(new Error('SignalR updated state timeout')), 15000);
        onVersion = version => { if (version === 2) { clearTimeout(timer); resolve(); } };
      });
    }
    assert.equal((await (await request(url, `${route}/state`)).json()).runs[0].results[0].hundredths, 5458);
    assert.equal((await request(url, `${route}/pause`, 'POST', undefined, session.publisherToken)).status, 200, 'Pause');
    assert.equal((await (await request(url, `${route}/state`)).json()).paused, true);
  } finally {
    socket?.close();
    assert.equal((await request(url, `${route}/data`, 'DELETE', undefined, session.publisherToken)).status, 204, 'Synthetic session cleanup');
    assert.equal((await request(url, `${route}/state`)).status, 404, 'Deleted state unavailable');
  }
}

if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href) {
  try {
    await smoke(process.argv[2], {requirePublisherKey: process.argv.includes('--require-publisher-key')});
    console.log('Live check passed: health, authentication, snapshot, WebSocket, event, pause and deletion.');
  }
  catch (error) {
    // Name the failed check (an assertion label or request route); never print responses or credentials.
    const check = error instanceof assert.AssertionError || error.message?.startsWith('Live check') ? `: ${error.message.split('\n')[0]}` : '';
    console.error(`Live check failed${check}. Inspect deployment health; request data and credentials are intentionally not logged.`);
    process.exitCode = 1;
  }
}
