/* Race statistics below the unofficial live results: how a run went (finished, DNS, DNF, DSQ, still to come) and how many
   racers each nation and club has. Counts come from the published start order and statuses only. */
globalThis.liveRunCounts = run => {
  const counts = {racers: run.startOrder.length, finished: 0, onCourse: 0, waiting: 0, DNS: 0, DNF: 0, DSQ: 0, NPS: 0, review: 0};
  for (const bib of run.startOrder) {
    const status = (run.results.find(r => r.bib === bib) || {status: 'Ready'}).status;
    if (status === 'Finished') counts.finished++; else if (status === 'OnCourse') counts.onCourse++;
    else if (status === 'Ready') counts.waiting++; else if (status === 'Review') counts.review++;
    else if (status in counts) counts[status]++;
  }
  return counts;
};
/* Racers per value of a field (nation or club), most racers first and then alphabetically; a blank value is counted as ''. */
globalThis.liveGroupCounts = (competitors, field) => {
  const groups = new Map();
  for (const c of competitors) { const key = String(c[field] || '').trim(); groups.set(key, (groups.get(key) || 0) + 1); }
  return [...groups].map(([key, count]) => ({key, count})).sort((a, b) => b.count - a.count || (a.key === '') - (b.key === '') || a.key.localeCompare(b.key));
};
