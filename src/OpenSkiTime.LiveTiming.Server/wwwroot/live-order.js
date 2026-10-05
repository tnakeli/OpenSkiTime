/* Unofficial live list order. Ranks come from the publisher's timing results; equal ranks are ex aequo and list the
   higher bib first, as in official results (FIS ICR 617.3.3). Rows without a rank keep the run's start order. */
globalThis.liveResultOrder = (rows, startOrder) => rows.slice().sort((a, b) =>
  (a.rank || 99999) - (b.rank || 99999) || (a.rank ? b.bib - a.bib : startOrder.indexOf(a.bib) - startOrder.indexOf(b.bib)));
