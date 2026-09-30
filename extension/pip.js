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
  function setOver(now) {
    if (now === over) return;
    over = now;
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
    if (!msg || msg.type !== 'pip') return;
    // from the keyboard shortcut there is no right click: use the video that plays (else the biggest)
    toggle(!msg.guess && target && document.contains(target) ? target : guess()).then(respond);
    return true;                                                         // answer asynchronously
  });
})();
