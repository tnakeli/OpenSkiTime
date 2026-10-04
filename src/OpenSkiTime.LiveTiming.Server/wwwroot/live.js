/* Minimal SignalR JSON protocol client. All assets work offline; no CDN dependency. */
(() => {
  const id = location.pathname.split('/')[2];
  const get = name => document.getElementById(name);
  let state, selectedRun, socket, retry, attempt = 0, waiting = false, stale = false, watchPending = false;
  // The server pings every 15 s; longer silence means a dead or half-open connection (e.g. replaced revision).
  const serverTimeout = 35000, pingInterval = 15000;
  const time = value => value == null ? '—' : `${Math.floor(value / 6000)}:${String(Math.floor(value / 100) % 60).padStart(2, '0')}.${String(value % 100).padStart(2, '0')}`;
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
    if (!next.runs.some(r => r.number === selectedRun)) selectedRun = next.currentRun;
    next.runs.forEach(r => { const b = document.createElement('button'); b.textContent = `Run ${r.number}`; b.setAttribute('aria-pressed', r.number === selectedRun); b.onclick = () => { selectedRun = r.number; render(state); }; get('runs').append(b); });
    const run = next.runs.find(r => r.number === selectedRun);
    const onCourse = next.runs.find(r => r.number === next.currentRun).results.filter(r => r.status === 'OnCourse').map(r => { const c = next.competitors.find(c => c.bib === r.bib); return `${r.bib} ${c.lastName} ${c.firstName}`; });
    get('course').textContent = stale ? 'Waiting for publisher · Last state retained' : next.paused ? 'Publishing stopped · Last state retained' : `On course · ${onCourse.join(' / ') || '—'}`;
    get('updated').textContent = `Run ${selectedRun} · Last live update ${new Date(next.updatedAt).toLocaleString()}`;
    // Show the intermediates column only when this run has at least one intermediate time.
    const intermediates = run.results.some(r => (r.intermediates || []).length > 0);
    get('intermediates').hidden = !intermediates;
    const rows = run.startOrder.map(bib => run.results.find(r => r.bib === bib) || {bib, status:'Ready'});
    rows.sort((a,b) => (a.rank || 99999) - (b.rank || 99999) || run.startOrder.indexOf(a.bib)-run.startOrder.indexOf(b.bib));
    rows.forEach(r => {
      const c = next.competitors.find(c => c.bib === r.bib), tr = document.createElement('tr'); tr.dataset.bib = r.bib; tr.className = r.status.toLowerCase();
      [r.rank || '—', r.bib, `${c.lastName} ${c.firstName}`, [c.nation,c.club].filter(Boolean).join(' / '), r.status,
        intermediates ? (r.intermediates || []).map(i => `I${i.number} ${time(i.hundredths)}`).join(' · ') || '—' : null, time(r.hundredths), r.difference == null ? '—' : '+' + time(r.difference)]
        .filter(value => value !== null)
        .forEach(value => { const td = document.createElement('td'); td.textContent = value; tr.append(td); });
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
  if (id) { get('race').hidden = false; connect(); } else { get('list').hidden = false; list(); }
})();
