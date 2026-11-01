// Dynamic Notch Media — runs in every page and frame. It tells the notch what this page plays (a video, a song, a
// stream), and acts on the notch's buttons: play / pause, next, previous, seek. Nothing leaves this PC: the notch
// listens on 127.0.0.1 only.
(() => {
  if (window.__dynamicNotchMedia) return;
  window.__dynamicNotchMedia = true;
  const api = globalThis.browser ?? globalThis.chrome;

  let current = null;     // the media element this frame reports
  let lastSent = '';
  let sendTimer = 0;
  let tick = 0;

  // something you'd call "playing media": not muted, long enough, not a hover preview or a silent background loop
  const counts = m => m.isConnected && !m.muted && m.volume > 0 && m.readyState > 0 &&
    (m.duration === Infinity || m.duration > 3) && (m.tagName === 'AUDIO' || m.offsetWidth * m.offsetHeight > 120 * 68 || !m.paused);

  function pick() {
    const all = Array.from(document.querySelectorAll('video, audio')).filter(counts);
    if (current && current.isConnected && counts(current) && !all.includes(current)) all.push(current); // seen through an event
    return all.find(m => !m.paused && !m.ended)
      || (current && all.includes(current) && current.currentTime > 0 ? current : null)
      || all.find(m => m.currentTime > 0)
      || null;
  }

  const absolute = u => { try { return u ? new URL(u, location.href).href : ''; } catch { return ''; } };
  const meta = name => document.querySelector(`meta[property="${name}"], meta[name="${name}"]`)?.content || '';
  const area = a => { const s = String(a.sizes || '').split(/[x ]/i).map(Number); return (s[0] || 0) * (s[1] || 0); };
  const visible = el => !!el && el.offsetParent !== null && !el.disabled;

  // the sites' own next / previous buttons (the page's media keys aren't reachable from here)
  const NEXT = ['.ytp-next-button', 'ytmusic-player-bar .next-button', '[data-testid="control-button-skip-forward"]',
    '.skipControl__next', '[aria-label="Next track"]', '[aria-label="Next"]', 'button[title="Next"]'];
  const PREV = ['.ytp-prev-button', 'ytmusic-player-bar .previous-button', '[data-testid="control-button-skip-back"]',
    '.skipControl__previous', '[aria-label="Previous track"]', '[aria-label="Previous"]', 'button[title="Previous"]'];
  const find = list => { for (const s of list) { const el = document.querySelector(s); if (visible(el)) return el; } return null; };

  function state() {
    const m = pick();
    if (!m) return null;
    current = m;
    let md = null;
    try { md = navigator.mediaSession && navigator.mediaSession.metadata; } catch { }
    const artwork = md && md.artwork && md.artwork.length ? Array.from(md.artwork).sort((a, b) => area(b) - area(a))[0].src : '';
    const icon = document.querySelector('link[rel~="icon"][sizes="192x192"], link[rel="apple-touch-icon"], link[rel~="icon"]')?.href
      || location.origin + '/favicon.ico';
    return {
      playing: !m.paused && !m.ended,
      title: ((md && md.title) || document.title || location.hostname).trim().slice(0, 300),
      artist: ((md && md.artist) || '').trim().slice(0, 200),
      art: absolute(artwork || (m.tagName === 'VIDEO' ? m.poster : '') || meta('og:image')),
      icon: absolute(icon),
      site: location.hostname.replace(/^www\./, ''),
      pos: isFinite(m.currentTime) ? m.currentTime : 0,
      dur: isFinite(m.duration) ? m.duration : 0,
      next: !!find(NEXT),
      prev: !!find(PREV),
    };
  }

  function send(force) {
    clearTimeout(sendTimer);
    sendTimer = setTimeout(() => {
      const s = state();
      // the position only matters when it jumps; the notch counts the seconds itself
      const key = JSON.stringify(s && { ...s, pos: Math.round(s.pos / 15) });
      if (!force && key === lastSent) return;
      lastSent = key;
      try { api.runtime.sendMessage({ t: 'state', s }).catch?.(() => { }); } catch { /* the extension was reloaded */ }
    }, 250);
  }

  for (const ev of ['play', 'playing', 'pause', 'ended', 'emptied', 'loadedmetadata', 'durationchange', 'volumechange'])
    document.addEventListener(ev, e => { if (e.target instanceof HTMLMediaElement) { current = e.target; send(false); } }, true);
  document.addEventListener('seeked', e => { if (e.target instanceof HTMLMediaElement) send(true); }, true);
  // the page's title is watched only once it has played something (pages that never play cost nothing)
  let titleWatch = null;
  function watchTitle() {
    const t = document.querySelector('head > title');
    if (titleWatch || !t) return;
    titleWatch = new MutationObserver(() => send(false));
    titleWatch.observe(t, { childList: true, characterData: true, subtree: true });
  }
  document.addEventListener('play', () => watchTitle(), true);
  window.addEventListener('pagehide', () => { try { api.runtime.sendMessage({ t: 'state', s: null })?.catch?.(() => { }); } catch { } });

  // while something plays, a light check every 5 s: a new song on the same element, a site that changes title late
  setInterval(() => { if (current && !current.paused) { if (++tick % 6 === 0) send(true); else send(false); } }, 5000);

  api.runtime.onMessage.addListener(msg => {
    const m = pick() || current;
    if (!msg || !m) return;
    switch (msg.cmd) {
      case 'toggle': m.paused ? m.play().catch(() => { }) : m.pause(); break;
      case 'play': m.play().catch(() => { }); break;
      case 'pause': m.pause(); break;
      case 'seek': if (isFinite(msg.pos)) m.currentTime = Math.max(0, msg.pos); break;
      case 'next': { const b = find(NEXT); if (b) b.click(); break; }
      case 'prev': { const b = find(PREV); if (b) b.click(); else m.currentTime = 0; break; }
    }
    send(true);
  });

  if (document.querySelector('video, audio')) send(true);
})();
