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
