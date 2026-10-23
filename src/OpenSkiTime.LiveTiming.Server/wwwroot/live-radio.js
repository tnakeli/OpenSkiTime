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
    // A new conversation starts only at its opening line: a reply whose opening never arrived is not played alone.
    const startable = open.filter(c => c.batchId === lastBatch || open.some(o => o.batchId === c.batchId && o.line === 0));
    if (!startable.length) return null;
    const next = startable.sort((a, b) => a.priority - b.priority || a.sequence - b.sequence)[0];
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
    // A browser that refuses playback outside a click shows the control paused again instead of silently dropping clips.
    audio.play().catch(e => { if (e && e.name === 'NotAllowedError') { current = null; pause(); } else finish(); });
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
  // A short silent sound played inside the click unlocks audio for later clips on browsers that require a user
  // gesture for every media element (iOS Safari), also when nothing is queued yet.
  function unlock() {
    try {
      const n = 80, bytes = new Uint8Array(44 + n), view = new DataView(bytes.buffer), text = (o, s) => [...s].forEach((c, i) => { bytes[o + i] = c.charCodeAt(0); });
      text(0, 'RIFF'); view.setUint32(4, 36 + n, true); text(8, 'WAVEfmt '); view.setUint32(16, 16, true); view.setUint16(20, 1, true); view.setUint16(22, 1, true);
      view.setUint32(24, 8000, true); view.setUint32(28, 8000, true); view.setUint16(32, 1, true); view.setUint16(34, 8, true); text(36, 'data'); view.setUint32(40, n, true);
      bytes.fill(128, 44);
      audio.src = URL.createObjectURL(new Blob([bytes], { type: 'audio/wav' }));
      audio.play().catch(() => {});
    } catch { /* Playback is unlocked by the first clip instead. */ }
  }
  function play() {
    const now = Date.now();
    clips.forEach(c => { if (now - (seenAt.get(c.clipId) || now) > liveEdge) { done.add(c.clipId); skippedBatches.add(c.batchId); } });
    playing = true; lastBatch = null; lastInterruptible = true; label();
    if (!pick()) unlock();
    next();
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
      clips.forEach(c => {
        if (seenAt.has(c.clipId)) return;
        const seen = c.receivedAt ? Math.min(now, Date.parse(c.receivedAt) + offset) : now;
        seenAt.set(c.clipId, seen);
        // Also after a reconnect: a clip that was already old when it reached this page is not news. Its whole
        // conversation goes with it, so a reply ("Hän on nyt toisena") never plays without its opening line.
        if (now - seen > liveEdge) { done.add(c.clipId); skippedBatches.add(c.batchId); }
      });
      button.hidden = !available;
      if (!available && playing) pause();
      // The clip being played was withdrawn (a correction made it false): stop it at once and drop its conversation.
      if (current && !clips.some(c => c.clipId === current.clipId)) {
        skippedBatches.add(current.batchId); current = null;
        audio.pause(); audio.removeAttribute('src');
        if (playing && !timer) timer = setTimeout(next, lineGap);
      }
      if (playing && !current && !timer) next();
    }
  };
})();
