const SERVER = 'http://127.0.0.1:6800';
const $ = id => document.getElementById(id);

const fmt = n => {
  if (!n || n < 0) return '?';
  const u = ['B', 'KB', 'MB', 'GB']; let i = 0;
  while (n >= 1024 && i < u.length - 1) { n /= 1024; i++; }
  return n.toFixed(i ? 1 : 0) + ' ' + u[i];
};

function el(tag, cls, text) {
  const e = document.createElement(tag);
  if (cls) e.className = cls;
  if (text != null) e.textContent = text;   // textContent only: file names are untrusted
  return e;
}

async function post(path, body) {
  const r = await fetch(SERVER + path, {
    method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body),
  });
  if (!r.ok) throw new Error(String(r.status));
}

async function cookieHeader(url) {
  const cookies = await chrome.cookies.getAll({ url });
  return cookies.map(c => `${c.name}=${c.value}`).join('; ');
}

async function sendUrl(url, referer) {
  // prompt: the app shows its "New download" window (if enabled in its settings)
  await post('/api/add', { url, referer, prompt: true, cookie: await cookieHeader(url), user_agent: navigator.userAgent });
  refresh();
}

function fillList(box, found, tab) {
  if (!found.length) { box.replaceChildren(el('div', 'none', 'None detected yet.')); return; }
  box.replaceChildren(...found.map(m => {
    const item = el('div', 'item');
    let name = m.url;
    try { name = decodeURIComponent(new URL(m.url).pathname.split('/').pop()) || m.url; } catch {}
    const meta = el('div', 'm');
    const btn = el('button', 'p', 'Download');
    btn.onclick = async () => { btn.disabled = true; btn.textContent = 'Sent'; await sendUrl(m.url, tab.url).catch(() => { btn.textContent = 'Failed'; }); };
    meta.append(el('span', null, `${m.type.split(';')[0]} · ${fmt(m.size)}`), btn);
    item.append(el('div', 'n', name), meta);
    item.title = m.url;
    return item;
  }));
}

async function renderMedia() {
  const [tab] = await chrome.tabs.query({ active: true, currentWindow: true });
  const key = `media_${tab?.id}`;
  const found = (await chrome.storage.session.get(key))[key] || [];
  fillList($('media'), found.filter(m => m.kind === 'media'), tab);
  fillList($('images'), found.filter(m => m.kind === 'image'), tab);
}

async function renderLog() {
  const log = (await chrome.storage.session.get('capture_log')).capture_log || [];
  const box = $('log');
  if (!log.length) { box.replaceChildren(el('div', 'none', 'No downloads seen by the browser yet.')); return; }
  box.replaceChildren(...log.slice(0, 6).map(e => {
    const item = el('div', 'item');
    const ok = e.result.startsWith('captured');
    const time = new Date(e.t).toLocaleTimeString([], { hour: '2-digit', minute: '2-digit', second: '2-digit' });
    const meta = el('div', 'm');
    meta.append(el('span', null, `${e.result}`));
    meta.firstChild.style.color = ok ? 'var(--ok)' : 'var(--muted)';
    item.append(el('div', 'n', e.name), el('div', 'm', `${time} · ${e.host} · ${e.src}`), meta);
    return item;
  }));
}

async function refresh() {
  renderLog();
  let data;
  try {
    data = await (await fetch(SERVER + '/api/downloads', { signal: AbortSignal.timeout(1500) })).json();
  } catch {
    $('dot').classList.remove('on'); $('offline').hidden = false; return;
  }
  $('dot').classList.add('on'); $('offline').hidden = true;
  const recent = data.downloads.filter(d => d.status !== 'awaiting').sort((a, b) => b.created - a.created).slice(0, 5);
  $('recent').replaceChildren(...(recent.length ? recent.map(d => {
    const item = el('div', 'item');
    const pct = d.size > 0 ? Math.min(100, d.downloaded / d.size * 100) : (d.status === 'completed' ? 100 : 0);
    const track = el('div', 'track'), fill = el('div', 'fill');
    fill.style.width = pct + '%'; track.append(fill);
    const right = d.status === 'downloading' ? fmt(d.speed) + '/s' : d.status;
    const meta = el('div', 'm'); meta.append(el('span', null, `${pct.toFixed(0)}% of ${fmt(d.size)}`), el('span', null, right));
    item.append(el('div', 'n', d.filename), track, meta);
    return item;
  }) : [el('div', 'none', 'Nothing yet.')]));
}

(async () => {
  $('ver').textContent = 'v' + chrome.runtime.getManifest().version;
  const cfg = await chrome.storage.local.get({ enabled: true, videoPanel: true });
  $('enabled').checked = cfg.enabled;
  $('videoPanel').checked = cfg.videoPanel !== false;
  $('videoPanel').onchange = e => chrome.storage.local.set({ videoPanel: e.target.checked });
  $('enabled').onchange = e => chrome.storage.local.set({ enabled: e.target.checked });
  $('add').onclick = async () => {
    const url = $('url').value.trim();
    if (!/^https?:\/\//i.test(url)) return;
    try { await sendUrl(url); $('url').value = ''; } catch { $('offline').hidden = false; }
  };
  $('dash').onclick = async () => {              // bring the desktop window to the front
    try { await post('/api/show', { window: 'downloads' }); window.close(); } catch { $('offline').hidden = false; }
  };
  renderMedia();
  refresh();
  setInterval(refresh, 1000);
})();
