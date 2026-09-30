// Utylix video download panel: hover a video and a "Download this video" button appears with the
// video files this page loaded (like IDM's video panel). Picking one sends it to the Utylix app.
// Only plain video/audio files can be listed; streaming players (HLS/DASH/blob:) are not downloadable.
(() => {
  'use strict';
  if (window.__idmCloneVideoPanel) return;
  window.__idmCloneVideoPanel = true;

  const MIN_W = 220, MIN_H = 120;          // ignore tiny videos (ads, previews, icons)
  const CACHE_MS = 1500;
  const EXT_BY_TYPE = {
    'video/mp4': 'mp4', 'video/webm': 'webm', 'video/x-matroska': 'mkv', 'video/quicktime': 'mov', 'video/x-flv': 'flv',
    'video/x-msvideo': 'avi', 'video/ogg': 'ogv', 'audio/mpeg': 'mp3', 'audio/mp4': 'm4a', 'audio/aac': 'aac',
    'audio/ogg': 'ogg', 'audio/wav': 'wav', 'audio/x-wav': 'wav', 'audio/flac': 'flac', 'audio/webm': 'weba',
  };
  const STREAM_PIECE = /\.(m4s|ts|cmfv|cmfa)(\?|#|$)|[?&](range|bytestart|byterange|sq|rn)=|\/videoplayback|\/(seg|segment|chunk|frag|fragment)[-_]?\d+/i;

  let enabled = true;
  let host = null, root = null, wrap = null, btn = null, lbl = null, badge = null, menu = null, toast = null;
  let current = null, entries = [], visible = false, menuOpen = false;
  let mode = 'files';                      // 'files' = plain video files, 'media' = streaming site via yt-dlp, 'info' = can't download
  let lastTools = {};                      // which helpers Utylix has installed (from the app)
  let appAllowsButton = true;              // the "video button" switch in Utylix's Settings
  let lastStreams = 0;                     // how many stream playlists (.m3u8/.mpd) this tab has loaded
  let hideTimer = 0, lastMove = 0, toastTimer = 0, cache = { at: 0, video: null, entries: [] };

  try {
    chrome.storage.local.get({ videoPanel: true }).then(v => { enabled = v.videoPanel !== false; }).catch(() => {});
    chrome.storage.onChanged.addListener((changes, area) => {
      if (area === 'local' && changes.videoPanel) {
        enabled = changes.videoPanel.newValue !== false;
        if (!enabled) hide();
      }
    });
  } catch { /* extension was reloaded; this page's copy of the script is orphaned */ }

  // ---------- the floating button + menu (inside a shadow root so the page's CSS can't touch it) ----------
  const TEMPLATE = `
    <style>
      :host { all: initial; }
      .wrap { position: fixed; display: none; flex-direction: column; align-items: flex-end;
              font: 12.5px/1.4 system-ui, "Segoe UI", sans-serif; color: #fff; }
      .btn { display: flex; align-items: center; gap: 7px; padding: 6px 11px; border-radius: 8px; cursor: pointer;
             background: rgba(20,24,34,.92); border: 1px solid rgba(255,255,255,.18);
             box-shadow: 0 2px 10px rgba(0,0,0,.4); user-select: none; white-space: nowrap; }
      .btn:hover, .btn.open { background: #2563eb; border-color: #2563eb; }
      .btn.dim { background: rgba(58,63,76,.92); color: #cbd5e1; }
      .btn.dim:hover { background: rgba(80,86,102,.95); border-color: rgba(255,255,255,.3); }
      .badge { background: #2563eb; border-radius: 9px; padding: 0 6px; font-size: 11px; }
      .btn:hover .badge, .btn.open .badge { background: rgba(255,255,255,.25); }
      .menu { display: none; margin-top: 6px; min-width: 320px; max-width: min(560px, 90vw); max-height: 300px; overflow: auto;
              background: rgba(20,24,34,.97); border: 1px solid rgba(255,255,255,.18); border-radius: 10px;
              box-shadow: 0 8px 28px rgba(0,0,0,.5); padding: 4px; }
      .row { display: block; width: 100%; box-sizing: border-box; text-align: left; padding: 8px 11px; border: 0;
             border-radius: 6px; background: none; color: inherit; font: inherit; cursor: pointer;
             white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
      .row:hover { background: #2563eb; }
      .head { font-weight: 600; margin-bottom: 3px; border-bottom: 1px solid rgba(255,255,255,.12); border-radius: 6px 6px 0 0; }
      .tabs { display: flex; gap: 6px; padding: 6px 8px 8px; }
      .tab { border: 1px solid rgba(255,255,255,.2); background: none; color: inherit; font: inherit; font-size: 12px;
             padding: 4px 11px; border-radius: 14px; cursor: pointer; }
      .tab:hover { border-color: #2563eb; }
      .tab.on { background: #2563eb; border-color: #2563eb; }
      .note { padding: 8px 11px; color: #cbd5e1; max-width: 520px; }
      .note.title { font-weight: 600; color: #fff; border-bottom: 1px solid rgba(255,255,255,.12); margin-bottom: 3px; }
      .note.err { color: #fecaca; }
      .row.off { opacity: .45; cursor: default; }
      .row.off:hover { background: none; }
      .toast { display: none; margin-top: 6px; padding: 7px 11px; border-radius: 8px; background: rgba(20,24,34,.95);
               border: 1px solid rgba(255,255,255,.18); }
      .toast.err { border-color: #f87171; color: #fecaca; }
    </style>
    <div class="wrap">
      <div class="btn"><span class="ico">&#11015;</span><span class="lbl">Download this video</span><span class="badge"></span></div>
      <div class="menu"></div>
      <div class="toast"></div>
    </div>`;

  function build() {
    if (host) return;
    host = document.createElement('idm-clone-video-panel');
    host.style.cssText = 'all:initial;position:fixed;top:0;left:0;width:0;height:0;z-index:2147483647;';
    root = host.attachShadow({ mode: 'open' });
    root.innerHTML = TEMPLATE;                               // static markup only; page data is added with textContent
    wrap = root.querySelector('.wrap');
    btn = root.querySelector('.btn');
    lbl = root.querySelector('.lbl');
    badge = root.querySelector('.badge');
    menu = root.querySelector('.menu');
    toast = root.querySelector('.toast');
    wrap.addEventListener('mouseenter', () => clearTimeout(hideTimer));
    wrap.addEventListener('mouseleave', () => scheduleHide());
    btn.addEventListener('click', (e) => { if (e.isTrusted) toggleMenu(); });     // ignore clicks faked by the page
    document.documentElement.appendChild(host);
  }

  // While something is fullscreen (a video, a player) the button stays out of the way.
  const inFullscreen = () => !!(document.fullscreenElement || document.webkitFullscreenElement);
  const onFullscreenChange = () => { if (inFullscreen()) hide(); };
  document.addEventListener('fullscreenchange', onFullscreenChange);
  document.addEventListener('webkitfullscreenchange', onFullscreenChange);

  // ---------- finding videos and their files ----------
  function videoAt(x, y) {
    let hit = null;
    for (const v of document.querySelectorAll('video')) {
      const r = v.getBoundingClientRect();
      if (r.width >= MIN_W && r.height >= MIN_H && x >= r.left && x <= r.right && y >= r.top && y <= r.bottom) hit = v;
    }
    return hit;
  }

  const extOf = (url, type) => {
    const m = /\.([a-z0-9]{2,4})(?:[?#]|$)/i.exec(new URL(url).pathname + '#');
    const fromUrl = m ? m[1].toLowerCase() : '';
    return EXT_BY_TYPE[(type || '').split(';')[0].trim().toLowerCase()] || (/^(mp4|webm|mkv|mov|flv|avi|ogv|mp3|m4a|aac|ogg|wav|flac|m4v)$/.test(fromUrl) ? fromUrl : '');
  };

  function qualityOf(e, video) {
    const s = `${e.label || ''} ${e.url}`;
    let m = /(\d{3,4})\s*p\b/i.exec(s);
    if (m) return `${m[1]}p`;
    m = /(\d{3,4})\s*[x×]\s*(\d{3,4})/.exec(s);
    if (m) return `${Math.min(+m[1], +m[2])}p`;
    if (/^\d{3,4}$/.test((e.label || '').trim())) return `${e.label.trim()}p`;
    if (video && video.videoHeight && e.url === video.currentSrc) return `${video.videoHeight}p`;
    return '';
  }

  async function getEntries(video) {
    if (cache.video === video && performance.now() - cache.at < CACHE_MS) return cache.entries;
    const found = new Map();
    const add = (raw, info) => {
      let url;
      try { const u = new URL(raw, location.href); if (!/^https?:$/.test(u.protocol)) return; url = u.href.split('#')[0]; } catch { return; }
      found.set(url, { ...(found.get(url) || {}), ...info, url });
    };

    // 1. files the page itself points the <video> at
    for (const el of [video, ...video.querySelectorAll('source')]) {
      const src = el.currentSrc || el.getAttribute('src');
      if (src) add(src, {
        declared: true,
        type: el.getAttribute('type') || '',
        label: el.getAttribute('label') || el.getAttribute('res') || el.getAttribute('data-res') || el.getAttribute('size') || el.getAttribute('title') || '',
      });
    }
    // 2. media files this tab has downloaded (seen by the extension's network watcher)
    try {
      const r = await chrome.runtime.sendMessage({ type: 'get-media' });
      lastTools = (r && r.tools) || lastTools;
      lastStreams = (r && r.manifests) || 0;                      // streams (HLS/DASH) this tab has loaded
      appAllowsButton = !(r && r.videoButton === false);          // switched off in Utylix > Settings
      for (const m of (r && r.media) || []) add(m.url, { type: m.type, size: m.size });
    } catch { /* background asleep or extension reloaded */ }

    const list = [...found.values()]
      .filter(e => e.declared || !STREAM_PIECE.test(e.url))
      .map(e => ({ ...e, ext: extOf(e.url, e.type), quality: qualityOf(e, video) }))
      .sort((a, b) => (parseInt(b.quality) || 0) - (parseInt(a.quality) || 0) || (b.size || 0) - (a.size || 0));
    cache = { at: performance.now(), video, entries: list };
    return list;
  }

  // ---------- showing / hiding ----------
  function place(video) {
    const r = video.getBoundingClientRect();
    const vw = document.documentElement.clientWidth, vh = document.documentElement.clientHeight;
    const right = Math.min(r.right, vw);
    wrap.style.right = Math.max(6, vw - right + 10) + 'px';
    wrap.style.top = Math.min(Math.max(r.top, 0) + 10, vh - 44) + 'px';
  }

  async function attach(video) {
    if (inFullscreen()) return;
    current = video;
    const list = await getEntries(video);
    if (current !== video || !enabled) return;
    if (!appAllowsButton) { hide(); return; }                     // floating button is off; right-click download still works
    if (!list.length && !isStreamed(video) && !lastStreams) { hide(); return; }
    entries = list;
    build();
    // No plain file means a streamed video (blob: address, or a page that loaded an .m3u8/.mpd stream, e.g. YouTube,
    // Facebook, anime sites). If Utylix has its video support installed the button offers the qualities;
    // otherwise it explains what to install.
    mode = list.length ? 'files' : (lastTools.ytdlp ? 'media' : 'info');
    btn.classList.toggle('dim', mode === 'info');
    lbl.textContent = mode === 'info' ? "Can't download this video" : 'Download this video';
    badge.textContent = mode === 'files' ? String(list.length) : '';
    badge.style.display = mode === 'files' ? '' : 'none';
    place(video);
    wrap.style.display = 'flex';
    visible = true;
    if (menuOpen && mode === 'files') renderMenu();
  }

  function hide() {
    clearTimeout(hideTimer);
    visible = false;
    menuOpen = false;
    current = null;
    if (wrap) { wrap.style.display = 'none'; menu.style.display = 'none'; toast.style.display = 'none'; btn.classList.remove('open'); }
  }

  function scheduleHide() {
    clearTimeout(hideTimer);
    if (!menuOpen) hideTimer = setTimeout(hide, 900);
  }

  document.addEventListener('mousemove', (e) => {
    if (!enabled) return;
    if (inFullscreen()) { if (visible) hide(); return; }
    const now = performance.now();
    if (now - lastMove < 90) return;
    lastMove = now;
    if (host && e.composedPath().includes(host)) { clearTimeout(hideTimer); return; }
    const v = videoAt(e.clientX, e.clientY);
    if (v) {
      clearTimeout(hideTimer);
      if (v !== current || !visible) attach(v); else place(v);
    } else if (visible) scheduleHide();
  }, { passive: true, capture: true });

  document.addEventListener('mouseleave', () => { if (visible) scheduleHide(); });
  window.addEventListener('scroll', () => { if (visible && current) place(current); }, { passive: true, capture: true });
  window.addEventListener('resize', () => { if (visible && current) place(current); }, { passive: true });
  document.addEventListener('mousedown', (e) => {                            // click anywhere else closes the menu
    if (menuOpen && !(host && e.composedPath().includes(host))) { menuOpen = false; menu.style.display = 'none'; btn.classList.remove('open'); }
  }, true);

  // ---------- menu ----------
  const title = () => (document.title || location.hostname).replace(/\s+/g, ' ').trim().slice(0, 70);
  const safe = s => s.replace(/[<>:"/\\|?*\u0000-\u001f]/g, '').replace(/\s+/g, ' ').trim();
  const fmt = n => {
    if (!n) return '';
    const u = ['B', 'KB', 'MB', 'GB']; let i = 0;
    while (n >= 1024 && i < u.length - 1) { n /= 1024; i++; }
    return n.toFixed(i > 1 ? 1 : 0) + ' ' + u[i];
  };

  function label(e, i) {
    const kind = e.ext ? `${e.ext.toUpperCase()} file` : 'file';
    return `${i}. ${title()} · ${kind}${e.quality ? ', quality ' + e.quality : ''}${e.size ? ' · ' + fmt(e.size) : ''}`;
  }

  const nameFor = e => e.ext ? `${safe(title())}${e.quality ? ' ' + e.quality : ''}.${e.ext}` : undefined;

  function renderMenu() {
    menu.replaceChildren();
    const row = (text, cls, fn) => {
      const b = document.createElement('button');
      b.className = 'row' + (cls ? ' ' + cls : '');
      b.textContent = text;
      b.title = text;
      b.addEventListener('click', (ev) => { if (ev.isTrusted) fn(); });
      menu.appendChild(b);
    };
    if (entries.length > 1) row('Download all', 'head', () => send(entries, true));
    entries.forEach((e, i) => row(label(e, i + 1), '', () => send([e], false)));
  }

  const isStreamed = v => /^blob:/i.test(v.currentSrc || v.src || '');

  function toggleMenu() {
    if (mode === 'info') {
      say("This site streams the video in small pieces. To download from YouTube, Facebook and similar sites, open Utylix > Settings > Video sites and click Install.", true);
      return;
    }
    menuOpen = !menuOpen;
    btn.classList.toggle('open', menuOpen);
    menu.style.display = menuOpen ? 'block' : 'none';
    toast.style.display = 'none';
    if (menuOpen) {
      clearTimeout(hideTimer);
      if (mode === 'media') openMediaMenu(); else renderMenu();
    }
  }

  // ---------- YouTube, Facebook and other streaming sites (through Utylix's yt-dlp) ----------
  let mediaCache = { url: '', at: 0, info: null };

  const textRow = (text, cls) => { const d = document.createElement('div'); d.className = cls; d.textContent = text; return d; };

  /** The address yt-dlp should be given. In a Facebook feed the page address is the feed, so use the video's own link. */
  function pageUrlFor(video) {
    const here = location.href;
    if (!/(^|\.)(facebook\.com|fb\.watch)$/i.test(location.hostname) || /[?&]v=|\/(videos|watch|reel)\//i.test(here)) return here;
    let el = video;
    for (let i = 0; el && i < 14; i++, el = el.parentElement) {
      const a = el.querySelector && el.querySelector('a[href*="/videos/"], a[href*="/watch/"], a[href*="/reel/"]');
      if (a && a.href) return a.href;
    }
    return here;
  }

  async function openMediaMenu() {
    const url = pageUrlFor(current);
    menu.replaceChildren(textRow('Getting the available qualities…', 'note'));
    let cached = mediaCache.url === url && performance.now() - mediaCache.at < 120000 ? mediaCache : null;
    if (!cached) {
      try {
        // referer = the page that embedded this player (many sites refuse a stream without it);
        // the background tries the page address first, then any stream (.m3u8/.mpd) the page loaded
        const r = await chrome.runtime.sendMessage({ type: 'media-info', url, referer: document.referrer || undefined });
        if (!menuOpen) return;                                            // closed while we waited
        if (!r || !r.ok) { menu.replaceChildren(textRow((r && r.error) || 'Could not read this video.', 'note err')); return; }
        // a bare stream is called things like "master"; the page's own title makes a far better file name
        const src = r.source || { url };
        const pageTitle = document.title.replace(/\s+/g, ' ').trim().slice(0, 150);
        cached = mediaCache = { url, at: performance.now(), info: r.info, source: src,
                                title: src.url !== url && pageTitle ? pageTitle : r.info.title };
      } catch {
        menu.replaceChildren(textRow('Could not reach Utylix. Reload this page.', 'note err'));
        return;
      }
    }
    if (menuOpen) renderMediaMenu(cached.source, cached.info, cached.title);
  }

  // What to save: picture and sound together (default), the picture alone, or just the sound.
  let mediaMode = 'both';
  const groupOf = o => o.id.startsWith('v:') ? 'video' : (o.id === 'audio' || o.id === 'mp3') ? 'sound' : 'both';

  function renderMediaMenu(source, info, title) {
    const groups = { both: [], video: [], sound: [] };
    info.options.forEach(o => groups[groupOf(o)].push(o));
    if (!groups[mediaMode].length) mediaMode = groups.both.length ? 'both' : groups.video.length ? 'video' : 'sound';

    menu.replaceChildren();
    menu.appendChild(textRow(title, 'note title'));
    const kinds = [['both', 'Video + sound'], ['video', 'Video only'], ['sound', 'Sound only']].filter(([k]) => groups[k].length);
    if (kinds.length > 1) {
      const tabs = document.createElement('div');
      tabs.className = 'tabs';
      for (const [key, text] of kinds) {
        const t = document.createElement('button');
        t.className = 'tab' + (key === mediaMode ? ' on' : '');
        t.textContent = text;
        t.addEventListener('click', (ev) => { if (ev.isTrusted) { mediaMode = key; renderMediaMenu(source, info, title); } });
        tabs.appendChild(t);
      }
      menu.appendChild(tabs);
    }
    groups[mediaMode].forEach((o, i) => {
      const kind = o.id === 'mp3' ? 'Audio only · MP3' : o.id === 'audio' ? 'Audio only · M4A'
        : mediaMode === 'video' ? `${o.label} · Video only, no sound` : `${o.label} · MP4`;
      const size = o.size ? ` · ~${fmt(o.size)}` : '';
      const b = document.createElement('button');
      b.className = 'row' + (o.available ? '' : ' off');
      b.textContent = `${i + 1}. ${kind}${size}${o.available ? '' : ' — ' + o.note}`;
      b.title = b.textContent;
      b.addEventListener('click', (ev) => { if (ev.isTrusted && o.available) sendMedia(source, o, title); });
      menu.appendChild(b);
    });
  }

  async function sendMedia(source, option, title) {
    menuOpen = false;
    menu.style.display = 'none';
    btn.classList.remove('open');
    try {
      const r = await chrome.runtime.sendMessage({ type: 'media-download', url: source.url, referer: source.referer, option: option.id, title, size: option.size });
      if (r && r.ok) say('Video added to Utylix', false);
      else say((r && r.error) || 'Could not reach Utylix', true);
    } catch {
      say('Could not reach Utylix. Reload this page.', true);
    }
  }

  function say(text, isError) {
    toast.textContent = text;
    toast.className = 'toast' + (isError ? ' err' : '');
    toast.style.display = 'block';
    clearTimeout(toastTimer);
    toastTimer = setTimeout(() => { toast.style.display = 'none'; }, 3500);
  }

  async function send(list, all) {
    menuOpen = false;
    menu.style.display = 'none';
    btn.classList.remove('open');
    try {
      const msg = all
        ? { type: 'download-all', items: list.map(e => ({ url: e.url, filename: nameFor(e) })) }
        : { type: 'download', url: list[0].url, filename: nameFor(list[0]) };
      const r = await chrome.runtime.sendMessage(msg);
      if (r && r.ok) say(all ? `Sent ${r.count} downloads to Utylix` : 'Sent to Utylix', false);
      else say((r && r.error) || 'Could not reach Utylix', true);
    } catch {
      say('Could not reach Utylix. Reload this page.', true);
    }
  }
})();
