/* Unofficial live list order. Ranks come from the publisher's timing results; equal ranks are ex aequo and list the
   higher bib first, as in official results (FIS ICR 617.3.3). Rows without a rank keep the run's start order.
   rankOf selects the rank that orders the list: the run rank, or from Run 2 on the rank of the combined time. */
globalThis.liveResultOrder = (rows, startOrder, rankOf = row => row.rank) => rows.slice().sort((a, b) =>
  (rankOf(a) || 99999) - (rankOf(b) || 99999) || (rankOf(a) ? b.bib - a.bib : startOrder.indexOf(a.bib) - startOrder.indexOf(b.bib)));
/* The racer who left the course most recently in a run: finished, or with the given statuses also DNF/DSQ. Device
   finish timestamps are 100 ns ticks beyond the safe integer range, so they are compared as BigInt. One key orders every
   candidate: device timestamps when all have one, otherwise an instant per candidate as below (mixing the two keys
   would make the choice depend on order). */
globalThis.liveLatestFinish = (results, statuses = ['Finished']) => {
  const finished = results.filter(r => statuses.includes(r.status));
  if (finished.length === 0) return null;
  const byTicks = finished.every(r => r.finishSourceTicks != null);
  // Without device timestamps for all: a finisher's finish instant, from the start instant plus the device start-to-finish
  // difference, which no correction moves (the run time only when the impulses are not published); a DNF/DSQ, or a
  // finisher without a start instant, uses its publish time.
  const instant = r => {
    if (r.status !== 'Finished' || !r.startedAt) return Date.parse(r.at);
    if (r.startSourceTicks != null && r.finishSourceTicks != null)
      return Date.parse(r.startedAt) + Number((BigInt(r.finishSourceTicks) - BigInt(r.startSourceTicks)) / 10000n);
    return r.hundredths != null ? Date.parse(r.startedAt) + r.hundredths * 10 : Date.parse(r.at);
  };
  return finished.reduce((latest, r) => (byTicks ? BigInt(r.finishSourceTicks) > BigInt(latest.finishSourceTicks)
    : instant(r) > instant(latest)) ? r : latest);
};
/* Running time on course, from the published start instant to now, truncated to tenths (m:ss.t). The viewer's clock
   decides "now", so it is a display estimate; official times come only from the timing results. */
globalThis.liveRunningTime = (startedAt, now) => {
  // A start well in the future means the viewer's clock is wrong: show no time rather than a misleading one.
  const elapsed = now - Date.parse(startedAt);
  if (!(elapsed > -5000)) return '—';
  const ms = Math.max(0, elapsed), tenths = Math.floor(ms / 100);
  return `${Math.floor(tenths / 600)}:${String(Math.floor(tenths / 10) % 60).padStart(2, '0')}.${tenths % 10}`;
};
