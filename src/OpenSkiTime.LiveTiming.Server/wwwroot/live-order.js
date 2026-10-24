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
/* Running time on course, so viewers see that the racer is on course. Its origin is chosen once, when this viewer first
   sees the racer on course (seenAt): the published start instant when it is plausible for the viewer's clock at that
   moment (at most five seconds ahead and less than ten minutes back), otherwise seenAt itself, for a missing start or a
   device clock or date that differs from real time. The choice depends only on these two values, so the clock never
   jumps during the run. The viewer's clock decides "now", so it is a display estimate; official times come only from the
   timing results. */
globalThis.liveClockOrigin = (startedAt, seenAt) => {
  const start = Date.parse(startedAt), lead = seenAt - start;
  return lead > -5000 && lead < 600000 ? start : seenAt;
};
/* Time since the origin, truncated to tenths (m:ss.t); never negative. */
globalThis.liveRunningTime = (origin, now) => {
  const tenths = Math.floor(Math.max(0, now - origin) / 100);
  return `${Math.floor(tenths / 600)}:${String(Math.floor(tenths / 10) % 60).padStart(2, '0')}.${tenths % 10}`;
};
/* The next racers to start in a run, in start order. A racer is waiting while Ready (or without a result). Waiting racers
   after the furthest racer who has started come first; one still waiting before that position (a late start) follows
   them. A DNS or NPS marked ahead of time is not a start, so it never moves the position. */
globalThis.liveNextStarters = (run, count = 2) => {
  const status = bib => (run.results.find(r => r.bib === bib) || {status: 'Ready'}).status;
  const started = run.startOrder.reduce((at, bib, i) => ['OnCourse', 'Finished', 'DNF', 'DSQ', 'Review'].includes(status(bib)) ? i : at, -1);
  const waiting = run.startOrder.map((bib, i) => ({bib, i})).filter(x => status(x.bib) === 'Ready');
  return [...waiting.filter(x => x.i > started), ...waiting.filter(x => x.i < started)].slice(0, count).map(x => x.bib);
};
