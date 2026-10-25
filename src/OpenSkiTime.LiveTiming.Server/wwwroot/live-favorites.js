/* The viewer's favorite racers. They exist only in this browser (local storage) and are never sent to the server. A
   favorite is the racer's FIS code with name, nation and club: in another race the same racer is found by FIS code when
   both have one, otherwise by last and first name (ignoring case and extra spaces). The stored list is validated on read
   and bounded, so a damaged or foreign value never breaks the page. */
globalThis.liveFavoriteLimit = 200;
(() => {
  const text = value => typeof value === 'string' ? value.trim() : '';
  const name = value => text(value).normalize('NFC').replace(/\s+/g, ' ').toUpperCase();
  /** The favorite entry of a published competitor. */
  globalThis.liveFavoriteOf = c => ({code: text(c.fisCode), lastName: text(c.lastName), firstName: text(c.firstName), nation: text(c.nation), club: text(c.club)});
  /** Whether a published competitor is this favorite. */
  globalThis.liveIsFavorite = (favorite, c) => {
    const code = text(c.fisCode);
    if (favorite.code && code) return favorite.code === code;
    return name(favorite.lastName) === name(c.lastName) && name(favorite.firstName) === name(c.firstName);
  };
  /** The stored favorites: valid entries only, without duplicates, at most liveFavoriteLimit. */
  globalThis.liveFavoritesParse = raw => {
    let value; try { value = JSON.parse(raw || '[]'); } catch { return []; }
    if (!Array.isArray(value)) return [];
    const list = [];
    for (const item of value) {
      if (!item || typeof item !== 'object') continue;
      const favorite = {code: text(item.code), lastName: text(item.lastName), firstName: text(item.firstName), nation: text(item.nation), club: text(item.club)};
      if (!favorite.lastName && !favorite.firstName) continue;
      const same = {fisCode: favorite.code, lastName: favorite.lastName, firstName: favorite.firstName};
      if (!list.some(f => liveIsFavorite(f, same)) && list.length < liveFavoriteLimit) list.push(favorite);
    }
    return list;
  };
  /** The favorites with this competitor added, or removed when already a favorite. A full list adds nothing. */
  globalThis.liveFavoritesToggle = (list, c) => list.some(f => liveIsFavorite(f, c)) ? list.filter(f => !liveIsFavorite(f, c))
    : list.length < liveFavoriteLimit ? [...list, liveFavoriteOf(c)] : list;
  /** A favorite's results in one race: each run's status, time and rank, and the latest combined time and rank. */
  globalThis.liveFavoriteResults = (snapshot, favorite) => {
    const c = (snapshot?.competitors || []).find(x => liveIsFavorite(favorite, x));
    if (!c) return null;
    const runs = (snapshot.runs || []).slice().sort((a, b) => a.number - b.number)
      .filter(r => r.startOrder.includes(c.bib) || r.results.some(x => x.bib === c.bib))
      .map(r => { const x = r.results.find(y => y.bib === c.bib) || {status: 'Ready'}; return {number: r.number, status: x.status, hundredths: x.hundredths ?? null, rank: x.rank ?? null, x}; });
    const totalOf = runs.filter(r => r.number > 1 && r.x.totalHundredths != null).pop();
    return {competitor: c, runs: runs.map(({x, ...r}) => r), total: totalOf ? {run: totalOf.number, hundredths: totalOf.x.totalHundredths, rank: totalOf.x.totalRank ?? null} : null};
  };
})();
