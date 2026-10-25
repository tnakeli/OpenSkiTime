/* Filters of the unofficial live results. A filter is the text being typed in the search plus any number of filter chips:
   searches the viewer added, clubs and nations, and the viewer's favorites. Chips of one kind are alternatives (two
   nations show the racers of either); the typed text, every kind of chip and favorites must all match. A search ignores
   case, accents and extra spaces, and every word must occur in the racer's name, club, nation code or nation name
   ("mäki" and "Maki" both find MÄKI, "swe" and "sweden" find a Swedish racer); digits alone find that bib. Filters only
   hide rows, never change ranks. */
globalThis.liveSearchText = value => String(value || '').normalize('NFD').replace(/\p{M}/gu, '').toLowerCase().replace(/\s+/g, ' ').trim();
globalThis.liveFilterKinds = ['text', 'club', 'nation'];
/** The chip value of a kind: a normalized search, a trimmed club or an upper-case nation code; '' adds nothing. */
globalThis.liveFilterValue = (kind, value) => kind === 'text' ? liveSearchText(value) : kind === 'nation' ? String(value || '').trim().toUpperCase() : String(value || '').trim();
globalThis.liveFilterActive = filter => !!(liveSearchText(filter.text) || filter.chips.length || filter.favorites);
/** The chips with this one added, or removed when it is already there. */
globalThis.liveFilterToggle = (chips, kind, value) => {
  const v = liveFilterValue(kind, value);
  if (!v) return chips;
  return chips.some(c => c.kind === kind && c.value === v) ? chips.filter(c => !(c.kind === kind && c.value === v)) : [...chips, {kind, value: v}];
};
(() => {
  const searchMatch = (competitor, text) => {
    if (/^\d+$/.test(text)) return Number(text) === competitor.bib;
    const nation = String(competitor.nation || '').trim();
    const fields = liveSearchText([competitor.lastName, competitor.firstName, competitor.club, nation,
      typeof liveNation === 'function' ? liveNation(nation).name : ''].join(' '));
    return text.split(' ').every(word => fields.includes(word));
  };
  const chipMatch = (competitor, chip) => chip.kind === 'text' ? searchMatch(competitor, chip.value)
    : chip.kind === 'club' ? String(competitor.club || '').trim() === chip.value
    : String(competitor.nation || '').trim().toUpperCase() === chip.value;
  globalThis.liveFilterMatch = (competitor, filter, favorite = false) => {
    const text = liveSearchText(filter.text);
    if (text && !searchMatch(competitor, text)) return false;
    for (const kind of liveFilterKinds) {
      const chips = filter.chips.filter(c => c.kind === kind);
      if (chips.length && !chips.some(c => chipMatch(competitor, c))) return false;
    }
    return !filter.favorites || favorite;
  };
})();
