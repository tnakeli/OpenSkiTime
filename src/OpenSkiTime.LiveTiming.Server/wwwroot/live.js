/* Minimal SignalR JSON protocol client. All assets work offline; no CDN dependency. */
(() => {
  const id = location.pathname.split('/')[2];
  const get = name => document.getElementById(name);
  let state, selectedRun, followedRun, socket, retry, attempt = 0, waiting = false, stale = false, watchPending = false;
  // The server pings every 15 s; longer silence means a dead or half-open connection (e.g. replaced revision).
  const serverTimeout = 35000, pingInterval = 15000;
  const time = value => value == null ? '—' : `${Math.floor(value / 6000)}:${String(Math.floor(value / 100) % 60).padStart(2, '0')}.${String(value % 100).padStart(2, '0')}`;
  // The viewer's choice of intermediate or sector times is remembered in this browser only; storage may be unavailable.
  const splitModeKey = 'openskitime.live.splitMode';
  let splitMode = (() => { try { return localStorage.getItem(splitModeKey) === 'sector' ? 'sector' : 'intermediate'; } catch { return 'intermediate'; } })();
  const gender = value => ({M:'Men', L:'Women', W:'Women', A:'Mixed'})[value] || '';
  async function list() {
    try {
      const response = await fetch('/api/sessions', {cache:'no-store'});
      if (!response.ok) throw Error('List failed');
      const races = await response.json();
      get('connection').textContent = 'Live';
      get('races').replaceChildren(...races.map(r => {
        const li = document.createElement('li'), a = document.createElement('a'), meta = document.createElement('span');
        a.href = `/r/${encodeURIComponent(r.sessionId)}`; a.textContent = r.name;
        meta.textContent = [r.date, r.place, r.discipline, gender(r.gender), r.isFis && r.codex ? `Codex ${r.codex}` : '', r.paused ? 'Publishing stopped' : ''].filter(Boolean).join(' · ');
        li.append(a, meta); return li;
      }));
      get('empty').hidden = races.length > 0;
    } catch { get('connection').textContent = 'Reconnecting'; }
    retry = setTimeout(list, 30000);
  }
  function render(next) {
    state = next;
    get('results').replaceChildren(); get('runs').replaceChildren();
    if (!next) { get('name').textContent = 'Live timing unavailable'; get('metadata').textContent = ''; get('course').textContent = 'Session ended or awaiting publisher'; get('updated').textContent = ''; return; }
    get('name').textContent = next.competition.name;
    get('metadata').textContent = `${next.competition.place} · ${next.competition.discipline} · ${next.competition.date}`;
    // A viewer may look at another run; when the race moves on to a new current run, every viewer follows it.
    if (next.currentRun !== followedRun || !next.runs.some(r => r.number === selectedRun)) { selectedRun = followedRun = next.currentRun; }
    next.runs.forEach(r => { const b = document.createElement('button'); b.textContent = `Run ${r.number}`; b.setAttribute('aria-pressed', r.number === selectedRun); b.onclick = () => { selectedRun = r.number; render(state); }; get('runs').append(b); });
    const run = next.runs.find(r => r.number === selectedRun);
    const onCourse = next.runs.find(r => r.number === next.currentRun).results.filter(r => r.status === 'OnCourse').map(r => { const c = next.competitors.find(c => c.bib === r.bib); return `${r.bib} ${c.lastName} ${c.firstName}`; });
    // Once every starter of the current run is finished or classified, the banner says so instead of an empty course.
    const current = next.runs.find(r => r.number === next.currentRun);
    const complete = current.startOrder.length > 0 && current.startOrder.every(bib => { const r = current.results.find(x => x.bib === bib); return r && !['Ready', 'OnCourse', 'Review'].includes(r.status); });
    get('course').textContent = stale ? 'Waiting for publisher · Last state retained' : next.paused ? 'Publishing stopped · Last state retained'
      : onCourse.length ? `On course · ${onCourse.join(' / ')}` : complete ? `Run ${next.currentRun} complete` : 'On course · —';
    get('updated').textContent = `Run ${selectedRun} · Last live update ${new Date(next.updatedAt).toLocaleString()}`;
    // Split columns appear only when this run has at least one intermediate time; the viewer picks intermediate or sector times.
    const splits = liveSplitCount(run.results);
    get('splitMode').hidden = splits === 0;
    get('splitMode').querySelectorAll('button').forEach(b => b.setAttribute('aria-pressed', b.dataset.mode === splitMode));
    // From Run 2 on, each earlier run's time and the combined time have their own columns, and standings use the combined
    // time. A publisher without totals keeps the run standings.
    const earlier = next.runs.filter(r => r.number < run.number).map(r => r.number).sort((a, b) => a - b);
    const later = run.number > 1, totals = later && run.results.some(r => r.totalHundredths != null);
    const rank = r => totals ? r.totalRank : r.rank, gap = r => totals ? r.totalDifference : r.difference;
    const fixed = ['Rank', 'Bib', 'Competitor', 'Nation / club', 'Status'];
    get('head').replaceChildren(...[...fixed, ...earlier.map(n => `Run ${n}`), ...liveSplitHeaders(splits, splitMode),
      later ? `Run ${run.number}` : 'Finish', ...(later ? ['Total'] : []), 'Difference'].map((value, i) => {
      const th = document.createElement('th'); th.textContent = value; if (i >= fixed.length) th.className = 'num'; return th;
    }));
    // An earlier run shows its time, or its status when the racer did not finish it.
    const earlierRun = x => !x ? '—' : x.status === 'Finished' ? time(x.hundredths) : ['Ready', 'OnCourse'].includes(x.status) ? '—' : x.status;
    const rows = liveResultOrder(run.startOrder.map(bib => run.results.find(r => r.bib === bib) || {bib, status:'Ready'}), run.startOrder, rank);
    rows.forEach(r => {
      const c = next.competitors.find(c => c.bib === r.bib), tr = document.createElement('tr'); tr.dataset.bib = r.bib; tr.className = r.status.toLowerCase();
      [rank(r) || '—', r.bib, `${c.lastName} ${c.firstName}`, [c.nation,c.club].filter(Boolean).join(' / '), r.status,
        ...liveEarlierRuns(next, run.number, r.bib).map(x => earlierRun(x.result)), ...liveSplitValues(r, splits, splitMode).map(time),
        time(r.hundredths), ...(later ? [time(r.totalHundredths)] : []), gap(r) == null ? '—' : '+' + time(gap(r))]
        .forEach((value, i) => { const td = document.createElement('td'); td.textContent = value; if (i >= fixed.length) td.className = 'num'; tr.append(td); });
      get('results').append(tr);
    });
  }
  function schedule() {
    get('connection').textContent = 'Reconnecting'; clearTimeout(retry); waiting = true;
    // Exponential backoff with jitter spreads every viewer's reconnect after a service restart.
    retry = setTimeout(connect, Math.min(15000, 1000 * 2 ** attempt++) * (0.5 + Math.random() / 2));
  }
  function drop(closing) {
    if (socket !== closing) return; // Late events from an abandoned socket never start a second connection.
    socket = null; clearInterval(closing.keepAlive);
    closing.onopen = closing.onmessage = closing.onclose = closing.onerror = null;
    try { closing.close(); } catch { /* Already closed. */ }
    schedule();
  }
  async function connect() {
    clearTimeout(retry); waiting = false;
    try {
      get('connection').textContent = 'Connecting';
      const response = await fetch('/live/negotiate?negotiateVersion=1', {method:'POST'});
      if (!response.ok) throw Error('Negotiation failed');
      const info = await response.json(), url = new URL('/live', location.href);
      url.protocol = location.protocol === 'https:' ? 'wss:' : 'ws:'; url.searchParams.set('id', info.connectionToken);
      const current = socket = new WebSocket(url); let pending = '', handshaken = false, heard = Date.now(), pinged = Date.now();
      current.onopen = () => current.send(JSON.stringify({protocol:'json',version:1}) + '\u001e');
      current.onmessage = event => {
        heard = Date.now(); pending += event.data; const messages = pending.split('\u001e'); pending = messages.pop();
        for (const raw of messages) {
          if (!raw) continue; const message = JSON.parse(raw);
          if (!handshaken) { if (message.error) { drop(current); return; } handshaken = true; attempt = 0; watchPending = true; get('connection').textContent = 'Live'; current.send(JSON.stringify({type:1,target:'Watch',arguments:[id],invocationId:'1'}) + '\u001e'); }
          else if (message.type === 1 && message.target === 'State') {
            // After a service restart the server is empty until the publisher restores it; keep showing the last results meanwhile.
            const next = message.arguments[0]; stale = watchPending && !next && !!state; watchPending = false;
            render(stale ? state : next);
          }
          else if (message.type === 7) drop(current);
        }
      };
      current.onclose = current.onerror = () => drop(current);
      current.keepAlive = setInterval(() => {
        if (Date.now() - heard > serverTimeout) drop(current);
        else if (handshaken && Date.now() - pinged >= pingInterval) { pinged = Date.now(); current.send(JSON.stringify({type:6}) + '\u001e'); }
      }, 5000);
    } catch { socket = null; schedule(); }
  }
  // A returning network reconnects at once instead of waiting out the backoff.
  addEventListener('online', () => { if (id && waiting) { attempt = 0; connect(); } });
  get('splitMode').querySelectorAll('button').forEach(b => b.onclick = () => {
    splitMode = b.dataset.mode;
    try { localStorage.setItem(splitModeKey, splitMode); } catch { /* Not remembered; the choice still applies now. */ }
    if (state) render(state);
  });
  if (id) { get('race').hidden = false; connect(); } else { get('list').hidden = false; list(); }
})();
