/* Optional race radio. The control appears only while the server announces a healthy radio, starts paused and never
   autoplays. Clips play one at a time with short pauses; starting or resuming joins at the live edge. */
const liveRadio = (() => {
  const button = document.getElementById('radio'), audio = new Audio();
  // lineWait covers the worker's slowest successful next line: speech (15 s) plus an upload to each of two servers (10 s each).
  const lineGap = 350, batchGap = 1200, liveEdge = 15000, lineWait = 40000, wait = {};
  // offset maps server times to this browser's clock (browser minus server).
  let session = null, clips = [], playing = false, current = null, timer = null, offset = 0;
  let lastBatch = null, lastClip = null, lastInterruptible = true, lastEnded = 0, nextLine = 0;
  const done = new Set(), skippedBatches = new Set(), seenAt = new Map();
  // Background music: the operator's tracks play in order while the radio plays and dip under speech. A Web Audio gain
  // fades also where the media element volume is fixed (iOS); without Web Audio the element volume is used.
  const musicButton = document.getElementById('radio-music'), music = new Audio();
  const musicLevel = 0.35, duckLevel = 0.08;
  let tracks = [], trackIndex = -1, musicOn = musicPreference(), audioContext = null, gain = null, unduckTimer = null;
  function musicPreference() { try { return localStorage.getItem('ost-radio-music') !== 'off'; } catch { return true; } }
  function musicLabel() {
    musicButton.textContent = musicOn ? '♪ Music' : '♪ Music off';
    musicButton.setAttribute('aria-pressed', musicOn);
    musicButton.title = musicOn ? 'Turn the background music off' : 'Turn the background music on';
  }
  function level(target, seconds) {
    if (!gain) { music.volume = target; return; }
    const t = audioContext.currentTime;
    gain.gain.cancelScheduledValues(t); gain.gain.setValueAtTime(gain.gain.value, t); gain.gain.linearRampToValueAtTime(target, t + seconds);
  }
  // Created inside the click that starts the radio, which browsers require before audio may play.
  function audioGraph() {
    const Context = globalThis.AudioContext || globalThis.webkitAudioContext;
    if (gain || !Context) return;
    try {
      audioContext = new Context(); gain = audioContext.createGain(); gain.gain.value = 0;
      audioContext.createMediaElementSource(music).connect(gain).connect(audioContext.destination);
    } catch { audioContext = null; gain = null; }
  }
  function playMusic() {
    if (!playing || !musicOn || !tracks.length) { stopMusic(); return; }
    if (audioContext && audioContext.state === 'suspended') audioContext.resume().catch(() => {});
    if (music.src && !music.paused) return;
    trackIndex = (trackIndex + 1) % tracks.length;
    music.src = `/api/sessions/${encodeURIComponent(session)}/radio/music/${encodeURIComponent(tracks[trackIndex])}`;
    level(current ? duckLevel : musicLevel, 1.5);
    music.play().catch(() => {});
  }
  function stopMusic() { clearTimeout(unduckTimer); unduckTimer = null; music.pause(); music.removeAttribute('src'); }
  // Speech starts: the music dips at once. Speech ends: it returns after the pause in which another line could follow.
  function duck() { clearTimeout(unduckTimer); unduckTimer = null; if (music.src) level(duckLevel, 0.4); }
  function unduck() { clearTimeout(unduckTimer); unduckTimer = setTimeout(() => { unduckTimer = null; if (!current && music.src) level(musicLevel, 2); }, batchGap + 300); }
  music.addEventListener('ended', () => { music.removeAttribute('src'); playMusic(); });
  // A track that cannot be played is skipped after a pause, so a broken file never loops tightly.
  music.addEventListener('error', () => { music.removeAttribute('src'); setTimeout(playMusic, 5000); });
  musicButton.addEventListener('click', () => {
    musicOn = !musicOn; musicLabel();
    try { localStorage.setItem('ost-radio-music', musicOn ? 'on' : 'off'); } catch { /* Remembered for this page only. */ }
    if (musicOn) { audioGraph(); playMusic(); } else stopMusic();
  });
  musicLabel();
  function label() {
    button.textContent = playing ? '❚❚ Radio' : '▶ Radio';
    button.setAttribute('aria-pressed', playing);
    button.title = playing ? 'Pause the race radio' : 'Listen to the race radio: unofficial, AI-generated commentary';
  }
  // Clips the server still lists are valid for the latest results; a batch interrupted by urgent news is not resumed.
  function pick() {
    const now = Date.now();
    const open = clips.filter(c => !done.has(c.clipId) && !skippedBatches.has(c.batchId) && Date.parse(c.expiresAt) + offset > now);
    const sameBatch = open.filter(c => c.batchId === lastBatch);
    // Lines of a conversation play strictly in order; a missing line is never skipped over.
    const expected = sameBatch.find(c => c.line === nextLine);
    if (!lastInterruptible) {
      if (expected) return expected;
      // The next line of an unfinished sentence pair may still be on its way. A batch whose played line the server no
      // longer lists went stale (the worker drops it by the same rules) and never finishes.
      const alive = clips.some(c => c.clipId === lastClip && Date.parse(c.expiresAt) + offset > now);
      if (alive && now - lastEnded < lineWait) return wait;
      skippedBatches.add(lastBatch);
    } else if (sameBatch.length && !expected) { skippedBatches.add(lastBatch); }
    // A new conversation starts only at its opening line: a reply whose opening never arrived is not played alone.
    const startable = open.filter(c => !skippedBatches.has(c.batchId) && (c === expected || (c.line === 0 && c.batchId !== lastBatch)));
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
    current = clip; done.add(clip.clipId); duck();
    audio.src = `/api/sessions/${encodeURIComponent(session)}/radio/clips/${encodeURIComponent(clip.clipId)}`;
    // A browser that refuses playback outside a click shows the control paused again instead of silently dropping clips.
    audio.play().catch(e => { if (e && e.name === 'NotAllowedError') { current = null; pause(); } else fail(); });
  }
  // A clip that could not be loaded or played was not heard: the rest of its conversation is dropped with it.
  function fail() {
    const failed = current; current = null;
    if (!failed) return;
    unduck();
    skippedBatches.add(failed.batchId);
    if (playing) timer = setTimeout(next, lineGap);
  }
  function finish() {
    const ended = current; current = null;
    if (!ended) return;
    unduck();
    lastBatch = ended.batchId; lastClip = ended.clipId; lastInterruptible = ended.interruptible || ended.line === ended.lineCount - 1; lastEnded = Date.now(); nextLine = ended.line + 1;
    if (playing) timer = setTimeout(next, lineGap);
  }
  audio.addEventListener('ended', finish);
  audio.addEventListener('error', fail);
  function pause() {
    playing = false; clearTimeout(timer); timer = null; current = null;
    audio.pause(); audio.removeAttribute('src'); stopMusic(); label();
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
    audioGraph(); playMusic();
    next();
  }
  button.addEventListener('click', () => (playing ? pause() : play()));
  return {
    update(id, announcement) {
      if (id !== session) { pause(); done.clear(); skippedBatches.clear(); seenAt.clear(); session = id; trackIndex = -1; }
      const available = !!announcement && announcement.available;
      clips = available ? announcement.clips || [] : [];
      const previous = tracks;
      tracks = available ? announcement.music || [] : [];
      musicButton.hidden = !tracks.length;
      // A track the operator removed stops; the next one in the new playlist follows.
      if (music.src && !tracks.some(t => music.src.endsWith('/' + t))) { stopMusic(); trackIndex = -1; }
      if (playing && tracks.length && tracks.join() !== previous.join()) playMusic();
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
      // The clip being played was withdrawn because the results moved on (a finish changes places and counts): the
      // sentence already playing ends normally, but the rest of its conversation is dropped. Cutting speech mid-word
      // would sound like a fault.
      if (current && !clips.some(c => c.clipId === current.clipId)) skippedBatches.add(current.batchId);
      if (playing && !current && !timer) next();
    }
  };
})();
