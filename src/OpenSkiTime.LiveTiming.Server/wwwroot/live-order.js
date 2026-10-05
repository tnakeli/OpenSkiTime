/* Unofficial live list order. Ranks come from the publisher's timing results; equal ranks are ex aequo and list the
   higher bib first, as in official results (FIS ICR 617.3.3). Rows without a rank keep the run's start order.
   rankOf selects the rank that orders the list: the run rank, or from Run 2 on the rank of the combined time. */
globalThis.liveResultOrder = (rows, startOrder, rankOf = row => row.rank) => rows.slice().sort((a, b) =>
  (rankOf(a) || 99999) - (rankOf(b) || 99999) || (rankOf(a) ? b.bib - a.bib : startOrder.indexOf(a.bib) - startOrder.indexOf(b.bib)));
