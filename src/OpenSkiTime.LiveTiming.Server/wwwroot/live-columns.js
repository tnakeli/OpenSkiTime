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
