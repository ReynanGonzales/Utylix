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
  function waitForClick(video) {
    say('Click the video once to start Picture in Picture');
    const onDown = (e) => {
      if (!e.isTrusted) return;
      cleanup();
      video.requestPictureInPicture().catch(err => say('Picture in Picture failed: ' + err.message));
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
    setTimeout(() => { if (!document.pictureInPictureElement) { stopCaptions(); following = false; clearInterval(followTimer); log('floating window closed'); mediaKeys(false); try { chrome.runtime.sendMessage({ type: 'pip-active', on: false }).catch(() => {}); } catch { /* reloaded */ } } }, 400);    // (a switch leaves and enters at once)
  }, true);
  document.addEventListener('enterpictureinpicture', (e) => { log('floating: ' + (e.target.videoWidth || '?') + 'x' + (e.target.videoHeight || '?') + (feedLike() ? ', following the feed' : '')); startFollowing(); mediaKeys(true); startCaptions(e.target); try { chrome.runtime.sendMessage({ type: 'pip-active', on: true }).catch(() => {}); } catch { /* reloaded */ } }, true);

  // ---- subtitles in the floating window ----
  // The floating window only draws what the browser itself draws for the video: its text tracks that are "showing". Sites that fetch subtitles (OpenSubtitles ...) usually keep them in a
  // track that is "hidden" and draw the words themselves in a layer over the video, or draw words with no track at all. Neither shows in the floating window. So, while it floats:
  //  1. a hidden track that has subtitles in it is switched to "showing" (put back afterwards);
  //  2. otherwise the words the page draws over the video are read from the page and fed into a track of our own on that video, so the browser draws them in the floating window.
  let capVideo = null, capTrack = null, capCue = null, capText = '', capSince = 0, capObserver = null, capTimer = 0, capSoon = 0, promoted = [];
  const capTracks = new WeakMap();                              // video -> the track we made for it (a track can't be removed again: reuse it)
  const CAP_GOOD = /subtitle|caption|\bcues?\b|lyric|transcript|text-?track|\bsubs?\b/i;
  const CAP_BAD_CLASS = /control|progress|timeline|volume|menu|tooltip|button|toast|seek|thumb|duration|settings|badge|watermark|logo|\bads?\b|advert/i;
  const CAP_BAD_TAG = 'button,a,[role=button],[role=menuitem],[role=slider],[role=tooltip],[role=menu],[role=dialog],input,select,textarea,nav,header,video,canvas,svg,script,style';

  const nameOf = el => String((el.className && el.className.baseVal !== undefined ? el.className.baseVal : el.className) || '') + ' ' + (el.id || '');
  const showing = el => {
    if (!el.getClientRects().length) return false;
    const cs = getComputedStyle(el);
    return cs.visibility !== 'hidden' && cs.display !== 'none' && parseFloat(cs.opacity) > 0.1;
  };

  /** The words the page is drawing over the lower part of the video right now ('' when none). */
  function captionText(v) {
    const r = v.getBoundingClientRect();
    if (r.width < 80 || r.height < 60) return '';
    // the player: the video's parents, as far up as they stay small (the layer with the words is somewhere in there). Positions are read from the layout, not by pointing at the screen, so it also
    // works while this tab is in the background behind the floating window.
    let box = v.parentElement;
    for (let a = box, n = 0; a && a !== document.body && n < 8; a = a.parentElement, n++) { if (a.getElementsByTagName('*').length > 1500) break; box = a; }
    box = box || document.body;
    const overVideo = el => {                                     // (it lies over the video's lower half)
      const b = el.getBoundingClientRect();
      if (b.width < 1 || b.height < 1) return false;
      const cx = b.left + b.width / 2, cy = b.top + b.height / 2;
      return cx >= r.left - 4 && cx <= r.right + 4 && cy >= r.top + r.height * 0.4 && cy <= r.bottom + 4;
    };
    const all = [...box.querySelectorAll('*')];
    // a layer named like subtitles: take all of its words (its lines may be anywhere in it)
    const named = new Set();
    for (const el of all) {
      if (el.contains(v) || el.matches(CAP_BAD_TAG) || el.closest(CAP_BAD_TAG) || !CAP_GOOD.test(nameOf(el))) continue;
      const b = el.getBoundingClientRect();
      if (b.width > 0 && b.height > 0 && b.right > r.left && b.left < r.right && b.bottom > r.top && b.top < r.bottom) named.add(el);
    }
    if (named.size) {
      const tops = [...named].filter(a => { for (let p = a.parentElement; p; p = p.parentElement) if (named.has(p)) return false; return true; });         // (the outermost one of nested ones)
      return tops.filter(showing).map(a => a.innerText.trim()).filter(t => t && t.length < 500).join('\n');
    }
    // otherwise: words that stand over the video and are not part of the player's controls
    const lines = [];
    for (const el of all) {
      let hasText = false;
      for (const n of el.childNodes) if (n.nodeType === 3 && n.textContent.trim()) { hasText = true; break; }
      if (!hasText || !overVideo(el)) continue;
      if (el.matches(CAP_BAD_TAG) || el.closest(CAP_BAD_TAG) || el.contains(v) || CAP_BAD_CLASS.test(nameOf(el))) continue;
      const own = [...el.childNodes].filter(n => n.nodeType === 3).map(n => n.textContent).join(' ').replace(/\s+/g, ' ').trim();
      if (!own || own.length > 300 || /^\d{1,2}:\d{2}(:\d{2})?(\s*\/\s*\d{1,2}:\d{2}(:\d{2})?)?$/.test(own)) continue;
      if (!showing(el) || parseFloat(getComputedStyle(el).fontSize) < 11) continue;
      lines.push({ el, own });
    }
    lines.sort((a, b) => (a.el.compareDocumentPosition(b.el) & Node.DOCUMENT_POSITION_FOLLOWING) ? -1 : 1);
    return lines.map(l => l.own).join('\n');
  }

  function putCue(text) {
    if (!capTrack) return;
    const now = capVideo.currentTime;
    if (capCue) { try { capCue.endTime = Math.max(capCue.startTime + 0.05, now); } catch { /* cue gone */ } capCue = null; }
    if (text) { capCue = new VTTCue(now, now + 30, text); capCue.line = -3; try { capTrack.addCue(capCue); } catch { capCue = null; } }
  }

  function capTick() {
    if (!capVideo || !capTrack) return;
    let text = '';
    try { text = captionText(capVideo); } catch { /* the page changed under us */ }
    const t = performance.now();
    if (text !== capText) { capText = text; capSince = t; putCue(text); }
    else if (text && t - capSince > 30000 && capCue) { putCue(''); }                       // (the same words for 30 seconds: a title or a logo, not a subtitle)
  }

  /** A hidden track that holds subtitles becomes visible for the floating window; the one the page shows itself is left alone. */
  function promote(v) {
    let any = false;
    for (const tr of v.textTracks) {
      if (tr === capTrack || (tr.kind !== 'subtitles' && tr.kind !== 'captions')) continue;
      if (tr.mode === 'showing') { any = true; continue; }
      if (tr.mode === 'hidden' && tr.cues && tr.cues.length > 0 && (!any || (tr.activeCues && tr.activeCues.length > 0))) { tr.mode = 'showing'; promoted.push(tr); any = true; }
    }
    return any;
  }

  function startCaptions(v) {
    stopCaptions();
    if (!v || !v.textTracks) return;
    capVideo = v;
    const haveTracks = promote(v);
    if (!haveTracks) {
      capTrack = capTracks.get(v) || v.addTextTrack('captions', 'Utylix', '');
      capTracks.set(v, capTrack);
      for (const c of [...(capTrack.cues || [])]) capTrack.removeCue(c);
      capTrack.mode = 'showing';
      capObserver = new MutationObserver(() => { clearTimeout(capSoon); capSoon = setTimeout(capTick, 150); });
      capObserver.observe(document.documentElement, { subtree: true, childList: true, characterData: true, attributes: true, attributeFilter: ['class', 'hidden'] });
      capTimer = setInterval(capTick, 700);
      v.addEventListener('seeking', capSeek);
      capTick();
    } else capTimer = setInterval(() => { if (capVideo) promote(capVideo); }, 2000);        // (the person may pick another language on the page)
  }

  function capSeek() { capText = ''; capCue = null; if (capTrack) for (const c of [...(capTrack.cues || [])]) capTrack.removeCue(c); }

  function stopCaptions() {
    clearInterval(capTimer); clearTimeout(capSoon);
    capObserver?.disconnect(); capObserver = null;
    if (capVideo) capVideo.removeEventListener('seeking', capSeek);
    if (capTrack) { try { for (const c of [...(capTrack.cues || [])]) capTrack.removeCue(c); capTrack.mode = 'disabled'; } catch { /* gone */ } }
    for (const tr of promoted) { try { tr.mode = 'hidden'; } catch { /* gone */ } }
    promoted = []; capTrack = null; capCue = null; capText = ''; capVideo = null;
  }

  if (window.__utylixPipTest) Object.assign(window.__utylixPipTest, { startCaptions, stopCaptions, captionText });       // (only a test page asks for this)

  async function toggle(video) {
    if (!video) return { error: 'There is no video here.' };
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
    toggle(!msg.guess && target && document.contains(target) ? target : guess()).then(respond);
    return true;                                                         // answer asynchronously
  });
})();
