// Utylix browser integration: hands downloads to the local app on 127.0.0.1.
//
// How capture works: the browser announces every download it is about to save. We cancel it
// straight away and give the URL (plus the cookies, referrer and user-agent the browser was
// using) to the desktop app. Two hooks are used:
//   * downloads.onDeterminingFilename - the browser WAITS for us here before it shows a
//     "Save as" dialog, and the real file name is known, so this is the earliest, most reliable spot.
//   * downloads.onCreated - fallback for browsers/situations where the first hook doesn't fire.
// Which files to capture (all / only some types, minimum size, excluded sites) is configured in the
// app's Settings and read from the app; only the on/off switch lives in the browser.
const SERVER = 'http://127.0.0.1:6800';
const DEFAULTS = { enabled: true };
const MEDIA_EXT = /\.(mp4|m4v|webm|mkv|mov|avi|flv|mp3|m4a|aac|ogg|opus|wav|flac)(\?|#|$)/i;
const IMAGE_EXT = /\.(jpe?g|png|gif|webp|bmp|avif|heic|tiff?)(\?|#|$)/i;
const MAX_PER_KIND = 30;
const MIN_IMAGE_BYTES = 100 * 1024;      // skip thumbnails, icons and UI graphics
const ALIVE_TTL = 10_000;

// ---------- browser-side setting (on/off), cached so events never wait on storage ----------
let cfg = { ...DEFAULTS };
const cfgReady = chrome.storage.local.get(DEFAULTS).then(v => { cfg = { ...DEFAULTS, ...v }; });
chrome.storage.onChanged.addListener((changes, area) => {
  if (area !== 'local') return;
  for (const [k, c] of Object.entries(changes)) cfg[k] = c.newValue;
});

// ---------- talking to the app ----------
let capture = { types_only: false, types: [], min_kb: 0, exclude: [] };
let tools = {};                       // which video helpers (yt-dlp, ffmpeg) the app has installed
let alive = { ok: false, at: 0 };

async function serverAlive(force = false) {
  if (!force && Date.now() - alive.at < ALIVE_TTL) return alive.ok;
  let ok = false;
  try {
    const r = await fetch(SERVER + '/api/ping', { signal: AbortSignal.timeout(800) });
    const j = await r.json();
    ok = j.app === 'idm-clone';
    if (ok && j.capture) capture = j.capture;             // settings come from the app
    if (ok) tools = j.tools || {};
  } catch { /* not running */ }
  alive = { ok, at: Date.now() };
  return ok;
}

// ---------- start Utylix when it is closed ----------
// The app registers itself with the browser as a "native messaging host" (once, when it runs). Asking the browser to
// connect to it makes the browser launch Utylix.exe in a tiny helper mode that starts the real app and exits.
// No pop-ups. If it isn't registered or is switched off in the app's Settings, this quietly returns false.
const NATIVE_HOST = 'com.idmclone.host';
let startFailedAt = 0;

function startApp() {
  return new Promise(resolve => {
    let done = false, port = null;
    const finish = ok => { if (!done) { done = true; clearTimeout(timer); try { port && port.disconnect(); } catch { /* already closed */ } resolve(ok); } };
    const timer = setTimeout(() => finish(false), 15000);
    try {
      port = chrome.runtime.connectNative(NATIVE_HOST);
      port.onMessage.addListener(m => finish(!!(m && m.ok)));
      port.onDisconnect.addListener(() => { void chrome.runtime.lastError; finish(false); });   // not registered / disabled
      port.postMessage({ cmd: 'start' });
    } catch { finish(false); }
  });
}

/** True when the app is reachable, starting it first if needed (and allowed). */
async function ensureApp() {
  if (await serverAlive()) return true;
  if (Date.now() - startFailedAt < 5 * 60 * 1000) return false;          // it just failed: don't stall every download again
  if (await startApp()) {
    for (let i = 0; i < 20; i++) {                                        // the helper waits for the app; this only double-checks
      if (await serverAlive(true)) return true;
      await new Promise(r => setTimeout(r, 300));
    }
  }
  startFailedAt = Date.now();
  return false;
}

async function cookieHeader(url) {
  try {
    const cookies = await chrome.cookies.getAll({ url });
    return cookies.map(c => `${c.name}=${c.value}`).join('; ');
  } catch {
    return '';
  }
}

/** Returns the id the app gave the new download. `prompt` = show the app's confirmation window. */
async function sendToServer(url, { referer, filename, prompt = true } = {}) {
  const res = await fetch(SERVER + '/api/add', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({
      url, referer, filename, prompt,
      cookie: await cookieHeader(url),
      user_agent: navigator.userAgent,
    }),
  });
  if (!res.ok) throw new Error('server refused: ' + res.status);
  alive = { ok: true, at: Date.now(), };
  return (await res.json()).added?.[0];
}

// ---------- video sites (YouTube, Facebook, ...): the app runs yt-dlp; we hand it the page + your login cookies ----------
async function pageCookies(url) {
  try {
    const list = await chrome.cookies.getAll({ url });
    return list.map(c => ({
      domain: c.domain, name: c.name, value: c.value, path: c.path,
      secure: !!c.secure, httpOnly: !!c.httpOnly, expires: c.expirationDate ? Math.floor(c.expirationDate) : 0,
    }));
  } catch {
    return [];
  }
}

async function callMedia(path, body, withCookies = true) {
  const res = await fetch(SERVER + path, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ ...body, cookies: withCookies ? await pageCookies(body.url) : [], user_agent: navigator.userAgent }),
    signal: AbortSignal.timeout(120000),                  // reading a video's qualities can take a while
  });
  const data = await res.json().catch(() => ({}));
  if (!res.ok) throw new Error(data.error || `Utylix answered ${res.status}`);
  return data;
}

const isYouTube = url => { try { return /(^|\.)(youtube\.com|youtu\.be|youtube-nocookie\.com)$/i.test(new URL(url).hostname); } catch { return false; } };
const needsLogin = new Set();                             // pages that only worked with the user's login cookies

/**
 * YouTube rejects the cookies of a live browser session ("The page needs to be reloaded"), so it gets none unless a
 * video really asks for a login. Other sites get the cookies (the app itself retries without them if they cause trouble).
 */
async function callMediaSmart(path, body) {
  if (!isYouTube(body.url) || needsLogin.has(body.url)) return callMedia(path, body, true);
  try {
    return await callMedia(path, body, false);
  } catch (e) {
    if (path.endsWith('/info') && /sign in|bot|log ?in|age|confirm/i.test(e.message)) {
      const r = await callMedia(path, body, true);         // this one needs the account
      needsLogin.add(body.url);
      return r;
    }
    throw e;
  }
}

/**
 * Find an address yt-dlp can actually read. First the page itself (works for YouTube, Facebook, ...). Many other
 * sites (anime sites, embedded players) hide the video behind a stream (.m3u8/.mpd) that the page loads; those are
 * remembered per tab, so if the page address fails, try the newest streams, with the player's page as referrer.
 * Returns { info, source: { url, referer } } - the address that worked.
 */
async function resolveMedia(tabId, pageUrl, referer, frameUrl) {
  let firstError;
  try {
    return { info: await callMediaSmart('/api/media/info', { url: pageUrl, referer }), source: { url: pageUrl, referer } };
  } catch (e) {
    firstError = e;
    if (/not running|not installed|isn't installed/i.test(e.message)) throw e;
  }
  const found = tabId != null ? (await chrome.storage.session.get(mediaKey(tabId)))[mediaKey(tabId)] || [] : [];
  const streams = found.filter(m => m.kind === 'manifest').slice(-3).reverse();
  for (const m of streams) {
    const ref = frameUrl && /^https?:/i.test(frameUrl) ? frameUrl : pageUrl;
    try {
      return { info: await callMediaSmart('/api/media/info', { url: m.url, referer: ref }), source: { url: m.url, referer: ref } };
    } catch { /* try the next stream */ }
  }
  throw firstError;
}

function flashBadge(text, color) {
  chrome.action.setBadgeBackgroundColor({ color });
  chrome.action.setBadgeText({ text });
  setTimeout(() => chrome.action.setBadgeText({ text: '' }), 2500);
}

/**
 * If the app could not even start fetching the file (e.g. the site rejects it), give the URL back
 * to the browser so a download is never silently lost. While the confirmation window is open the
 * download is "awaiting", which doesn't count against the time limit.
 */
async function watchHandoff(id, url) {
  for (let i = 0, tries = 0; i < 120 && tries < 10; i++) {
    await new Promise(r => setTimeout(r, 1500));
    try {
      const { downloads } = await (await fetch(SERVER + '/api/downloads')).json();
      const d = downloads.find(x => x.id === id);
      if (!d || d.downloaded > 0 || d.status === 'completed') return;   // cancelled by user, or working
      if (d.status === 'awaiting') continue;
      tries++;
      if (d.status === 'error') {
        await fetch(SERVER + '/api/discard', {
          method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ id }),
        });
        chrome.downloads.download({ url });
        return;
      }
    } catch { return; }
  }
}

// ---------- capture rules ----------
function hostOf(url) {
  try { return new URL(url).hostname.toLowerCase(); } catch { return ''; }
}

function excluded(sites, ...urls) {
  return urls.some(u => {
    const h = hostOf(u);
    return h && sites.some(s => h === s || h.endsWith('.' + s));
  });
}

function extensionOf(name) {
  const m = /\.([a-z0-9]{1,8})$/i.exec(name || '');
  return m ? m[1].toLowerCase() : '';
}

const baseName = p => (p ? p.split(/[\\/]/).pop() : '');

// Download id -> promise that settles once the browser's own download has been cancelled. Both browser
// events (filename step + created) can fire for the same download at nearly the same time; whichever
// gets here first claims it, the other just waits for the cancel and leaves it alone.
const claimed = new Map();

// Read-modify-write on storage.session must not overlap, or concurrent events overwrite each other's changes.
let serialQueue = Promise.resolve();
function serial(fn) {
  const run = serialQueue.then(fn);
  serialQueue = run.catch(() => {});
  return run;
}

// ---------- capture log (shown in the popup so it's clear why something was or wasn't captured) ----------
const LOG_KEY = 'capture_log';

function note(item, source, result) {
  const url = item.finalUrl || item.url || '';
  let name = baseName(item.filename);
  if (!name) { try { name = decodeURIComponent(new URL(url).pathname.split('/').pop()); } catch { /* not a URL */ } }
  return serial(async () => {
    const log = (await chrome.storage.session.get(LOG_KEY))[LOG_KEY] || [];
    log.unshift({ t: Date.now(), name: name || '(no name yet)', host: hostOf(url) || url.slice(0, 24), src: source, result });
    await chrome.storage.session.set({ [LOG_KEY]: log.slice(0, 12) });
  }).catch(() => { /* logging must never break capture */ });
}

/** Captures the download if the app wants it. Returns true when it was handed to the app. */
async function tryCapture(item, browserName, source, release) {
  const alreadyClaimed = async () => {
    await claimed.get(item.id).catch(() => {});                        // wait until the browser download is really cancelled
    release?.();
    return true;
  };
  if (claimed.has(item.id)) return alreadyClaimed();
  const skip = async why => { await note(item, source, 'left to browser: ' + why); return false; };
  await cfgReady;
  if (!cfg.enabled) return skip('capture is switched off in the popup');
  if (item.byExtensionId) return skip('started by another extension');
  if (item.state !== 'in_progress') return skip('download not in progress (' + item.state + ')');
  const url = item.finalUrl || item.url;
  if (!/^https?:/i.test(url)) return skip('not a normal web link (blob:/data:)');   // stays in the browser
  if (!(await ensureApp())) {                                          // closed? try to start it (silently) first
    flashBadge('!', '#dc2626');                                         // make "app not running" visible instead of silent
    return skip('Utylix is not running / not reachable');
  }

  if (item.totalBytes > 0 && item.totalBytes < capture.min_kb * 1024) return skip(`smaller than ${capture.min_kb} KB`);
  if (excluded(capture.exclude, url, item.referrer)) return skip('site is on the never-capture list');
  if (capture.types_only) {
    const ext = extensionOf(browserName) || extensionOf(new URL(url).pathname);
    if (!capture.types.includes(ext)) return skip(`file type "${ext || '?'}" is not in the capture list`);
  }

  // Claim it. There is deliberately NO await between this check and claimed.set(), so two concurrent
  // calls can never both get past it.
  if (claimed.has(item.id)) return alreadyClaimed();
  const cancelled = chrome.downloads.cancel(item.id).catch(() => {});  // cancel first ...
  claimed.set(item.id, cancelled);
  setTimeout(() => claimed.delete(item.id), 120_000);
  await cancelled;
  release?.();                                                         // ... then let the browser move on (no Save dialog)
  await chrome.downloads.erase({ id: item.id });
  try {
    const id = await sendToServer(url, { referer: item.referrer, filename: browserName || undefined });
    await note(item, source, 'captured -> sent to Utylix');
    flashBadge('OK', '#16a34a');
    if (id) watchHandoff(id, url);
  } catch (e) {
    alive = { ok: false, at: 0 };
    await note(item, source, 'Utylix refused it (' + e.message + ') -> given back to browser');
    chrome.downloads.download({ url });                                // ... and hand it back if the app fails
  }
  return true;
}

if (chrome.downloads.onDeterminingFilename) {                          // Chromium: browser waits for us here
  chrome.downloads.onDeterminingFilename.addListener((item, suggest) => {
    let released = false;
    const release = () => {
      if (released) return;
      released = true;
      try { suggest(); } catch { /* download already cancelled */ }
    };
    const guard = setTimeout(release, 15000);                          // never stall the browser (starting a closed app takes a few seconds)
    tryCapture(item, baseName(item.filename), 'filename step', release)
      .catch(e => console.warn('Utylix capture failed', e))
      .finally(() => { clearTimeout(guard); release(); });
    return true;                                                       // we will call suggest() asynchronously
  });
}

chrome.downloads.onCreated.addListener((item) => {
  tryCapture(item, baseName(item.filename), 'created').catch(e => console.warn('Utylix capture failed', e));
});

// ---------- right-click "Download with Utylix" (links, videos, audio, images) ----------
const VIDEO_SITES = [
  '*://*.youtube.com/*', '*://youtube.com/*', '*://*.facebook.com/*', '*://fb.watch/*', '*://*.instagram.com/*',
  '*://*.twitter.com/*', '*://x.com/*', '*://*.tiktok.com/*', '*://*.vimeo.com/*', '*://*.dailymotion.com/*',
];

chrome.runtime.onInstalled.addListener(() => {
  chrome.contextMenus.create({
    id: 'idm-download',
    title: 'Download with Utylix',
    contexts: ['link', 'video', 'audio', 'image'],
  });
  chrome.contextMenus.create({                                          // fallback when the hover button can't be reached
    id: 'idm-page-video',
    title: "Download this page's video with Utylix (up to 1080p)",
    contexts: ['page', 'video'],
    documentUrlPatterns: VIDEO_SITES,
  });
});

/** Send the video on a page (YouTube, Facebook, anime sites...) to the app: best quality up to 1080p, picture and sound in one MP4. */
async function downloadPageVideo(pageUrl, tabId, tabTitle) {
  if (!(await ensureApp())) throw new Error('Utylix is not running');
  if (!tools.ytdlp) throw new Error('video support is not installed (Utylix > Settings > Video sites)');
  const { info, source } = await resolveMedia(tabId, pageUrl, undefined, pageUrl);   // page first, then streams the page loaded
  // the best quality up to 1080p (its size gives the confirmation window an estimate); a bare stream is named after the tab
  const best = info.options.filter(o => /^h:\d+$/.test(o.id) && o.available && +o.id.slice(2) <= 1080).sort((a, b) => +b.id.slice(2) - +a.id.slice(2))[0];
  const title = source.url !== pageUrl && tabTitle ? tabTitle.slice(0, 150) : info.title;
  await callMediaSmart('/api/media/add', { url: source.url, referer: source.referer, option: 'h:1080', title, size: best ? best.size : 0, prompt: true });
}

chrome.contextMenus.onClicked.addListener(async (info, tab) => {
  if (info.menuItemId !== 'idm-download' && info.menuItemId !== 'idm-page-video') return;
  let url = info.linkUrl || info.srcUrl || '';
  const pageUrl = info.pageUrl || tab?.url || '';
  const fail = async why => {                                             // never fail silently: badge + popup log say why
    await note({ url: url || pageUrl }, 'right-click', 'nothing downloaded: ' + why);
    flashBadge('!', '#dc2626');
  };
  try {
    if (info.menuItemId === 'idm-page-video') {                           // "download this page's video"
      await downloadPageVideo(pageUrl, tab?.id, tab?.title);
      await note({ url: pageUrl }, 'right-click', 'captured -> video from this page sent to Utylix (up to 1080p)');
      return flashBadge('OK', '#16a34a');
    }
    if (!/^https?:/i.test(url)) {
      // Streamed videos give the page a temporary blob: address. Fall back to the media files this tab loaded.
      const found = tab?.id != null ? (await chrome.storage.session.get(mediaKey(tab.id)))[mediaKey(tab.id)] || [] : [];
      const media = found.filter(m => m.kind === 'media').sort((a, b) => (b.size || 0) - (a.size || 0));
      if (!media.length) {
        // no single file: a streaming site. If Utylix has its video support installed, let it fetch the page's video.
        if (await ensureApp() && tools.ytdlp && /^https?:/i.test(pageUrl)) {
          await downloadPageVideo(pageUrl, tab?.id, tab?.title);
          await note({ url: pageUrl }, 'right-click', 'captured -> streamed video sent to Utylix (up to 1080p)');
          return flashBadge('OK', '#16a34a');
        }
        return fail('this video is streamed in small pieces (blob/HLS/DASH). Install video support in Utylix > Settings to download sites like YouTube and Facebook');
      }
      url = media[0].url;
    }
    if (!(await ensureApp())) return fail('Utylix is not running');
    await sendToServer(url, { referer: info.pageUrl || tab?.url });
    await note({ url }, 'right-click', 'captured -> sent to Utylix');
    flashBadge('OK', '#16a34a');
  } catch (e) {
    await fail(e.message);
  }
});

// ---------- messages from the video button on web pages (content.js) ----------
chrome.runtime.onMessage.addListener((msg, sender, sendResponse) => {
  if (msg && msg.type === 'pip-hover') return false;                     // (answered by the Picture in Picture code below)
  (async () => {
    try {
      if (msg && msg.type === 'pip-hover') return;                       // (answered by the Picture in Picture code below)
      const tabId = sender.tab?.id;
      if (tabId == null) return sendResponse({ error: 'no tab' });

      if (msg.type === 'get-media') {                                    // what has this tab loaded that we can download?
        await serverAlive();                                             // also refreshes which video helpers are installed
        const found = (await chrome.storage.session.get(mediaKey(tabId)))[mediaKey(tabId)] || [];
        return sendResponse({
          media: found.filter(m => m.kind === 'media'),
          manifests: found.filter(m => m.kind === 'manifest').length,    // streams (HLS/DASH) the page loaded
          tools, videoButton: capture.video_button !== false,
        });
      }

      if (msg.type === 'media-info' || msg.type === 'media-download') {  // YouTube/Facebook/anime sites/...: yt-dlp inside the app
        if (typeof msg.url !== 'string' || !/^https?:/i.test(msg.url)) return sendResponse({ error: 'bad link' });
        if (!(await ensureApp())) return sendResponse({ error: 'Utylix is not running' });
        const referer = typeof msg.referer === 'string' && /^https?:/i.test(msg.referer) ? msg.referer : undefined;
        if (msg.type === 'media-info') {
          const { info, source } = await resolveMedia(tabId, msg.url, referer, sender.url);
          return sendResponse({ ok: true, info, source });
        }
        await callMediaSmart('/api/media/add', { url: msg.url, referer, option: msg.option, title: msg.title, size: msg.size, prompt: true });
        flashBadge('OK', '#16a34a');
        return sendResponse({ ok: true });
      }

      if (msg.type === 'download' || msg.type === 'download-all') {
        const items = msg.type === 'download' ? [msg] : (Array.isArray(msg.items) ? msg.items.slice(0, 20) : []);
        if (!items.length || !items.every(i => typeof i.url === 'string' && /^https?:/i.test(i.url))) {
          return sendResponse({ error: 'bad link' });
        }
        if (!(await ensureApp())) {
          flashBadge('!', '#dc2626');
          return sendResponse({ error: 'Utylix is not running' });
        }
        // one video -> show the "New download" window; "Download all" -> just start them all
        for (const i of items) {
          await sendToServer(i.url, { referer: sender.tab.url, filename: i.filename, prompt: msg.type === 'download' });
        }
        flashBadge('OK', '#16a34a');
        return sendResponse({ ok: true, count: items.length });
      }
      sendResponse({ error: 'unknown request' });
    } catch (e) {
      sendResponse({ error: e.message });
    }
  })();
  return true;                                                           // answer asynchronously
});

// ---------- sniff video/audio files and big images per tab (shown in the popup) ----------
const mediaKey = tabId => `media_${tabId}`;

// Streaming players fetch a video in many small pieces; those aren't downloadable files.
const STREAM_PIECE = /\.(m4s|ts|cmfv|cmfa)(\?|#|$)|[?&](range|bytestart|byterange|sq|rn)=|\/videoplayback|\/(seg|segment|chunk|frag|fragment)[-_]?\d+/i;

async function refreshBadge(tabId) {
  if (tabId == null || tabId < 0) return;
  const found = (await chrome.storage.session.get(mediaKey(tabId)))[mediaKey(tabId)] || [];
  const n = found.filter(m => m.kind === 'media').length;               // badge counts videos/audio files only
  chrome.action.setBadgeText({ tabId, text: n ? String(n) : '' });
  chrome.action.setBadgeBackgroundColor({ color: '#2563eb' });
}

chrome.webRequest.onHeadersReceived.addListener((d) => {
  if (d.tabId < 0 || (d.statusCode !== 200 && d.statusCode !== 206)) return;
  const h = n => d.responseHeaders.find(x => x.name.toLowerCase() === n)?.value || '';
  const type = h('content-type').toLowerCase();
  let kind = null;
  if (/mpegurl|dash\+xml/.test(type) || /\.(m3u8|mpd)(\?|#|$)/i.test(d.url)) kind = 'manifest';   // a stream's playlist
  else if (type.startsWith('video/') || type.startsWith('audio/') || (MEDIA_EXT.test(d.url) && !type.startsWith('text/'))) kind = 'media';
  else if (type.startsWith('image/') || (IMAGE_EXT.test(d.url) && !type.startsWith('text/'))) kind = 'image';
  if (!kind || /mp2t/.test(type)) return;                                // (MPEG-TS pieces are never shown)
  if (kind === 'manifest') {                                            // remembered so Utylix's video support can use it
    const key = mediaKey(d.tabId);
    serial(async () => {
      const found = (await chrome.storage.session.get(key))[key] || [];
      if (found.some(m => m.kind === 'manifest' && m.url === d.url)) return;
      found.push({ url: d.url, size: 0, type, kind });
      for (let extra = found.filter(m => m.kind === 'manifest').length - 10; extra > 0; extra--)
        found.splice(found.findIndex(m => m.kind === 'manifest'), 1);      // keep only the newest 10 streams
      await chrome.storage.session.set({ [key]: found });
    });
    return;
  }
  if (kind === 'media' && STREAM_PIECE.test(d.url)) return;              // one piece of a streamed video
  const range = h('content-range').match(/\/(\d+)$/);
  const size = range ? +range[1] : +h('content-length') || 0;
  if (kind === 'media' && size && size < 100 * 1024) return;            // ignore tiny UI sounds and fragments
  if (kind === 'image' && size < MIN_IMAGE_BYTES) return;               // unknown or small size: skip
  if (type.includes('svg')) return;

  const key = mediaKey(d.tabId);
  serial(async () => {                                                  // one update at a time, or bursts overwrite each other
    const found = (await chrome.storage.session.get(key))[key] || [];
    const clean = u => u.split('#')[0];
    if (found.some(m => clean(m.url) === clean(d.url)) || found.filter(m => m.kind === kind).length >= MAX_PER_KIND) return;
    found.push({ url: d.url, size, type, kind });
    await chrome.storage.session.set({ [key]: found });
    refreshBadge(d.tabId);
  });
}, { urls: ['<all_urls>'], types: ['media', 'xmlhttprequest', 'other', 'image'] }, ['responseHeaders']);

chrome.tabs.onUpdated.addListener((tabId, change) => {
  if (change.status === 'loading' && change.url) {                      // new page -> forget old items
    chrome.storage.session.remove(mediaKey(tabId));
    chrome.action.setBadgeText({ tabId, text: '' });
  }
});
chrome.tabs.onRemoved.addListener(tabId => chrome.storage.session.remove(mediaKey(tabId)));

// ---------- right-click a video -> Picture in Picture (not on YouTube, which has its own) ----------
const PIP_ID = 'utylix-pip';
const pipIsYouTube = url => /^https?:\/\/([^/]*\.)?(youtube\.com|youtube-nocookie\.com|youtu\.be)(\/|$)/i.test(url || '');
const pipQuiet = () => void chrome.runtime.lastError;                       // (ignore "already exists" and "tab is gone")

// ONE entry, for a right-click on a video and also on a video that a player covers with its own layer (there the browser shows
// the page menu). It is only visible while the pointer is over a video (the page tells us as the mouse moves), so it never
// clutters other menus and never shows twice.
function createPipMenu() {
  chrome.contextMenus.remove('utylix-pip-covered', pipQuiet);                // (two earlier entries: they made it appear twice)
  chrome.contextMenus.remove('utylix-pip-video', pipQuiet);
  chrome.contextMenus.create({
    id: PIP_ID, title: 'Picture in Picture', contexts: ['video', 'page', 'frame'], visible: false,
    documentUrlPatterns: ['http://*/*', 'https://*/*'],
  }, pipQuiet);
}
createPipMenu();
chrome.runtime.onInstalled.addListener(createPipMenu);

const pipOver = new Map();                                               // "tabId:frameId" -> the pointer is over a video there
const pipOverIn = tabId => [...pipOver.keys()].some(k => k.startsWith(tabId + ':'));

async function syncPipMenu() {
  try {
    const [tab] = await chrome.tabs.query({ active: true, lastFocusedWindow: true });
    if (!tab) return;
    chrome.contextMenus.update(PIP_ID, { visible: pipOverIn(tab.id) && !pipIsYouTube(tab.url) }, pipQuiet);
  } catch { /* no window right now */ }
}
chrome.tabs.onActivated.addListener(syncPipMenu);
chrome.windows.onFocusChanged.addListener(syncPipMenu);
chrome.tabs.onUpdated.addListener((tabId, change) => {
  if (change.status === 'loading') { for (const k of [...pipOver.keys()]) if (k.startsWith(tabId + ':')) pipOver.delete(k); }
  if (change.url || change.status === 'loading') syncPipMenu();
});
chrome.tabs.onRemoved.addListener(tabId => { for (const k of [...pipOver.keys()]) if (k.startsWith(tabId + ':')) pipOver.delete(k); });
syncPipMenu();

chrome.runtime.onMessage.addListener((msg, sender) => {
  if (!msg || msg.type !== 'pip-hover' || sender.tab?.id == null) return;
  const key = `${sender.tab.id}:${sender.frameId || 0}`;
  if (msg.over) pipOver.set(key, true); else pipOver.delete(key);
  if (sender.tab.active) chrome.contextMenus.update(PIP_ID, { visible: pipOverIn(sender.tab.id) && !pipIsYouTube(sender.tab.url) }, pipQuiet);
});

chrome.contextMenus.onClicked.addListener(async (info, tab) => {
  if (info.menuItemId !== PIP_ID || tab?.id == null) return;
  let result;
  try { result = await chrome.tabs.sendMessage(tab.id, { type: 'pip' }, { frameId: info.frameId || 0 }); }
  catch { result = { error: 'this page has to be reloaded once (the extension was updated)' }; }
  if (!result || !result.ok) {
    await note({ url: tab.url || '' }, 'right-click', 'Picture in Picture: ' + ((result && result.error) || 'no answer'));
    flashBadge('!', '#dc2626');
  }
});
