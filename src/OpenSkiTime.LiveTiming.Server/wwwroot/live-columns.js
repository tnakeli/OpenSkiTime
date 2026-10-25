/* Split and earlier-run columns of the unofficial live view. Published splits are cumulative hundredths from the start
   (intermediate times). A sector is the time between consecutive timing points: start to Intermediate 1, each
   intermediate to the next, and the last intermediate to the finish, so there is one sector more than intermediates.
   A sector is shown only when both of its timing points have a time. Earlier runs are listed from Run 2 on. */
globalThis.liveSplitCount = results => Math.max(0, ...results.flatMap(r => (r.intermediates || []).map(i => i.number)));
globalThis.liveSplitHeaders = (count, mode) => count === 0 ? [] : mode === 'sector'
  ? Array.from({length: count + 1}, (_, i) => `Sector ${i + 1}`)
  : Array.from({length: count}, (_, i) => `Intermediate ${i + 1}`);
globalThis.liveSplitValues = (result, count, mode) => {
  if (count === 0) return [];
  const at = n => n === 0 ? 0 : ((result.intermediates || []).find(i => i.number === n) || {}).hundredths ?? null;
  const intermediates = Array.from({length: count}, (_, i) => at(i + 1));
  if (mode !== 'sector') return intermediates;
  const points = [0, ...intermediates, result.status === 'Finished' ? result.hundredths ?? null : null];
  return points.slice(1).map((point, i) => point == null || points[i] == null ? null : point - points[i]);
};
globalThis.liveEarlierRuns = (snapshot, runNumber, bib) => snapshot.runs.filter(r => r.number < runNumber)
  .sort((a, b) => a.number - b.number).map(r => ({number: r.number, result: r.results.find(x => x.bib === bib) || null}));
/* The rank of each time among the times at the same timing point (an intermediate or a sector), shown in brackets after
   the time. Equal times share a rank and the next rank is skipped (1, 1, 3); a missing time has none. */
globalThis.liveTimeRanks = values => {
  const sorted = values.filter(v => v != null).sort((a, b) => a - b), first = new Map();
  sorted.forEach((v, i) => { if (!first.has(v)) first.set(v, i + 1); });
  return values.map(v => v == null ? null : first.get(v));
};
/* The standing before a run: after Run 1 its run rank, after a later run the rank of the combined time. A racer without
   a finished time in the previous run has none. */
globalThis.livePreviousRank = (snapshot, runNumber, bib) => {
  const previous = snapshot.runs.find(r => r.number === runNumber - 1), result = previous?.results.find(x => x.bib === bib);
  if (!result || result.status !== 'Finished') return null;
  return (runNumber === 2 ? result.rank : result.totalRank) ?? null;
};
/* Places gained (positive) or lost (negative) in the standings since the previous run, as in the FIS "Pr" column (+/− here). */
globalThis.livePositionChange = (previous, current) => previous && current ? previous - current : null;
/* Provisional standing of each racer who has finished this run (from Run 2 on), so +/− moves with every finish while the
   run is still going: one plus the finishers with a better combined time plus the racers who were ahead after the
   previous run and have not finished this run yet (to start, on course or under review), as if those still to come stay
   ahead. A racer out of this run (DNS, DNF, DSQ, NPS) no longer counts, so finishing ahead of one is a place gained. Once
   everyone has run it equals the combined rank. Returns bib → {rank, pending, previous}: pending is the number of racers
   still counted ahead, previous the standing after the previous run. */
globalThis.liveProvisionalRanks = (snapshot, runNumber) => {
  const run = snapshot.runs.find(r => r.number === runNumber), before = snapshot.runs.find(r => r.number === runNumber - 1), ranks = new Map();
  if (!run || !before || runNumber < 2) return ranks;
  const results = new Map(run.results.map(x => [x.bib, x])), result = bib => results.get(bib) || {bib, status: 'Ready'};
  // The standing after the previous run, as livePreviousRank gives it, for every bib at once.
  const previous = new Map(before.results.filter(x => x.status === 'Finished')
    .map(x => [x.bib, (runNumber === 2 ? x.rank : x.totalRank) ?? null]).filter(([, rank]) => rank != null));
  const finished = run.results.filter(r => r.status === 'Finished' && r.totalHundredths != null);
  const totals = finished.map(r => r.totalHundredths).sort((a, b) => a - b);
  const waiting = run.startOrder.filter(bib => previous.get(bib) != null && ['Ready', 'OnCourse', 'Review'].includes(result(bib).status))
    .map(bib => previous.get(bib)).sort((a, b) => a - b);
  // How many sorted values are below a value.
  const below = (sorted, value) => { let lo = 0, hi = sorted.length; while (lo < hi) { const mid = (lo + hi) >> 1; if (sorted[mid] < value) lo = mid + 1; else hi = mid; } return lo; };
  for (const r of finished) {
    const pending = previous.get(r.bib) == null ? 0 : below(waiting, previous.get(r.bib));
    ranks.set(r.bib, {rank: 1 + below(totals, r.totalHundredths) + pending, pending, previous: previous.get(r.bib) ?? null});
  }
  return ranks;
};
