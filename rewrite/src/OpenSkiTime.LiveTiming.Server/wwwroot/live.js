/* Minimal SignalR JSON protocol client. All assets work offline; no CDN dependency. */
(() => {
  const id = location.pathname.split('/')[2];
  const get = name => document.getElementById(name);
  let state, selectedRun, socket, retry;
  const time = value => value == null ? '—' : `${Math.floor(value / 6000)}:${String(Math.floor(value / 100) % 60).padStart(2, '0')}.${String(value % 100).padStart(2, '0')}`;
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
    get('course').textContent = next.paused ? 'Publishing stopped · Last state retained' : `On course · ${onCourse.join(' / ') || '—'}`;
    get('updated').textContent = `Run ${selectedRun} · Last live update ${new Date(next.updatedAt).toLocaleString()}`;
    const rows = run.startOrder.map(bib => run.results.find(r => r.bib === bib) || {bib, status:'Ready'});
    rows.sort((a,b) => (a.rank || 99999) - (b.rank || 99999) || run.startOrder.indexOf(a.bib)-run.startOrder.indexOf(b.bib));
    rows.forEach(r => {
      const c = next.competitors.find(c => c.bib === r.bib), tr = document.createElement('tr'); tr.dataset.bib = r.bib; tr.className = r.status.toLowerCase();
      [r.rank || '—', r.bib, `${c.lastName} ${c.firstName}`, [c.nation,c.club].filter(Boolean).join(' / '), r.status,
        (r.intermediates || []).map(i => `I${i.number} ${time(i.hundredths)}`).join(' · ') || '—', time(r.hundredths), r.difference == null ? '—' : '+' + time(r.difference)]
        .forEach(value => { const td = document.createElement('td'); td.textContent = value; tr.append(td); });
      get('results').append(tr);
    });
  }
  async function connect() {
    if (!id) { render(null); return; }
    try {
      get('connection').textContent = 'Connecting';
      const response = await fetch('/live/negotiate?negotiateVersion=1', {method:'POST'});
      if (!response.ok) throw Error('Negotiation failed');
      const info = await response.json(), url = new URL('/live', location.href);
      url.protocol = location.protocol === 'https:' ? 'wss:' : 'ws:'; url.searchParams.set('id', info.connectionToken);
      socket = new WebSocket(url); let pending = '', handshaken = false;
      socket.onopen = () => socket.send(JSON.stringify({protocol:'json',version:1}) + '\u001e');
      socket.onmessage = event => {
        pending += event.data; const messages = pending.split('\u001e'); pending = messages.pop();
        for (const raw of messages) {
          if (!raw) continue; const message = JSON.parse(raw);
          if (!handshaken) { if (message.error) { socket.close(); return; } handshaken = true; get('connection').textContent = 'Live'; socket.send(JSON.stringify({type:1,target:'Watch',arguments:[id],invocationId:'1'}) + '\u001e'); }
          else if (message.type === 1 && message.target === 'State') render(message.arguments[0]);
          else if (message.type === 7) socket.close();
        }
      };
      socket.onclose = () => { get('connection').textContent = 'Reconnecting'; clearTimeout(retry); retry = setTimeout(connect,2000); };
      socket.onerror = () => socket.close();
    } catch { get('connection').textContent = 'Reconnecting'; retry = setTimeout(connect,2000); }
  }
  connect();
})();
