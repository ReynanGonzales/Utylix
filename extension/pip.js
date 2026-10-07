// Utylix: right-click a video -> "Picture in Picture" (a small floating player that stays on top).
// YouTube has its own button for this, so this script does nothing there.
(() => {
  'use strict';
  if (window.__utylixPip) return;
  window.__utylixPip = true;
  if (/(^|\.)(youtube\.com|youtube-nocookie\.com|youtu\.be)$/i.test(location.hostname)) return;

  const MIN_W = 120, MIN_H = 70;          // ignore tiny videos (ads, previews, icons)
  let target = null;                      // the video under the pointer at the last right click
  let over = false, lastMove = 0;

  const usable = v => {
    const r = v.getBoundingClientRect();
    if (r.width < MIN_W || r.height < MIN_H) return false;
    const cs = getComputedStyle(v);
    return cs.display !== 'none' && cs.visibility !== 'hidden';
  };

  /** The video at a point, also when a player puts its own layer on top of it (then the browser's "video" menu does not appear). */
  function videoAt(x, y) {
    const direct = document.elementsFromPoint(x, y).find(el => el.tagName === 'VIDEO' && usable(el));
    if (direct) return direct;
    for (const v of document.querySelectorAll('video')) {
      if (!usable(v)) continue;
      const r = v.getBoundingClientRect();
      if (x >= r.left && x <= r.right && y >= r.top && y <= r.bottom) return v;
    }
    return null;
  }

  /** No video under the pointer (odd page): the one that is playing, else the biggest. */
  function guess() {
    const all = [...document.querySelectorAll('video')].filter(usable);
    return all.find(v => !v.paused && !v.ended) || all.sort((a, b) => b.clientWidth * b.clientHeight - a.clientWidth * a.clientHeight)[0] || null;
  }

  // Tell the extension whether the pointer is over a video, so the menu entry for covered videos can be shown only then.
  let lastSent = 0;
  function setOver(now, force = false) {
    const t = performance.now();
    if (now === over && !(force || (now && t - lastSent > 1000))) return;           // (while on a video: say so again every second, see background.js)
    over = now;
    lastSent = t;
    try { chrome.runtime.sendMessage({ type: 'pip-hover', over }).catch(() => {}); } catch { /* extension was reloaded: this page needs a refresh */ }
  }

  document.addEventListener('mousemove', (e) => {
    const t = performance.now();
    if (t - lastMove < 120) return;
    lastMove = t;
    setOver(!!videoAt(e.clientX, e.clientY));
  }, { passive: true, capture: true });
  document.addEventListener('mouseover', (e) => { setOver(!!videoAt(e.clientX, e.clientY)); }, { passive: true, capture: true });
  document.addEventListener('mouseout', (e) => { if (!e.relatedTarget) setOver(false); }, true);
  window.addEventListener('blur', () => setOver(false));
  // Facebook and Instagram replace the browser's right-click menu on videos with their own, so the browser menu (and our entry in
  // it) never shows. On those sites, swallow the right click before the page sees it when it is on a video: the normal menu opens.
  const OWN_MENU = /(^|\.)(facebook\.com|fb\.com|fb\.watch|instagram\.com)$/i.test(location.hostname);
  window.addEventListener('contextmenu', (e) => {
    target = videoAt(e.clientX, e.clientY);
    setOver(!!target, true);                                                          // right before the browser builds its menu
    if (OWN_MENU && target && e.isTrusted) e.stopImmediatePropagation();
  }, true);

  let toast = null;
  function say(text) {
    toast?.remove();
    toast = document.createElement('div');
    toast.textContent = text;
    toast.style.cssText = 'all:initial;position:fixed;left:50%;bottom:28px;transform:translateX(-50%);z-index:2147483647;' +
      'background:#1a1f2b;color:#e6e9f0;font:14px Segoe UI,system-ui,sans-serif;padding:10px 16px;border-radius:8px;' +
      'box-shadow:0 4px 18px rgba(0,0,0,.4);border:1px solid #2a3142;pointer-events:none';
    (document.fullscreenElement || document.documentElement).appendChild(toast);
    const mine = toast;
    setTimeout(() => { mine.remove(); if (toast === mine) toast = null; }, 4500);
  }

  /** The browser wants a click on the page before it allows a floating video: wait for the next one. */
  function waitForClick(video, start) {
    say('Click the video once to start Picture in Picture');
    const onDown = (e) => {
      if (!e.isTrusted) return;
      cleanup();
      (start ? start() : video.requestPictureInPicture()).catch(err => say('Picture in Picture failed: ' + ((err && err.message) || err)));
    };
    const cleanup = () => { document.removeEventListener('pointerdown', onDown, true); clearTimeout(timer); };
    const timer = setTimeout(cleanup, 8000);
    document.addEventListener('pointerdown', onDown, true);
  }

  // ---- follow the feed: Reels, Shorts-like players and playlists swap to the next video; the floating window should follow ----
  let following = false, followTimer = 0, switchTimer = 0;
  const log = text => { try { chrome.runtime.sendMessage({ type: 'pip-log', text }).catch(() => {}); } catch { /* extension reloaded */ } };
  // Only where the page swaps videos by swiping (Reels, Shorts, TikTok): on ordinary pages a floating video stays what it is.
  const feedLike = () => /\/(reels?|shorts)(\/|$)/i.test(location.pathname) || /(^|\.)tiktok\.com$/i.test(location.hostname);

  const inView = v => {
    const r = v.getBoundingClientRect();
    const w = Math.min(r.right, innerWidth) - Math.max(r.left, 0), h = Math.min(r.bottom, innerHeight) - Math.max(r.top, 0);
    return w > 0 && h > 0 && (w * h) / Math.max(1, r.width * r.height) > 0.5;      // more than half of it is on screen
  };

  /** The floating video stopped (or was swiped away) and another one is playing on screen: float that one instead. */
  function maybeSwitch() {
    const cur = document.pictureInPictureElement;
    if (!following || !cur || !feedLike()) return;
    if (!(cur.paused || cur.ended || !cur.isConnected || !inView(cur))) return;     // it still plays in view: leave it
    const next = [...document.querySelectorAll('video')].find(v => v !== cur && !v.paused && !v.ended && v.readyState > 0 && usable(v) && inView(v));
    if (next) { log('following to the next video'); next.requestPictureInPicture().catch(e => log('could not follow: ' + (e && e.name))); }   // allowed without a click while a floating window exists
  }

  /** Next / previous video on a swipe-style page: the same thing the keyboard does, else scroll the list by one screen. */
  function step(dir) {
    const cur = document.pictureInPictureElement || target;
    const key = dir > 0 ? 'ArrowDown' : 'ArrowUp', code = dir > 0 ? 40 : 38;
    const aim = document.activeElement && document.activeElement !== document.body ? document.activeElement : document.body;
    for (const type of ['keydown', 'keyup'])
      aim.dispatchEvent(new KeyboardEvent(type, { key, code: key, keyCode: code, which: code, bubbles: true, cancelable: true, composed: true }));
    setTimeout(() => {
      const still = document.pictureInPictureElement === cur && cur && !cur.paused && inView(cur);
      if (!still) return;                                                               // the key worked: the page moved on
      let el = cur && cur.parentElement;
      while (el && el !== document.body && !(el.scrollHeight > el.clientHeight + 40 && /(auto|scroll)/.test(getComputedStyle(el).overflowY))) el = el.parentElement;
      const box = el && el !== document.body ? el : document.scrollingElement;
      box.scrollBy({ top: dir * (box === document.scrollingElement ? innerHeight : box.clientHeight), behavior: 'smooth' });
    }, 500);
    log(dir > 0 ? 'wheel: next video' : 'wheel: previous video');
  }

  function mediaKeys(on) {                              // also the ⏮ ⏭ buttons some browsers show on the floating window
    try {
      navigator.mediaSession.setActionHandler('nexttrack', on ? () => step(1) : null);
      navigator.mediaSession.setActionHandler('previoustrack', on ? () => step(-1) : null);
    } catch { /* not supported */ }
  }

  function startFollowing() {
    following = true;
    clearInterval(followTimer);
    followTimer = setInterval(maybeSwitch, 1000);
  }

  const soon = () => { clearTimeout(switchTimer); switchTimer = setTimeout(maybeSwitch, 180); };
  for (const type of ['play', 'playing', 'pause', 'ended', 'emptied']) document.addEventListener(type, soon, true);   // media events do not bubble
  document.addEventListener('leavepictureinpicture', () => {
    setTimeout(() => { if (!document.pictureInPictureElement) { following = false; clearInterval(followTimer); log('floating window closed'); mediaKeys(false); try { chrome.runtime.sendMessage({ type: 'pip-active', on: false }).catch(() => {}); } catch { /* reloaded */ } } }, 400);    // (a switch leaves and enters at once)
  }, true);
  document.addEventListener('enterpictureinpicture', (e) => { log('floating: ' + (e.target.videoWidth || '?') + 'x' + (e.target.videoHeight || '?') + (feedLike() ? ', following the feed' : '')); startFollowing(); mediaKeys(true); try { chrome.runtime.sendMessage({ type: 'pip-active', on: true }).catch(() => {}); } catch { /* reloaded */ } }, true);

  // ---- subtitles in the floating window ----
  // Chrome / Brave's own floating window never draws a video's subtitles (not even its text tracks: a long-standing Chromium gap), so sites that import subtitles (OpenSubtitles ...) lose them there.
  // When the video has subtitles, Utylix floats it in a "document Picture in Picture" window instead (a small page of our own, newer browsers only): the very same video element is moved into it
  // (and back when it closes), with simple controls, and the subtitles are drawn over it by us. Where the words come from:
  //  1. a subtitle text track of the video that has cues (the page's own track, even a hidden one);
  //  2. else the words the page draws in its own layer over the video, read from the page.
  const CAP_GOOD = /subtitle|caption|\bcues?\b|lyric|transcript|text-?track|\bsubs?\b/i;
  const CAP_BAD_CLASS = /control|progress|timeline|volume|menu|tooltip|button|toast|seek|thumb|duration|settings|badge|watermark|logo|\bads?\b|advert/i;
  const CAP_BAD_TAG = 'button,a,[role=button],[role=menuitem],[role=slider],[role=tooltip],[role=menu],[role=dialog],input,select,textarea,nav,header,video,canvas,svg,script,style';

  const nameOf = el => String((el.className && el.className.baseVal !== undefined ? el.className.baseVal : el.className) || '') + ' ' + (el.id || '');
  const showing = el => {
    if (!el.getClientRects().length) return false;
    const cs = getComputedStyle(el);
    return cs.visibility !== 'hidden' && cs.display !== 'none' && parseFloat(cs.opacity) > 0.1;
  };

  /** The player around an element (its parents, as far up as they stay small): the layer with the words is somewhere in there. */
  function playerBox(anchor) {
    let box = anchor.parentElement;
    for (let a = box, n = 0; a && a !== document.body && n < 8; a = a.parentElement, n++) { if (a.getElementsByTagName('*').length > 1500) break; box = a; }
    return box || document.body;
  }

  /** Layers over the video that are named like subtitles. Positions come from the layout, not from pointing at the screen, so it also works while this tab is in the background. */
  function captionLayers(anchor) {
    const r = anchor.getBoundingClientRect();
    const named = new Set();
    if (r.width < 80 || r.height < 60) return named;
    for (const el of playerBox(anchor).querySelectorAll('*')) {
      if (el.contains(anchor) || el.matches(CAP_BAD_TAG) || el.closest(CAP_BAD_TAG) || !CAP_GOOD.test(nameOf(el))) continue;
      const b = el.getBoundingClientRect();
      if (b.width > 0 && b.height > 0 && b.right > r.left && b.left < r.right && b.bottom > r.top && b.top < r.bottom) named.add(el);
    }
    return named;
  }

  /** The words the page is drawing over the lower part of the video right now ('' when none). `anchor` is the video, or the empty box that stands where it was. */
  function captionText(anchor) {
    const r = anchor.getBoundingClientRect();
    if (r.width < 80 || r.height < 60) return '';
    const named = captionLayers(anchor);
    if (named.size) {
      const tops = [...named].filter(a => { for (let p = a.parentElement; p; p = p.parentElement) if (named.has(p)) return false; return true; });         // (the outermost one of nested ones)
      return tops.filter(showing).map(a => a.innerText.trim()).filter(t => t && t.length < 500).join('\n');
    }
    // otherwise: words that stand over the video and are not part of the player's controls
    const overVideo = el => {                                     // (it lies over the video's lower half)
      const b = el.getBoundingClientRect();
      if (b.width < 1 || b.height < 1) return false;
      const cx = b.left + b.width / 2, cy = b.top + b.height / 2;
      return cx >= r.left - 4 && cx <= r.right + 4 && cy >= r.top + r.height * 0.4 && cy <= r.bottom + 4;
    };
    const lines = [];
    for (const el of playerBox(anchor).querySelectorAll('*')) {
      let hasText = false;
      for (const n of el.childNodes) if (n.nodeType === 3 && n.textContent.trim()) { hasText = true; break; }
      if (!hasText || !overVideo(el)) continue;
      if (el.matches(CAP_BAD_TAG) || el.closest(CAP_BAD_TAG) || el.contains(anchor) || CAP_BAD_CLASS.test(nameOf(el))) continue;
      const own = [...el.childNodes].filter(n => n.nodeType === 3).map(n => n.textContent).join(' ').replace(/\s+/g, ' ').trim();
      if (!own || own.length > 300 || /^\d{1,2}:\d{2}(:\d{2})?(\s*\/\s*\d{1,2}:\d{2}(:\d{2})?)?$/.test(own)) continue;
      if (!showing(el) || parseFloat(getComputedStyle(el).fontSize) < 11) continue;
      lines.push({ el, own });
    }
    lines.sort((a, b) => (a.el.compareDocumentPosition(b.el) & Node.DOCUMENT_POSITION_FOLLOWING) ? -1 : 1);
    return lines.map(l => l.own).join('\n');
  }

  const subtitleTracks = v => [...(v.textTracks || [])].filter(t => (t.kind === 'subtitles' || t.kind === 'captions') && (t.mode === 'showing' || (t.cues && t.cues.length > 0)));

  /** Does this video have subtitles worth the subtitle window? (a track with cues, or a subtitle layer on the page) */
  function hasCaptions(v) {
    if (subtitleTracks(v).length) return true;
    try { return captionLayers(v).size > 0 || captionText(v) !== ''; } catch { return false; }
  }

  let doc = null;                                                    // the open subtitle window: { win, video, holder, ... }

  // ---- a subtitle file of the person's own (.srt .vtt .ass .ssa): picked in the window or dropped on it; kept per video while the page stays ----
  const loadedSubs = new WeakMap();                                   // video -> { cues: [{start, end, text}], name, offset }

  /** SubRip, WebVTT and Advanced SubStation Alpha text -> cues (seconds), the tags taken out. */
  function parseSubtitles(text) {
    text = text.replace(/^﻿/, '').replace(/\r/g, '');
    const cues = [];
    const ts = s => {
      const m = String(s).trim().match(/^(?:(\d+):)?(\d{1,2}):(\d{1,2})(?:[.,](\d{1,3}))?$/);
      return m ? (+(m[1] || 0)) * 3600 + (+m[2]) * 60 + (+m[3]) + (m[4] ? +('0.' + m[4]) : 0) : NaN;
    };
    const clean = t => t.replace(/<[^>]*>/g, '').replace(/\{\\[^}]*\}/g, '').replace(/\\N/g, '\n').replace(/\\n/g, '\n').replace(/&nbsp;/g, ' ').replace(/&lt;/g, '<').replace(/&gt;/g, '>').replace(/&amp;/g, '&').trim();
    if (/^\s*\[Script Info\]/im.test(text) || /^Dialogue:/m.test(text)) {
      let fmt = null;
      for (const line of text.split('\n')) {
        if (/^Format:/i.test(line) && /\bstart\b/i.test(line)) fmt = line.slice(7).split(',').map(x => x.trim().toLowerCase());
        else if (/^Dialogue:/i.test(line) && fmt) {
          const parts = line.slice(9).split(',');
          const iS = fmt.indexOf('start'), iE = fmt.indexOf('end'), iT = fmt.indexOf('text');
          if (iS < 0 || iE < 0 || iT < 0) continue;
          const start = ts(parts[iS]), end = ts(parts[iE]), body = clean(parts.slice(iT).join(','));
          if (isFinite(start) && isFinite(end) && body) cues.push({ start, end, text: body });
        }
      }
    } else {
      for (const block of text.split(/\n{2,}/)) {
        const lines = block.split('\n');
        const i = lines.findIndex(l => l.includes('-->'));
        if (i < 0) continue;
        const [a, b] = lines[i].split('-->');
        const start = ts(a), end = ts(b.trim().split(/\s+/)[0]), body = clean(lines.slice(i + 1).join('\n'));
        if (isFinite(start) && isFinite(end) && body) cues.push({ start, end, text: body });
      }
    }
    return cues.sort((x, y) => x.start - y.start);
  }

  /** A subtitle file's text: UTF-8, else the Windows code page many older files use. */
  async function readSubtitleFile(file) {
    const bytes = await file.arrayBuffer();
    try { return new TextDecoder('utf-8', { fatal: true }).decode(bytes); }
    catch { return new TextDecoder('windows-1252').decode(bytes); }
  }

  const fmtTime = s => { s = Math.max(0, Math.floor(s || 0)); const h = Math.floor(s / 3600), m = Math.floor(s % 3600 / 60), x = s % 60; return (h ? h + ':' + String(m).padStart(2, '0') : m) + ':' + String(x).padStart(2, '0'); };

  /**
   * Floats the video in a window of our own, with the subtitles drawn over it. `make` gives the window (the browser's documentPictureInPicture.requestWindow; a test can pass its own).
   * The video is moved there, and an empty box of the same size stays on the page in its place (the page keeps its layout, and the subtitle layer on it can still be read).
   */
  async function openSubtitleWindow(video, make) {
    const r = video.getBoundingClientRect();
    const aspect = (video.videoWidth && video.videoHeight) ? video.videoWidth / video.videoHeight : (r.width / Math.max(1, r.height) || 16 / 9);
    const w = Math.round(Math.min(560, Math.max(320, r.width || 480)));
    const win = await make({ width: w, height: Math.round(w / aspect) });
    const d = win.document;
    const holder = document.createElement('div');                     // (stands where the video was)
    holder.setAttribute('data-utylix-holder', '1');
    holder.style.cssText = `width:${r.width}px;height:${r.height}px;background:#000;display:${getComputedStyle(video).display === 'inline' ? 'inline-block' : getComputedStyle(video).display};`;
    const wasPlaying = !video.paused && !video.ended;
    const oldStyle = video.getAttribute('style');
    const oldModes = [...video.textTracks].map(t => [t, t.mode]);

    const style = d.createElement('style');
    style.textContent = `
      html,body{margin:0;height:100%;background:#000;overflow:hidden;font-family:Segoe UI,system-ui,sans-serif;color:#fff;user-select:none}
      video{position:absolute;inset:0;width:100%;height:100%;object-fit:contain;background:#000}
      #cap{position:absolute;left:4%;right:4%;bottom:56px;text-align:center;pointer-events:none;white-space:pre-line;line-height:1.3;font-weight:600}
      #cap span{background:rgba(0,0,0,.72);padding:.05em .4em;border-radius:4px;box-decoration-break:clone;-webkit-box-decoration-break:clone;text-shadow:0 1px 2px #000}
      #bar{position:absolute;left:0;right:0;bottom:0;display:flex;align-items:center;gap:8px;padding:6px 10px;background:linear-gradient(transparent,rgba(0,0,0,.8));transition:opacity .25s}
      body.idle #bar{opacity:0}
      #bar button{all:unset;cursor:pointer;width:30px;height:28px;text-align:center;border-radius:5px;font-size:15px;line-height:28px}
      #bar button:hover{background:rgba(255,255,255,.18)}
      #seek{flex:1;accent-color:#5b8def;height:4px}
      #time{font-size:12px;min-width:84px;text-align:right;opacity:.9}
      #bar button.on{background:rgba(91,141,239,.55)}
      #bar button.wide{width:auto;padding:0 6px;font-size:12px}
      #msg{position:absolute;z-index:5;left:10px;top:10px;max-width:80%;background:rgba(0,0,0,.75);border-radius:6px;padding:6px 10px;font-size:13px;opacity:0;transition:opacity .3s;pointer-events:none}
      #msg.show{opacity:1}
      body.drop::after{content:"Drop the subtitle file here";position:absolute;inset:8px;border:2px dashed #5b8def;border-radius:8px;display:flex;align-items:center;justify-content:center;font-size:16px;background:rgba(0,0,0,.55)}`;
    d.head.append(style);
    d.title = 'Utylix video';
    const cap = d.createElement('div'); cap.id = 'cap';
    const bar = d.createElement('div'); bar.id = 'bar';
    const play = d.createElement('button'), seek = d.createElement('input'), time = d.createElement('span'), mute = d.createElement('button'), smaller = d.createElement('button'), bigger = d.createElement('button');
    seek.type = 'range'; seek.id = 'seek'; seek.min = 0; seek.max = 1000; seek.value = 0; time.id = 'time';
    smaller.textContent = 'A−'; bigger.textContent = 'A+'; smaller.title = 'Smaller subtitles'; bigger.title = 'Bigger subtitles';
    const choose = d.createElement('button'), earlier = d.createElement('button'), later = d.createElement('button'), msg = d.createElement('div'), picker = d.createElement('input');
    choose.textContent = 'CC'; choose.title = 'Load a subtitle file (.srt  .vtt  .ass), or drop one on this window';
    earlier.textContent = '−.5s'; later.textContent = '+.5s'; earlier.className = later.className = 'wide';
    earlier.title = 'Subtitles show earlier'; later.title = 'Subtitles show later';
    msg.id = 'msg'; picker.type = 'file'; picker.accept = '.srt,.vtt,.ass,.ssa,.txt,text/vtt'; picker.style.display = 'none';
    bar.append(play, seek, time, mute, choose, earlier, later, smaller, bigger);
    d.body.append(msg, picker);

    // the video moves over; the empty box takes its place on the page
    video.replaceWith(holder);
    video.style.cssText = '';
    d.body.append(video, cap, bar);
    video.removeAttribute('controls');
    // tracks the page shows itself would be drawn twice: we draw them, the page's own drawing of them is switched off meanwhile
    const tracks = subtitleTracks(video);
    for (const t of tracks) if (t.mode === 'disabled') t.mode = 'hidden'; else if (t.mode === 'showing') t.mode = 'hidden';
    if (wasPlaying && video.paused) video.play().catch(() => {});

    let size = 4.2;                                                    // subtitle size, in % of the window's width
    const fit = () => { cap.style.fontSize = Math.max(13, (win.innerWidth || 480) * size / 100) + 'px'; };
    smaller.onclick = () => { size = Math.max(2.4, size - 0.5); fit(); };
    bigger.onclick = () => { size = Math.min(8, size + 0.5); fit(); };
    win.addEventListener('resize', fit); fit();

    const sync = () => {
      play.textContent = video.paused ? '▶' : '❚❚'; play.title = video.paused ? 'Play' : 'Pause';
      mute.textContent = video.muted || video.volume === 0 ? '🔇' : '🔊'; mute.title = 'Sound on / off';
      if (document.activeElement !== seek && video.duration && isFinite(video.duration)) seek.value = Math.round(video.currentTime / video.duration * 1000);
      time.textContent = fmtTime(video.currentTime) + (video.duration && isFinite(video.duration) ? ' / ' + fmtTime(video.duration) : '');
    };
    play.onclick = () => (video.paused ? video.play() : video.pause());
    video.addEventListener('click', play.onclick);
    mute.onclick = () => { video.muted = !video.muted; sync(); };
    seek.oninput = () => { if (video.duration && isFinite(video.duration)) video.currentTime = seek.value / 1000 * video.duration; };
    d.addEventListener('keydown', (e) => {
      if (e.key === ' ') { play.onclick(); e.preventDefault(); }
      else if (e.key === 'ArrowRight') video.currentTime += 5;
      else if (e.key === 'ArrowLeft') video.currentTime -= 5;
      else if (e.key === 'm' || e.key === 'M') mute.onclick();
    });
    let idle = 0;
    const wake = () => { d.body.classList.remove('idle'); clearTimeout(idle); idle = setTimeout(() => d.body.classList.add('idle'), 2500); };
    d.addEventListener('mousemove', wake); wake();
    for (const type of ['play', 'pause', 'timeupdate', 'volumechange', 'durationchange']) video.addEventListener(type, sync);
    sync();

    // the words: the person's own subtitle file if one was loaded, else a text track of the video, else the page's own layer
    let sub = loadedSubs.get(video) || null;
    let shown = null;
    let msgTimer = 0;
    const tell = text => { msg.textContent = text; msg.classList.add('show'); clearTimeout(msgTimer); msgTimer = setTimeout(() => msg.classList.remove('show'), 3500); };
    const showOffset = () => { later.title = 'Subtitles show later (now ' + (sub ? (sub.offset >= 0 ? '+' : '') + sub.offset.toFixed(1) + ' s' : '0') + ')'; };
    const refreshSub = () => { choose.classList.toggle('on', !!sub); earlier.style.display = later.style.display = sub ? '' : 'none'; showOffset(); shown = null; draw(); };
    const loadFile = async (file) => {
      if (!file) return;
      try {
        const cues = parseSubtitles(await readSubtitleFile(file));
        if (!cues.length) { tell('No subtitles found in ' + file.name); return; }
        sub = { cues, name: file.name, offset: 0 }; loadedSubs.set(video, sub);
        refreshSub(); tell(file.name + ': ' + cues.length + ' lines');
      } catch (e) { tell("Couldn't read that file"); }
    };
    choose.onclick = () => picker.click();
    picker.onchange = () => { loadFile(picker.files && picker.files[0]); picker.value = ''; };
    const nudge = by => { if (!sub) return; sub.offset = Math.round((sub.offset + by) * 10) / 10; showOffset(); tell('Subtitles ' + (sub.offset >= 0 ? '+' : '') + sub.offset.toFixed(1) + ' s'); shown = null; draw(); };
    earlier.onclick = () => nudge(-0.5); later.onclick = () => nudge(0.5);
    d.addEventListener('dragover', e => { e.preventDefault(); d.body.classList.add('drop'); });
    d.addEventListener('dragleave', e => { if (!e.relatedTarget) d.body.classList.remove('drop'); });
    d.addEventListener('drop', e => { e.preventDefault(); d.body.classList.remove('drop'); loadFile(e.dataTransfer && e.dataTransfer.files && e.dataTransfer.files[0]); });
    const words = () => {
      let text = '';
      if (sub) { const t = video.currentTime - sub.offset; text = sub.cues.filter(c => t >= c.start && t < c.end).map(c => c.text).join('\n'); return text.trim(); }
      for (const t of tracks) { const cues = [...(t.activeCues || [])]; if (cues.length) { text = cues.map(c => (c.text || '').replace(/<[^>]+>/g, '')).join('\n'); break; } }
      if (!text && !tracks.length) { try { text = captionText(holder); } catch { /* the page changed under us */ } }
      return text.trim();
    };
    const draw = () => {
      const text = words();
      if (text === shown) return;
      shown = text;
      cap.textContent = '';
      if (text) { const span = d.createElement('span'); span.textContent = text; cap.append(span); }
    };
    const tick = setInterval(draw, 200);
    const observer = new MutationObserver(() => { setTimeout(draw, 60); });
    if (!tracks.length) observer.observe(document.documentElement, { subtree: true, childList: true, characterData: true, attributes: true, attributeFilter: ['class', 'hidden'] });
    refreshSub();                                                        // (also draws; the buttons for a loaded file show only when there is one)

    let closed = false;
    const close = () => {
      if (closed) return; closed = true;
      clearInterval(tick); clearTimeout(idle); clearTimeout(msgTimer); observer.disconnect();
      for (const type of ['play', 'pause', 'timeupdate', 'volumechange', 'durationchange']) video.removeEventListener(type, sync);
      video.removeEventListener('click', play.onclick);
      const resume = !video.paused && !video.ended;
      if (holder.isConnected) holder.replaceWith(video); else document.body.append(video);   // (back where it was)
      if (oldStyle === null) video.removeAttribute('style'); else video.setAttribute('style', oldStyle);
      for (const [t, m] of oldModes) { try { t.mode = m; } catch { /* gone */ } }
      if (resume && video.paused) video.play().catch(() => {});
      doc = null;
      try { chrome.runtime.sendMessage({ type: 'pip-active', on: false }).catch(() => {}); } catch { /* reloaded */ }
    };
    win.addEventListener('pagehide', close);
    doc = { win, video, close: () => { try { win.close(); } catch { /* already closed */ } close(); } };
    try { chrome.runtime.sendMessage({ type: 'pip-active', on: true }).catch(() => {}); } catch { /* reloaded */ }
    log('subtitle window: ' + (tracks.length ? 'from the video\'s own track' : 'from the page\'s subtitle layer'));
  }

  if (window.__utylixPipTest) Object.assign(window.__utylixPipTest, { openSubtitleWindow, hasCaptions, captionText, parseSubtitles, closeSubtitleWindow: () => doc && doc.close() });       // (only a test page asks for this)

  const subtitleWindow = video => openSubtitleWindow(video, opts => window.documentPictureInPicture.requestWindow(opts));

  async function toggle(video, forceSubtitleWindow = false) {
    if (!video) return { error: 'There is no video here.' };
    if (doc) { doc.close(); return { ok: true }; }                         // the subtitle window is open: put the video back
    if (forceSubtitleWindow && !window.documentPictureInPicture) { say("This browser can't open the subtitle window (it needs a recent Chrome / Brave)"); return { error: 'no document Picture in Picture here' }; }
    if (document.pictureInPictureElement !== video && (forceSubtitleWindow || loadedSubs.has(video) || hasCaptions(video))) {
      if (window.documentPictureInPicture) {
        try { await subtitleWindow(video); return { ok: true, subtitles: true }; }
        catch (e) {
          if (e && e.name === 'NotAllowedError') { waitForClick(video, () => subtitleWindow(video)); return { ok: true, waiting: true }; }
          log('subtitle window failed: ' + ((e && e.message) || e));                   // (then the plain floating window below)
        }
      } else say("This browser can't show subtitles in the floating window, so the video floats without them");
    }
    if (!document.pictureInPictureEnabled) return { error: "This page doesn't allow Picture in Picture." };
    try {
      if (document.pictureInPictureElement === video) {                  // already floating: bring it back
        await document.exitPictureInPicture();
        return { ok: true };
      }
      video.removeAttribute('disablepictureinpicture');                  // some sites switch it off
      video.disablePictureInPicture = false;
      if (video.readyState === 0) return { error: 'The video has not started loading yet.' };
      await video.requestPictureInPicture();
      return { ok: true };
    } catch (e) {
      if (e && e.name === 'NotAllowedError') { waitForClick(video); return { ok: true, waiting: true }; }
      say('Picture in Picture failed: ' + ((e && e.message) || e));
      return { error: (e && e.message) || String(e) };
    }
  }

  chrome.runtime.onMessage.addListener((msg, sender, respond) => {
    if (msg && msg.type === 'pip-step' && (msg.dir === 1 || msg.dir === -1)) { step(msg.dir); return; }
    if (!msg || msg.type !== 'pip') return;
    // from the keyboard shortcut there is no right click: use the video that plays (else the biggest)
    toggle(!msg.guess && target && document.contains(target) ? target : guess(), !!msg.subs).then(respond);
    return true;                                                         // answer asynchronously
  });
})();
