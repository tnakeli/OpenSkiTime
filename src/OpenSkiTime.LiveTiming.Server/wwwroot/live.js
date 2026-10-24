/* Minimal SignalR JSON protocol client. All assets work offline; no CDN dependency. */
(() => {
  const id = location.pathname.split('/')[2];
  const get = name => document.getElementById(name);
  let state, selectedRun, followedRun, socket, retry, attempt = 0, waiting = false, stale = false, watchPending = false;
  // The latest finisher (or DNF/DSQ) is highlighted for ten seconds when it changes, not on the first view.
  const flashMs = 10000;
  let latestKey = null, flashUntil = 0, flashTimer;
  // The server pings every 15 s; longer silence means a dead or half-open connection (e.g. replaced revision).
  const serverTimeout = 35000, pingInterval = 15000;
  const time = value => value == null ? '—' : `${Math.floor(value / 6000)}:${String(Math.floor(value / 100) % 60).padStart(2, '0')}.${String(value % 100).padStart(2, '0')}`;
  // The viewer's choice of intermediate or sector times is remembered in this browser only; storage may be unavailable.
  const splitModeKey = 'openskitime.live.splitMode';
  let splitMode = (() => { try { return localStorage.getItem(splitModeKey) === 'sector' ? 'sector' : 'intermediate'; } catch { return 'intermediate'; } })();
  const gender = value => ({M:'Men', L:'Women', W:'Women', A:'Mixed'})[value] || '';
  // The connection state is shown in the race bar (and on the race list); a dot marks a live connection.
  const connection = text => document.querySelectorAll('[data-connection]').forEach(e => { e.textContent = text; e.dataset.state = text === 'Live' ? 'live' : 'waiting'; });
  const title = name => { document.title = name ? `${name} · OpenSkiTime Live` : 'OpenSkiTime Live Timing'; };
  async function list() {
    try {
      const response = await fetch('/api/sessions', {cache:'no-store'});
      if (!response.ok) throw Error('List failed');
      const races = await response.json();
      connection('Live');
      get('races').replaceChildren(...races.map(r => {
        const li = document.createElement('li'), a = document.createElement('a'), meta = document.createElement('span');
        a.href = `/r/${encodeURIComponent(r.sessionId)}`; a.textContent = r.name;
        meta.textContent = [r.date, r.place, r.discipline, gender(r.gender), r.isFis && r.codex ? `Codex ${r.codex}` : '', r.paused ? 'Publishing stopped' : ''].filter(Boolean).join(' · ');
        li.append(a, meta); return li;
      }));
      get('empty').hidden = races.length > 0;
    } catch { connection('Reconnecting'); }
    retry = setTimeout(list, 30000);
  }
  // One element with optional class and text.
  const element = (tag, className, text) => { const e = document.createElement(tag); if (className) e.className = className; if (text != null) e.textContent = text; return e; };
  // Flags are Unicode regional-indicator pairs. Where the platform draws them as two letters (Windows desktops) they would
  // only repeat the code, so they are left out: a drawn flag has coloured pixels, letters do not.
  const flagsDrawn = (() => {
    try {
      const canvas = document.createElement('canvas'); canvas.width = canvas.height = 20;
      const context = canvas.getContext('2d', {willReadFrequently: true}); context.textBaseline = 'top'; context.font = '16px sans-serif';
      context.fillText(liveNation('FIN').flag, 0, 0);
      const pixels = context.getImageData(0, 0, 20, 20).data;
      for (let i = 0; i < pixels.length; i += 4) if (pixels[i + 3] > 0 && (pixels[i] !== pixels[i + 1] || pixels[i + 1] !== pixels[i + 2])) return true;
      return false;
    } catch { return false; }
  })();
  // A nation as flag and code; the full name is the tooltip where it is used.
  const nation = code => {
    const n = liveNation(code), part = document.createDocumentFragment();
    if (flagsDrawn && n.flag) { const flag = element('span', 'flag', n.flag); flag.setAttribute('aria-hidden', 'true'); part.append(flag); }
    part.append(element('span', 'code', n.code)); return part;
  };
  const who = (c, bib) => { const part = document.createDocumentFragment(); part.append(`${bib} ${c.lastName} ${c.firstName}`); if (c.nation) part.append(' ', nation(c.nation)); return part; };
  // Phones get two-line cells (name over club, Run 1 over Run 2, total over difference) instead of a wide table.
  const narrow = matchMedia('(max-width: 640px)');
  narrow.addEventListener?.('change', () => { if (state) render(state); });
  // A cell value is text, a node, {text, className} or a running time {started}; a stacked cell has one value per line.
  const put = (parent, value) => {
    if (value instanceof Node) parent.append(value);
    else if (value && value.started) { const clock = element('span', 'clock', liveRunningTime(value.started, Date.now())); clock.dataset.started = value.started; parent.append(clock); }
    else if (value && value.text != null) parent.append(element('span', value.className, value.text));
    else parent.append(String(value));
  };
  const fill = (cell, value) => {
    if (!Array.isArray(value)) { put(cell, value); return; }
    cell.classList.add('stack'); value.forEach(line => { const span = element('span', 'line'); put(span, line); cell.append(span); });
  };
  function render(next) {
    state = next;
    get('results').replaceChildren(); get('runs').replaceChildren();
    if (!next) { liveRadio.update(id, null); get('name').textContent = 'Live timing unavailable'; get('metadata').textContent = ''; get('course').textContent = 'Session ended or awaiting publisher'; get('next').hidden = true; get('latest').hidden = true; get('forerunners').hidden = true; get('summary').hidden = true; get('updated').textContent = ''; title(); return; }
    get('name').textContent = next.competition.name; title(next.competition.name);
    get('metadata').textContent = `${next.competition.place} · ${next.competition.discipline} · ${next.competition.date}`;
    // A viewer may look at another run; when the race moves on to a new current run, every viewer follows it.
    if (next.currentRun !== followedRun || !next.runs.some(r => r.number === selectedRun)) { selectedRun = followedRun = next.currentRun; }
    next.runs.forEach(r => { const b = document.createElement('button'); b.textContent = `Run ${r.number}`; b.setAttribute('aria-pressed', r.number === selectedRun); b.onclick = () => { selectedRun = r.number; render(state); }; get('runs').append(b); });
    const run = next.runs.find(r => r.number === selectedRun);
    const competitor = bib => next.competitors.find(c => c.bib === bib);
    // Everyone on course of the current run, one per line in start order, each with a running time. Forerunners are
    // listed first with an F and never get a time.
    const current = next.runs.find(r => r.number === next.currentRun);
    const onCourse = (current.forerunners || []).filter(f => f.status === 'OnCourse').map(f => ({bib: `F ${f.letter}`, name: `${f.lastName} ${f.firstName}`, forerunner: true}))
      .concat(current.results.filter(r => r.status === 'OnCourse').sort((a, b) => (Date.parse(a.startedAt) || 0) - (Date.parse(b.startedAt) || 0))
        .map(r => { const c = competitor(r.bib); return {bib: String(r.bib), name: `${c.lastName} ${c.firstName}`, nation: c.nation, startedAt: r.startedAt}; }));
    // Once every starter of the current run is finished or classified, the banner says so instead of an empty course.
    const complete = current.startOrder.length > 0 && current.startOrder.every(bib => { const r = current.results.find(x => x.bib === bib); return r && !['Ready', 'OnCourse', 'Review'].includes(r.status); });
    const course = get('course');
    if (stale || next.paused || !onCourse.length) {
      course.textContent = stale ? 'Waiting for publisher · Last state retained' : next.paused ? 'Publishing stopped · Last state retained'
        : complete ? `Run ${next.currentRun} complete` : 'On course · —';
    } else {
      const label = element('span', 'label', 'On course'), list = element('ol', 'oncourse');
      onCourse.forEach(x => {
        const li = document.createElement('li'), name = element('span', 'who', x.name), clock = element('span', 'clock');
        if (x.nation) name.append(' ', nation(x.nation));
        if (x.forerunner) { clock.textContent = 'forerunner'; clock.classList.add('muted'); }
        else if (x.startedAt) { clock.dataset.started = x.startedAt; clock.textContent = liveRunningTime(x.startedAt, Date.now()); }
        else clock.textContent = '—';
        li.append(element('span', 'bib', x.bib), name, clock); list.append(li);
      });
      course.replaceChildren(label, list);
    }
    // The next two racers in the start gate of the current run, so viewers see who is coming.
    const coming = stale || next.paused || complete ? [] : liveNextStarters(current, 2);
    get('next').hidden = coming.length === 0;
    get('next').replaceChildren(...(coming.length === 0 ? [] : [element('span', 'label', 'Next'),
      ...coming.map(bib => { const item = element('span', 'item'); item.append(who(competitor(bib), bib)); return item; })]));
    // The latest finisher of the current run (or the latest DNF/DSQ) stays visible even when the time ranks low.
    const last = liveLatestFinish(current.results, ['Finished', 'DNF', 'DSQ']), totalRanked = current.number > 1 && last?.totalHundredths != null;
    const latest = get('latest');
    latest.hidden = !last;
    if (last) {
      const c = competitor(last.bib), place = totalRanked ? last.totalRank : last.rank, finished = last.status === 'Finished';
      latest.textContent = finished ? `Last finish · ${last.bib} ${c.lastName} ${c.firstName} · ${time(last.hundredths)}`
        + (totalRanked ? ` · Total ${time(last.totalHundredths)}` : '') + (place ? ` · Rank ${place}` : '')
        : `Last · ${last.bib} ${c.lastName} ${c.firstName} · ${last.status}`;
      latest.classList.toggle('out', !finished);
    }
    const key = last ? `${current.number}:${last.bib}:${last.status}:${last.hundredths}` : '';
    if (latestKey !== null && key && key !== latestKey) {
      flashUntil = Date.now() + flashMs; clearTimeout(flashTimer);
      // Redraw when the highlight ends; the redraw decides from the time, so a later update cannot keep it on.
      flashTimer = setTimeout(() => { if (state) render(state); }, flashMs + 50);
    }
    latestKey = key;
    const flashing = !!last && Date.now() < flashUntil;
    latest.classList.toggle('flash', flashing);
    // Forerunners of the shown run: where each one is (at start, on course, finished), never a time.
    const forerunnerState = s => ({Ready:'at start', OnCourse:'on course', Finished:'finished', DNS:'DNS', DNF:'DNF', Review:'checking'})[s] || s;
    const shownRunners = run.forerunners || [];
    get('forerunners').hidden = shownRunners.length === 0;
    get('forerunners').textContent = shownRunners.length === 0 ? '' : 'Forerunners · ' + shownRunners.map(f => `${f.letter} ${f.lastName} ${f.firstName}: ${forerunnerState(f.status)}`).join(' · ');
    get('updated').textContent = `Run ${selectedRun} · ${new Date(next.updatedAt).toLocaleTimeString()}`;
    // Split columns appear only when this run has at least one intermediate time; the viewer picks intermediate or sector times.
    const splits = liveSplitCount(run.results);
    get('splitMode').hidden = splits === 0;
    get('splitMode').querySelectorAll('button').forEach(b => b.setAttribute('aria-pressed', b.dataset.mode === splitMode));
    // From Run 2 on, each earlier run's time and the combined time have their own columns, and standings use the combined
    // time. A publisher without totals keeps the run standings.
    const earlier = next.runs.filter(r => r.number < run.number).map(r => r.number).sort((a, b) => a - b);
    const later = run.number > 1, totals = later && run.results.some(r => r.totalHundredths != null);
    const rank = r => totals ? r.totalRank : r.rank, gap = r => totals ? r.totalDifference : r.difference;
    // No status column: the run's time column says DNS, DNF, DSQ or NPS, and a racer on course has the running time.
    const runTime = r => r.status === 'Finished' ? time(r.hundredths) : r.status === 'OnCourse' ? (r.startedAt ? {started: r.startedAt} : '—')
      : r.status === 'Ready' ? '—' : r.status;
    // An earlier run shows its time, or its status when the racer did not finish it.
    const earlierRun = x => !x ? '—' : x.status === 'Finished' ? time(x.hundredths) : ['Ready', 'OnCourse'].includes(x.status) ? '—' : x.status;
    const difference = r => gap(r) == null ? '—' : '+' + time(gap(r));
    const nations = next.competitors.some(c => c.nation), clubs = next.competitors.some(c => c.club);
    const splitHeads = liveSplitHeaders(splits, splitMode), compact = narrow.matches;
    const splitValues = x => liveSplitValues(x.r, splits, splitMode).map(time);
    // Each column: its heading (one entry per line of a stacked cell) and the value of a row {r: result, c: competitor,
    // before: earlier runs}. The last column ("result") leads with the time that decides the standings.
    const columns = compact ? [
      {head: ['Rank', 'Bib'], value: x => [rank(x.r) || '—', {text: String(x.r.bib), className: 'muted'}]},
      {head: ['Name', 'Club'], className: 'name', value: x => [{text: x.c.lastName, className: 'last'}, x.c.firstName, ...(x.c.club ? [{text: x.c.club, className: 'muted'}] : [])]},
      ...(nations ? [{head: 'Nation', className: 'nation', value: x => [...nation(x.c.nation).childNodes], title: x => liveNation(x.c.nation).name}] : []),
      ...(splits ? [{head: splitHeads.map(h => h.replace('Intermediate', 'Int.').replace('Sector', 'Sec.')), num: true, className: 'splits', value: splitValues}] : []),
      ...(later ? [{head: [...earlier.map(n => `Run ${n}`), `Run ${run.number}`], num: true, className: 'runs', value: x => [...x.before.map(b => earlierRun(b.result)), runTime(x.r)]},
        {head: ['Total', 'Difference'], num: true, className: 'result', value: x => [time(x.r.totalHundredths), difference(x.r)]}]
        : [{head: ['Time', 'Difference'], num: true, className: 'result', value: x => [runTime(x.r), difference(x.r)]}])
    ] : [
      {head: 'Rank', value: x => rank(x.r) || '—'}, {head: 'Bib', value: x => x.r.bib},
      {head: 'Competitor', className: 'name', value: x => `${x.c.lastName} ${x.c.firstName}`},
      ...(nations ? [{head: 'Nation', className: 'nation', value: x => nation(x.c.nation), title: x => liveNation(x.c.nation).name}] : []),
      ...(clubs ? [{head: 'Club', value: x => x.c.club || ''}] : []),
      ...earlier.map((n, i) => ({head: `Run ${n}`, num: true, value: x => earlierRun(x.before[i].result)})),
      ...splitHeads.map((head, i) => ({head, num: true, value: x => splitValues(x)[i]})),
      {head: later ? `Run ${run.number}` : 'Finish', num: true, value: x => runTime(x.r)},
      ...(later ? [{head: 'Total', num: true, value: x => time(x.r.totalHundredths)}] : []),
      {head: 'Difference', num: true, value: x => difference(x.r)}
    ];
    get('head').replaceChildren(...columns.map(column => { const th = document.createElement('th'); if (column.num) th.className = 'num'; fill(th, column.head); return th; }));
    document.querySelector('.table').classList.toggle('compact', compact);
    const rows = liveResultOrder(run.startOrder.map(bib => run.results.find(r => r.bib === bib) || {bib, status:'Ready'}), run.startOrder, rank);
    rows.forEach(r => {
      const x = {r, c: competitor(r.bib), before: liveEarlierRuns(next, run.number, r.bib)}, tr = document.createElement('tr');
      tr.dataset.bib = r.bib; tr.className = r.status.toLowerCase();
      if (flashing && run.number === current.number && r.bib === last.bib) tr.classList.add('flash');
      columns.forEach(column => {
        const td = document.createElement('td'); if (column.num) td.classList.add('num'); if (column.className) td.classList.add(column.className);
        if (column.title) td.title = column.title(x);
        fill(td, column.value(x)); tr.append(td);
      });
      get('results').append(tr);
    });
    summary(next);
  }
  // Race statistics under the results: racers, each run's outcome, and racers per nation and club, opened on demand. The
  // details elements stay in the page, so a list a viewer opened stays open across updates.
  function summary(next) {
    const count = (value, one, many) => `${value} ${value === 1 ? one : many}`;
    get('summary').hidden = next.competitors.length === 0;
    const nations = liveGroupCounts(next.competitors, 'nation').filter(g => g.key), clubs = liveGroupCounts(next.competitors, 'club').filter(g => g.key);
    get('totals').textContent = [count(next.competitors.length, 'racer', 'racers'), nations.length ? count(nations.length, 'nation', 'nations') : '',
      clubs.length ? count(clubs.length, 'club', 'clubs') : ''].filter(Boolean).join(' · ');
    get('runCounts').replaceChildren(...next.runs.slice().sort((a, b) => a.number - b.number).map(r => {
      const c = liveRunCounts(r), li = document.createElement('li');
      li.append(element('strong', '', `Run ${r.number}`), ' ' + [count(c.racers, 'starter', 'starters'), `${c.finished} finished`,
        `${c.DNS} DNS`, `${c.DNF} DNF`, `${c.DSQ} DSQ`, ...(c.NPS ? [`${c.NPS} NPS`] : []), ...(c.onCourse ? [`${c.onCourse} on course`] : []),
        ...(c.review ? [`${c.review} checking`] : []), ...(c.waiting ? [`${c.waiting} to start`] : [])].join(' · '));
      return li;
    }));
    get('byNation').hidden = nations.length === 0; get('byClub').hidden = clubs.length === 0;
    get('nations').replaceChildren(...nations.map(g => {
      const li = document.createElement('li'), name = element('span', 'group'); name.append(nation(g.key), ' ' + liveNation(g.key).name);
      li.append(name, element('span', 'count', String(g.count))); return li;
    }));
    get('clubs').replaceChildren(...clubs.map(g => { const li = document.createElement('li'); li.append(element('span', 'group', g.key), element('span', 'count', String(g.count))); return li; }));
  }
  function schedule() {
    connection('Reconnecting'); clearTimeout(retry); waiting = true;
    // Exponential backoff with jitter spreads every viewer's reconnect after a service restart.
    retry = setTimeout(connect, Math.min(15000, 1000 * 2 ** attempt++) * (0.5 + Math.random() / 2));
  }
  function drop(closing) {
    if (socket !== closing) return; // Late events from an abandoned socket never start a second connection.
    socket = null; clearInterval(closing.keepAlive);
    // Without a connection there are no corrections or deletions: the radio stops until a fresh announcement arrives.
    liveRadio.update(id, null);
    closing.onopen = closing.onmessage = closing.onclose = closing.onerror = null;
    try { closing.close(); } catch { /* Already closed. */ }
    schedule();
  }
  async function connect() {
    clearTimeout(retry); waiting = false;
    try {
      connection('Connecting');
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
          if (!handshaken) { if (message.error) { drop(current); return; } handshaken = true; attempt = 0; watchPending = true; connection('Live'); current.send(JSON.stringify({type:1,target:'Watch',arguments:[id],invocationId:'1'}) + '\u001e'); }
          else if (message.type === 1 && message.target === 'Radio') liveRadio.update(id, message.arguments[0]);
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
  // Running times on course (banner and results) tick between updates; only the clocks change, nothing else is redrawn.
  setInterval(() => { const now = Date.now(); document.querySelectorAll('.clock[data-started]').forEach(e => { e.textContent = liveRunningTime(e.dataset.started, now); }); }, 100);
  if (id) { get('race').hidden = false; connect(); } else { get('list').hidden = false; list(); }
})();
