/* Optional race radio. The control appears only while the server announces a healthy radio, starts paused and never
   autoplays. Clips play one at a time with short pauses; starting or resuming joins at the live edge. */
const liveRadio = (() => {
  const button = document.getElementById('radio'), audio = new Audio();
  const lineGap = 350, batchGap = 1200, liveEdge = 15000, lineWait = 8000, wait = {};
  // offset maps server times to this browser's clock (browser minus server).
  let session = null, clips = [], playing = false, current = null, timer = null, offset = 0;
  let lastBatch = null, lastInterruptible = true, lastEnded = 0;
  const done = new Set(), skippedBatches = new Set(), seenAt = new Map();
  function label() {
    button.textContent = playing ? '❚❚ Radio' : '▶ Radio';
    button.setAttribute('aria-pressed', playing);
    button.title = playing ? 'Pause the race radio' : 'Listen to the race radio: unofficial, AI-generated commentary';
  }
  // Clips the server still lists are valid for the latest results; a batch interrupted by urgent news is not resumed.
  function pick() {
    const now = Date.now();
    const open = clips.filter(c => !done.has(c.clipId) && !skippedBatches.has(c.batchId) && Date.parse(c.expiresAt) + offset > now);
    const sameBatch = open.filter(c => c.batchId === lastBatch).sort((a, b) => a.line - b.line);
    if (!lastInterruptible) {
      if (sameBatch.length) return sameBatch[0];
      // The next line of an unfinished sentence pair may still be on its way; a batch dropped as stale never finishes.
      if (now - lastEnded < lineWait) return wait;
      skippedBatches.add(lastBatch);
    }
    if (!open.length) return null;
    const next = open.sort((a, b) => a.priority - b.priority || a.sequence - b.sequence)[0];
    if (lastBatch && next.batchId !== lastBatch && sameBatch.length) skippedBatches.add(lastBatch);
    return next;
  }
  // A line follows the previous line of its conversation after a short pause; a new conversation after a longer one.
  function next(waited = false) {
    timer = null;
    if (!playing || current) return;
    const clip = pick();
    if (clip === wait) { timer = setTimeout(next, 1000); return; }
    if (!clip) return;
    if (!waited && lastBatch !== null && clip.batchId !== lastBatch) { timer = setTimeout(() => next(true), batchGap - lineGap); return; }
    current = clip; done.add(clip.clipId);
    audio.src = `/api/sessions/${encodeURIComponent(session)}/radio/clips/${encodeURIComponent(clip.clipId)}`;
    audio.play().catch(finish);
  }
  function finish() {
    const ended = current; current = null;
    if (!ended) return;
    lastBatch = ended.batchId; lastInterruptible = ended.interruptible || ended.line === ended.lineCount - 1; lastEnded = Date.now();
    if (playing) timer = setTimeout(next, lineGap);
  }
  audio.addEventListener('ended', finish);
  audio.addEventListener('error', finish);
  function pause() {
    playing = false; clearTimeout(timer); timer = null; current = null;
    audio.pause(); audio.removeAttribute('src'); label();
  }
  // Joining or resuming skips clips announced more than a few seconds ago.
  function play() {
    const now = Date.now();
    clips.forEach(c => { if (now - (seenAt.get(c.clipId) || now) > liveEdge) done.add(c.clipId); });
    playing = true; lastBatch = null; lastInterruptible = true; label(); next();
  }
  button.addEventListener('click', () => (playing ? pause() : play()));
  return {
    update(id, announcement) {
      if (id !== session) { pause(); done.clear(); skippedBatches.clear(); seenAt.clear(); session = id; }
      const available = !!announcement && announcement.available;
      clips = available ? announcement.clips || [] : [];
      // Clip age on the server's clock, mapped to this browser's clock.
      const now = Date.now();
      offset = announcement && announcement.serverTime ? now - Date.parse(announcement.serverTime) : 0;
      clips.forEach(c => { if (!seenAt.has(c.clipId)) seenAt.set(c.clipId, c.receivedAt ? Math.min(now, Date.parse(c.receivedAt) + offset) : now); });
      button.hidden = !available;
      if (!available && playing) pause();
      if (playing && !current && !timer) next();
    }
  };
})();
