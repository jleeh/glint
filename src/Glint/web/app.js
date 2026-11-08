'use strict';
/* Glint dashboard — vanilla JS, no build step. Talks to the C# host via chrome.webview (or mock.js in a browser). */

const bridge = window.chrome?.webview ?? window.mockBridge;
const S = {
  init: null, cfg: null, state: null, hist: {}, page: 'overview',
  profile: null, rule: 0, curve: null, target: 'all',
  rgb: null, ledCountsDirty: false, lastPerf: null, selfCpu: 0,
  setup: null, setupChecking: false, testZone: null, testTimer: null,
};
const HIST = 150;
const INPUT_COLORS = ['#8b6cff', '#3fd0ff', '#ffb547', '#3ddc97', '#ff6a8a', '#c9a0ff'];
const $ = (s, el = document) => el.querySelector(s);
const $$ = (s, el = document) => [...el.querySelectorAll(s)];
const esc = s => String(s ?? '').replace(/[&<>"']/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
const clamp = (v, a, b) => Math.min(b, Math.max(a, v));
// Untrusted data (plugins, hand-edited config.json, hardware names) must never become markup. Text goes through esc();
// numbers that land in markup go through num() (a JSON string where a number belongs falls back to d); colours that land
// in a style attribute or a colour input go through color() (#rgb..#rrggbbaa only, so no `;`, quotes or url()).
const num = (v, d = 0) => { const n = typeof v === 'number' ? v : typeof v === 'string' && v.trim() !== '' ? Number(v) : NaN; return Number.isFinite(n) ? n : d; };
const color = (c, d = '#000000') => typeof c === 'string' && /^#(?:[0-9a-f]{3,4}|[0-9a-f]{6}|[0-9a-f]{8})$/i.test(c) ? c : d;
const own = (o, k) => o != null && Object.prototype.hasOwnProperty.call(o, k) ? o[k] : undefined;   // lookup tables keyed by untrusted ids
const send = m => bridge?.postMessage(m);

/* ------------------------------------------------------------------ naming */

const SENSOR_LABELS = { cpu: 'CPU', gpu: 'GPU', 'gpu.hotspot': 'GPU hotspot', liquid: 'Liquid', storage: 'Storage', ram: 'Memory' };
function sensorLabel(id) {
  if (!id) return '';
  const m = /^(max|min|avg)\((.*)\)$/.exec(id);
  if (m) return ({ max: 'Hottest of ', min: 'Coolest of ', avg: 'Average of ' })[m[1]] + m[2].split(',').map(x => sensorLabel(x.trim())).join(' / ');
  if (S.cfg?.sensorNames?.[id]) return S.cfg.sensorNames[id];
  if (own(SENSOR_LABELS, id)) return SENSOR_LABELS[id];
  let p = /^(\w+):probe(\d+)$/.exec(id); if (p) return `${hubName(p[1], true)} probe ${p[2]}`;
  p = /^(\w+):liquid$/.exec(id); if (p) return `${hubName(p[1], true)} liquid`;
  p = /^disk:(.*)$/.exec(id); if (p) return 'Drive ' + p[1].replace(/-/g, ' ');
  p = /^mb:(.*)$/.exec(id); if (p) return 'Board ' + p[1].replace('Temperature #', 'sensor ');
  return id;
}
// Friendly names for well-known device ids ([short, long] or one name); anything else uses the name the host reports.
const HUB_NAMES = {
  st: ['AIO', 'AIO · Commander ST'], xt: ['Case hub', 'Case fans · Commander Core XT'], cc: ['AIO', 'AIO · Commander Core'],
  mb: 'Motherboard', lnp: 'Lighting Node Pro', aura: 'Motherboard RGB', mystic: 'Motherboard RGB', ram: 'Memory',
};
function hubName(id, short) {
  const n = own(HUB_NAMES, id);
  if (n) return Array.isArray(n) ? n[short ? 0 : 1] : n;
  // graphics cards: "gpu", "gpu2"... (LHM fan outputs); the long name carries the card's model
  const gm = /^gpu(\d*)$/.exec(id);
  if (gm) {
    const label = 'Graphics card' + (gm[1] ? ' ' + gm[1] : '');
    const model = S.init?.fans?.find(f => f.id.startsWith(id + ':'))?.device;
    return short || !model ? label : `${label} · ${model}`;
  }
  return S.init?.deviceNames?.[id] ?? S.init?.hubs?.find(h => h.id === id)?.name ?? id;
}
function fanLabel(id) {
  if (S.cfg?.fanNames?.[id]) return S.cfg.fanNames[id];
  const [dev, f] = id.split(':');
  if (f === 'pump') return 'Pump';
  const n = /fan(\d+)/.exec(f)?.[1];
  if (dev === 'st' && n) return `Radiator ${n}`;
  if (dev === 'xt' && n) return `Case ${n}`;
  if (dev === 'mb') return 'Header ' + f.replace('Fan #', '');
  if (/^gpu\d*$/.test(dev)) return 'GPU ' + (f ?? '').replace(/^Fan/i, 'fan');
  if (n) return `Fan ${n}`;
  return id;
}
function zoneLabel(z) {
  const [dev, part = ''] = z.split(':');
  if (dev === 'st' || dev === 'xt' || S.init?.fans?.some(f => f.id === z)) return fanLabel(z);   // fan rings follow the fan's name
  if (dev === 'ram') return 'RAM stick ' + part.replace('dimm', '');
  if (dev === 'lnp') return 'Node channel ' + part.replace('ch', '');
  if (dev === 'aura') return part === 'board' ? 'Board LEDs' : 'ARGB header ' + part.replace('argb', '');
  if (dev === 'mystic') {
    if (part === 'onboard') return 'Board LEDs';
    const m = /^(jrainbow|jrgb|pipe)(\d)$/.exec(part);
    if (m) return ({ jrainbow: 'ARGB header ', jrgb: 'RGB header ', pipe: 'Light pipe ' })[m[1]] + m[2];
    if (part === 'jcorsair') return 'Corsair header';
  }
  const n = S.init?.zones?.find(q => q.id === z)?.name;
  return n && n !== z ? n : z;
}

/* ------------------------------------------------------------------ math */

function interp(pts, t) {
  if (!pts?.length) return 100;
  if (t <= pts[0][0]) return pts[0][1];
  for (let i = 1; i < pts.length; i++) if (t <= pts[i][0]) {
    const [t0, d0] = pts[i - 1], [t1, d1] = pts[i];
    return t1 === t0 ? d1 : d0 + (d1 - d0) * (t - t0) / (t1 - t0);
  }
  return pts[pts.length - 1][1];
}
function resolve(id, s) {
  const m = /^(max|min|avg)\((.*)\)$/.exec(id ?? '');
  if (m) {
    const v = m[2].split(',').map(x => resolve(x.trim(), s));
    if (v.some(x => x == null)) return null;
    return m[1] === 'max' ? Math.max(...v) : m[1] === 'min' ? Math.min(...v) : v.reduce((a, b) => a + b, 0) / v.length;
  }
  return s?.[id] ?? null;
}
const HEAT = [[25, [63, 208, 255]], [45, [61, 220, 151]], [65, [255, 181, 71]], [80, [255, 106, 61]], [92, [255, 61, 94]]];
function heat(t) {
  if (t == null) return '#6f7689';
  if (t <= HEAT[0][0]) return rgbStr(HEAT[0][1]);
  for (let i = 1; i < HEAT.length; i++) if (t <= HEAT[i][0]) {
    const k = (t - HEAT[i - 1][0]) / (HEAT[i][0] - HEAT[i - 1][0]);
    return rgbStr(HEAT[i - 1][1].map((c, j) => c + (HEAT[i][1][j] - c) * k));
  }
  return rgbStr(HEAT[HEAT.length - 1][1]);
}
const rgbStr = a => `rgb(${a.map(Math.round).join(',')})`;
const fmtT = v => v == null ? '–' : v.toFixed(v >= 100 ? 0 : 1);

/* ------------------------------------------------------------------ messaging */

let commitTimer = null, commitSend = null;
function commit(rerender) {
  clearTimeout(commitTimer);
  S.ovrLocalUntil = Date.now() + 2500; // local edits win over the host's (older) override list until it has caught up
  commitSend = () => {
    commitSend = null;
    send({ type: 'config', config: S.cfg });
    // a typed MQTT password is sent once, then forgotten: the page never holds or shows the stored one
    const ha = S.cfg.homeAssistant;
    if (ha && 'password' in ha) { S.init.haHasPassword = ha.password !== ''; delete ha.password; haPassUi(); }
  };
  commitTimer = setTimeout(commitSend, 140);
  if (rerender) renderPage(S.page);
}
// send a pending config edit now (before an operation that the host must see after it, like a rename)
function flushCommit() { if (commitSend) { clearTimeout(commitTimer); commitSend(); } }

bridge?.addEventListener('message', e => {
  const m = typeof e.data === 'string' ? JSON.parse(e.data) : e.data;
  if (m.type === 'init') {
    S.init = m; S.cfg = m.config;
    S.profile = S.cfg.activeProfile;
    renderAll();
  } else if (m.type === 'state') {
    S.state = m;
    for (const [k, v] of Object.entries(m.sensors)) (S.hist[k] ??= []).push(v), S.hist[k].length > HIST && S.hist[k].shift();
    if (S.lastPerf) {
      const dt = 1, dc = m.perf.cpuSeconds - S.lastPerf.cpuSeconds;
      S.selfCpu = Math.max(0, dc / dt / m.perf.cores * 100);
    }
    S.lastPerf = m.perf;
    const sig = Object.keys(m.sensors).sort().join('|');
    if (sig !== S.sensorSig) { S.sensorSig = sig; if (S.page === 'overview' || S.page === 'devices') renderPage(S.page); }
    // the host's state lags a profile switch made here by a moment: don't let it undo the switch before the config has been sent
    if (m.profile !== S.cfg.activeProfile && Date.now() >= (S.ovrLocalUntil ?? 0)) S.cfg.activeProfile = m.profile;
    syncOverrides(m.overrides);
    live();
  } else if (m.type === 'sensors') {
    S.sens = m.catalog;
    if (S.page === 'sensors') liveSensors();
    else if (S.page === 'overlay') ovCatalog();
  } else if (m.type === 'preview') {
    const bin = atob(m.rgb); const a = new Uint8Array(bin.length);
    for (let i = 0; i < bin.length; i++) a[i] = bin.charCodeAt(i);
    S.rgb = a;
    if (m.field) {
      const fb = atob(m.field), img = new ImageData(m.fw, m.fh);
      for (let i = 0, j = 0; i < m.fw * m.fh; i++, j += 3) { img.data[i * 4] = fb.charCodeAt(j); img.data[i * 4 + 1] = fb.charCodeAt(j + 1); img.data[i * 4 + 2] = fb.charCodeAt(j + 2); img.data[i * 4 + 3] = 255; }
      S.fieldCanvas ??= document.createElement('canvas');
      S.fieldCanvas.width = m.fw; S.fieldCanvas.height = m.fh;
      S.fieldCanvas.getContext('2d').putImageData(img, 0, 0);
    }
    drawStage(); drawStrip();
  } else if (m.type === 'plugins') {
    S.init.plugins = m.plugins;
    pluginBadge();
    if (['lighting', 'devices', 'plugins'].includes(S.page)) renderPage(S.page);
  } else if (m.type === 'layout') {
    S.cfg = m.config; S.init.placements = m.placements;
    if (S.page === 'lighting') renderLighting();
    toast('Layout auto-arranged: drag anything that is in the wrong place');
  } else if (m.type === 'docs') {
    navigator.clipboard?.writeText(m.text).then(() => toast('Plugin guide copied: paste it into your AI chat'), () => toast('Could not copy', 'error'));
  } else if (m.type === 'setup') {
    S.setup = m.report; S.setupChecking = false;
    setupBadge();
    if (S.page === 'setup') renderSetup();
    else if (S.page === 'overview') setupBanner();
  } else if (m.type === 'updateInfo') {
    S.updChecking = false;
    if (S.state) S.state.update = m.update;
    liveUpdate();
  } else if (m.type === 'updater') {
    if (S.state) S.state.install = m.install;
    liveUpdate();
  } else if (m.type === 'configChanged') {
    // a change made elsewhere (MCP, CLI, tray, Home Assistant). The page's own edits echo back too: keep local edits
    // for a moment, and never re-render under the user's typing (apply once they leave the field).
    if (Date.now() < (S.ovrLocalUntil ?? 0)) return;
    if (JSON.stringify(m.config) === JSON.stringify(S.cfg)) return;
    const apply = () => { S.cfg = m.config; if (!own(S.cfg.profiles, S.profile)) S.profile = S.cfg.activeProfile; renderProfiles(); renderPage(S.page); };
    const f = document.activeElement;
    if (f && f.closest?.('#content') && /^(INPUT|TEXTAREA|SELECT)$/.test(f.tagName)) {
      S.pendingCfg = apply;
      f.addEventListener('blur', () => { const fn = S.pendingCfg; S.pendingCfg = null; fn?.(); }, { once: true });
    } else apply();
  } else if (m.type === 'tuning') {
    S.tuning = m;
    if (S.page === 'tuning') liveTuning();
    if (S.pm) pmRender();
  } else if (m.type === 'tuningHistory') {
    tnHist.data = m.history;
    const last = Math.max(0, Math.ceil(num(m.history?.total) / tnHist.size) - 1);
    if (tnHist.page > last) { tnHist.page = last; tnSave(); tnAskHistory(); }       // tests were deleted: stay on a page that exists
    if (S.page === 'tuning') liveHistory();
  } else if (m.type === 'toast') toast(m.text, m.kind);
});

function toast(text, kind) {
  const t = document.createElement('div');
  t.className = 'toast ' + (kind ?? '');
  t.textContent = text;
  $('#toasts').append(t);
  setTimeout(() => t.remove(), 3500);
}

/* ------------------------------------------------------------------ shell */

const PAGES = {
  overview: ['Overview', 'Live temperatures, fans and lighting'],
  cooling: ['Cooling', 'Fan rules: any fans, driven by any mix of sensors'],
  sensors: ['Sensors', 'Every sensor on this PC, with the lowest and highest values seen'],
  lighting: ['Lighting', 'Effects across every RGB device'],
  devices: ['Devices', 'Detected hardware, names and LED counts'],
  plugins: ['Plugins', 'Add hardware support and effects with small JavaScript files'],
  overlay: ['Game overlay', 'FPS, temperatures and clocks on top of your games'],
  tuning: ['Tuning', 'AI overclocking for your GPU and CPU, with crash watch · off until you turn it on'],
  setup: ['Setup check', 'Is this PC set up right for Glint? Each problem comes with a fix'],
  settings: ['Settings', 'Safety, performance and files'],
};
$('#nav').addEventListener('click', e => {
  const b = e.target.closest('button[data-page]'); if (!b) return;
  go(b.dataset.page);
});
function go(page) {
  if (page !== 'devices' && S.testZone) testPatternOff();          // the LED-count test pattern only lives on the Devices page
  if (page === 'setup' && S.page !== 'setup') { S.setupChecking = true; send({ type: 'setupCheck' }); }
  if (page === 'sensors' && S.page !== 'sensors') send({ type: 'sensorsWanted', reset: true }); // min/max since the page opened
  S.page = page;
  $$('#nav button').forEach(b => { b.classList.toggle('active', b.dataset.page === page); b.toggleAttribute('aria-current', b.dataset.page === page); });
  $$('.page').forEach(p => p.classList.toggle('active', p.id === 'page-' + page));
  $('#page-title').textContent = PAGES[page][0];
  $('#page-sub').textContent = PAGES[page][1];
  send({ type: 'preview', on: page === 'lighting' || page === 'overview' });
  renderPage(page);
  $('#content').scrollTop = 0;
}

function renderAll() {
  renderProfiles();
  pluginBadge();
  go(S.page);
}
function renderPage(p) {
  if (!S.cfg) return;
  ({ overview: renderOverview, cooling: renderCooling, sensors: renderSensors, lighting: renderLighting, devices: renderDevices, plugins: renderPlugins, overlay: renderOverlay, tuning: renderTuning, setup: renderSetup, settings: renderSettings })[p]();
  live();
}
// The header switcher: segmented buttons while the names fit, a dropdown when there are many or long ones.
const PROFILE_SEG_MAX = { count: 6, chars: 52 };
function profilesFit(names) { return names.length <= PROFILE_SEG_MAX.count && names.reduce((n, p) => n + p.length, 0) <= PROFILE_SEG_MAX.chars; }
function switchProfile(name) {
  if (!own(S.cfg.profiles, name)) return;
  S.cfg.activeProfile = name; S.profile = name;
  renderProfiles(); commit(true);
  toast(`Fan profile: ${name}`);
}
function renderProfiles() {
  const names = Object.keys(S.cfg.profiles), seg = $('#profile-seg'), sel = $('#profile-sel'), fit = profilesFit(names);
  seg.hidden = !fit; sel.hidden = fit;
  seg.innerHTML = fit ? names.map(p => `<button data-p="${esc(p)}" class="${p === S.cfg.activeProfile ? 'on' : ''}">${esc(p)}</button>`).join('') : '';
  sel.innerHTML = fit ? '' : names.map(p => `<option value="${esc(p)}" ${p === S.cfg.activeProfile ? 'selected' : ''}>${esc(p)}</option>`).join('');
  $('#profile-manage').innerHTML = IC.sliders;
  seg.onclick = e => { const b = e.target.closest('button'); if (b) switchProfile(b.dataset.p); };
  sel.onchange = () => switchProfile(sel.value);
  $('#profile-manage').onclick = () => openProfileManager();
  if (S.pm) pmRender();
}

/* live updates that don't rebuild the page */
function live() {
  const s = S.state?.sensors ?? {};
  $('#mini-cpu').textContent = s.cpu != null ? fmtT(s.cpu) + '°' : '–';
  $('#mini-gpu').textContent = s.gpu != null ? fmtT(s.gpu) + '°' : '–';
  $('#mini-liquid').textContent = s.liquid != null ? fmtT(s.liquid) + '°' : '–';
  $('#mini-cpu').style.color = heat(s.cpu); $('#mini-gpu').style.color = heat(s.gpu); $('#mini-liquid').style.color = heat(s.liquid);
  if (S.state) {
    $('#mini-self-cpu').textContent = S.selfCpu.toFixed(2) + '%';
    $('#mini-self-mem').textContent = S.state.perf.privateMB + ' MB';
  }
  ({ overview: liveOverview, cooling: liveCooling, lighting: () => { }, devices: liveDevices, settings: liveSettings })[S.page]?.();
}

/* ------------------------------------------------------------------ icons */
const IC = {
  cpu: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8"><rect x="5" y="5" width="14" height="14" rx="2"/><rect x="9" y="9" width="6" height="6"/><path d="M9 2v3M15 2v3M9 19v3M15 19v3M19 9h3M19 15h3M2 9h3M2 15h3"/></svg>',
  gpu: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8"><rect x="2" y="6" width="20" height="12" rx="2"/><circle cx="9" cy="12" r="3"/><path d="M15 10h4M15 14h4"/></svg>',
  drop: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8"><path d="M12 2.7S5 10 5 15a7 7 0 0 0 14 0c0-5-7-12.3-7-12.3z"/></svg>',
  fan: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round"><circle cx="12" cy="12" r="1.6"/><path d="M12 10.4c0-3.5.8-6.4 3.3-6.4 1.8 0 2.6 1.7 1.8 3.3s-3.3 2.6-5.1 3.1M13.6 12c3.5 0 6.4.8 6.4 3.3 0 1.8-1.7 2.6-3.3 1.8s-2.6-3.3-3.1-5.1M12 13.6c0 3.5-.8 6.4-3.3 6.4-1.8 0-2.6-1.7-1.8-3.3s3.3-2.6 5.1-3.1M10.4 12C6.9 12 4 11.2 4 8.7 4 6.9 5.7 6.1 7.3 6.9s2.6 3.3 3.1 5.1"/></svg>',
  plus: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round"><path d="M12 5v14M5 12h14"/></svg>',
  x: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round"><path d="M6 6l12 12M18 6 6 18"/></svg>',
  sliders: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round"><path d="M4 7h9M17 7h3M4 17h3M11 17h9"/><circle cx="15" cy="7" r="2"/><circle cx="9" cy="17" r="2"/></svg>',
  copy: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8"><rect x="9" y="9" width="12" height="12" rx="2"/><path d="M5 15H4a2 2 0 0 1-2-2V4a2 2 0 0 1 2-2h9a2 2 0 0 1 2 2v1"/></svg>',
  trash: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round"><path d="M3 6h18M8 6V4h8v2M6 6l1 14h10l1-14"/></svg>',
  hub: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8"><rect x="3" y="6" width="18" height="12" rx="2"/><path d="M7 10h2M7 14h2M12 10h5M12 14h5"/></svg>',
  ram: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8"><rect x="2" y="7" width="20" height="10" rx="1"/><path d="M6 11v2M10 11v2M14 11v2M18 11v2M6 17v3M18 17v3"/></svg>',
  board: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8"><rect x="3" y="3" width="18" height="18" rx="2"/><rect x="7" y="7" width="6" height="6"/><path d="M16 7h2M16 11h2M7 17h11"/></svg>',
  strip: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8"><path d="M3 12h18"/><circle cx="6" cy="12" r="1.5"/><circle cx="12" cy="12" r="1.5"/><circle cx="18" cy="12" r="1.5"/></svg>',
  folder: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8"><path d="M3 7a2 2 0 0 1 2-2h4l2 2h8a2 2 0 0 1 2 2v8a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2z"/></svg>',
  keyboard: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round"><rect x="2" y="6" width="20" height="12" rx="2"/><path d="M6 10h.01M10 10h.01M14 10h.01M18 10h.01M7 14h10"/></svg>',
  mouse: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round"><rect x="6" y="3" width="12" height="18" rx="6"/><path d="M12 7v4"/></svg>',
  bolt: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linejoin="round"><path d="M13 2 4 14h7l-1 8 9-12h-7z"/></svg>',
};

/* ------------------------------------------------------------------ overview */

function sparkPath(arr, w, h, lo, hi) {
  if (!arr || arr.length < 2) return '';
  const n = arr.length, step = w / (n - 1), off = 0;
  const y = v => h - 6 - (v - lo) / Math.max(1, hi - lo) * (h - 14);
  return arr.map((v, i) => `${i ? 'L' : 'M'}${((off + i) * step).toFixed(1)},${y(v).toFixed(1)}`).join('');
}

function renderOverview() {
  const s = S.state?.sensors ?? {};
  const hero = [['cpu', 'CPU', IC.cpu], ['gpu', 'GPU', IC.gpu], ['liquid', 'Liquid', IC.drop]];
  const others = Object.keys(s).filter(k => !['cpu', 'gpu', 'liquid'].includes(k) && !(k.endsWith(':liquid') && s[k] === s.liquid) && !(k.startsWith('mb:') && (s[k] < 10 || s[k] > 110)));
  const rules = S.cfg.profiles[S.cfg.activeProfile] ?? [];
  $('#page-overview').innerHTML = `
    <div id="setup-banner"></div>
    <div class="grid temps-hero">
      ${hero.map(([k, n, ic]) => `
      <div class="card temp-tile" data-tile="${k}">
        <div class="t-label">${ic}${n}</div>
        <div class="t-value"><span data-v>–</span><small>°C</small></div>
        <div class="t-meta"><span data-min></span><span data-max></span></div>
        <svg class="spark" viewBox="0 0 300 64" preserveAspectRatio="none"><defs><linearGradient id="g-${k}" x1="0" x2="0" y1="0" y2="1"><stop offset="0" stop-opacity=".35"/><stop offset="1" stop-opacity="0"/></linearGradient></defs><path data-area fill="url(#g-${k})"/><path data-line fill="none" stroke-width="2"/></svg>
      </div>`).join('')}
    </div>
    <div class="section-title">Other sensors<span class="line"></span></div>
    <div class="grid temps-small">
      ${others.map(k => `<div class="card temp-small" data-small="${esc(k)}"><div class="v num">–</div><div class="k" title="${esc(k)}">${esc(sensorLabel(k))}</div></div>`).join('') || '<div class="muted">No other sensors</div>'}
    </div>
    <div class="section-title">Cooling · ${esc(S.cfg.activeProfile)}<span class="line"></span><span class="row" style="gap:8px;margin-right:10px"><span class="muted" style="font-size:12px">Fan control</span><div class="toggle ${S.cfg.fanControl !== false ? 'on' : ''}" data-fancontrol title="Off: fans go back to the BIOS, GPU driver and device firmware"></div></span><button class="btn ghost small" data-go="cooling">Edit curves →</button></div>
    ${S.cfg.fanControl === false ? fanOffNote() : ''}
    <div class="grid rules-grid">
      ${rules.map((r, i) => `
      <div class="card rule-card" data-rule="${i}">
        <div class="row between">
          <div><div class="card-title">${esc(r.name)}</div><div class="card-sub">${r.fans.length} fan${r.fans.length === 1 ? '' : 's'}</div></div>
          <div class="big num"><span data-duty>–</span><small>%</small></div>
        </div>
        <div style="margin-top:10px"><span class="why" data-why><span class="dot"></span><span></span></span></div>
        <div class="fan-rows">
          ${r.fans.map(f => `<div class="fan-row" data-fan="${esc(f)}"><span class="fan-icon">${IC.fan}</span><div><div class="name">${esc(fanLabel(f))}</div><div class="bar"><i></i></div></div><div class="rpm num">–</div></div>`).join('')}
        </div>
      </div>`).join('')}
    </div>
    <div class="section-title">Lighting<span class="line"></span><button class="btn ghost small" data-go="lighting">Open lighting →</button></div>
    <div class="card card-pad">
      <div class="row between" style="margin-bottom:14px">
        <div><div class="card-title">${esc(fxMeta(S.cfg.lighting.effect.type, S.cfg.lighting.effect).name)}</div><div class="card-sub">${S.cfg.lighting.enabled ? `Brightness ${num(S.cfg.lighting.brightness)}%` : 'Lights off'}${Object.keys(S.cfg.lighting.overrides).length ? ` · ${Object.keys(S.cfg.lighting.overrides).length} override(s)` : ''}</div></div>
        <div class="row"><span class="muted" style="font-size:12px">Lights</span><div class="toggle ${S.cfg.lighting.enabled ? 'on' : ''}" data-lights></div></div>
      </div>
      <canvas class="strip" id="ov-strip"></canvas>
      <div class="preset-list" style="margin-top:14px">
        ${Object.keys(S.cfg.lighting.presets).slice(0, 12).map(p => `<button class="chip" data-preset="${esc(p)}">${esc(p)}</button>`).join('')}
      </div>
    </div>`;
  setupBanner();
  const pg = $('#page-overview');
  pg.onclick = e => {
    const g = e.target.closest('[data-go]'); if (g) return go(g.dataset.go);
    const pr = e.target.closest('[data-preset]'); if (pr) return applyPreset(pr.dataset.preset, 'all');
    if (e.target.closest('[data-lights]')) { S.cfg.lighting.enabled = !S.cfg.lighting.enabled; commit(true); }
    if (e.target.closest('[data-fancontrol]')) { S.cfg.fanControl = S.cfg.fanControl === false; commit(true); toast(S.cfg.fanControl ? 'Fan control on' : 'Fan control off: fans are back on their own control'); }
    const rc = e.target.closest('[data-rule]'); if (rc) { S.profile = S.cfg.activeProfile; S.rule = +rc.dataset.rule; S.curve = null; go('cooling'); }
  };
}

function winner(rule, s) {
  if (rule.override != null) return { label: `Held at ${rule.override}% by override`, color: '#ffb547' };
  if (rule.fixed != null) return { label: `Fixed at ${rule.fixed}%`, color: '#8b6cff' };
  let best = null;
  rule.inputs.forEach((inp, i) => {
    const t = resolve(inp.sensor, s);
    const pts = inp.points ?? S.cfg.curves[inp.curve];
    const d = t == null ? (S.cfg.failsafeDuty ?? 100) : interp(pts, t);
    if (!best || d > best.d) best = { d, i, t, inp };
  });
  if (!best) return { label: 'No inputs', color: '#6f7689' };
  return { label: best.t == null ? `${sensorLabel(best.inp.sensor)} missing → fail-safe` : `${sensorLabel(best.inp.sensor)} ${fmtT(best.t)}° is driving`, color: INPUT_COLORS[best.i % INPUT_COLORS.length], i: best.i };
}

function liveOverview() {
  const st = S.state; if (!st) return;
  const s = st.sensors;
  $$('[data-tile]').forEach(el => {
    const k = el.dataset.tile, v = s[k], h = S.hist[k] ?? [], col = heat(v);
    $('[data-v]', el).textContent = fmtT(v);
    $('[data-v]', el).style.color = v == null ? '' : '#fff';
    if (h.length) {
      const lo = Math.min(...h), hi = Math.max(...h);
      $('[data-min]', el).textContent = `min ${fmtT(lo)}°`;
      $('[data-max]', el).textContent = `max ${fmtT(hi)}°`;
      const pad = Math.max(3, (hi - lo) * 0.25);
      const line = sparkPath(h, 300, 64, lo - pad, hi + pad);
      $('[data-line]', el).setAttribute('d', line);
      $('[data-line]', el).setAttribute('stroke', col);
      $('[data-area]', el).setAttribute('d', line ? `${line}L300,64L0,64Z` : '');
      $('stop', el).setAttribute('stop-color', col); $$('stop', el)[1].setAttribute('stop-color', col);
    }
    el.style.setProperty('--heat', col);
  });
  $$('[data-small]').forEach(el => {
    const v = s[el.dataset.small];
    $('.v', el).textContent = v == null ? '–' : fmtT(v) + '°';
    $('.v', el).style.color = heat(v);
  });
  const rules = S.cfg.profiles[S.cfg.activeProfile] ?? [];
  $$('[data-rule]').forEach(el => {
    const r = rules[+el.dataset.rule]; if (!r) return;
    const duties = r.fans.map(f => st.duty[f]).filter(x => x != null);
    $('[data-duty]', el).textContent = duties.length ? Math.max(...duties) : '–';
    const w = winner(r, s);
    $('[data-why] .dot', el).style.background = w.color;
    $('[data-why] span:last-child', el).textContent = w.label;
    $$('[data-fan]', el).forEach(fr => {
      const f = fr.dataset.fan, rpm = st.rpm[f], d = st.duty[f];
      $('.rpm', fr).textContent = rpm != null ? rpm + ' rpm' : '–';
      $('.bar i', fr).style.width = (d ?? 0) + '%';
      const ic = $('.fan-icon', fr);
      ic.classList.toggle('spin', rpm > 0);
      if (rpm > 0) ic.style.setProperty('--spd', (60 / Math.max(rpm, 1) * 40).toFixed(2) + 's');
    });
  });
}

/* ------------------------------------------------------------------ cooling */

function curUsers(name) {
  const u = [];
  for (const [p, rules] of Object.entries(S.cfg.profiles)) rules.forEach(r => r.inputs.forEach(i => { if (i.curve === name) u.push(`${r.name} (${p})`); }));
  return [...new Set(u)];
}
function allSensorIds() {
  const s = Object.keys(S.state?.sensors ?? {});
  const base = ['cpu', 'gpu', 'gpu.hotspot', 'liquid', 'max(cpu,gpu)', 'avg(cpu,gpu)'];
  const used = Object.values(S.cfg.profiles).flat().flatMap(r => r.inputs.map(i => i.sensor));
  return [...new Set([...base, ...s.filter(k => !(k.startsWith('mb:') && (S.state.sensors[k] < 10))), ...used])];
}
function fanGroups() {
  const g = {};
  for (const f of S.init.fans) (g[f.id.split(':')[0]] ??= []).push(f.id);
  return g;
}

const TEMPLATES = {
  Silent: [[30, 20], [55, 25], [70, 45], [82, 80], [90, 100]],
  Balanced: [[30, 25], [50, 35], [65, 55], [78, 85], [85, 100]],
  Performance: [[30, 40], [45, 50], [60, 70], [72, 90], [80, 100]],
  'Flat 50%': [[30, 50], [90, 50]],
};

// Shown while the global fan-control switch is off (overview + cooling page).
function fanOffNote(withButton) {
  return `<div class="card card-pad" data-fanoff style="margin-bottom:14px;border-left:3px solid var(--warn)">
    <div class="row between"><div><div class="card-title">Fan control is off</div>
    <div class="card-sub">Motherboard, cooler and GPU fans are on their BIOS / driver curves. Hubs that can't hand their fans back without their lighting stay on Glint's curves.</div></div>
    ${withButton ? '<button class="btn small" data-fancontrol-on>Turn fan control on</button>' : ''}</div></div>`;
}

function renderCooling() {
  const profiles = Object.keys(S.cfg.profiles);
  if (!profiles.includes(S.profile)) S.profile = S.cfg.activeProfile;
  const rules = S.cfg.profiles[S.profile];
  S.rule = clamp(S.rule, 0, Math.max(0, rules.length - 1));
  const r = rules[S.rule];
  if (r && !r.fixed && r.inputs.length && !r.inputs.some(i => i.curve === S.curve)) S.curve = r.inputs[0].curve;
  const isActive = S.profile === S.cfg.activeProfile;
  const groups = fanGroups();
  const owner = {}; rules.forEach((x, i) => x.fans.forEach(f => { if (i !== S.rule) owner[f] = x.name; }));
  const curveNames = Object.keys(S.cfg.curves);

  $('#page-cooling').innerHTML = `
  <div class="card card-pad row between" style="margin-bottom:14px;gap:12px">
    <div><div class="card-title">Fan control</div><div class="card-sub">On: Glint drives every fan below from your curves. Off: fans go back to the BIOS, GPU driver and device firmware.</div></div>
    <div class="toggle ${S.cfg.fanControl !== false ? 'on' : ''}" id="c-fancontrol" title="${S.cfg.fanControl !== false ? 'Turn fan control off' : 'Turn fan control on'}"></div>
  </div>
  ${S.cfg.fanControl === false ? fanOffNote() : ''}
  <div class="cool-layout">
    <div>
      <div class="field" style="margin-bottom:14px">
        <label>Profile</label>
        <div class="row">
          <select id="c-profile" style="flex:1">${profiles.map(p => `<option ${p === S.profile ? 'selected' : ''}>${esc(p)}</option>`).join('')}</select>
          <button class="icon-btn" id="c-dup" title="Duplicate profile">${IC.copy}</button>
        </div>
        ${isActive ? '<span class="muted" style="font-size:12px">● Active profile</span>' : `<button class="btn small" id="c-activate" style="align-self:flex-start">Make active</button>`}
      </div>
      <div class="rule-list">
        ${rules.map((x, i) => `
        <div class="card rule-item ${i === S.rule ? 'sel' : ''}" data-ri="${i}">
          <div class="ri-top"><span class="ri-name">${esc(x.name)}</span><span class="ri-duty num" data-rduty="${i}">–</span></div>
          <div class="ri-meta">
            <span class="tag">${x.fans.length} fan${x.fans.length === 1 ? '' : 's'}</span>
            ${x.override != null ? `<span class="tag ovr" title="Held by an override from Home Assistant or the API">Held ${esc(x.override)}%</span>` : ''}
            ${x.fixed != null ? `<span class="tag">Fixed ${esc(x.fixed)}%</span>` : x.inputs.map((inp, j) => `<span class="tag" style="color:${INPUT_COLORS[j % INPUT_COLORS.length]}">${esc(sensorLabel(inp.sensor))}</span>`).join('')}
          </div>
        </div>`).join('')}
        <button class="add-tile" id="c-add">${IC.plus.replace('<svg', '<svg width="14" height="14" style="vertical-align:-2px"')} New rule</button>
      </div>
    </div>

    ${!r ? '<div class="card empty">No rules in this profile yet.</div>' : `
    <div class="editor">
      <div class="card card-pad">
        <div class="row between" style="margin-bottom:14px">
          <input class="inline-edit" id="c-name" value="${esc(r.name)}" spellcheck="false" style="max-width:340px">
          <div class="row">
            <span class="muted" style="font-size:12px">Fixed speed</span>
            <div class="toggle ${r.fixed != null ? 'on' : ''}" id="c-fixed"></div>
            <button class="icon-btn danger" id="c-del" title="Delete rule">${IC.trash}</button>
          </div>
        </div>
        ${r.override != null ? `
          <div class="override-bar" id="c-ovr">
            <span class="ovr-dot"></span>
            <div class="ovr-text"><b>Held at ${esc(r.override)}% by override</b><span>Set from Home Assistant or the API. The curves below are paused; the fail-safe still wins if a sensor is lost.</span></div>
            <button class="btn small" id="c-auto">Back to auto</button>
          </div>` : ''}
        ${r.fixed != null ? `
          <div class="field"><label>Speed</label>${slider('c-fixedv', r.fixed, 0, 100, 1, v => v + '%')}</div>
        ` : `
          <div class="row between" style="margin-bottom:10px">
            <div class="label">Inputs: each sensor runs through its own curve</div>
            <div class="seg" id="c-combine">
              ${[['max', 'Hottest wins'], ['average', 'Average'], ['weighted', 'Weighted']].map(([k, n]) => `<button data-c="${k}" class="${(r.combine ?? 'max') === k ? 'on' : ''}">${n}</button>`).join('')}
            </div>
          </div>
          <div class="inputs" id="c-inputs">
            ${r.inputs.map((inp, j) => `
            <div class="input-row" data-ii="${j}">
              <span class="swatch" style="background:${INPUT_COLORS[j % INPUT_COLORS.length]}"></span>
              <select data-k="sensor">${allSensorIds().map(id => `<option value="${esc(id)}" ${id === inp.sensor ? 'selected' : ''}>${esc(sensorLabel(id))}</option>`).join('')}</select>
              <select data-k="curve">${curveNames.map(c => `<option ${c === inp.curve ? 'selected' : ''}>${esc(c)}</option>`).join('')}</select>
              <span class="live" data-live="${j}">–</span>
              <div class="adv">
                ${r.combine === 'weighted' ? `<label>Weight</label><input type="number" step="0.5" min="0" data-k="weight" value="${num(inp.weight, 1)}">` : ''}
                <label title="Seconds of smoothing applied to the sensor">Smooth</label><input type="number" step="1" min="0" data-k="smoothingSec" value="${num(inp.smoothingSec, 0)}">
                <label title="Degrees the temperature must drop before fans slow down">Hyst.</label><input type="number" step="0.5" min="0" data-k="hysteresis" value="${num(inp.hysteresis, 2)}">
                <button class="icon-btn" data-rm="${j}" title="Remove input">${IC.x}</button>
              </div>
            </div>`).join('')}
          </div>
          <div style="margin-top:10px"><button class="btn small" id="c-addin">${IC.plus} Add sensor</button></div>
        `}
      </div>

      ${r.fixed != null ? '' : `
      <div class="card card-pad">
        <div class="row between" style="margin-bottom:6px">
          <div>
            <div class="card-title">Curve · <select id="c-curve" style="font-weight:600">${curveNames.map(c => `<option ${c === S.curve ? 'selected' : ''}>${esc(c)}</option>`).join('')}</select></div>
            <div class="card-sub" style="margin-top:6px">Used by ${esc(curUsers(S.curve).join(', ') || 'nothing')} · drag points, double-click to add, right-click to remove</div>
          </div>
          <button class="btn small" id="c-newcurve" title="Give this input its own copy of the curve">${IC.copy} Make a copy</button>
        </div>
        <div class="tpl-row"><span class="label" style="align-self:center;margin-right:4px">Shape</span>${Object.keys(TEMPLATES).map(t => `<button class="chip" data-tpl="${esc(t)}">${esc(t)}</button>`).join('')}</div>
        <div class="curve-wrap"><svg class="curve" id="curve"></svg><div class="curve-tip" id="curve-tip"></div></div>
      </div>`}

      <div class="card card-pad">
        <div class="card-head"><div class="card-title">Fans</div><div class="card-sub">Pick which fans this rule drives</div></div>
        ${Object.entries(groups).map(([g, ids]) => `
          <div class="group-label">${esc(hubName(g))}</div>
          <div class="fan-pick">
            ${ids.map(f => `<div class="fan-chip ${r.fans.includes(f) ? 'on' : ''}" data-fan="${esc(f)}"><span class="box"></span><span>${esc(fanLabel(f))}${owner[f] ? `<span class="other">in ${esc(owner[f])}</span>` : ''}${g === 'mb' && !r.fans.includes(f) && !owner[f] ? '<span class="other">BIOS control</span>' : ''}${/^gpu\d*$/.test(g) && !r.fans.includes(f) && !owner[f] ? '<span class="other">Driver control</span>' : ''}</span></div>`).join('')}
          </div>`).join('')}
      </div>

      <div class="card card-pad">
        <div class="card-head"><div class="card-title">Limits &amp; response</div></div>
        <div class="grid" style="grid-template-columns:1fr 1fr; gap:18px 28px">
          <div class="field"><label>Minimum speed</label>${slider('c-min', r.minDuty, 0, 100, 1, v => v + '%')}</div>
          <div class="field"><label>Maximum speed</label>${slider('c-max', r.maxDuty, 0, 100, 1, v => v + '%')}</div>
          <div class="field"><label>Speed up rate</label>${slider('c-up', r.rampUpPerSec, 1, 100, 1, v => v + '%/s')}</div>
          <div class="field"><label>Slow down rate</label>${slider('c-down', r.rampDownPerSec, 1, 50, 1, v => v + '%/s')}</div>
        </div>
      </div>
    </div>`}
  </div>`;
  bindCooling(r, rules);
  if (r && r.fixed == null) drawCurve();
}

function slider(id, v, min, max, step, fmt) {
  // every value may come from config.json or a plugin's param schema: numbers only, the id escaped
  min = num(min, 0); max = num(max, min + 1); step = num(step, 1); v = num(v, min);
  const p = (v - min) / (max - min) * 100;
  return `<div class="slider"><input type="range" id="${esc(id)}" min="${min}" max="${max}" step="${step}" value="${v}" style="--p:${num(p)}%"><output for="${esc(id)}">${esc(fmt(v))}</output></div>`;
}
function bindSlider(id, fmt, onInput) {
  const el = document.getElementById(id); if (!el) return;
  el.addEventListener('input', () => {
    const v = +el.value;
    el.style.setProperty('--p', ((v - el.min) / (el.max - el.min) * 100) + '%');
    el.nextElementSibling.textContent = fmt(v);
    onInput(v);
  });
}

function bindCooling(r, rules) {
  const pg = $('#page-cooling');
  $('#c-fancontrol').onclick = () => { S.cfg.fanControl = S.cfg.fanControl === false; commit(true); toast(S.cfg.fanControl ? 'Fan control on' : 'Fan control off: fans are back on their own control'); };
  $('#c-profile').onchange = e => { S.profile = e.target.value; S.rule = 0; S.curve = null; renderCooling(); };
  $('#c-activate')?.addEventListener('click', () => { S.cfg.activeProfile = S.profile; renderProfiles(); commit(true); });
  $('#c-dup').onclick = () => {
    let n = S.profile + ' copy', i = 2; while (S.cfg.profiles[n]) n = `${S.profile} copy ${i++}`;
    S.cfg.profiles[n] = structuredClone(S.cfg.profiles[S.profile]); S.profile = n; renderProfiles(); commit(true);
  };
  $('#c-add').onclick = () => {
    rules.push({ name: 'New rule', fans: [], inputs: [{ sensor: 'cpu', curve: Object.keys(S.cfg.curves)[0], weight: 1, hysteresis: 2, smoothingSec: 0 }], combine: 'max', minDuty: 20, maxDuty: 100, rampUpPerSec: 25, rampDownPerSec: 4 });
    S.rule = rules.length - 1; commit(true);
  };
  pg.querySelectorAll('[data-ri]').forEach(el => el.onclick = () => { S.rule = +el.dataset.ri; S.curve = null; renderCooling(); live(); });
  if (!r) return;
  $('#c-name').oninput = e => { r.name = e.target.value; $(`[data-ri="${S.rule}"] .ri-name`).textContent = r.name; commit(); };
  $('#c-del').onclick = () => { rules.splice(S.rule, 1); S.rule = Math.max(0, S.rule - 1); commit(true); };
  $('#c-fixed').onclick = () => { r.fixed = r.fixed != null ? null : 50; commit(true); };
  $('#c-auto')?.addEventListener('click', () => { r.override = null; S.ovrLocalUntil = Date.now() + 3000; commit(true); toast(`${r.name}: back to automatic control`); });
  bindSlider('c-fixedv', v => v + '%', v => { r.fixed = v; commit(); });
  $('#c-combine')?.addEventListener('click', e => { const b = e.target.closest('button'); if (!b) return; r.combine = b.dataset.c; commit(true); });
  $('#c-inputs')?.addEventListener('change', e => {
    const row = e.target.closest('[data-ii]'); if (!row) return;
    const inp = r.inputs[+row.dataset.ii], k = e.target.dataset.k;
    inp[k] = e.target.type === 'number' ? +e.target.value : e.target.value;
    if (k === 'curve') S.curve = inp.curve;
    commit(k === 'sensor' || k === 'curve');
  });
  $('#c-inputs')?.addEventListener('click', e => {
    const rm = e.target.closest('[data-rm]');
    if (rm) { r.inputs.splice(+rm.dataset.rm, 1); commit(true); return; }
    const row = e.target.closest('[data-ii]');
    if (row && e.target.tagName !== 'SELECT' && e.target.tagName !== 'INPUT') { S.curve = r.inputs[+row.dataset.ii].curve; renderCooling(); live(); }
  });
  $('#c-addin')?.addEventListener('click', () => { r.inputs.push({ sensor: 'gpu', curve: S.curve ?? Object.keys(S.cfg.curves)[0], weight: 1, hysteresis: 2, smoothingSec: 0 }); commit(true); });
  $('#c-curve')?.addEventListener('change', e => { S.curve = e.target.value; renderCooling(); live(); });
  pg.querySelectorAll('[data-tpl]').forEach(b => b.onclick = () => { S.cfg.curves[S.curve] = structuredClone(TEMPLATES[b.dataset.tpl]); drawCurve(); commit(); });
  $('#c-newcurve')?.addEventListener('click', () => {
    let n = S.curve + '-copy', i = 2; while (S.cfg.curves[n]) n = `${S.curve}-copy${i++}`;
    S.cfg.curves[n] = structuredClone(S.cfg.curves[S.curve]);
    const inp = r.inputs.find(x => x.curve === S.curve); if (inp) inp.curve = n;
    S.curve = n; commit(true); toast(`Created ${n} for this input`);
  });
  pg.querySelectorAll('.fan-chip').forEach(el => el.onclick = () => {
    const f = el.dataset.fan;
    if (r.fans.includes(f)) r.fans = r.fans.filter(x => x !== f);
    else { rules.forEach(x => x.fans = x.fans.filter(y => y !== f)); r.fans.push(f); } // a fan belongs to one rule
    commit(true);
  });
  bindSlider('c-min', v => v + '%', v => { r.minDuty = v; commit(); });
  bindSlider('c-max', v => v + '%', v => { r.maxDuty = v; commit(); });
  bindSlider('c-up', v => v + '%/s', v => { r.rampUpPerSec = v; commit(); });
  bindSlider('c-down', v => v + '%/s', v => { r.rampDownPerSec = v; commit(); });
}

function liveCooling() {
  const st = S.state; if (!st) return;
  const rules = S.cfg.profiles[S.profile] ?? [];
  $$('[data-rduty]').forEach(el => {
    const r = rules[+el.dataset.rduty];
    const d = r?.fans.map(f => st.duty[f]).filter(x => x != null);
    el.textContent = d?.length ? Math.max(...d) + '%' : '–';
  });
  const r = rules[S.rule]; if (!r || r.fixed != null) return;
  const w = winner(r, st.sensors);
  $$('[data-live]').forEach(el => {
    const j = +el.dataset.live, inp = r.inputs[j]; if (!inp) return;
    const t = resolve(inp.sensor, st.sensors), pts = S.cfg.curves[inp.curve];
    el.innerHTML = t == null ? '<b style="color:var(--bad)">missing</b> → fail-safe' : `<b>${fmtT(t)}°</b> → ${Math.round(interp(pts, t))}%${w.i === j ? '<span class="crown">driving</span>' : ''}`;
    el.closest('.input-row').classList.toggle('win', w.i === j);
  });
  drawCurve(true);
}

/* curve editor */
const CX = { t0: 20, t1: 100, padL: 46, padR: 18, padT: 16, padB: 30 };
let drag = null;
function drawCurve(liveOnly) {
  const svg = $('#curve'); if (!svg) return;
  const r = (S.cfg.profiles[S.profile] ?? [])[S.rule]; if (!r) return;
  const W = svg.clientWidth || 800, H = svg.clientHeight || 360;
  svg.setAttribute('viewBox', `0 0 ${W} ${H}`);
  const pw = W - CX.padL - CX.padR, ph = H - CX.padT - CX.padB;
  const X = t => CX.padL + (t - CX.t0) / (CX.t1 - CX.t0) * pw;
  const Y = d => CX.padT + (1 - d / 100) * ph;
  const s = S.state?.sensors ?? {};
  const pts = S.cfg.curves[S.curve] ?? [];
  const path = p => p.length ? `M${X(CX.t0)},${Y(p[0][1])}` + p.map(q => `L${X(clamp(q[0], CX.t0, CX.t1))},${Y(q[1])}`).join('') + `L${X(CX.t1)},${Y(p[p.length - 1][1])}` : '';
  let g = '';
  g += `<defs><linearGradient id="cfill" x1="0" x2="0" y1="0" y2="1"><stop offset="0" stop-color="#8b6cff" stop-opacity=".32"/><stop offset="1" stop-color="#8b6cff" stop-opacity="0"/></linearGradient></defs>`;
  g += '<g class="grid">';
  for (let t = 20; t <= 100; t += 10) g += `<line x1="${X(t)}" x2="${X(t)}" y1="${CX.padT}" y2="${CX.padT + ph}"/>`;
  for (let d = 0; d <= 100; d += 20) g += `<line x1="${CX.padL}" x2="${CX.padL + pw}" y1="${Y(d)}" y2="${Y(d)}"/>`;
  g += '</g><g class="axis">';
  for (let t = 20; t <= 100; t += 10) g += `<text x="${X(t)}" y="${H - 8}" text-anchor="middle">${t}°</text>`;
  for (let d = 0; d <= 100; d += 20) g += `<text x="${CX.padL - 10}" y="${Y(d) + 4}" text-anchor="end">${d}%</text>`;
  g += '</g>';
  // min/max band
  g += `<rect x="${CX.padL}" y="${Y(r.minDuty)}" width="${pw}" height="${Y(0) - Y(r.minDuty)}" fill="rgba(255,255,255,0.025)"/>`;
  g += `<line x1="${CX.padL}" x2="${CX.padL + pw}" y1="${Y(r.minDuty)}" y2="${Y(r.minDuty)}" stroke="rgba(255,255,255,0.12)" stroke-dasharray="3 5"/>`;
  g += `<text x="${CX.padL + pw - 4}" y="${Y(r.minDuty) - 6}" text-anchor="end" fill="#6f7689" font-size="11">min ${esc(r.minDuty)}%</text>`;
  // other inputs' curves
  r.inputs.forEach((inp, j) => {
    if (inp.curve === S.curve) return;
    g += `<path d="${path(S.cfg.curves[inp.curve] ?? [])}" fill="none" stroke="${INPUT_COLORS[j % INPUT_COLORS.length]}" stroke-opacity=".35" stroke-width="1.6" stroke-dasharray="5 5"/>`;
  });
  // edited curve
  const editColor = INPUT_COLORS[Math.max(0, r.inputs.findIndex(i => i.curve === S.curve)) % INPUT_COLORS.length];
  const p = path(pts);
  g += `<path d="${p}L${X(CX.t1)},${Y(0)}L${X(CX.t0)},${Y(0)}Z" fill="url(#cfill)"/>`;
  g += `<path d="${p}" fill="none" stroke="${editColor}" stroke-width="2.6" stroke-linejoin="round"/>`;
  // live markers
  r.inputs.forEach((inp, j) => {
    const t = resolve(inp.sensor, s); if (t == null) return;
    const c = INPUT_COLORS[j % INPUT_COLORS.length], d = interp(S.cfg.curves[inp.curve] ?? [], t), x = X(clamp(t, CX.t0, CX.t1));
    g += `<line x1="${x}" x2="${x}" y1="${CX.padT}" y2="${CX.padT + ph}" stroke="${c}" stroke-opacity=".45" stroke-dasharray="2 4"/>`;
    g += `<circle cx="${x}" cy="${Y(d)}" r="6" fill="${c}" stroke="#0b0c11" stroke-width="2"/>`;
    g += `<text x="${x + 9}" y="${CX.padT + 12 + j * 15}" fill="${c}" font-size="11.5" font-weight="600">${esc(sensorLabel(inp.sensor))} ${fmtT(t)}°</text>`;
  });
  // handles
  pts.forEach((q, i) => {
    g += `<g class="handle" data-h="${i}"><circle cx="${X(q[0])}" cy="${Y(q[1])}" r="14" fill="transparent"/><circle cx="${X(q[0])}" cy="${Y(q[1])}" r="6.5" fill="#fff" stroke="${editColor}" stroke-width="2.5"/></g>`;
  });
  svg.innerHTML = g;
  if (liveOnly) return;

  const toData = ev => {
    const b = svg.getBoundingClientRect();
    const x = (ev.clientX - b.left) / b.width * W, y = (ev.clientY - b.top) / b.height * H;
    return [Math.round(CX.t0 + (x - CX.padL) / pw * (CX.t1 - CX.t0)), Math.round(clamp((1 - (y - CX.padT) / ph) * 100, 0, 100))];
  };
  const tip = $('#curve-tip');
  svg.onpointerdown = ev => {
    const h = ev.target.closest('[data-h]'); if (!h || ev.button !== 0) return;
    drag = +h.dataset.h; svg.setPointerCapture(ev.pointerId);
  };
  svg.onpointermove = ev => {
    const [t, d] = toData(ev);
    const b = svg.getBoundingClientRect();
    if (drag != null) {
      const pts = S.cfg.curves[S.curve];
      const lo = drag > 0 ? pts[drag - 1][0] + 1 : CX.t0, hi = drag < pts.length - 1 ? pts[drag + 1][0] - 1 : CX.t1;
      pts[drag] = [clamp(t, lo, hi), d];
      drawCurve(true);
      tip.style.display = 'block'; tip.style.left = (ev.clientX - b.left) + 'px'; tip.style.top = (ev.clientY - b.top) + 'px';
      tip.textContent = `${pts[drag][0]}° → ${d}%`;
      commit();
    } else if (t >= CX.t0 && t <= CX.t1) {
      tip.style.display = 'block'; tip.style.left = (ev.clientX - b.left) + 'px'; tip.style.top = (ev.clientY - b.top) + 'px';
      tip.textContent = `${t}° → ${Math.round(interp(S.cfg.curves[S.curve], t))}%`;
    }
  };
  svg.onpointerup = () => { drag = null; };
  svg.onpointerleave = () => { if (drag == null) tip.style.display = 'none'; };
  svg.ondblclick = ev => {
    const [t, d] = toData(ev); const pts = S.cfg.curves[S.curve];
    if (pts.some(q => q[0] === t)) return;
    pts.push([t, d]); pts.sort((a, b) => a[0] - b[0]); drawCurve(); commit();
  };
  svg.oncontextmenu = ev => {
    ev.preventDefault();
    const h = ev.target.closest('[data-h]'); const pts = S.cfg.curves[S.curve];
    if (h && pts.length > 2) { pts.splice(+h.dataset.h, 1); drawCurve(); commit(); }
  };
}
window.addEventListener('resize', () => { if (S.page === 'cooling') drawCurve(); if (S.page === 'lighting') layoutStage(); });

/* ------------------------------------------------------------------ lighting */

// Default palettes for effects that are built from several colours (used when the current palette has only one).
const EFFECT_DEFAULTS = { speed: 0.2, perZone: false, reverse: false, scale: 1, size: 0.3, angle: 0, offset: 0, density: 0.5 };
const MULTI_PALETTE = {
  gradient: ['#ff0040', '#8000ff', '#00a0ff'], colorWave: ['#ff0040', '#ffb000', '#00ff80', '#00a0ff'],
  spin: ['#00a0ff', '#ff00a0'], marquee: ['#ff0000', '#ffffff'], comet: ['#ff0040', '#ff8000', '#ffff00'],
  meteor: ['#00e0ff', '#8040ff', '#ffffff'], ripple: ['#00a0ff', '#ff0080', '#ffe000'],
  twinkle: ['#ffffff', '#80c0ff', '#ffd080'], rain: ['#0080ff', '#00ffd0'], strobe: ['#ffffff', '#ff0040'],
  temperature: ['#0060ff', '#00ff60', '#ff2000'], audio: ['#ff0040', '#ffb000', '#00ff80', '#00a0ff'],
  audioPulse: ['#ff0060', '#00d0ff', '#ffe000'], spectrum: ['#ff0000'], plasma: ['#ff0000'], fire: ['#ffffff'],
  wave: ['#ff0000'],
};
const FX = {
  Off: ['Off', 'All LEDs dark', 'th-off', []],
  Static: ['Static', 'One solid colour', 'th-static', ['colors']],
  Gradient: ['Gradient', 'Colours blended across the rig', 'th-gradient', ['colors', 'angle']],
  Breathing: ['Breathing', 'Slow fade in and out', 'th-breath', ['colors', 'speed']],
  Spectrum: ['Spectrum', 'Whole rig cycles hues', 'th-spectrum', ['speed', 'colors?']],
  Wave: ['Rainbow wave', 'Rainbow sweeping across', 'th-wave', ['speed', 'angle', 'scale', 'perZone', 'reverse']],
  ColorWave: ['Colour wave', 'Your palette sweeping across', 'th-cwave', ['colors', 'speed', 'angle', 'scale', 'perZone', 'reverse']],
  Spin: ['Spinner', 'Arc chasing around each fan', 'th-spin', ['colors', 'speed', 'size', 'offset', 'reverse']],
  Marquee: ['Marquee', 'Theatre-style chase', 'th-marquee', ['colors', 'speed', 'block', 'reverse']],
  Comet: ['Comet', 'Bright band sweeping across', 'th-comet', ['colors', 'speed', 'angle', 'size', 'perZone', 'reverse']],
  Meteor: ['Meteor rain', 'Streaks shooting across the PC', 'th-meteor', ['colors', 'speed', 'angle', 'density']],
  Ripple: ['Ripple', 'Rings spreading out from the CPU', 'th-ripple', ['colors', 'speed', 'size', 'scale', 'reverse']],
  Twinkle: ['Starfield', 'Sparkles that fade out', 'th-twinkle', ['colors', 'density', 'speed', 'background']],
  Fire: ['Fire', 'Flickering flames', 'th-fire', ['speed']],
  Plasma: ['Plasma', 'Flowing colour fields', 'th-plasma', ['speed', 'colors?']],
  Rain: ['Rain', 'Drops landing at random', 'th-rain', ['colors', 'density', 'speed', 'background']],
  Strobe: ['Strobe', 'Sharp flashes', 'th-strobe', ['colors', 'speed']],
  Temperature: ['Temperature', 'Colour follows a sensor', 'th-temp', ['sensor', 'range', 'colors', 'speed']],
  Audio: ['Audio spectrum', 'Bars follow your music', 'th-audio', ['colors', 'angle']],
  AudioPulse: ['Beat pulse', 'Flashes on the bass', 'th-pulse', ['colors', 'background']],
};
const fxKey = t => Object.keys(FX).find(k => k.toLowerCase() === String(t).toLowerCase()) ?? 'Static';
const pluginFx = name => (S.init?.plugins?.effects ?? []).find(e => e.name === name);
const fxMeta = (t, fx) => {
  if (String(t).toLowerCase() === 'plugin') {
    const p = pluginFx(fx?.plugin);
    return { name: fx?.plugin ?? 'Plugin', desc: p ? (p.description || 'Plugin effect') : 'Plugin not found: check the Devices page', th: '', params: ['plugin'], plugin: p };
  }
  const m = FX[fxKey(t)]; return { name: m[0], desc: m[1], th: m[2], params: m[3] };
};
const thumbStyle = cols => Array.isArray(cols) && cols.length ? `background:linear-gradient(135deg, ${cols.map(c => color(c)).join(', ')})` : '';

function targetEffect() {
  const L = S.cfg.lighting;
  return S.target === 'all' ? L.effect : (L.overrides[S.target] ?? null);
}
function editableEffect() {
  const L = S.cfg.lighting;
  if (S.target === 'all') return L.effect;
  return (L.overrides[S.target] ??= structuredClone(L.effect));
}
// Pick what the effect/preset controls edit. Picking the current selection again goes back to all lights.
function setTarget(t) {
  S.target = !t || t === S.target ? 'all' : t;
  if (S.target === 'all') S.selZone = null;
  renderLighting();
}
function applyPreset(name, target) {
  const fx = structuredClone(S.cfg.lighting.presets[name]);
  if (target === 'all') { S.cfg.lighting.effect = fx; S.cfg.lighting.overrides = {}; }
  else S.cfg.lighting.overrides[target] = fx;
  S.cfg.lighting.enabled = true;
  commit(true); toast(`Lighting: ${name}`);
}

function renderLighting() {
  const L = S.cfg.lighting;
  if (!S.init.zones.length) {
    $('#page-lighting').innerHTML = `<div class="card empty-state">
      <div class="es-ico">${IC.bolt}</div>
      <div class="card-title">No RGB devices found</div>
      <div class="muted">Glint lights anything a plugin supports. Check the Plugins page for problems, or add support for your hardware.</div>
      <div class="row" style="justify-content:center;gap:8px;margin-top:14px"><button class="btn small primary" data-go-plugins>Open plugins</button><button class="btn small ghost" data-go-devices>Devices</button></div>
    </div>`;
    $('#page-lighting [data-go-plugins]').onclick = () => go('plugins');
    $('#page-lighting [data-go-devices]').onclick = () => go('devices');
    return;
  }
  const devs = [...new Set(S.init.zones.map(z => z.dev))];
  const fx = targetEffect();
  const shown = fx ?? L.effect;
  const meta = fxMeta(shown.type, shown);
  const ovr = new Set(Object.keys(L.overrides));
  const tName = S.target === 'all' ? 'All lights' : devs.includes(S.target) ? hubName(S.target) : zoneLabel(S.target);

  $('#page-lighting').innerHTML = `
    <div class="layout-wrap">
      <div class="stage-col">
        <div class="stage-tools">
          <button class="btn small ${S.arrange ? 'primary' : ''}" id="lay-arrange">${S.arrange ? 'Done arranging' : 'Arrange layout'}</button>
          <button class="btn small ghost" id="lay-auto" title="Place every device from what it is (pump on the CPU, radiator fans on top, RAM by the CPU, peripherals on the desk)">Auto-arrange</button>
          ${S.arrange && S.selZone ? `<span class="tool-sep"></span>
            <button class="icon-btn" id="lay-rot-l" title="Rotate left">⟲</button><button class="icon-btn" id="lay-rot-r" title="Rotate right">⟳</button>
            <button class="btn small ghost" id="lay-reset" title="Put this zone back to its automatic position">Reset</button>` : ''}
          <span class="spacer"></span>
          <span class="muted" id="stage-hover" style="font-size:12px"></span>
          ${S.target !== 'all' ? `<button class="btn small" id="lay-identify" title="Flash it on the real hardware">${IC.bolt} Identify</button>` : ''}
        </div>
        <div class="stage ${S.arrange ? 'arranging' : ''}" id="stage"><canvas id="stage-c"></canvas></div>
        <div class="muted" style="font-size:12px">${S.arrange ? 'Drag to move · drag the corner to resize · arrow keys nudge · R rotates 90°' : 'Click a fan, strip or stick to give it its own effect · Arrange layout to match your PC'}</div>
      </div>
      <div class="card legend" id="legend">${legendHtml()}</div>
    </div>
    <div class="target-bar ${S.target !== 'all' ? 'scoped' : ''}">
      <span class="label">Editing</span>
      <button class="chip ${S.target === 'all' ? 'on' : ''}" data-t="all">All lights</button>
      ${devs.map(d => `<button class="chip ${S.target === d ? 'on' : ''}" data-t="${esc(d)}">${ovr.has(d) ? '<span class="dot" style="background:#ffb547"></span>' : ''}${esc(devName(d))}</button>`).join('')}
      ${S.target !== 'all' && !devs.includes(S.target) ? `<button class="chip on" data-t="${esc(S.target)}">${esc(zoneLabel(S.target))}</button>` : ''}
      ${S.target !== 'all' && ovr.has(S.target) ? `<button class="btn small ghost" id="l-reset">Follow global effect</button>` : ''}
      ${S.target !== 'all' && !fx ? `<span class="muted" style="font-size:12px">Following the global effect, pick one to override</span>` : ''}
      ${S.target !== 'all' ? `<span class="scope-note">Effects and presets now change only <b>${esc(tName)}</b></span><button class="btn small" id="l-all">✕ Back to all lights</button><span class="muted" style="font-size:12px">or press Esc</span>` : ''}
    </div>
    <div class="light-layout">
      <div class="card card-pad">
        <div class="card-head"><div class="card-title">Effects</div><div class="card-sub">for ${esc(tName)}</div>
          <div class="search small" style="margin-left:auto"><svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round"><circle cx="11" cy="11" r="7"/><path d="m20 20-3.5-3.5"/></svg><input type="text" id="fx-q" placeholder="Search effects" value="${esc(S.fxq ?? '')}" spellcheck="false"></div></div>
        <div class="fx-grid" id="fx-grid">
          ${Object.entries(FX).map(([k, m]) => `<button class="fx ${fx && fx.type !== 'plugin' && fxKey(fx.type) === k ? 'on' : ''}" data-fx="${k}"><div class="thumb ${m[2]}"></div><div class="meta"><b>${m[0]}</b><span>${m[1]}</span></div></button>`).join('')}
        </div>
        <div class="row between" style="margin:18px 0 10px"><div class="label">Plugin effects <span class="muted">${(S.init.plugins?.effects ?? []).length}</span></div><button class="btn small ghost" data-go-plugins>Manage plugins</button></div>
        <div class="fx-grid ${S.fxAll ? '' : 'capped'}" id="pfx-grid">
          ${(S.init.plugins?.effects ?? []).map(p => `<button class="fx ${fx && fx.type === 'plugin' && fx.plugin === p.name ? 'on' : ''}" data-pfx="${esc(p.name)}"><div class="thumb th-plugin" style="${thumbStyle(p.preview)}"></div><div class="meta"><b>${esc(p.name)}</b><span>${esc(p.description || 'Plugin')}</span></div></button>`).join('')}
          <button class="fx add-fx" data-go-plugins><div class="thumb th-add"></div><div class="meta"><b>Add your own</b><span>Write one, or ask an AI to</span></div></button>
        </div>
        ${(S.init.plugins?.effects ?? []).length > 11 ? `<button class="btn small ghost" id="pfx-more" style="margin-top:10px">${S.fxAll ? 'Show fewer' : `Show all ${(S.init.plugins?.effects ?? []).length}`}</button>` : ''}
      </div>
      <div class="panel-stack">
        <div class="card card-pad">
          <div class="card-head"><div class="card-title">${esc(meta.name)}</div><div class="card-sub">${esc(meta.desc)}</div></div>
          <div class="panel-stack" id="fx-params">${fxParams(shown, meta)}</div>
        </div>
        <div class="card card-pad">
          <div class="card-head"><div class="card-title">Presets</div><div class="card-sub">for ${esc(tName)}</div><button class="btn small ghost" id="l-save" style="margin-left:auto">${IC.plus} Save current</button></div>
          <div class="preset-list">${Object.keys(L.presets).map(p => `<button class="chip" data-preset="${esc(p)}">${esc(p)}</button>`).join('')}</div>
        </div>
        <div class="card card-pad">
          <div class="row between" style="margin-bottom:14px"><div class="card-title">All lighting</div><div class="toggle ${L.enabled ? 'on' : ''}" id="l-enabled"></div></div>
          <div class="field" style="margin-bottom:12px"><label>Brightness</label>${slider('l-bright', L.brightness, 0, 100, 1, v => v + '%')}</div>
          <div class="field"><label>Animation frame cap</label>${slider('l-fps', L.fps, 5, 60, 1, v => v + ' fps')}</div>
        </div>
      </div>
    </div>`;
  bindLighting();
  layoutStage();
}

function pluginParams(fx, meta) {
  const schema = meta.plugin?.params ?? {}, vals = fx.params ?? {}, out = [];
  const disabled = !targetEffect() ? 'style="opacity:.5;pointer-events:none"' : '';
  for (const [k, d] of Object.entries(schema)) {
    const v = vals[k] ?? d.default, label = esc(d.label ?? k);
    if (d.type === 'number') out.push(`<div class="field" ${disabled}><label>${label}</label>${slider('pp-' + k, v ?? d.min ?? 0, d.min ?? 0, d.max ?? 1, d.step ?? 0.01, x => (+x).toFixed(d.step >= 1 ? 0 : 2))}</div>`);
    else if (d.type === 'color') out.push(`<div class="field" ${disabled}><label>${label}</label><div class="swatches"><div class="sw" style="background:${color(v)}"><input type="color" data-pp="${esc(k)}" value="${color(v)}"></div></div></div>`);
    else if (d.type === 'colors') out.push(`<div class="field" ${disabled}><label>${label}</label><div class="swatches">${(Array.isArray(v) ? v : []).map((c, i) => `<div class="sw" style="background:${color(c)}"><input type="color" data-ppc="${esc(k)}" data-i="${i}" value="${color(c)}">${v.length > 1 ? `<span class="x" data-ppdel="${esc(k)}" data-i="${i}">×</span>` : ''}</div>`).join('')}<button class="sw add" data-ppadd="${esc(k)}">+</button></div></div>`);
    else if (d.type === 'bool') out.push(`<div class="row between" ${disabled}><span style="font-size:13px;color:var(--text-2)">${label}</span><div class="toggle ${v ? 'on' : ''}" data-ppb="${esc(k)}"></div></div>`);
    else if (d.type === 'select') out.push(`<div class="field" ${disabled}><label>${label}</label><select data-pps="${esc(k)}">${(Array.isArray(d.options) ? d.options : []).map(o => `<option ${o === v ? 'selected' : ''}>${esc(o)}</option>`).join('')}</select></div>`);
    else if (d.type === 'sensor') out.push(`<div class="field" ${disabled}><label>${label}</label><select data-pps="${esc(k)}">${allSensorIds().map(id => `<option value="${esc(id)}" ${id === v ? 'selected' : ''}>${esc(sensorLabel(id))}</option>`).join('')}</select></div>`);
  }
  if (!meta.plugin) out.push('<div class="muted" style="font-size:13px">This plugin isn\'t loaded. Check the Plugins card on the Devices page.</div>');
  else if (!out.length) out.push('<div class="muted" style="font-size:13px">No settings for this effect.</div>');
  return out.join('');
}

function fxParams(fx, meta) {
  if (meta.plugin !== undefined || String(fx.type).toLowerCase() === 'plugin') return pluginParams(fx, meta);
  const p = meta.params, out = [];
  const disabled = !targetEffect() ? 'style="opacity:.5;pointer-events:none"' : '';
  if (p.includes('colors') || p.includes('colors?')) {
    out.push(`<div class="field" ${disabled}><label>Colours${p.includes('colors?') ? ' (one colour = automatic rainbow)' : ''}</label><div class="swatches">
      ${fx.colors.map((c, i) => `<div class="sw" style="background:${color(c)}"><input type="color" value="${color(c)}" data-col="${i}">${fx.colors.length > 1 ? `<span class="x" data-delcol="${i}">×</span>` : ''}</div>`).join('')}
      ${fx.colors.length < 8 ? '<button class="sw add" id="fx-addcol">+</button>' : ''}
    </div></div>`);
  }
  const sl = (key, label, min, max, step, fmt) => `<div class="field" ${disabled}><label>${label}</label>${slider('fx-' + key, fx[key] ?? 0, min, max, step, fmt)}</div>`;
  if (p.includes('speed')) out.push(sl('speed', 'Speed', 0.02, 2, 0.01, v => (+v).toFixed(2) + '×'));
  if (p.includes('size')) out.push(sl('size', 'Length', 0.05, 1, 0.01, v => Math.round(v * 100) + '%'));
  if (p.includes('block')) out.push(sl('size', 'Block size', 1, 8, 1, v => v + ' LEDs'));
  if (p.includes('density')) out.push(sl('density', 'Density', 0.05, 2, 0.05, v => (+v).toFixed(2)));
  if (p.includes('scale')) out.push(sl('scale', 'Repeats', 0.5, 4, 0.1, v => (+v).toFixed(1) + '×'));
  if (p.includes('offset')) out.push(sl('offset', 'Stagger between fans', 0, 0.5, 0.01, v => Math.round(v * 100) + '%'));
  if (p.includes('angle')) out.push(sl('angle', 'Direction across your PC', 0, 345, 15, v => ({ 0: '→', 45: '↘', 90: '↓', 135: '↙', 180: '←', 225: '↖', 270: '↑', 315: '↗' })[v] ? `${({ 0: '→', 45: '↘', 90: '↓', 135: '↙', 180: '←', 225: '↖', 270: '↑', 315: '↗' })[v]} ${v}°` : v + '°'));
  if (p.includes('sensor')) out.push(`<div class="field" ${disabled}><label>Sensor</label><select id="fx-sensor">${allSensorIds().map(id => `<option value="${esc(id)}" ${id === fx.sensor ? 'selected' : ''}>${esc(sensorLabel(id))}</option>`).join('')}</select></div>`);
  if (p.includes('range')) {
    const r = Array.isArray(fx.range) ? fx.range : [40, 85];
    out.push(`<div class="row" ${disabled}><div class="field" style="flex:1"><label>Cool at</label>${slider('fx-r0', r[0], 20, 90, 1, v => v + '°')}</div><div class="field" style="flex:1"><label>Hot at</label>${slider('fx-r1', r[1], 30, 100, 1, v => v + '°')}</div></div>`);
  }
  if (p.includes('background')) out.push(`<div class="field" ${disabled}><label>Background</label><div class="swatches"><div class="sw" style="background:${color(fx.background)}"><input type="color" id="fx-bg" value="${color(fx.background)}"></div></div></div>`);
  const flags = [];
  if (p.includes('perZone')) flags.push(['perZone', 'Run around each fan instead of across the rig']);
  if (p.includes('reverse')) flags.push(['reverse', 'Reverse direction']);
  for (const [k, l] of flags) out.push(`<div class="row between" ${disabled}><span style="font-size:13px;color:var(--text-2)">${l}</span><div class="toggle ${fx[k] ? 'on' : ''}" data-flag="${k}"></div></div>`);
  if (!out.length) out.push('<div class="muted" style="font-size:13px">No settings for this effect.</div>');
  return out.join('');
}

function bindLighting() {
  const pg = $('#page-lighting');
  pg.querySelectorAll('[data-t]').forEach(b => b.onclick = () => setTarget(b.dataset.t === 'all' ? 'all' : b.dataset.t));
  $('#l-all')?.addEventListener('click', () => setTarget('all'));
  $('#lay-arrange').onclick = () => { S.arrange = !S.arrange; if (!S.arrange) S.selZone = null; renderLighting(); };
  $('#lay-auto').onclick = () => { if (confirm('Replace your layout with an automatic arrangement?')) send({ type: 'autoLayout' }); };
  $('#lay-identify')?.addEventListener('click', () => send({ type: 'identify', target: S.target }));
  const rot = d => { const p = { ...placement(S.selZone) }; p.rotation = (((p.rotation || 0) + d) % 360 + 360) % 360; (S.cfg.lighting.layout ??= {})[S.selZone] = p; commit(); drawStage(); };
  $('#lay-rot-l')?.addEventListener('click', () => rot(-15));
  $('#lay-rot-r')?.addEventListener('click', () => rot(15));
  $('#lay-reset')?.addEventListener('click', () => { delete S.cfg.lighting.layout[S.selZone]; commit(); drawStage(); });
  const lg = $('#legend');
  lg.onclick = e => {
    const idb = e.target.closest('[data-identify]'); if (idb) { send({ type: 'identify', target: idb.dataset.identify }); toast('Flashing ' + (idb.dataset.identify.includes(':') ? zoneLabel(idb.dataset.identify) : devName(idb.dataset.identify)) + ' on your PC'); return; }
    const z = e.target.closest('[data-lg-zone]'); if (z) { const t = z.dataset.lgZone; if (S.arrange && t !== S.target) S.selZone = t; setTarget(t); return; }
    const d = e.target.closest('[data-lg-target]'); if (d) setTarget(d.dataset.lgTarget);
  };
  lg.onmouseover = e => { const z = e.target.closest('[data-lg-zone]'); const id = z?.dataset.lgZone ?? null; if (id !== S.hoverZone) { S.hoverZone = id; drawStage(); } };
  $('#l-reset')?.addEventListener('click', () => { delete S.cfg.lighting.overrides[S.target]; commit(true); });
  $('#fx-grid').onclick = e => {
    const b = e.target.closest('[data-fx]'); if (!b) return;
    const fx = editableEffect();
    const type = b.dataset.fx.charAt(0).toLowerCase() + b.dataset.fx.slice(1);
    // An effect brings its own look: the colours/settings of its preset (or its default palette), never the
    // previous effect's colours. The user can still edit them afterwards.
    const preset = Object.values(S.cfg.lighting.presets).find(p => p.type === type);
    const fresh = preset ? structuredClone(preset)
      : { ...EFFECT_DEFAULTS, type, colors: [...(MULTI_PALETTE[type] ?? fx.colors.slice(0, 1))] };
    if (fresh.type === 'temperature') { fresh.sensor ??= 'cpu'; fresh.range ??= [40, 85]; }
    for (const k of Object.keys(fx)) delete fx[k];
    Object.assign(fx, fresh);
    S.cfg.lighting.enabled = true;
    commit(true);
  };
  pg.querySelectorAll('[data-pfx]').forEach(b => b.onclick = () => {
    const fx = editableEffect();
    fx.type = 'plugin'; fx.plugin = b.dataset.pfx; fx.params = {};
    S.cfg.lighting.enabled = true;
    commit(true);
  });
  pg.querySelectorAll('[data-go-plugins]').forEach(b => b.onclick = () => go('plugins'));
  $('#pfx-more')?.addEventListener('click', () => { S.fxAll = !S.fxAll; renderLighting(); });
  const fxFilter = () => {
    const q = (S.fxq ?? '').trim().toLowerCase();
    pg.querySelectorAll('.fx[data-fx], .fx[data-pfx]').forEach(el => { el.style.display = !q || el.textContent.toLowerCase().includes(q) ? '' : 'none'; });
    $('#pfx-grid')?.classList.toggle('capped', !S.fxAll && !q);
  };
  $('#fx-q').oninput = e => { S.fxq = e.target.value; fxFilter(); };
  fxFilter();
  pg.querySelectorAll('[data-preset]').forEach(b => b.onclick = () => applyPreset(b.dataset.preset, S.target));
  $('#l-save').onclick = () => {
    const name = prompt('Preset name', 'My preset'); if (!name) return;
    S.cfg.lighting.presets[name] = structuredClone(targetEffect() ?? S.cfg.lighting.effect); commit(true);
  };
  $('#l-enabled').onclick = () => { S.cfg.lighting.enabled = !S.cfg.lighting.enabled; commit(true); };
  bindSlider('l-bright', v => v + '%', v => { S.cfg.lighting.brightness = v; commit(); });
  bindSlider('l-fps', v => v + ' fps', v => { S.cfg.lighting.fps = v; commit(); });

  const params = $('#fx-params');
  const meta = fxMeta((targetEffect() ?? S.cfg.lighting.effect).type, targetEffect() ?? S.cfg.lighting.effect);
  for (const [k, d] of Object.entries(meta.plugin?.params ?? {}))
    if (d.type === 'number') bindSlider('pp-' + k, x => (+x).toFixed(d.step >= 1 ? 0 : 2), v => { const fx = editableEffect(); (fx.params ??= {})[k] = v; commit(); });
  const pp = fx => (fx.params ??= {});
  const ppDefault = k => structuredClone(meta.plugin?.params?.[k]?.default ?? []);
  params.addEventListener('input', e => {
    const el = e.target;
    if (el.dataset.pp) { pp(editableEffect())[el.dataset.pp] = el.value; el.parentElement.style.background = el.value; commit(); return; }
    if (el.dataset.ppc) { const f = editableEffect(), k = el.dataset.ppc; pp(f)[k] ??= ppDefault(k); f.params[k][+el.dataset.i] = el.value; el.parentElement.style.background = el.value; commit(); return; }
  });
  params.addEventListener('change', e => {
    const el = e.target;
    if (el.dataset.pps) { pp(editableEffect())[el.dataset.pps] = el.value; commit(); }
  });
  params.addEventListener('click', e => {
    const f = e.target.closest('[data-ppb]'); if (f) { const fx = editableEffect(), k = f.dataset.ppb; pp(fx)[k] = !(fx.params[k] ?? meta.plugin?.params?.[k]?.default); commit(true); return; }
    const a = e.target.closest('[data-ppadd]'); if (a) { const fx = editableEffect(), k = a.dataset.ppadd; pp(fx)[k] ??= ppDefault(k); fx.params[k].push(fx.params[k][fx.params[k].length - 1] ?? '#ffffff'); commit(true); return; }
    const d = e.target.closest('[data-ppdel]'); if (d) { e.preventDefault(); const fx = editableEffect(), k = d.dataset.ppdel; pp(fx)[k] ??= ppDefault(k); fx.params[k].splice(+d.dataset.i, 1); commit(true); }
  });
  params.addEventListener('input', e => {
    const fx = editableEffect(), el = e.target;
    if (el.dataset.col != null) { fx.colors[+el.dataset.col] = el.value; el.parentElement.style.background = el.value; }
    else if (el.id === 'fx-bg') { fx.background = el.value; el.parentElement.style.background = el.value; }
    commit();
  });
  params.addEventListener('click', e => {
    const fx = editableEffect();
    if (e.target.id === 'fx-addcol') { fx.colors.push(fx.colors[fx.colors.length - 1] ?? '#ffffff'); commit(true); }
    const d = e.target.closest('[data-delcol]'); if (d) { e.preventDefault(); fx.colors.splice(+d.dataset.delcol, 1); commit(true); }
    const f = e.target.closest('[data-flag]'); if (f) { fx[f.dataset.flag] = !fx[f.dataset.flag]; f.classList.toggle('on'); commit(); }
  });
  $('#fx-sensor')?.addEventListener('change', e => { editableEffect().sensor = e.target.value; commit(); });
  for (const k of ['speed', 'size', 'density', 'scale', 'offset', 'angle']) {
    const el = document.getElementById('fx-' + k); if (!el) continue;
    bindSlider('fx-' + k, v => el.nextElementSibling.textContent, v => { editableEffect()[k] = v; commit(); });
    // keep the original formatter: re-run the template's formatting
    el.addEventListener('input', () => { el.nextElementSibling.textContent = fmtParam(k, +el.value); });
  }
  bindSlider('fx-r0', v => v + '°', v => { const fx = editableEffect(); fx.range = [v, (fx.range ?? [40, 85])[1]]; commit(); });
  bindSlider('fx-r1', v => v + '°', v => { const fx = editableEffect(); fx.range = [(fx.range ?? [40, 85])[0], v]; commit(); });
}
function fmtParam(k, v) {
  const t = fxKey(targetEffect()?.type ?? S.cfg.lighting.effect.type);
  if (k === 'angle') return ({ 0: '→ ', 45: '↘ ', 90: '↓ ', 135: '↙ ', 180: '← ', 225: '↖ ', 270: '↑ ', 315: '↗ ' })[v] ?? '' + v + '°';
  return k === 'speed' ? v.toFixed(2) + '×' : k === 'size' ? (t === 'Marquee' ? v + ' LEDs' : Math.round(v * 100) + '%') : k === 'scale' ? v.toFixed(1) + '×' : k === 'offset' ? Math.round(v * 100) + '%' : v.toFixed(2);
}

/* ---------- layout editor: every zone placed on a 16:10 canvas of the PC (case + desk) ---------- */
const DEV_COLORS = ['#8b6cff', '#3fd0ff', '#ffb547', '#3ddc97', '#ff6a8a', '#c9a0ff', '#6cf0ff', '#ffd966', '#ff9a5c'];
function devColor(dev) { const ids = [...new Set(S.init.zones.map(z => z.dev))]; return DEV_COLORS[Math.max(0, ids.indexOf(dev)) % DEV_COLORS.length]; }
function devName(dev) { return S.init.deviceNames?.[dev] ?? hubName(dev); }
function zoneName(z) { return zoneLabel(z.id); }
function placement(id) { return S.cfg.lighting.layout?.[id] ?? S.init.placements?.[id] ?? { x: 0.1, y: 0.1, w: 0.08, h: 0.128, rotation: 0 }; }

// same maths as Core/Layout.cs Positions()
function ledPositions(p, z) {
  const A = S.init.aspect ?? 1.6, n = z.count, pts = [];
  const cx = (p.x + p.w / 2) * A, cy = p.y + p.h / 2, w = p.w * A, h = p.h, r = (p.rotation || 0) * Math.PI / 180, c = Math.cos(r), s = Math.sin(r);
  const put = (lx, ly) => [(cx + lx * c - ly * s) / A, cy + lx * s + ly * c];
  if (z.kind === 'ring') { const R = Math.min(w, h) / 2 * 0.85; for (let i = 0; i < n; i++) { const a = 2 * Math.PI * i / n - Math.PI / 2; pts.push(put(Math.cos(a) * R, Math.sin(a) * R)); } }
  else if (z.kind === 'matrix' && z.cols > 0) { const rows = Math.ceil(n / z.cols); for (let i = 0; i < n; i++) { const fx = z.cols === 1 ? .5 : (i % z.cols) / (z.cols - 1), fy = rows === 1 ? .5 : Math.floor(i / z.cols) / (rows - 1); pts.push(put((fx - .5) * w * .92, (fy - .5) * h * .85)); } }
  else if (z.kind === 'single') { for (let i = 0; i < n; i++) pts.push(put(n === 1 ? 0 : (i / (n - 1) - .5) * w * .6, 0)); }
  else { const v = h > w, len = (v ? h : w) * .94; for (let i = 0; i < n; i++) { const f = n === 1 ? 0 : i / (n - 1) - .5; pts.push(v ? put(0, f * len) : put(f * len, 0)); } }
  return pts;
}

let stageGeo = null;  // { W, H, ox, oy, scale } canvas pixel mapping
function layoutStage() {
  const c = $('#stage-c'); if (!c) return;
  const box = c.parentElement, W = box.clientWidth, A = S.init.aspect ?? 1.6;
  const H = Math.round(Math.min(W / A, 520));
  const cw = Math.round(H * A) > W ? W : Math.round(H * A), ch = Math.round(cw / A);
  box.style.height = (ch + 2) + 'px';
  const dpr = devicePixelRatio || 1;
  c.style.width = cw + 'px'; c.style.height = ch + 'px'; c.width = cw * dpr; c.height = ch * dpr;
  stageGeo = { W: cw, H: ch };
  bindStage(c);
  drawStage();
}
const toPx = (x, y) => [x * stageGeo.W, y * stageGeo.H];
function zoneAt(px, py) {
  for (const z of [...S.init.zones].reverse()) {
    const p = placement(z.id), pad = 6;
    const [x0, y0] = toPx(p.x, p.y), [x1, y1] = toPx(p.x + p.w, p.y + p.h);
    if (px >= x0 - pad && px <= x1 + pad && py >= y0 - pad && py <= y1 + pad) return z;
  }
  return null;
}
function bindStage(c) {
  let drag = null;
  const pos = ev => { const b = c.getBoundingClientRect(); return [ev.clientX - b.left, ev.clientY - b.top]; };
  c.onpointermove = ev => {
    const [px, py] = pos(ev);
    if (drag) {
      const p = drag.p, dx = (px - drag.px) / stageGeo.W, dy = (py - drag.py) / stageGeo.H, snap = v => Math.round(v / 0.005) * 0.005;
      if (drag.mode === 'move') { p.x = clamp(snap(drag.ox + dx), 0, 1 - p.w); p.y = clamp(snap(drag.oy + dy), 0, 1 - p.h); }
      else {
        p.w = clamp(snap(drag.ow + dx), 0.012, 0.9); p.h = clamp(snap(drag.oh + dy), 0.012, 0.9);
        if (drag.z.kind === 'ring') p.h = p.w * (S.init.aspect ?? 1.6);       // fans stay round
      }
      (S.cfg.lighting.layout ??= {})[drag.z.id] = p;
      drawStage(); return;
    }
    const z = zoneAt(px, py);
    $('#stage-hover').textContent = z ? `${devName(z.dev)} · ${zoneName(z)}${S.cfg.lighting.overrides[z.id] ? ' · own effect' : ''}` : '';
    if (S.arrange && z) {
      const p = placement(z.id), [x1, y1] = toPx(p.x + p.w, p.y + p.h);
      c.style.cursor = Math.abs(px - x1) < 10 && Math.abs(py - y1) < 10 ? 'nwse-resize' : 'move';
    } else c.style.cursor = z ? 'pointer' : 'default';
    if (z?.id !== S.hoverZone) { S.hoverZone = z?.id; highlightLegend(); drawStage(); }
  };
  c.onpointerdown = ev => {
    const [px, py] = pos(ev), z = zoneAt(px, py);
    if (!S.arrange) { setTarget(z ? z.id : 'all'); return; }   // empty space or the selected zone again → all lights
    if (!z) { setTarget('all'); return; }
    const p = { ...placement(z.id) }, [x1, y1] = toPx(p.x + p.w, p.y + p.h);
    drag = { z, p, px, py, ox: p.x, oy: p.y, ow: p.w, oh: p.h, mode: Math.abs(px - x1) < 10 && Math.abs(py - y1) < 10 ? 'resize' : 'move' };
    c.setPointerCapture(ev.pointerId);
    if (S.selZone !== z.id) { S.selZone = z.id; S.target = z.id; }
  };
  c.onpointerup = () => { if (drag) { drag = null; commit(); renderLighting(); } };
  c.onpointerleave = () => { if (!drag && S.hoverZone) { S.hoverZone = null; highlightLegend(); drawStage(); } };
}
document.addEventListener('keydown', e => {
  if (e.key === 'Escape' && S.page === 'lighting' && S.target !== 'all' && !e.target.closest('input,select,textarea')) { e.preventDefault(); setTarget('all'); return; }
  if (S.page !== 'lighting' || !S.arrange || !S.selZone || e.target.closest('input,select')) return;
  const p = { ...placement(S.selZone) }, d = e.shiftKey ? 0.02 : 0.005;
  const k = { ArrowLeft: () => p.x -= d, ArrowRight: () => p.x += d, ArrowUp: () => p.y -= d, ArrowDown: () => p.y += d, r: () => p.rotation = ((p.rotation || 0) + 90) % 360, R: () => p.rotation = ((p.rotation || 0) + 90) % 360 }[e.key];
  if (!k) return;
  e.preventDefault(); k();
  p.x = clamp(p.x, 0, 1 - p.w); p.y = clamp(p.y, 0, 1 - p.h);
  (S.cfg.lighting.layout ??= {})[S.selZone] = p; commit(); drawStage();
});

let glowLayer = null;
function drawStage() {
  const c = $('#stage-c'); if (!c || S.page !== 'lighting' || !stageGeo) return;
  const dpr = devicePixelRatio || 1, W = stageGeo.W, H = stageGeo.H, a = S.rgb;
  if (!glowLayer || glowLayer.width !== c.width) { glowLayer = document.createElement('canvas'); glowLayer.width = c.width; glowLayer.height = c.height; }
  const L = glowLayer.getContext('2d'), g = c.getContext('2d');
  L.setTransform(dpr, 0, 0, dpr, 0, 0); L.clearRect(0, 0, W, H);
  g.setTransform(dpr, 0, 0, dpr, 0, 0); g.clearRect(0, 0, W, H);
  // scenery: case and desk
  const rr = (x, y, w, h, r) => { g.beginPath(); g.roundRect(x, y, w, h, r); };
  g.fillStyle = '#0d0f16'; rr(W * .008, H * .012, W * .752, H * .815, 14); g.fill();
  g.strokeStyle = 'rgba(255,255,255,0.08)'; g.lineWidth = 1.5; g.stroke();
  g.fillStyle = '#0b0c12'; rr(W * .008, H * .845, W * .984, H * .14, 10); g.fill(); g.strokeStyle = 'rgba(255,255,255,0.05)'; g.stroke();
  g.fillStyle = 'rgba(255,255,255,0.14)'; g.font = '600 10.5px Segoe UI, sans-serif';
  g.fillText('CASE', W * .02, H * .05); g.fillText('DESK', W * .02, H * .875);
  // the effect painted across the whole canvas: every LED takes its colour from where it sits
  if (S.fieldCanvas) {
    g.save(); g.globalAlpha = 0.28; g.imageSmoothingEnabled = true;
    g.beginPath(); g.roundRect(W * .008, H * .012, W * .752, H * .815, 14); g.roundRect(W * .008, H * .845, W * .984, H * .14, 10); g.clip();
    g.drawImage(S.fieldCanvas, 0, 0, W, H); g.restore();
  }
  // motherboard hint
  g.strokeStyle = 'rgba(255,255,255,0.05)'; g.setLineDash([4, 5]); rr(W * .30, H * .16, W * .40, H * .52, 8); g.stroke(); g.setLineDash([]);
  const col = i => a && i * 3 + 2 < a.length ? `rgb(${a[i * 3]},${a[i * 3 + 1]},${a[i * 3 + 2]})` : '#2a2d38';
  const placed = [];   // label boxes already drawn (simple collision avoidance)
  for (const z of S.init.zones) {
    const p = placement(z.id), [x0, y0] = toPx(p.x, p.y), w = p.w * W, h = p.h * H;
    const sel = S.target === z.id || S.target === z.dev || S.selZone === z.id, hov = S.hoverZone === z.id, dc = devColor(z.dev);
    // body
    g.save(); g.translate(x0 + w / 2, y0 + h / 2); g.rotate((p.rotation || 0) * Math.PI / 180);
    g.fillStyle = '#12141c'; g.strokeStyle = sel ? '#fff' : hov ? dc : dc + '66'; g.lineWidth = sel ? 2 : 1.5;
    if (z.kind === 'ring') { g.beginPath(); g.arc(0, 0, Math.min(w, h) / 2, 0, Math.PI * 2); g.fill(); g.stroke(); }
    else { rr(-w / 2, -h / 2, w, h, Math.min(w, h) / 2.5); g.fill(); g.stroke(); }
    g.restore();
    // LEDs
    const pts = ledPositions(p, z), dot = z.kind === 'ring' ? Math.max(2, Math.min(4.5, w / 12)) : z.kind === 'matrix' ? 2.4 : Math.max(1.6, Math.min(3.4, (z.kind === 'stick' ? h : Math.max(w, h)) / z.count / 1.6));
    for (let i = 0; i < pts.length; i++) { L.fillStyle = col(z.start + i); L.beginPath(); L.arc(pts[i][0] * W, pts[i][1] * H, dot, 0, Math.PI * 2); L.fill(); }
    // label
    const label = zoneName(z);
    g.font = `${sel || hov ? 600 : 500} 10.5px Segoe UI, sans-serif`; g.textAlign = 'center';
    const tw = g.measureText(label).width;
    let ly = y0 + h + 13;
    const box = () => [x0 + w / 2 - tw / 2 - 4, ly - 10, tw + 8, 14];
    const hits = b => placed.some(q => b[0] < q[0] + q[2] && q[0] < b[0] + b[2] && b[1] < q[1] + q[3] && q[1] < b[1] + b[3]);
    for (let k = 0; k < 4 && hits(box()); k++) ly += 14;
    placed.push(box());
    g.fillStyle = 'rgba(11,12,17,0.8)'; g.fillRect(...box());
    g.fillStyle = sel ? '#fff' : hov ? dc : 'rgba(220,224,235,0.78)'; g.fillText(label, x0 + w / 2, ly);
    g.textAlign = 'left';
    if (S.arrange && sel) { g.fillStyle = '#fff'; g.fillRect(x0 + w - 4, y0 + h - 4, 8, 8); }
  }
  // glow + crisp LEDs
  g.setTransform(1, 0, 0, 1, 0, 0); g.globalCompositeOperation = 'lighter';
  g.filter = `blur(${6 * dpr}px)`; g.globalAlpha = .9; g.drawImage(glowLayer, 0, 0);
  g.filter = 'none'; g.globalAlpha = 1; g.drawImage(glowLayer, 0, 0);
  g.globalCompositeOperation = 'source-over';
}

function legendHtml() {
  const devs = [...new Set(S.init.zones.map(z => z.dev))];
  return `<div class="card-head" style="margin-bottom:8px"><div class="card-title">Devices</div><div class="card-sub">${S.init.zones.length} zones</div></div>` +
    devs.map(d => `<div class="lg-dev">
      <div class="lg-head"><span class="lg-dot" style="background:${devColor(d)}"></span><span class="lg-name" data-lg-target="${esc(d)}">${esc(devName(d))}</span>
        <button class="icon-btn" data-identify="${esc(d)}" title="Flash on the real hardware">${IC.bolt}</button></div>
      ${S.init.zones.filter(z => z.dev === d).map(z => `<div class="lg-zone ${S.target === z.id ? 'on' : ''}" data-lg-zone="${esc(z.id)}"><span>${esc(zoneName(z))}</span><span class="muted num">${esc(z.count)}</span>
        <button class="icon-btn small" data-identify="${esc(z.id)}" title="Flash on the real hardware">${IC.bolt}</button></div>`).join('')}
    </div>`).join('');
}
function highlightLegend() { $$('.lg-zone').forEach(el => el.classList.toggle('hover', el.dataset.lgZone === S.hoverZone)); }

function drawStrip() {
  const c = $('#ov-strip'); if (!c || S.page !== 'overview' || !S.rgb) return;
  const W = c.clientWidth, H = c.clientHeight, dpr = devicePixelRatio || 1;
  if (c.width !== W * dpr) { c.width = W * dpr; c.height = H * dpr; }
  const g = c.getContext('2d'); g.setTransform(dpr, 0, 0, dpr, 0, 0); g.clearRect(0, 0, W, H);
  const zones = S.init.zones, gap = 6, n = zones.length, w = (W - gap * (n + 1)) / n;
  zones.forEach((z, k) => {
    const x0 = gap + k * (w + gap), lw = w / z.count;
    for (let i = 0; i < z.count; i++) {
      const j = (z.start + i) * 3; g.fillStyle = `rgb(${S.rgb[j]},${S.rgb[j + 1]},${S.rgb[j + 2]})`;
      g.fillRect(x0 + i * lw, 10, Math.max(1, lw + 0.3), H - 20);
    }
  });
}

/* ------------------------------------------------------------------ devices */

function renderDevices() {
  const I = S.init, L = S.cfg.lighting;
  const hubFans = id => I.fans.filter(f => f.id.startsWith(id + ':'));
  const probes = id => Object.keys(S.state?.sensors ?? {}).filter(k => k.startsWith(id + ':'));
  const category = id => I.zones.find(z => z.dev === id && z.category && z.category !== 'case')?.category;
  const devIcon = id => ({ st: IC.drop, cc: IC.drop, xt: IC.hub, lnp: IC.strip, aura: IC.board, mystic: IC.board, ram: IC.ram, mb: IC.board })[id] ?? ({ keyboard: IC.keyboard, mouse: IC.mouse })[category(id)] ?? IC.hub;
  // ports that can't report their length: the user sets the LED count
  const editable = z => /^(lnp|aura):(ch|argb)|^mystic:(jrainbow|jcorsair)/.test(z.id) || z.id in (L.ledCounts ?? {});
  const cards = [];
  for (const h of I.hubs) {
    const light = I.devices.find(d => d.id === h.id);
    cards.push(`
    <div class="card card-pad dev-card">
      <div class="dev-head"><div class="dev-ico">${devIcon(h.id)}</div><div style="flex:1"><div class="card-title">${esc(hubName(h.id))}</div><div class="card-sub">${h.firmware ? `Firmware ${esc(h.firmware)} · ` : ''}${hubFans(h.id).length} fan channels${light ? ` · ${light.zones.reduce((a, z) => a + num(z.leds), 0)} LEDs` : ''}</div></div><span class="status-dot" title="Connected"></span></div>
      <div class="group-label">Fan names</div>
      <div class="kv">${hubFans(h.id).map(f => `<span class="k">${esc(f.id.split(':')[1])} <span class="muted num" data-rpm="${esc(f.id)}"></span></span><input type="text" data-fanname="${esc(f.id)}" value="${esc(S.cfg.fanNames[f.id] ?? '')}" placeholder="${esc(fanLabel(f.id))}">`).join('')}</div>
      ${probes(h.id).length ? `<div class="group-label">Temperature probes</div><div class="kv">${probes(h.id).map(p => `<span class="k">${esc(p.split(':')[1])} <span class="muted num" data-temp="${esc(p)}"></span></span><input type="text" data-sensorname="${esc(p)}" value="${esc(S.cfg.sensorNames?.[p] ?? '')}" placeholder="${esc(sensorLabel(p))}">`).join('')}</div>` : ''}
    </div>`);
  }
  for (const d of I.devices.filter(d => !I.hubs.some(h => h.id === d.id))) {
    cards.push(`
    <div class="card card-pad dev-card">
      <div class="dev-head"><div class="dev-ico">${devIcon(d.id)}</div><div style="flex:1"><div class="card-title">${esc(hubName(d.id))}</div><div class="card-sub">${hubName(d.id) !== d.name ? esc(d.name) + ' · ' : ''}${d.zones.length} zone${d.zones.length === 1 ? '' : 's'} · ${d.zones.reduce((a, z) => a + num(L.ledCounts[z.id] ?? z.leds), 0)} LEDs</div></div><span class="status-dot"></span></div>
      <table class="t">${d.zones.map(z => `<tr><td>${esc(zoneLabel(z.id))}</td><td>${editable(z) ? `<input type="number" min="0" max="300" data-leds="${esc(z.id)}" value="${num(L.ledCounts[z.id] ?? z.leds)}"> <span class="muted">LEDs</span>${tpNote(z)}` : `<span class="muted">${esc(z.leds)} LED${z.leds === 1 ? '' : 's'}</span>`}</td>${editable(z) ? `<td><div class="tp-row"><span class="tp-label">Test pattern</span><div class="toggle ${S.testZone === z.id ? 'on' : ''}" data-tp="${esc(z.id)}" title="Test pattern: white first LED, red last LED, a new colour every 8 LEDs"></div></div></td>` : '<td></td>'}</tr>`).join('')}</table>
      ${d.zones.some(editable) ? '<div class="muted" style="font-size:12px;margin-top:10px">These ports can\'t report their length. Set the real LED count; use 0 for an empty header. Switch on <b>Test pattern</b>: the first LED turns white, the last one red, and the colour changes every 8 LEDs (one fan). The count is right when the red LED is the last one lit.</div>' : ''}
    </div>`);
  }
  // graphics cards (built in via LibreHardwareMonitor): one card per GPU with fans Glint can drive
  for (const g of [...new Set(I.fans.filter(f => /^gpu\d*:/.test(f.id)).map(f => f.id.split(':')[0]))]) {
    const fans = I.fans.filter(f => f.id.startsWith(g + ':'));
    const rule = (S.cfg.profiles[S.cfg.activeProfile] ?? []).find(r => fans.some(f => r.fans.includes(f.id)));
    cards.push(`
    <div class="card card-pad dev-card" data-gpucard="${esc(g)}">
      <div class="dev-head"><div class="dev-ico">${IC.gpu}</div><div style="flex:1"><div class="card-title">${esc(hubName(g, true))}</div><div class="card-sub">${esc(fans[0].device)} · ${fans.length} fan${fans.length === 1 ? '' : 's'} · ${rule ? `driven by “${esc(rule.name)}”` : 'on the GPU driver\'s own curve (not in a rule)'}</div></div><span class="status-dot" title="Built in"></span></div>
      <div class="group-label">Fan names</div>
      <div class="kv">${fans.map(f => `<span class="k">${esc(f.id.split(':')[1])} <span class="muted num" data-rpm="${esc(f.id)}"></span></span><input type="text" data-fanname="${esc(f.id)}" value="${esc(S.cfg.fanNames[f.id] ?? '')}" placeholder="${esc(fanLabel(f.id))}">`).join('')}</div>
      <div class="muted" style="font-size:12px;margin-top:10px">Built in (switch off under Plugins → Graphics card fans). Fans not in a rule stay on the driver; if another app such as MSI Afterburner takes a fan over, Glint hands it back and says so on the Setup page.</div>
    </div>`);
  }
  const mb = I.fans.filter(f => f.id.startsWith('mb:'));
  if (mb.length) cards.push(`
    <div class="card card-pad dev-card">
      <div class="dev-head"><div class="dev-ico">${IC.board}</div><div style="flex:1"><div class="card-title">Motherboard fan headers</div><div class="card-sub">${mb.length} headers · unassigned headers stay on BIOS control</div></div><span class="status-dot"></span></div>
      <div class="kv">${mb.map(f => `<span class="k">${esc(f.id.slice(3))} <span class="muted num" data-rpm="${esc(f.id)}"></span></span><input type="text" data-fanname="${esc(f.id)}" value="${esc(S.cfg.fanNames[f.id] ?? '')}" placeholder="${esc(fanLabel(f.id))}">`).join('')}</div>
    </div>`);

  const PL = I.plugins?.list ?? [];
  const bad = PL.filter(x => !x.ok && x.enabled).length, appr = PL.filter(x => x.needsApproval).length;
  cards.unshift(`
    <div class="card card-pad dev-card" style="grid-column:1/-1">
      <div class="dev-head" style="margin:0"><div class="dev-ico">${IC.plus}</div><div style="flex:1"><div class="card-title">Plugins</div>
        <div class="card-sub">${PL.length} installed · ${PL.filter(x => x.kind === 'device' || x.kind === 'smbus').length} hardware · ${PL.filter(x => x.kind === 'effect').length} effects${bad ? ` · <span style="color:var(--bad)">${bad} with problems</span>` : ''}${appr ? ` · <span style="color:var(--warn)">${appr} need approval</span>` : ''}</div></div>
        <button class="btn small" data-go="plugins">Manage plugins →</button></div>
    </div>`);
  $('#page-devices').innerHTML = `
    <div class="banner ${S.ledCountsDirty ? '' : 'hidden'}" id="restart-banner">${IC.bolt.replace('<svg', '<svg width="18" height="18"')}<span>LED counts changed. The test pattern already uses the new count; Glint needs a quick restart for the effects and the device to use it.</span><span class="spacer"></span><button class="btn small primary" id="restart">Restart Glint</button></div>
    <div class="grid dev-grid">${cards.join('')}</div>`;
  const pg = $('#page-devices');
  pg.oninput = e => {
    const el = e.target;
    if (el.dataset.fanname != null) { if (el.value.trim()) S.cfg.fanNames[el.dataset.fanname] = el.value.trim(); else delete S.cfg.fanNames[el.dataset.fanname]; commit(); }
    if (el.dataset.sensorname != null) { S.cfg.sensorNames ??= {}; if (el.value.trim()) S.cfg.sensorNames[el.dataset.sensorname] = el.value.trim(); else delete S.cfg.sensorNames[el.dataset.sensorname]; commit(); }
    if (el.dataset.leds != null) {
      const id = el.dataset.leds;
      L.ledCounts[id] = clamp(+el.value || 0, 0, 300); S.ledCountsDirty = true; $('#restart-banner').classList.remove('hidden'); commit();
      if (S.testZone === id) testPatternOn(id);                       // live: the pattern follows the typed count
      const z = S.init.zones.find(x => x.id === id), note = pg.querySelector(`[data-tp-note="${CSS.escape(id)}"]`);
      if (z && note) note.outerHTML = tpNote(z);
    }
  };
  pg.onclick = e => {
    const t = e.target.closest('[data-tp]'); if (!t) return;
    if (S.testZone === t.dataset.tp) testPatternOff(); else testPatternOn(t.dataset.tp);
    $$('#page-devices [data-tp]').forEach(x => x.classList.toggle('on', x.dataset.tp === S.testZone));
  };
  $('#restart').onclick = () => { send({ type: 'config', config: S.cfg }); setTimeout(() => send({ type: 'restart' }), 300); };
  pg.querySelectorAll('[data-go]').forEach(b => b.onclick = () => go(b.dataset.go));
}
/* ARGB LED-count test pattern: the host paints the zone (white first, red last, a colour per 8 LEDs) for up to 60 s */
const TP_SECONDS = 60;
function tpCount(id) { const z = S.init.zones.find(x => x.id === id); return S.cfg.lighting.ledCounts?.[id] ?? z?.count ?? 0; }
function tpNote(z) {
  const n = S.cfg.lighting.ledCounts?.[z.id] ?? z.count;
  return `<div class="tp-note" data-tp-note="${esc(z.id)}"${n > z.count ? '' : ' hidden'}>Only ${esc(z.count)} LEDs exist until Glint restarts</div>`;
}
function testPatternOn(id) {
  S.testZone = id;
  send({ type: 'testPattern', zone: id, count: tpCount(id), on: true });
  clearTimeout(S.testTimer);
  S.testTimer = setTimeout(() => { testPatternOff(); $$('#page-devices [data-tp]').forEach(x => x.classList.remove('on')); }, TP_SECONDS * 1000);
}
function testPatternOff() {
  if (!S.testZone) return;
  send({ type: 'testPattern', zone: S.testZone, on: false });
  S.testZone = null; clearTimeout(S.testTimer);
}
function liveDevices() {
  const st = S.state; if (!st) return;
  $$('[data-rpm]').forEach(el => { const v = st.rpm[el.dataset.rpm]; el.textContent = v ? `${v} rpm` : ''; });
  $$('[data-temp]').forEach(el => { const v = st.sensors[el.dataset.temp]; el.textContent = v != null ? fmtT(v) + '°' : ''; });
}

/* ------------------------------------------------------------------ plugins */

const PF = { q: '', f: 'all' };
const KIND = { effect: ['Effect', '#8b6cff'], device: ['USB device', '#3fd0ff'], smbus: ['SMBus', '#ffb547'], '?': ['Unknown', '#6f7689'] };
function pluginBadge() {
  const PL = S.init?.plugins?.list ?? [];
  const attention = PL.filter(x => (!x.ok && x.enabled) || x.needsApproval).length;
  const el = $('#plugins-badge'); if (!el) return;
  el.textContent = attention ? attention : PL.length || '';
  el.classList.toggle('warn', attention > 0);
}
function pluginMatches(x) {
  const f = PF.f, q = PF.q.trim().toLowerCase();
  const fOk = f === 'all' || (f === 'effects' && x.kind === 'effect') || (f === 'devices' && x.kind === 'device') || (f === 'smbus' && x.kind === 'smbus')
    || (f === 'problems' && ((!x.ok && x.enabled) || x.needsApproval)) || (f === 'disabled' && !x.enabled) || (f === 'user' && x.source === 'user') || (f === 'bundled' && x.source === 'bundled');
  return fOk && (!q || [x.name, x.file, x.message, x.kind].some(v => String(v).toLowerCase().includes(q)));
}
function renderPlugins() {
  const PL = S.init.plugins?.list ?? [];
  const count = f => PL.filter(x => { const keep = PF.f; PF.f = f; const q = PF.q; PF.q = ''; const r = pluginMatches(x); PF.f = keep; PF.q = q; return r; }).length;
  const filters = [['all', 'All'], ['effects', 'Effects'], ['devices', 'USB devices'], ['smbus', 'SMBus'], ['problems', 'Problems'], ['disabled', 'Disabled'], ['user', 'Yours'], ['bundled', 'Bundled']];
  const exe = S.init.plugins?.exe ?? 'C:\\Program Files\\Glint\\Glint.exe';
  const mcp = `claude mcp add glint -- "${exe}" mcp`;
  const mcpWsl = `claude mcp add glint -- "${exe.replace(/^([A-Za-z]):\\/, (_, d) => '/mnt/' + d.toLowerCase() + '/').replace(/\\/g, '/')}" mcp`;
  $('#page-plugins').innerHTML = `
    <div class="plug-top">
      <div class="search"><svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round"><circle cx="11" cy="11" r="7"/><path d="m20 20-3.5-3.5"/></svg>
        <input type="text" id="pl-q" placeholder="Search ${PL.length} plugins…" value="${esc(PF.q)}" spellcheck="false"></div>
      <div class="row" style="gap:8px">
        <button class="btn small" id="pl-new-fx">${IC.plus} New effect</button>
        <button class="btn small" id="pl-new-dev">${IC.plus} New device plugin</button>
        <button class="btn small ghost" data-open-plugins>${IC.folder} Folder</button>
        <button class="btn small ghost" id="pl-reload">Reload</button>
        <button class="btn small ghost" id="pl-restart" title="Device plugins load when Glint starts">Restart Glint</button>
      </div>
    </div>
    <div class="chips" style="margin:14px 0 16px">${filters.map(([k, n]) => `<button class="chip ${PF.f === k ? 'on' : ''}" data-pf="${k}">${n} <span class="muted num">${count(k)}</span></button>`).join('')}</div>
    <div class="plug-layout">
      <div class="card plug-list" id="pl-list"></div>
      <div class="panel-stack">
        <div class="card card-pad">
          <div class="card-head"><div class="card-title">Use with an AI</div></div>
          <div class="muted" style="font-size:12.5px;margin-bottom:10px">Let Claude (or any MCP client) read your hardware, write and test plugins, and change lighting or fans for you. Run this once in a terminal:</div>
          <div class="code"><code id="pl-mcp">${esc(mcp)}</code><button class="icon-btn" data-copy="pl-mcp" title="Copy">${IC.copy}</button></div>
          <div class="muted" style="font-size:11.5px;margin:8px 0 6px">Claude Code inside WSL:</div>
          <div class="code"><code id="pl-mcp-wsl">${esc(mcpWsl)}</code><button class="icon-btn" data-copy="pl-mcp-wsl" title="Copy">${IC.copy}</button></div>
          <div class="muted" style="font-size:12.5px;margin:12px 0 8px">Then ask, for example: <i>"Add Glint support for my NZXT RGB controller"</i> or <i>"Make a lighting effect that looks like a sunset"</i>.</div>
          <div class="row" style="gap:8px;flex-wrap:wrap"><button class="btn small" id="pl-docs">${IC.copy} Copy plugin guide</button></div>
          <div class="muted" style="font-size:12px;margin-top:14px">Or script it: <span class="mono">Glint.exe ctl help</span>, <span class="mono">ctl hid.list</span>, <span class="mono">ctl plugins.add file=x.js code=…</span></div>
        </div>
        <div class="card card-pad">
          <div class="card-head"><div class="card-title">Safety</div></div>
          <div class="muted" style="font-size:12.5px">Plugins can't touch files, the network or the rest of the system, and a stuck plugin is stopped automatically. SMBus plugins (RAM, on-board chips) need your approval here, and editing one revokes it.</div>
        </div>
      </div>
    </div>`;
  const list = $('#pl-list');
  const drawList = () => {
    const rows = PL.filter(pluginMatches);
    const groups = [['device', 'Hardware'], ['smbus', 'Hardware (SMBus)'], ['effect', 'Effects'], ['?', 'Other']];
    list.innerHTML = rows.length ? groups.map(([k, title]) => {
      const g = rows.filter(x => (own(KIND, x.kind) ? x.kind : '?') === k);
      return g.length ? `<div class="plug-group">${title} <span class="muted">${g.length}</span></div>` + g.map(x => {
        const [kn, kc] = own(KIND, x.kind) ?? KIND['?'];
        const state = !x.enabled ? 'off' : x.needsApproval ? 'warn' : x.ok ? 'ok' : 'bad';
        return `<div class="plug-row ${x.enabled ? '' : 'disabled'}">
          <span class="pdot ${state}"></span>
          <div class="pmain"><div class="pname">${esc(x.name)} ${x.source === 'built-in' ? '<span class="ptag">Built in</span>' : `<span class="ptag" style="color:${kc};border-color:${kc}55">${kn}</span>`}${x.source === 'user' ? '<span class="ptag">Yours</span>' : ''}</div>
            <div class="pmsg ${state}" title="${esc(x.message)}">${esc(x.file)}${x.message ? ' · ' + esc(x.message) : ''}</div></div>
          <div class="pact">
            ${x.needsApproval ? `<button class="btn small primary" data-approve="${esc(x.file)}">Approve</button>` : ''}
            ${x.source === 'built-in' ? '<span style="width:30px"></span>' : `<button class="icon-btn" data-open="${esc(x.file)}" title="Open in editor"><svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round"><path d="M12 20h9M16.5 3.5a2.1 2.1 0 0 1 3 3L7 19l-4 1 1-4z"/></svg></button>`}
            ${x.source === 'user' ? `<button class="icon-btn danger" data-rm="${esc(x.file)}" title="Delete">${IC.trash}</button>` : '<span style="width:30px"></span>'}
            <div class="toggle ${x.enabled ? 'on' : ''}" data-en="${esc(x.file)}" title="${x.enabled ? 'Switch off' : 'Switch on'}"></div>
          </div></div>`;
      }).join('') : '';
    }).join('') : `<div class="empty">No plugins match${PF.q ? ` “${esc(PF.q)}”` : ''}.</div>`;
  };
  drawList();
  $('#pl-q').oninput = e => { PF.q = e.target.value; drawList(); };
  $$('[data-pf]').forEach(b => b.onclick = () => { PF.f = b.dataset.pf; renderPlugins(); });
  list.onclick = e => {
    const t = e.target.closest('[data-en],[data-rm],[data-open],[data-approve]'); if (!t) return;
    if (t.dataset.en) { const x = PL.find(p => p.file === t.dataset.en); x.enabled = !x.enabled; t.classList.toggle('on', x.enabled); send({ type: 'setPluginEnabled', file: x.file, enabled: x.enabled }); drawList(); }
    if (t.dataset.rm && confirm(`Delete ${t.dataset.rm}?`)) send({ type: 'removePlugin', file: t.dataset.rm });
    if (t.dataset.open) send({ type: 'openPluginFile', file: t.dataset.open });
    if (t.dataset.approve && confirm(`Allow "${t.dataset.approve}" to access the SMBus (RAM and other on-board chips)?\n\nOnly approve plugins you trust. Editing the file will require approval again.`)) send({ type: 'approvePlugin', file: t.dataset.approve });
  };
  $('#pl-new-fx').onclick = () => send({ type: 'newPlugin', kind: 'effect' });
  $('#pl-new-dev').onclick = () => send({ type: 'newPlugin', kind: 'device' });
  $('#pl-reload').onclick = () => send({ type: 'reloadPlugins' });
  $('#pl-restart').onclick = () => { send({ type: 'config', config: S.cfg }); setTimeout(() => send({ type: 'restart' }), 300); };
  $('#pl-docs').onclick = () => send({ type: 'pluginDocs' });
  $$('#page-plugins [data-open-plugins]').forEach(b => b.onclick = () => send({ type: 'openPlugins' }));
  $$('#page-plugins [data-copy]').forEach(b => b.onclick = () => navigator.clipboard?.writeText($('#' + b.dataset.copy).textContent).then(() => toast('Copied')));
}

/* ------------------------------------------------------------------ sensors (HWMonitor-style tree) */

const SENS = { q: '', collapsed: new Set(), sig: '', rows: new Map() };
setInterval(() => { if (S.page === 'sensors' || (S.page === 'overlay' && OV.open)) send({ type: 'sensorsWanted' }); }, 1000); // keeps the full catalog alive on the host
const SENS_TYPES = [['Temperature', 'Temperatures'], ['Voltage', 'Voltages'], ['Current', 'Currents'], ['Power', 'Powers'], ['Clock', 'Clocks'], ['Frequency', 'Frequencies'],
  ['Load', 'Utilization'], ['Fan', 'Fans'], ['Control', 'Fan control'], ['Flow', 'Flow'], ['Level', 'Levels'], ['Factor', 'Factors'], ['Data', 'Data'],
  ['Throughput', 'Throughput'], ['Energy', 'Energy'], ['TimeSpan', 'Time'], ['Noise', 'Noise'], ['Humidity', 'Humidity'], ['Conductivity', 'Conductivity']];
const sensGroupOf = t => t === 'SmallData' ? 'Data' : t;
const HW_KIND = {
  Cpu: ['Processor', 'cpu'], GpuNvidia: ['Graphics', 'gpu'], GpuAmd: ['Graphics', 'gpu'], GpuIntel: ['Graphics', 'gpu'], Motherboard: ['Motherboard', 'board'],
  SuperIO: ['Sensor chip', 'board'], EmbeddedController: ['Embedded controller', 'board'], Memory: ['Memory', 'ram'], Storage: ['Drive', 'disk'],
  Network: ['Network', 'net'], Psu: ['Power supply', 'bolt'], Cooler: ['Cooler', 'fan'], FanHub: ['Fan hub (Glint)', 'fan'], Battery: ['Battery', 'bolt'],
};
const IC_EXTRA = {
  disk: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8"><rect x="3" y="5" width="18" height="14" rx="2"/><path d="M7 15h.01M11 15h6"/></svg>',
  net: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round"><rect x="9" y="2" width="6" height="5" rx="1"/><rect x="2" y="17" width="6" height="5" rx="1"/><rect x="16" y="17" width="6" height="5" rx="1"/><path d="M12 7v5M5 17v-3h14v3"/></svg>',
  chev: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.2" stroke-linecap="round" stroke-linejoin="round"><path d="m9 6 6 6-6 6"/></svg>',
};

function fmtSensor(v, type, unit) {
  if (v == null || !isFinite(v)) return '–';
  const n = (x, d) => x.toLocaleString('en-US', { minimumFractionDigits: d, maximumFractionDigits: d });
  switch (type) {
    case 'Temperature': return n(v, 1) + ' °C';
    case 'Voltage': return n(v, 3) + ' V';
    case 'Current': return n(v, 2) + ' A';
    case 'Power': return n(v, v < 10 ? 2 : 1) + ' W';
    case 'Clock': return n(v, 0) + ' MHz';
    case 'Frequency': return n(v, v < 100 ? 1 : 0) + ' Hz';
    case 'Load': case 'Control': case 'Level': case 'Humidity': return n(v, 1) + ' %';
    case 'Fan': return n(v, 0) + ' RPM';
    case 'Flow': return n(v, 1) + ' L/h';
    case 'Data': return v >= 1024 ? n(v / 1024, 2) + ' TB' : n(v, v < 100 ? 1 : 0) + ' GB';
    case 'SmallData': return n(v, 0) + ' MB';
    case 'Throughput': { const u = ['B/s', 'KB/s', 'MB/s', 'GB/s']; let i = 0; while (v >= 1000 && i < 3) { v /= 1024; i++; } return n(v, i === 0 ? 0 : v < 10 ? 2 : 1) + ' ' + u[i]; }
    case 'Energy': return n(v, 0) + ' mWh';
    case 'TimeSpan': { const h = Math.floor(v / 3600), m = Math.floor(v % 3600 / 60); return h ? `${h} h ${m} min` : m ? `${m} min ${Math.floor(v % 60)} s` : n(v, 0) + ' s'; }
    case 'Factor': return n(v, 3);
    default: return n(v, 2) + (unit ? ' ' + unit : '');
  }
}
// sensor ids the active profile's fan rules read (max(a,b) expanded)
function curveInputs() {
  const ids = new Set();
  const add = id => { const m = /^(max|min|avg)\((.*)\)$/.exec(id ?? ''); if (m) m[2].split(',').forEach(x => add(x.trim())); else if (id) ids.add(id.toLowerCase()); };
  for (const r of S.cfg.profiles[S.cfg.activeProfile] ?? []) if (r.fixed == null) r.inputs.forEach(i => add(i.sensor));
  return ids;
}
function sensTags(x, used) {
  const al = x.aliases ?? [];
  let t = '';
  if (x.type !== 'Fan' && x.type !== 'Control' && al.some(a => used.has(a.toLowerCase()))) t += '<span class="stag drive" title="A fan rule in the active profile uses this sensor">drives fans</span>';
  const friendly = al.find(a => !a.startsWith('/'));
  if (friendly) {
    const fan = S.init.fans?.some(f => f.id === friendly);
    t += `<span class="stag" title="${fan ? 'Fan output' : 'Friendly id: use it in fan curves, effects and the API'}">${esc(fan ? fanLabel(friendly) : friendly)}</span>`;
  }
  return t;
}
function sensSig(cat) { const ids = []; const walk = h => { ids.push(h.id); h.sensors.forEach(x => ids.push(x.id)); h.sub?.forEach(walk); }; cat?.hardware.forEach(walk); return ids.join('|'); }

function renderSensors() {
  const cat = S.sens, pg = $('#page-sensors');
  const count = cat ? (() => { let n = 0, d = 0; const walk = h => { n += h.sensors.length; if (h.sensors.length) d++; h.sub?.forEach(walk); }; cat.hardware.forEach(walk); return [n, cat.hardware.length]; })() : [0, 0];
  const used = curveInputs();
  const typeGroups = (h, lvl) => {
    const by = new Map();
    for (const x of h.sensors) { const g = sensGroupOf(x.type); if (!by.has(g)) by.set(g, []); by.get(g).push(x); }
    return SENS_TYPES.filter(([k]) => by.has(k)).concat([...by.keys()].filter(k => !SENS_TYPES.some(t => t[0] === k)).map(k => [k, k])).map(([k, label]) => {
      const gid = h.id + '|' + k;
      return `<div class="sg ${SENS.collapsed.has(gid) ? 'collapsed' : ''}" data-g="${esc(gid)}">
        <button class="sg-h lvl${lvl + 1}" data-tg="${esc(gid)}" aria-expanded="${!SENS.collapsed.has(gid)}"><span class="chev">${IC_EXTRA.chev}</span>${esc(label)}<span class="sg-n">${by.get(k).length}</span></button>
        <div class="sg-body">${by.get(k).map(x => `<div class="sr lvl${lvl + 2}" data-sid="${esc(x.id)}" data-q="${esc([x.name, h.name, label, ...(x.aliases ?? [])].join(' ').toLowerCase())}">
          <span class="sn"><span class="sname" title="${esc(x.id)}">${esc(x.name)}</span>${sensTags(x, used)}</span>
          <span class="sv num" data-v>${esc(fmtSensor(x.value, x.type, x.unit))}</span><span class="sv num mm" data-min>${esc(fmtSensor(x.min, x.type, x.unit))}</span><span class="sv num mm" data-max>${esc(fmtSensor(x.max, x.type, x.unit))}</span></div>`).join('')}</div></div>`;
    }).join('');
  };
  const hwNode = (h, lvl) => {
    const [kind, ic] = own(HW_KIND, h.type) ?? [h.type, 'hub'];
    return `<div class="sg hw ${SENS.collapsed.has(h.id) ? 'collapsed' : ''}" data-g="${esc(h.id)}">
      <button class="sg-h lvl${lvl}" data-tg="${esc(h.id)}" aria-expanded="${!SENS.collapsed.has(h.id)}"><span class="chev">${IC_EXTRA.chev}</span><span class="hw-ic">${IC[ic] ?? IC_EXTRA[ic] ?? IC.hub}</span><span class="hw-name">${esc(h.name)}</span><span class="hw-kind">${esc(kind)}</span></button>
      <div class="sg-body">${(h.sub ?? []).map(sh => hwNode(sh, lvl + 1)).join('')}${typeGroups(h, lvl)}</div></div>`;
  };
  pg.innerHTML = `
    <div class="sens-top">
      <div class="search"><svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round"><circle cx="11" cy="11" r="7"/><path d="m20 20-3.5-3.5"/></svg>
        <input type="text" id="sens-q" placeholder="Search ${count[0]} sensors…" value="${esc(SENS.q)}" spellcheck="false"></div>
      <span class="muted sens-count" id="sens-count">${cat ? `${count[0]} sensors · ${count[1]} devices` : ''}</span>
      <div class="row" style="gap:8px">
        <button class="btn small ghost" id="sens-expand">Expand all</button>
        <button class="btn small ghost" id="sens-collapse">Collapse all</button>
        <button class="btn small" id="sens-reset" title="Start the min / max columns again from the current values">Reset min/max</button>
      </div>
    </div>
    <div class="card sens-table">
      <div class="sens-head"><span>Sensor</span><span>Value</span><span>Min</span><span>Max</span></div>
      <div id="sens-tree">${cat ? cat.hardware.map(h => hwNode(h, 0)).join('') + '<div class="empty hidden" id="sens-none"></div>' : '<div class="empty">Reading every sensor…</div>'}</div>
      ${cat?.error ? `<div class="muted" style="padding:10px 18px">${esc(cat.error)}</div>` : ''}
    </div>
    <div class="muted" style="font-size:12px;margin-top:12px">Min and max are since this page was opened. While it is open Glint reads every sensor each second (drives, memory, network included); closing it returns to the normal, lighter polling.</div>`;
  SENS.sig = sensSig(cat);
  SENS.rows = new Map($$('#sens-tree [data-sid]').map(el => [el.dataset.sid, el]));
  sensFilter();
  $('#sens-q').oninput = e => { SENS.q = e.target.value; sensFilter(); };
  $('#sens-tree').onclick = e => {
    const h = e.target.closest('[data-tg]'); if (!h) return;
    const id = h.dataset.tg, g = h.parentElement;
    if (SENS.collapsed.has(id)) SENS.collapsed.delete(id); else SENS.collapsed.add(id);
    g.classList.toggle('collapsed', SENS.collapsed.has(id)); h.setAttribute('aria-expanded', !SENS.collapsed.has(id));
  };
  const all = on => { $$('#sens-tree [data-tg]').forEach(h => { if (on) SENS.collapsed.delete(h.dataset.tg); else if (!h.closest('.sg-body')) SENS.collapsed.add(h.dataset.tg); }); renderSensors(); };
  $('#sens-expand').onclick = () => all(true);
  $('#sens-collapse').onclick = () => all(false);
  $('#sens-reset').onclick = () => {
    send({ type: 'sensorsReset' });
    // show it at once: min = max = the current value until the next reading arrives
    for (const h of S.sens?.hardware ?? []) (function walk(n) { n.sensors.forEach(x => { x.min = x.max = x.value; }); n.sub?.forEach(walk); })(h);
    liveSensors(); toast('Min / max reset');
  };
}
function sensFilter() {
  const q = SENS.q.trim().toLowerCase(), tree = $('#sens-tree'); if (!tree) return;
  tree.classList.toggle('searching', !!q);
  let shown = 0;
  for (const el of SENS.rows.values()) { const ok = !q || q.split(/\s+/).every(w => el.dataset.q.includes(w)); el.classList.toggle('hidden', !ok); if (ok) shown++; }
  // hide groups with nothing left, deepest first
  for (const g of $$('.sg', tree).reverse()) g.classList.toggle('hidden', !!q && !g.querySelector('.sr:not(.hidden)'));
  const none = $('#sens-none');
  if (none) { none.classList.toggle('hidden', shown > 0); none.textContent = `No sensors match “${SENS.q}”.`; }
}
function liveSensors() {
  if (!$('#sens-tree')) return;
  if (sensSig(S.sens) !== SENS.sig) { const y = $('#content').scrollTop, f = document.activeElement?.id; renderSensors(); $('#content').scrollTop = y; if (f === 'sens-q') { const i = $('#sens-q'); i.focus(); i.setSelectionRange(i.value.length, i.value.length); } return; }
  const walk = h => {
    for (const x of h.sensors) {
      const el = SENS.rows.get(x.id); if (!el) continue;
      el.children[1].textContent = fmtSensor(x.value, x.type, x.unit);
      el.children[2].textContent = fmtSensor(x.min, x.type, x.unit);
      el.children[3].textContent = fmtSensor(x.max, x.type, x.unit);
    }
    h.sub?.forEach(walk);
  };
  S.sens.hardware.forEach(walk);
}

/* ------------------------------------------------------------------ settings */

function renderSettings() {
  const I = S.init;
  $('#page-settings').innerHTML = `
    <div class="grid perf-grid">
      <div class="card card-pad stat"><div class="v" id="p-cpu">–</div><div class="k">Glint CPU (of whole machine)</div></div>
      <div class="card card-pad stat"><div class="v" id="p-mem">–</div><div class="k">Glint memory</div></div>
      <div class="card card-pad stat"><div class="v" id="p-fps">–</div><div class="k">Lighting frame rate</div></div>
      <div class="card card-pad stat"><div class="v">${I.elevated ? 'Full' : 'Limited'}</div><div class="k">${I.elevated ? 'Admin access: CPU temps, board fans, RAM' : 'No admin: run install.ps1'}</div></div>
    </div>
    <div class="grid" style="grid-template-columns:1fr 1fr; margin-top:14px">
      <div class="card card-pad">
        <div class="card-head"><div class="card-title">Safety</div></div>
        <div class="field" style="margin-bottom:14px"><label>Fail-safe speed (used when a sensor disappears)</label>${slider('s-fail', S.cfg.failsafeDuty, 50, 100, 1, v => v + '%')}</div>
        <div class="field"><label>Sensor and fan update interval</label>${slider('s-poll', S.cfg.pollMs, 500, 3000, 100, v => (v / 1000).toFixed(1) + ' s')}</div>
        <div class="muted" style="font-size:12px;margin-top:12px">If Glint closes or crashes, fan hubs fall back to their own built-in curves and motherboard headers return to BIOS control.</div>
      </div>
      <div class="card card-pad">
        <div class="card-head"><div class="card-title">Device frame rates</div><div class="card-sub">Hardware limits, not settings</div></div>
        <table class="t" id="p-devs"></table>
      </div>
      <div class="card card-pad">
        <div class="card-head"><div class="card-title">Files</div></div>
        <div class="muted" style="font-size:13px;margin-bottom:12px">Everything is stored in a single config.json. You can edit it by hand and Glint reloads it on save.</div>
        <button class="btn" id="s-open">${IC.folder} Open config folder</button>
      </div>
      <div class="card card-pad ha-card" id="ha-card">
        <div class="row between card-head"><div><div class="card-title">Home Assistant</div><div class="card-sub">Sensors, fans, profiles and lights as Home Assistant entities</div></div><div class="toggle ${HA().enabled ? 'on' : ''}" id="ha-enabled" title="Home Assistant bridge"></div></div>
        <div class="ha-status" id="ha-status"><span class="pdot off"></span><span>–</span></div>
        <div class="ha-grid">
          <div class="field ha-host"><label for="ha-host">Broker host</label><input type="text" id="ha-host" value="${esc(HA().host)}" placeholder="homeassistant.local" spellcheck="false"></div>
          <div class="field"><label for="ha-port">Port</label><input type="number" id="ha-port" value="${num(HA().port, 1883)}" min="1" max="65535"></div>
          <div class="field"><label>TLS</label><div class="toggle ${HA().tls ? 'on' : ''}" id="ha-tls" title="Use TLS (usually port 8883)"></div></div>
          <div class="field"><label for="ha-user">Username</label><input type="text" id="ha-user" value="${esc(HA().username)}" spellcheck="false" autocomplete="off"></div>
          <div class="field"><label for="ha-pass">Password</label><div class="row" style="gap:6px"><input type="password" id="ha-pass" value="" autocomplete="new-password" style="flex:1"><button class="btn small ghost" id="ha-pass-clear" title="Remove the saved password">Clear</button></div></div>
          <div class="field"><label for="ha-prefix">Discovery prefix</label><input type="text" id="ha-prefix" value="${esc(HA().discoveryPrefix ?? 'homeassistant')}" spellcheck="false"></div>
          <div class="field"><label for="ha-topic">Base topic</label><input type="text" id="ha-topic" value="${esc(HA().baseTopic ?? 'glint')}" spellcheck="false"></div>
        </div>
        <div class="muted" style="font-size:12px;margin-top:12px">Needs an MQTT broker, such as Home Assistant's Mosquitto add-on (Settings → Add-ons), with the MQTT integration enabled. Glint's entities then appear automatically; no YAML needed.</div>
      </div>
      <div class="card card-pad" id="upd-card">
        <div class="row between card-head"><div><div class="card-title">Updates</div><div class="card-sub">One request to GitHub at startup and every 6 hours. Nothing is installed unless you click Install or switch on automatic install.</div></div><div class="toggle ${UPD().check !== false ? 'on' : ''}" id="upd-check" title="Check for updates"></div></div>
        <div class="ha-status" id="upd-status"><span class="pdot off"></span><span>–</span><span class="spacer" style="flex:1"></span><button class="btn small primary" id="upd-install" style="display:none">Install update</button><button class="btn small" id="upd-open" style="display:none">Open release page</button><button class="btn small" id="upd-recheck" title="Ask GitHub for the latest release now">Check now</button></div>
        <div class="muted upd-progress" id="upd-progress" role="status" aria-live="polite"></div>
        <div class="row between" style="gap:12px"><div><div style="font-size:13px">Install updates automatically</div><div class="muted" style="font-size:12px">Only after 10 minutes without input, with no game in front and not on battery. Every setup is checked against its SHA-256 and GitHub build attestation first.</div></div><div class="toggle ${UPD().autoInstall ? 'on' : ''}" id="upd-auto" title="Install updates automatically"></div></div>
      </div>
      <div class="card card-pad ha-card" id="orgb-card">
        <div class="row between card-head"><div><div class="card-title">OpenRGB bridge</div><div class="card-sub">Drive devices Glint doesn't support yet through OpenRGB's SDK server</div></div><div class="toggle ${ORGB().enabled ? 'on' : ''}" id="orgb-enabled" title="OpenRGB bridge"></div></div>
        <div class="ha-status" id="orgb-status"><span class="pdot off"></span><span>–</span></div>
        <div class="ha-grid">
          <div class="field ha-host"><label for="orgb-host">SDK server host</label><input type="text" id="orgb-host" value="${esc(ORGB().host ?? '127.0.0.1')}" spellcheck="false"></div>
          <div class="field"><label for="orgb-port">Port</label><input type="number" id="orgb-port" value="${num(ORGB().port, 6742)}" min="1" max="65535"></div>
          <div class="field" style="grid-column:1/-1"><label for="orgb-exclude">Leave these OpenRGB devices alone (names, comma-separated)</label><input type="text" id="orgb-exclude" value="${esc((Array.isArray(ORGB().exclude) ? ORGB().exclude : []).join(', '))}" spellcheck="false" placeholder="e.g. Razer, Corsair Vengeance"></div>
        </div>
        <div class="row" style="margin-top:12px;gap:10px"><div class="muted" style="font-size:12px;flex:1">In OpenRGB: SDK Server tab → Start Server. Glint connects when it starts, skips devices it already drives itself, and sends frames at up to 30 fps.</div><button class="btn small" id="orgb-restart">Restart Glint</button></div>
      </div>
      <div class="card card-pad">
        <div class="card-head"><div class="card-title">About</div></div>
        <div class="kv"><span class="k">Version</span><span>${esc(I.version ?? '1.0')}</span><span class="k">Fan outputs</span><span>${I.fans.length}</span><span class="k">LED zones</span><span>${I.zones.length} (${I.zones.reduce((a, z) => a + num(z.count), 0)} LEDs)</span></div>
      </div>
    </div>`;
  bindSlider('s-fail', v => v + '%', v => { S.cfg.failsafeDuty = v; commit(); });
  bindSlider('s-poll', v => (v / 1000).toFixed(1) + ' s', v => { S.cfg.pollMs = v; commit(); });
  $('#s-open').onclick = () => send({ type: 'openConfig' });
  bindHa();
  bindUpdates();
  bindOpenRgb();
}

/* Update check + OpenRGB bridge cards */
function UPD() { return (S.cfg.updates ??= { check: true, autoInstall: false }); }
function ORGB() { return (S.cfg.openRgb ??= { enabled: false, host: '127.0.0.1', port: 6742, exclude: [] }); }
function bindUpdates() {
  $('#upd-check').onclick = e => {
    UPD().check = !UPD().check; e.currentTarget.classList.toggle('on', UPD().check);
    if (!UPD().check) UPD().autoInstall = false; // automatic install needs the check (the validator insists)
    commit(); liveUpdate();
  };
  $('#upd-auto').onclick = e => {
    if (UPD().check === false) return;
    UPD().autoInstall = !UPD().autoInstall; e.currentTarget.classList.toggle('on', UPD().autoInstall); commit(); liveUpdate();
  };
  $('#upd-open').onclick = () => { const url = S.state?.update?.url ?? S.init.releasesUrl; if (/^https:\/\//i.test(url ?? '')) send({ type: 'openUrl', url }); };
  $('#upd-install').onclick = () => installUpdate();
  $('#upd-recheck').onclick = () => { S.updChecking = true; liveUpdate(); send({ type: 'checkUpdate' }); };
  liveUpdate();
}
const UPD_BUSY = ['downloading', 'verifying', 'installing'];
function installUpdate() {
  const v = S.state?.update?.latest;
  if (!confirm(`Install Glint v${v ?? ''} now?\n\nGlint downloads the setup from its GitHub releases, checks its SHA-256 and GitHub build attestation, then closes and restarts with the new version.`)) return;
  send({ type: 'installUpdate' });
}
function installText(i) {
  if (!i || i.state === 'idle') return '';
  const mb = b => (b / 1048576).toFixed(1);
  const pct = i.state === 'downloading' && i.total > 0 ? ` ${mb(i.bytes)} of ${mb(i.total)} MB` : '';
  const head = { downloading: 'Downloading', verifying: 'Verifying', installing: 'Installing', failed: 'Failed' }[i.state] ?? i.state;
  return `${head}: ${i.message ?? ''}${pct}`;
}
function ago(t) {
  const m = t ? Math.round((Date.now() - Date.parse(t)) / 60000) : NaN;
  if (!(m >= 0)) return '';
  return ' · checked ' + (m < 1 ? 'just now' : m < 60 ? `${m} min ago` : m < 2880 ? `${Math.round(m / 60)} h ago` : `${Math.round(m / 1440)} days ago`);
}
function updateText() {
  if (S.updChecking) return ['off', 'Checking GitHub for the latest release…'];
  const u = S.state?.update, cur = u?.current ?? S.init.version;
  if (UPD().check === false) return ['off', `Glint ${cur} · update check is off`];
  if (u?.available) return ['warn', `Update available: v${u.latest} (you have ${cur})`];
  if (u?.latest) return ['ok', `Glint ${cur} is the latest version${ago(u.checkedAt)}`];
  return ['off', u?.checkedAt ? `Glint ${cur} · couldn't check (${u.note ?? 'offline'})` : `Glint ${cur} · not checked yet`];
}
function liveUpdate() {
  const el = $('#upd-status'); if (!el) return;
  const [cls, txt] = updateText();
  $('.pdot', el).className = 'pdot ' + cls;
  $('span:nth-child(2)', el).textContent = txt;
  const avail = S.state?.update?.available && UPD().check !== false, inst = S.state?.install, busy = UPD_BUSY.includes(inst?.state);
  const canInstall = avail && !S.init.cannotInstall;
  $('#upd-open').style.display = avail ? '' : 'none';
  $('#upd-install').style.display = canInstall ? '' : 'none';
  $('#upd-install').disabled = busy;
  $('#upd-recheck').disabled = !!S.updChecking || busy;
  $('#upd-recheck').style.display = avail ? 'none' : '';
  $('#upd-install').textContent = busy ? 'Installing…' : inst?.state === 'failed' ? 'Try again' : 'Install update';
  const pr = $('#upd-progress'), t = avail && S.init.cannotInstall ? `Install it from the release page: ${S.init.cannotInstall}.` : installText(inst);
  if (pr.textContent !== t) pr.textContent = t;
  pr.classList.toggle('bad', inst?.state === 'failed');
  const auto = $('#upd-auto');
  auto.classList.toggle('on', !!UPD().autoInstall);
  auto.classList.toggle('disabled', UPD().check === false);
  auto.title = UPD().check === false ? 'Switch the update check on first' : 'Install updates automatically';
}
function bindOpenRgb() {
  const o = ORGB();
  $('#orgb-enabled').onclick = e => { o.enabled = !o.enabled; e.currentTarget.classList.toggle('on', o.enabled); commit(); liveOpenRgb(); };
  $('#orgb-host').addEventListener('change', e => { o.host = e.target.value.trim() || '127.0.0.1'; e.target.value = o.host; commit(); });
  $('#orgb-port').addEventListener('change', e => { o.port = clamp(Math.round(+e.target.value) || 6742, 1, 65535); e.target.value = o.port; commit(); });
  $('#orgb-exclude').addEventListener('change', e => { o.exclude = e.target.value.split(',').map(x => x.trim()).filter(Boolean); commit(); });
  $('#orgb-restart').onclick = () => { send({ type: 'config', config: S.cfg }); setTimeout(() => send({ type: 'restart' }), 300); };
  liveOpenRgb();
}
function liveOpenRgb() {
  const el = $('#orgb-status'); if (!el) return;
  const o = ORGB(), st = S.state?.openRgb;
  let cls = 'off', txt = 'Off';
  if (o.enabled && !st) { cls = 'warn'; txt = 'Enabled · restart Glint to connect'; }
  else if (!o.enabled && st) { cls = 'warn'; txt = 'Switched off · restart Glint to disconnect'; }
  else if (st?.connected) { cls = st.error ? 'warn' : 'ok'; txt = `Connected to ${st.server} · ${st.devices.length} device${st.devices.length === 1 ? '' : 's'}${st.skipped.length ? ` · ${st.skipped.length} skipped (Glint drives them)` : ''}${st.error ? ` · ${st.error}` : ''}`; }
  else if (st) { cls = 'bad'; txt = `Not reachable at ${st.server}: ${st.error ?? 'no answer'}`; }
  $('.pdot', el).className = 'pdot ' + cls;
  $('span:last-child', el).textContent = txt;
}

/* ------------------------------------------------------------------ setup check */
const SETUP_ORDER = { fail: 0, warn: 1, ok: 2 };
function setupBadge() {
  const el = $('#setup-badge'); if (!el) return;
  const R = S.setup;
  el.className = 'nav-badge' + (R?.fail ? ' bad' : R?.warn ? ' warn dot' : '');
  el.textContent = R?.fail ? R.fail : R?.warn ? '•' : '';
  el.title = R ? `${R.fail} problem(s), ${R.warn} warning(s)` : '';
}
function setupBanner() {
  const el = $('#setup-banner'); if (!el) return;
  const bad = S.setup?.importantFail ? S.setup.items.find(i => i.important && i.status === 'fail') : null;
  const more = bad ? S.setup.items.filter(i => i.important && i.status === 'fail').length - 1 : 0;
  el.innerHTML = bad ? `<div class="banner bad" id="setup-alert">${IC.bolt.replace('<svg', '<svg width="18" height="18"')}<span>${esc(bad.title)}${more > 0 ? ` (and ${more} more)` : ''}</span><span class="spacer"></span><button class="btn small" data-go="setup">Open Setup check →</button></div>` : '';
}
function renderSetup() {
  const R = S.setup;
  const items = R ? [...R.items].sort((a, b) => SETUP_ORDER[a.status] - SETUP_ORDER[b.status]) : [];
  const when = R ? new Date(R.time).toLocaleTimeString([], { hour: '2-digit', minute: '2-digit', second: '2-digit' }) : '';
  const groups = [['fail', 'Needs fixing'], ['warn', 'Worth a look'], ['ok', 'All good']];
  $('#page-setup').innerHTML = `
    <div class="card card-pad setup-head">
      <div style="flex:1;min-width:240px"><div class="card-title">${!R ? 'Checking…' : R.fail ? `${esc(R.fail)} thing${R.fail === 1 ? '' : 's'} to fix` : R.warn ? 'Mostly set up' : 'Everything is set up'}</div>
        <div class="card-sub">${R ? `Checked at ${esc(when)}${S.setupChecking ? ' · checking again…' : ''}` : 'Looking at drivers, permissions and other apps…'}</div></div>
      ${R ? `<div class="counts"><span><b style="color:var(--bad)">${esc(R.fail)}</b>problem${R.fail === 1 ? '' : 's'}</span><span><b style="color:var(--warn)">${esc(R.warn)}</b>warning${R.warn === 1 ? '' : 's'}</span><span><b style="color:var(--good)">${esc(R.ok)}</b>ok</span></div>` : ''}
      <button class="btn" id="setup-recheck"${S.setupChecking ? ' disabled' : ''}>Re-check</button>
    </div>
    <div class="card setup-list" id="setup-list">${R ? groups.map(([st, title]) => {
      const g = items.filter(i => i.status === st); if (!g.length) return '';
      return `<div class="setup-group">${title}</div>` + g.map(i => `
      <div class="setup-row ${esc(i.status)}" data-item="${esc(i.id)}">
        <span class="pdot ${i.status === 'fail' ? 'bad' : esc(i.status)}"></span>
        <div><div class="st">${esc(i.title)}</div><div class="sd">${esc(i.detail)}</div></div>
        <div>${i.fix ? `<button class="btn small ${i.status === 'fail' ? 'primary' : ''}" data-fix="${esc(i.id)}">${esc(i.fixLabel)}</button>` : ''}</div>
      </div>`).join('');
    }).join('') : '<div class="empty">Checking…</div>'}</div>
    <div class="muted" style="font-size:12px;margin-top:12px">Glint only runs these checks when you open this page or press Re-check. The same report is available to scripts and AI agents as <span class="mono">setup.check</span>.</div>`;
  $('#setup-recheck').onclick = () => { S.setupChecking = true; send({ type: 'setupCheck' }); renderSetup(); };
  $('#setup-list').onclick = e => {
    const b = e.target.closest('[data-fix]'); if (!b) return;
    const it = S.setup.items.find(i => i.id === b.dataset.fix); if (!it) return;
    if (it.fix === 'page') { if (!own(PAGES, it.fixArg)) return; if (it.fixArg === 'plugins') PF.f = 'problems'; go(it.fixArg); return; }
    if (it.fix === 'restartAdmin' && !confirm('Restart Glint as administrator? Windows will ask for permission.')) return;
    if (it.fix === 'installUpdate') { installUpdate(); return; }
    send({ type: 'setupFix', fix: it.fix, arg: it.fixArg });
  };
}

/* Tuning page: AI overclocking. Off by default; nothing is tested, applied or watched until the user turns it on. */
const T_CFG = () => (S.cfg.tuning ??= { enabled: false, allowWorkstation: false, applyAtStartup: false, emergencyCoreC: 90, emergencyMemC: 100, unlockPower: true, crashWatch: true, autoBackoff: true, maxCrashes: 3, crashWindowDays: 7 });
let tuningPoll = null;
const TN_CTX_MAX = 1000, TN_GT_MAX = 300;
const tnGoalText = () => { if (S.tnGoalText == null) { try { S.tnGoalText = localStorage.getItem('glint.tn.goaltext') ?? ''; } catch { S.tnGoalText = ''; } } return String(S.tnGoalText).slice(0, TN_GT_MAX); };
const TN_STYLES = [['quiet', 'Quiet', 'Cool and quiet: modest gains, low power and temperature'], ['balanced', 'Balanced', 'A good gain without pushing the card'], ['performance', 'Performance', 'The fastest stable result the card can do'], ['custom', 'Custom', 'Follow only what you write below']];
const tnStyle = () => { if (S.tnStyle == null) { try { S.tnStyle = localStorage.getItem('glint.tn.style'); } catch { } } return TN_STYLES.some(x => x[0] === S.tnStyle) ? S.tnStyle : 'balanced'; };
const tnContext = () => { if (S.tnContext == null) { try { S.tnContext = localStorage.getItem('glint.tn.context') ?? ''; } catch { S.tnContext = ''; } } return String(S.tnContext).slice(0, TN_CTX_MAX); };
function tnDraftSave() { try { localStorage.setItem('glint.tn.style', tnStyle()); localStorage.setItem('glint.tn.context', tnContext()); localStorage.setItem('glint.tn.goaltext', tnGoalText()); } catch { } }
function tnCtxCount() { const e = $('#tn-ctx-n'); if (e) e.textContent = `${tnContext().length} / ${TN_CTX_MAX}`; const g = $('#tn-gt-n'); if (g) g.textContent = `${tnGoalText().length} / ${TN_GT_MAX}`; }
function tuningAsk(op = 'status', args) { send({ type: 'tuning', op, args }); }
function renderTuning() {
  const c = T_CFG();
  clearInterval(tuningPoll);
  tuningPoll = setInterval(() => { if (S.page === 'tuning') { tuningAsk(); tnAskHistory(); } else clearInterval(tuningPoll); }, 3000);
  tuningAsk(); tnAskHistory();
  $('#page-tuning').innerHTML = `
    <div class="card card-pad tn-head">
      <div style="flex:1;min-width:260px"><div class="card-title">AI overclocking</div>
        <div class="card-sub">Claude (your own Claude Code login) finds the fastest stable GPU and CPU settings with Glint's stress tests. Glint enforces every limit itself:
        clock offsets only (no voltage control); the GPU power limit goes up only to the card's own maximum; throttling at the card's temperature or power limit is a limit the AI optimises under, and only emergency temperatures stop a test; the card goes back to stock after every test; crashes back the overclock off automatically.</div></div>
      <div class="row" style="gap:10px"><span class="muted" style="font-size:12px">${c.enabled ? 'On' : 'Off'}</span><div class="toggle ${c.enabled ? 'on' : ''}" id="tn-enabled" title="Turn tuning on or off"></div></div>
    </div>
    ${!c.enabled ? `<div class="card card-pad muted" style="font-size:13px">Tuning is off. Nothing is tested, applied or watched, and Glint never touches your clocks. Turn it on above to start.
      Overclocking runs hardware beyond its factory settings: crashes during tests are expected, and some manufacturers don't cover overclocking under warranty.</div>` : `
    <div class="grid tn-grid">
      <div class="card card-pad"><div class="card-title">Tune with AI</div><div class="card-sub" id="tn-ai-sub">Looking for Claude Code…</div>
        <div class="row" style="gap:10px;margin-top:12px;flex-wrap:wrap">
          <div class="seg" id="tn-goal">${[['gpu', 'GPU'], ['cpu', 'CPU'], ['all', 'Both']].map(([g, l]) => `<button data-g="${g}" class="${(S.tnGoal ?? 'gpu') === g ? 'on' : ''}">${l}</button>`).join('')}</div>
          <select id="tn-budget" class="input">${[15, 30, 45, 90, 180].map(m => `<option value="${m}" ${(S.tnBudget ?? 45) === m ? 'selected' : ''}>${m} min</option>`).join('')}</select>
          <button class="btn primary" id="tn-ai-start">Tune with AI</button><button class="btn" id="tn-ai-stop">Stop</button><button class="btn" id="tn-monitor" title="A small window with live gauges, progress and the AI's reasoning">Open monitor</button>
        </div>
        <div class="field" style="margin-top:12px"><label>Goal</label>
          <div class="seg" id="tn-style" role="radiogroup" aria-label="Tuning goal">${TN_STYLES.map(([g, l, tip]) => `<button data-s="${g}" role="radio" aria-checked="${tnStyle() === g}" title="${esc(tip)}" class="${tnStyle() === g ? 'on' : ''}">${l}</button>`).join('')}</div></div>
        <div class="field" id="tn-gt-row" style="margin-top:10px" ${tnStyle() === 'custom' ? '' : 'hidden'}><label for="tn-goaltext">Your goal, in a sentence <span class="muted" id="tn-gt-n"></span></label>
          <input type="text" id="tn-goaltext" maxlength="${TN_GT_MAX}" placeholder="e.g. as quiet as possible while gaming at 4K, I don't care about benchmarks" value="${esc(tnGoalText())}" autocomplete="off" spellcheck="true"></div>
        <div class="field" style="margin-top:10px"><label for="tn-context">What should the end result be like? <span class="muted" id="tn-ctx-n"></span></label>
          <textarea id="tn-context" rows="3" maxlength="${TN_CTX_MAX}" placeholder="e.g. fans never above 50%; stability matters more than the last few MHz; I game at 4K and the card is in a small case" spellcheck="true">${esc(tnContext())}</textarea></div>
        <div class="muted" id="tn-ai-ran" style="font-size:12.5px;margin-top:8px"></div>
        <div id="tn-ai-restore"></div>
        <div class="tn-log tf" id="tn-log"></div>
      </div>
      <div class="card card-pad"><div class="card-title" id="tn-gpu-name">GPU</div><div class="card-sub" id="tn-gpu-sub"></div>
        <div id="tn-gpu"></div>
        <div id="tn-cmp" class="tn-cmp"></div>
        ${manMarkup()}
        <div class="row" style="gap:8px;margin-top:12px;flex-wrap:wrap"><button class="btn" id="tn-apply">Apply saved result</button><button class="btn" id="tn-stock">Back to stock</button></div>
        <div class="row tn-sw"><div class="toggle ${c.unlockPower !== false ? 'on' : ''}" id="tn-unlock"></div><label>Unlock power limit <span class="muted">(up to this card's maximum, never beyond; no voltage control)</span></label></div>
        <div class="row tn-sw"><div class="toggle ${c.applyAtStartup ? 'on' : ''}" id="tn-startup"></div><label>Apply the saved result when Glint starts <span class="muted">(skipped after a crash or a driver update)</span></label></div>
        <div class="row tn-sw" id="tn-ws-row"><div class="toggle ${c.allowWorkstation ? 'on' : ''}" id="tn-ws"></div><label>Allow tuning a workstation GPU <span class="muted">(RTX PRO, RTX A, Quadro: validated at stock clocks)</span></label></div>
      </div>
      <div class="card card-pad" style="grid-column:1/-1"><div class="row between"><div><div class="card-title">Overclock per profile</div><div class="card-sub">Every fan profile carries its own GPU overclock (or runs at stock). Switching profile applies it.</div></div><button class="btn small" id="tn-pm">Manage profiles…</button></div>
        <div id="tn-profiles"></div></div>
      <div class="card card-pad"><div class="card-title" id="tn-cpu-name">CPU</div><div class="card-sub" id="tn-cpu-sub"></div>
        <div id="tn-cpu"></div>
        <div class="row" style="gap:8px;margin-top:12px;flex-wrap:wrap"><button class="btn" id="tn-cpu-finish">Finish search</button><button class="btn" id="tn-cpu-stop">Stop search</button></div>
      </div>
      <div class="card card-pad"><div class="card-title">Crash watch</div><div class="card-sub" id="tn-crash-sub"></div>
        <div class="row tn-sw"><div class="toggle ${c.crashWatch ? 'on' : ''}" id="tn-cw"></div><label>Watch for driver resets, hardware errors and bluescreens while an overclock is applied</label></div>
        <div class="row tn-sw"><div class="toggle ${c.autoBackoff ? 'on' : ''}" id="tn-bo"></div><label>Back off automatically (core −15 MHz, then memory −250)</label></div>
        <div class="row" style="gap:8px;font-size:13px;margin-top:6px"><span class="muted">Back to stock after</span><input type="number" class="input tn-num" id="tn-max" min="1" max="20" value="${num(c.maxCrashes, 3)}"><span class="muted">crashes in</span><input type="number" class="input tn-num" id="tn-days" min="1" max="90" value="${num(c.crashWindowDays, 7)}"><span class="muted">days</span></div>
        <div id="tn-crashes"></div>
      </div>
      <div class="card card-pad" style="grid-column:1/-1"><div class="card-title">Test history</div><div class="card-sub">Every stress test, newest first. Hitting the card's own temperature or power limit is a limit, not a failure. A test that was running when the PC crashed is recorded as unstable.</div>
        <div id="tn-history"></div></div>
    </div>`}`;
  $('#tn-enabled').onclick = () => {
    if (!c.enabled && !confirm('Turn on overclocking?\n\nGlint will only change clocks when you (or the AI, when you start it) run tests or apply a result. Crashes during tests are expected.')) return;
    c.enabled = !c.enabled; commit(true);
  };
  if (!c.enabled) return;
  const flip = (id, key) => { $(id).onclick = () => { c[key] = !c[key]; commit(true); }; };
  flip('#tn-startup', 'applyAtStartup'); flip('#tn-unlock', 'unlockPower'); flip('#tn-ws', 'allowWorkstation'); flip('#tn-cw', 'crashWatch'); flip('#tn-bo', 'autoBackoff');
  $('#tn-max').onchange = e => { c.maxCrashes = clamp(Math.round(num(e.target.value, 3)), 1, 20); commit(); };
  $('#tn-days').onchange = e => { c.crashWindowDays = clamp(Math.round(num(e.target.value, 7)), 1, 90); commit(); };
  $('#tn-goal').onclick = e => { const b = e.target.closest('[data-g]'); if (!b) return; S.tnGoal = b.dataset.g; $$('#tn-goal button').forEach(x => x.classList.toggle('on', x === b)); };
  $('#tn-budget').onchange = e => { S.tnBudget = num(e.target.value, 45); };
  $('#tn-style').onclick = e => { const b = e.target.closest('[data-s]'); if (!b) return; S.tnStyle = b.dataset.s; tnDraftSave(); $('#tn-gt-row').hidden = S.tnStyle !== 'custom'; $$('#tn-style button').forEach(x => { x.classList.toggle('on', x === b); x.setAttribute('aria-checked', x === b); }); };
  $('#tn-context').oninput = e => { if (e.target.value.length > TN_CTX_MAX) e.target.value = e.target.value.slice(0, TN_CTX_MAX); S.tnContext = e.target.value; tnDraftSave(); tnCtxCount(); };
  $('#tn-goaltext').oninput = e => { if (e.target.value.length > TN_GT_MAX) e.target.value = e.target.value.slice(0, TN_GT_MAX); S.tnGoalText = e.target.value; tnDraftSave(); tnCtxCount(); };
  tnCtxCount();
  $('#tn-ai-start').onclick = () => {
    const context = tnContext().trim().slice(0, TN_CTX_MAX), goalText = tnGoalText().trim().slice(0, TN_GT_MAX);
    if (tnStyle() === 'custom' && !goalText) return toast('Custom goal: describe your goal in the box first', 'error');
    // goal = quiet / balanced / performance / custom (goalText for custom), target = gpu / cpu / all, context = the user's wishes
    const args = { goal: tnStyle(), target: S.tnGoal ?? 'gpu', budgetMinutes: S.tnBudget ?? 45, context };
    if (tnStyle() === 'custom') args.goalText = goalText;
    tuningAsk('aiStart', args);
  };
  $('#tn-ai-stop').onclick = () => tuningAsk('aiStop');
  $('#tn-monitor').onclick = () => tuningAsk('monitor');
  $('#tn-history').onclick = e => {
    const b = e.target.closest('[data-hp]'); if (!b || b.disabled) return;
    tnSetPage(b.dataset.hp === 'prev' ? tnHist.page - 1 : b.dataset.hp === 'next' ? tnHist.page + 1 : num(b.dataset.hp));
  };
  $('#tn-history').onchange = e => { if (e.target.id === 'tn-psize') { tnHist.size = clamp(num(e.target.value, 15), 5, 100); tnHist.page = 0; tnSave(); tnAskHistory(); } };
  $('#tn-pm').onclick = () => openProfileManager();
  manBind();
  $('#tn-apply').onclick = () => tuningAsk('applySaved');
  $('#tn-stock').onclick = () => tuningAsk('stock');
  $('#tn-cpu-stop').onclick = () => tuningAsk('cpuStop');
  $('#tn-cpu-finish').onclick = () => {
    const sr = S.tuning?.cpu?.search;
    const force = sr && !sr.summaryFound;
    if (force && !confirm('The search hasn\'t finished every core. Finish anyway? Untested cores keep the start value.')) return;
    tuningAsk('cpuFinish', { force });
  };
  liveTuning();
}
function liveTuning() {
  const m = S.tuning; if (!m || !T_CFG().enabled || !$('#tn-log')) return;
  const st = m.status ?? {}, ai = m.ai ?? {}, g = st.gpu, cr = m.crashes ?? {};
  const pm = v => (num(v) >= 0 ? '+' : '') + num(v);
  $('#tn-ai-sub').textContent = ai.running ? `Tuning ${ai.goal ?? ''}… (${ai.cli === 'wsl' ? 'Claude Code in WSL' : 'Claude Code'})`
    : !ai.cli ? 'Claude Code not found (Windows or WSL). Install it from claude.ai/code and sign in once with "claude".'
    : ai.error ? 'Last run: ' + ai.error : ai.summary ? 'Last run finished' : `Uses your Claude Code login (${ai.cli === 'wsl' ? 'WSL' : 'Windows'}). It can only run Glint's tuning tools.`;
  $('#tn-ai-start').disabled = !!ai.running || !ai.cli || !!st.busy; $('#tn-ai-stop').disabled = !ai.running;
  const log = $('#tn-log'), atEnd = log.scrollTop + log.clientHeight >= log.scrollHeight - 20;
  const html = TF.timeline(ai, st) + (st.busy && st.progress ? `<div class="tf-note">… ${esc(st.progress)}</div>` : '');
  log.innerHTML = html || '<div class="muted">The AI\'s plan, every test it runs and its summary appear here.</div>';
  if (atEnd) log.scrollTop = log.scrollHeight;
  $('#tn-gpu-name').textContent = g ? g.name : 'GPU';
  $('#tn-gpu-sub').textContent = g ? `Driver ${g.driver} · ${num(g.vramGiB)} GB · power limit ${num(g.powerLimitW?.now)} W now · ${num(g.powerLimitW?.default)} W default · ${num(g.powerLimitW?.max)} W card maximum` : (st.gpuError ?? 'No NVIDIA GPU found');
  $('#tn-ws-row').style.display = g?.workstation ? '' : 'none';
  const sv = st.saved, L = st.limits ?? {};
  $('#tn-gpu').innerHTML = !g ? '' : `
    ${st.reverted ? `<div class="tn-warn">${esc(st.reverted)} <button class="btn small" id="tn-clear">Allow again</button></div>` : ''}
    ${g.blocked ? `<div class="tn-warn">${esc(g.blocked)}</div>` : ''}
    <div class="tn-kv"><span>Now</span><b>core ${pm(g.offsets?.core)} MHz · memory ${pm(g.offsets?.memAb)}</b>
      <span>Saved result</span><b>${sv ? `core ${pm(sv.core)} · memory ${pm(sv.memAb)} <span class="muted">(${esc(new Date(sv.when).toLocaleDateString())})</span>` : '—'}</b>
      <span>This card's limits</span><b>core ≤ ${pm(L.maxCore)} · memory ≤ ${pm(L.maxMemAb)} · card slows down at ${g.slowdownC != null ? num(g.slowdownC) + ' °C' : 'its own limit'} · emergency stop at ${num(L.emergencyCoreC)} °C / ${num(L.emergencyMemC)} °C memory · power ${L.unlockPower ? 'raised to the card maximum' : 'default'}</b>
      ${g.eccCorrected != null ? `<span>ECC corrected errors</span><b>${num(g.eccCorrected)}</b>` : ''}</div>`;
  $('#tn-clear')?.addEventListener('click', () => tuningAsk('clearRevert'));
  liveMan(); liveProfileOc();
  const brief = TF.aiBrief(ai), rb = $('#tn-ai-ran');
  const prevP = st.previousProfile, rs = $('#tn-ai-restore');
  const rsHtml = st.activeIsAi && prevP && own(S.cfg.profiles, prevP) ? `<div class="tn-warn" style="margin-top:10px">Glint is on the AI's profile <b>${esc(S.cfg.activeProfile)}</b>. <button class="btn small" id="tn-restore">Go back to ${esc(prevP)}</button></div>` : '';
  if (rs._html !== rsHtml) { rs._html = rsHtml; rs.innerHTML = rsHtml; }
  rb.textContent = brief ? `${ai.running ? 'Running with' : 'Last run'} \u00b7 ${brief}` : '';
  $('#tn-apply').disabled = !sv || !!st.busy; 
  const cp = m.cpu ?? {}, ci = cp.cpu ?? {}, co = cp.curveOptimizer ?? {}, sr = cp.search;
  $('#tn-cpu-name').textContent = ci.name ?? 'CPU';
  $('#tn-cpu-sub').textContent = co.supported ? `${ci.label ?? ''} · per-core Curve Optimizer${ci.tempC != null ? ` · ${num(ci.tempC).toFixed(0)} °C now` : ''}` : (ci.label ?? '');
  const vals = sr?.values ?? [];
  $('#tn-cpu').innerHTML = (ci.advisory ? `<div class="tn-warn">${esc(ci.advisory)}</div>` : '')
    + (co.why ? `<div class="muted" style="font-size:13px;margin-top:10px">${esc(co.why)}</div>` : '')
    + (cp.goal || cp.goalPlan ? cpuPlan(cp) : '')
    + (cp.stoppedReason ? `<div class="tn-warn">Stopped: ${esc(cp.stoppedReason)}</div>` : '')
    + (sr ? `<div class="tn-kv"><span>Search</span><b>${sr.running ? 'running' : esc(cp.phase ?? '')} · started at ${num(sr.startValue)} · ${num(sr.adjustments)} adjustment${sr.adjustments === 1 ? '' : 's'}${num(sr.wheaSinceStart) ? ` · ${num(sr.wheaSinceStart)} hardware errors` : ''}${sr.peakC ? ` · peak ${num(sr.peakC).toFixed(0)} °C` : ''}</b>
        <span>Per core</span><b class="tn-cores">${vals.map((v, i) => `<span title="core ${i}">${i}: ${num(v)}</span>`).join('')}</b></div>` : co.supported && !co.why ? `<div class="muted" style="font-size:13px;margin-top:10px">Not started. Tune with AI → CPU starts the per-core search (${esc(co.expect ?? 'takes hours')}).</div>` : '')
    + (sr?.biosSheet ? `<div class="label" style="margin-top:12px">Enter these in the BIOS</div><pre class="tn-sheet">${esc(sr.biosSheet)}</pre>` : '');
  $('#tn-cpu-finish').disabled = !sr || cp.phase === 'done'; $('#tn-cpu-stop').disabled = !sr?.running;
  const ev = cr.events ?? [], cores = Object.entries(cr.cpuCoresToLoosen ?? {});
  $('#tn-crash-sub').textContent = cr.watching ? 'Watching (an overclock is applied)' : 'Idle: only watches while an overclock is applied';
  const KIND = { gpu_tdr: 'GPU driver reset', gpu_app_crash: 'Game crash in the NVIDIA driver', whea_corrected: 'CPU hardware error (corrected)', whea_fatal: 'CPU hardware error', unexpected_shutdown: 'Unexpected shutdown', bugcheck: 'Bluescreen' };
  $('#tn-crashes').innerHTML = (cores.length ? `<div class="tn-warn">CPU errors on core ${cores.map(([k, n]) => esc(k) + ' (' + num(n) + '×)').join(', ')}: loosen its Curve Optimizer value by 2.</div>` : '')
    + (ev.length ? `<table class="tn-table"><tbody>${ev.slice(0, 8).map(e => `<tr><td>${esc(new Date(e.time).toLocaleString())}</td><td>${esc(own(KIND, e.kind) ?? e.kind)}</td><td class="muted">${esc(e.actionText ?? '')}</td></tr>`).join('')}</tbody></table>` : '<div class="muted" style="font-size:12px;margin-top:8px">No crashes recorded.</div>');
  $('#tn-cmp').innerHTML = g ? tnCompare(st.compare) : '';
  liveHistory();
}

/* The CPU card's goal plan: what the goal means for the BIOS values besides the Curve Optimizer (all text escaped). */
function cpuPlan(cp) {
  const g = cp.goal, pl = cp.goalPlan ?? {}, row = (k, v) => v == null || v === '' ? '' : `<span>${k}</span><b>${esc(String(v))}</b>`;
  return `<div class="tn-kv" id="tn-cpu-plan">${row('Goal', g ? g.kind + (g.text ? ' (' + g.text + ')' : '') : pl.goal)}${row('Undervolt', pl.undervolt)}${row('Power limits (PBO)', pl.pbo)}${row('Boost override', pl.boostOverride)}${row('Platform limit', pl.platformLimitC != null ? pl.platformLimitC + (typeof pl.platformLimitC === 'number' ? ' °C' : '') : null)}${row('Fans', pl.fans)}${pl.advisoryOnly ? row('Mode', 'advisory only: Glint changes nothing') + row('Why', pl.note) : ''}</div>`
    + (cp.curveOptimizerNote ? `<div class="muted" style="font-size:12.5px;margin-top:8px" id="tn-co-note">${esc(cp.curveOptimizerNote)}</div>` : '');
}

/* Stock vs tuned: bars per metric, % delta (green = better, amber = costs more power / heat). */
function tnCompare(cp) {
  const head = '<div class="label">Stock vs tuned</div>';
  if (!cp?.stock) return head + '<div class="muted" style="font-size:12.5px;margin-top:6px" id="tn-cmp-empty">No stock baseline yet. Run Tune with AI: it measures stock first.</div>';
  if (!cp.tuned) return head + `<div class="muted" style="font-size:12.5px;margin-top:6px" id="tn-cmp-empty">Stock is measured (${cp.stock.loadMhz ? num(cp.stock.loadMhz) + ' MHz' : '—'}${cp.stock.rate ? ', ' + num(cp.stock.rate).toFixed(0) + ' G ops/s' : ''}). No passing tuned result to compare yet.</div>`;
  const a = cp.stock, b = cp.tuned;
  const rows = [
    ['Clock under load', 'loadMhz', v => num(v).toFixed(0) + ' MHz', true],
    ['Compute', 'rate', v => num(v).toFixed(0) + ' G/s', true],
    ['VRAM bandwidth', 'gBps', v => num(v).toFixed(0) + ' GB/s', true],
    ['Power', 'powerW', v => num(v).toFixed(0) + ' W', false],
    ['Peak temp', 'peakCoreC', v => num(v).toFixed(0) + ' °C', false],
  ].filter(([, k]) => num(a[k]) > 0 && num(b[k]) > 0);
  const pmv = v => (num(v) >= 0 ? '+' : '−') + Math.abs(num(v));
  return head + `<div class="tn-cmp-legend"><span><i style="background:#5b6478"></i>Stock</span><span><i style="background:var(--accent)"></i>Tuned (core ${pmv(b.core)} / memory ${pmv(b.memAb)})</span></div>`
    + rows.map(([label, k, f, perf]) => {
      const av = num(a[k]), bv = num(b[k]), mx = Math.max(av, bv), d = (bv - av) / av * 100;
      const cls = Math.abs(d) < 0.5 ? 'flat' : perf ? (d > 0 ? 'gain' : 'cost') : (d > 0 ? 'cost' : 'gain');
      return `<div class="cmp-row" data-cmp="${esc(k)}"><span class="lbl">${esc(label)}</span><div class="cmp-bars">
        <div class="cmp-bar cmp-stock"><i style="width:${(av / mx * 100).toFixed(1)}%"></i><span>${esc(f(av))}</span></div>
        <div class="cmp-bar cmp-tuned"><i style="width:${(bv / mx * 100).toFixed(1)}%"></i><span>${esc(f(bv))}</span></div></div>
        <span class="cmp-delta ${cls}">${d >= 0 ? '+' : '−'}${Math.abs(d).toFixed(1)}%</span></div>`;
    }).join('');
}

/* Test history: every saved test, paginated through the host (history op). The page and size survive refreshes and re-renders. */
const tnHist = (() => {
  let page = 0, size = 15;
  try { page = clamp(Math.round(num(sessionStorage.getItem('glint.tn.page'), 0)), 0, 100000); size = clamp(Math.round(num(localStorage.getItem('glint.tn.psize'), 15)), 5, 100); } catch { }
  return { page, size, data: null };
})();
function tnSave() { try { sessionStorage.setItem('glint.tn.page', String(tnHist.page)); localStorage.setItem('glint.tn.psize', String(tnHist.size)); } catch { } }
function tnAskHistory() { if (T_CFG().enabled) tuningAsk('history', { offset: tnHist.page * tnHist.size, limit: tnHist.size }); }
function tnSetPage(p) {
  const last = Math.max(0, Math.ceil(num(tnHist.data?.total) / tnHist.size) - 1);
  tnHist.page = clamp(Math.round(num(p)), 0, last); tnSave(); tnAskHistory(); liveHistory();
}
function liveHistory() {
  const el = $('#tn-history'); if (!el) return;
  const d = tnHist.data, pm = v => (num(v) >= 0 ? '+' : '') + num(v);
  const items = d?.items ?? (S.tuning?.status?.history ?? []).slice(tnHist.page * tnHist.size, (tnHist.page + 1) * tnHist.size);
  const total = d ? num(d.total) : (S.tuning?.status?.history ?? []).length;
  if (!items.length) { el.innerHTML = '<div class="muted" style="font-size:12px;margin-top:8px">No tests yet.</div>'; return; }
  const pages = Math.max(1, Math.ceil(total / tnHist.size)), cur = Math.min(tnHist.page, pages - 1);
  const nums = [...new Set([0, cur - 1, cur, cur + 1, pages - 1].filter(x => x >= 0 && x < pages))].sort((x, y) => x - y);
  let prev = -1; const btns = [];
  for (const n of nums) { if (prev >= 0 && n > prev + 1) btns.push('<span class="muted">…</span>'); btns.push(`<button class="btn small ${n === cur ? 'cur' : ''}" data-hp="${n}">${n + 1}</button>`); prev = n; }
  const first = cur * tnHist.size + 1;
  el.innerHTML = `<div style="overflow-x:auto"><table class="tn-table"><thead><tr><th>When</th><th>Core</th><th>Memory</th><th>Length</th><th>Result</th><th>Clock</th><th>Compute</th><th>VRAM</th><th>Power</th><th>Peak</th><th title="Average / peak fan duty during the test: a noise proxy">Fans</th><th>Notes</th></tr></thead><tbody>${items.map(r => `
    <tr><td>${esc(new Date(r.time).toLocaleString([], { month: 'short', day: 'numeric', hour: '2-digit', minute: '2-digit' }))}</td><td>${pm(r.core)}</td><td>${pm(r.memAb)}</td><td>${num(r.seconds)} s${r.light ? ' light' : ''}</td>
      <td class="${r.ok ? 'tn-pass' : 'tn-fail'}">${r.ok ? 'pass' : 'fail'}</td>
      <td>${r.loadMhz ? num(r.loadMhz) + ' MHz' : '—'}</td><td>${r.rate ? num(r.rate).toFixed(0) + ' G/s' : '—'}</td><td>${r.gBps ? num(r.gBps).toFixed(0) + ' GB/s' : '—'}</td>
      <td>${r.powerW ? num(r.powerW).toFixed(0) + ' W' : '—'}</td><td>${r.peakCoreC ? num(r.peakCoreC).toFixed(0) + ' °C' : '—'}</td>
      <td class="tn-fans">${r.avgFanPct != null ? `<span class="fan-chip ${num(r.avgFanPct) < 40 ? 'low' : num(r.avgFanPct) < 65 ? 'mid' : 'high'}" title="${r.avgFanRpm ? esc(num(r.avgFanRpm).toFixed(0)) + ' rpm average' : 'average fan duty'}">${num(r.avgFanPct).toFixed(0)}%</span><span class="muted"> / ${num(r.peakFanPct).toFixed(0)}%</span>` : '—'}${TN_STYLES.some(x => x[0] === r.goal) ? ` <span class="muted" title="The goal of the run that made this test">${esc(r.goal)}</span>` : ''}</td>
      <td>${TF.limitChips(r)}${r.why ? ` <span class="muted">${esc(r.why)}</span>` : ''}</td></tr>`).join('')}</tbody></table></div>
    <div class="tn-pager"><button class="btn small" data-hp="prev" ${cur <= 0 ? 'disabled' : ''}>‹ Newer</button>${btns.join('')}<button class="btn small" data-hp="next" ${cur >= pages - 1 ? 'disabled' : ''}>Older ›</button>
      <span class="info" id="tn-hinfo">${first}–${first + items.length - 1} of ${total} tests · page ${cur + 1} of ${pages}</span>
      <select id="tn-psize" class="input" title="Tests per page">${[15, 30, 50].map(n => `<option value="${n}" ${n === tnHist.size ? 'selected' : ''}>${n} / page</option>`).join('')}</select></div>`;
}

/* ------------------------------------------------------------------ profiles: manager, per-profile overclocks, manual overclocking */

// Same rules as the host (ConfigValidator.ProfileNameError + uniqueness): the host re-checks everything.
function profileNameError(name, except) {
  const n = String(name ?? '');
  if (!n.trim()) return 'Give the profile a name';
  if (n.length > 40) return 'At most 40 characters';
  if (/[\u0000-\u001f\u007f-\u009f]/.test(n)) return 'No control characters';
  if (n.trim().toLowerCase() === 'stock') return '"Stock" is reserved (it means no overclock)';
  if (Object.keys(S.cfg.profiles).some(k => k !== except && k.trim().toLowerCase() === n.trim().toLowerCase())) return 'A profile with that name already exists';
  return '';
}
const pmv = v => (num(v) >= 0 ? '+' : '−') + Math.abs(num(v));
/* A profile's overclock from the tuning status: {kind: 'stock' | 'tested' | 'untested', text, driver, when}. */
function profileOc(name) {
  const o = own(S.tuning?.status?.profiles, name);
  if (!o || (num(o.core) === 0 && num(o.memAb) === 0)) return { kind: 'stock', text: 'stock' };
  const tested = num(o.testedCore) >= num(o.core) && num(o.testedMemAb) >= num(o.memAb);
  return { kind: tested ? 'tested' : 'untested', text: `core ${pmv(o.core)} · memory ${pmv(o.memAb)}`, driver: o.driver, when: o.when };
}
const ocBadge = oc => oc.kind === 'stock' ? '<span class="muted">stock</span>'
  : `${esc(oc.text)} <span class="oc-tag ${oc.kind}" title="${oc.kind === 'tested' ? 'Passed a full stress test at or above these offsets' : 'Saved without a passing full test: it may be unstable'}">${oc.kind}</span>`;
const tuningOn = () => !!S.cfg?.tuning?.enabled;
const aiKind = name => { const k = own(S.tuning?.status?.aiProfiles, name); return typeof k === 'string' ? k : null; };
const aiBadge = name => aiKind(name) ? ` <span class="oc-tag ai" title="Created by Tune with AI for the ${esc(aiKind(name))} goal. You can rename or delete it; tuning the same goal again recreates it.">AI</span>` : '';

/* Manager (a dialog from the header button): create a copy, rename, reorder, delete, switch, clear the overclock. */
function openProfileManager() {
  if (S.pm) return;
  S.pm = { rename: null, err: '', prevFocus: document.activeElement };
  const d = document.createElement('div');
  d.id = 'pm'; d.className = 'pm-back';
  d.innerHTML = '<div class="pm-card" role="dialog" aria-modal="true" aria-labelledby="pm-title"></div>';
  document.body.append(d);
  d.addEventListener('mousedown', e => { if (e.target === d) closeProfileManager(); });
  if (tuningOn()) tuningAsk();
  pmRender();
  $('#pm-new')?.focus();
}
function closeProfileManager() {
  const f = S.pm?.prevFocus; $('#pm')?.remove(); S.pm = null;
  try { f?.focus?.(); } catch { }
}
function pmRender() {
  const card = $('#pm .pm-card'); if (!card) return;
  const keep = { new: $('#pm-new')?.value ?? '', from: $('#pm-from')?.value, ren: $('#pm-ren')?.value };
  const af = document.activeElement, afId = af && card.contains(af) ? af.id : '', afSel = afId && af.selectionStart != null ? [af.selectionStart, af.selectionEnd] : null;
  const names = Object.keys(S.cfg.profiles), act = S.cfg.activeProfile, oc = tuningOn();
  const row = (p, i) => {
    const isAct = p === act, o = profileOc(p), editing = S.pm.rename === p;
    return `<tr data-prow="${esc(p)}" class="${isAct ? 'act' : ''}">
      <td class="pm-name">${editing ? `<input type="text" id="pm-ren" maxlength="60" value="${esc(keep.ren ?? p)}" aria-label="New name for ${esc(p)}" spellcheck="false" autocomplete="off"><button class="btn small primary" data-pm="rename-ok">Save</button><button class="btn small" data-pm="rename-no">Cancel</button>`
        : `<b>${esc(p)}</b>${aiBadge(p)}${isAct ? ' <span class="oc-tag active">active</span>' : ''}`}</td>
      <td class="muted">${num(S.cfg.profiles[p]?.length)} rule${S.cfg.profiles[p]?.length === 1 ? '' : 's'}</td>
      ${oc ? `<td>${ocBadge(o)}</td>` : ''}
      <td class="pm-act">
        <button class="icon-btn small" data-pm="up" data-p="${esc(p)}" title="Move up" ${i === 0 ? 'disabled' : ''}>▲</button>
        <button class="icon-btn small" data-pm="down" data-p="${esc(p)}" title="Move down" ${i === names.length - 1 ? 'disabled' : ''}>▼</button>
        <button class="btn small" data-pm="switch" data-p="${esc(p)}" ${isAct ? 'disabled' : ''}>Switch</button>
        <button class="btn small" data-pm="rename" data-p="${esc(p)}">Rename</button>
        <button class="btn small danger" data-pm="delete" data-p="${esc(p)}" ${isAct ? 'disabled title="The active profile can\'t be deleted: switch to another one first"' : ''}>Delete</button>
      </td></tr>`;
  };
  const html = `
    <div class="row between" style="align-items:flex-start;gap:12px"><div style="flex:1;min-width:0"><div class="card-title" id="pm-title">Fan profiles</div><div class="card-sub">Each profile has its own fan rules${oc ? ' and its own GPU overclock' : ''}. Switching applies both.${Object.keys(S.cfg.profiles).some(aiKind) ? ' <span class="oc-tag ai">AI</span> profiles were made by Tune with AI: renaming or deleting them is fine, tuning the same goal again recreates them.' : ''}</div></div>
      <button class="icon-btn" data-pm="close" aria-label="Close">${IC.x}</button></div>
    <div style="overflow-x:auto"><table class="tn-table pm-table"><thead><tr><th>Profile</th><th>Rules</th>${oc ? '<th>Overclock</th>' : ''}<th></th></tr></thead><tbody>${names.map(row).join('')}</tbody></table></div>
    <div class="pm-new row" style="gap:8px;flex-wrap:wrap">
      <input type="text" id="pm-new" maxlength="60" placeholder="New profile name" value="${esc(keep.new)}" aria-label="New profile name" spellcheck="false" autocomplete="off">
      <span class="muted" style="font-size:12.5px">as a copy of</span>
      <select id="pm-from" aria-label="Copy of">${names.map(p => `<option value="${esc(p)}" ${p === (keep.from && own(S.cfg.profiles, keep.from) ? keep.from : act) ? 'selected' : ''}>${esc(p)}</option>`).join('')}</select>
      <button class="btn primary small" data-pm="create">Create</button>
    </div>
    <div class="pm-err" id="pm-err" role="alert"></div>`;
  if (card._html !== html) { card._html = html; card.innerHTML = html; }     // the tuning poll must not rebuild an unchanged dialog under the user's click
  $('#pm-err').textContent = S.pm.err;
  const back = afId && document.getElementById(afId);
  if (back) { back.focus(); if (afSel) try { back.setSelectionRange(...afSel); } catch { } }
  else if (S.pm.rename != null) { const r = $('#pm-ren'); if (r) { r.focus(); r.select(); } }
}
function pmError(t) { S.pm.err = t; const e = $('#pm-err'); if (e) e.textContent = t; }
document.addEventListener('click', e => {
  const b = e.target.closest('#pm [data-pm]'); if (!b || b.disabled) return;
  const op = b.dataset.pm, p = b.dataset.p;
  if (op === 'close') return closeProfileManager();
  if (op === 'switch') { switchProfile(p); return pmRender(); }
  if (op === 'rename') { S.pm.rename = p; S.pm.err = ''; return pmRender(); }
  if (op === 'rename-no') { S.pm.rename = null; S.pm.err = ''; return pmRender(); }
  if (op === 'rename-ok') {
    const from = S.pm.rename, to = $('#pm-ren').value.trim(), err = to === from ? '' : profileNameError(to, from);
    if (err) return pmError(err);
    S.pm.rename = null; S.pm.err = '';
    if (to !== from) { flushCommit(); S.ovrLocalUntil = 0; send({ type: 'profileRename', from, to }); }      // the host renames the profile, its overclock and activeProfile together
    return pmRender();
  }
  if (op === 'create') {
    const name = $('#pm-new').value.trim(), from = $('#pm-from').value, err = profileNameError(name);
    if (err) return pmError(err);
    if (!own(S.cfg.profiles, from)) return pmError('Pick a profile to copy');
    S.cfg.profiles[name] = structuredClone(S.cfg.profiles[from]);
    S.pm.err = ''; $('#pm-new').value = '';
    commit(true); renderProfiles(); return pmRender();
  }
  if (op === 'delete') {
    if (p === S.cfg.activeProfile) return pmError('The active profile can\'t be deleted: switch to another one first');
    if (Object.keys(S.cfg.profiles).length <= 1) return pmError('There must be at least one profile');
    if (!confirm(`Delete the profile "${p}"${profileOc(p).kind !== 'stock' ? ' and its saved overclock' : ''}? Its fan rules can't be recovered.${aiKind(p) ? ' (Tuning the same goal with AI again recreates it.)' : ''}`)) return;
    delete S.cfg.profiles[p]; if (S.profile === p) S.profile = S.cfg.activeProfile;
    S.pm.err = ''; commit(true); renderProfiles(); return pmRender();
  }
  if (op === 'up' || op === 'down') {
    const ks = Object.keys(S.cfg.profiles), i = ks.indexOf(p), j = i + (op === 'up' ? -1 : 1);
    if (i < 0 || j < 0 || j >= ks.length) return;
    [ks[i], ks[j]] = [ks[j], ks[i]];
    S.cfg.profiles = Object.fromEntries(ks.map(k => [k, S.cfg.profiles[k]]));
    commit(true); renderProfiles(); return pmRender();
  }
});
document.addEventListener('keydown', e => {
  if (e.key === 'Escape' && S.pm) { e.stopPropagation(); if (S.pm.rename != null) { S.pm.rename = null; S.pm.err = ''; pmRender(); } else closeProfileManager(); }
  if (e.key === 'Enter' && e.target.id === 'pm-new') $('#pm [data-pm="create"]')?.click();
  if (e.key === 'Enter' && e.target.id === 'pm-ren') $('#pm [data-pm="rename-ok"]')?.click();
});

/* Tuning page: every profile with its overclock, and what you can do with it. */
function liveProfileOc() {
  const el = $('#tn-profiles'); if (!el) return;
  const names = Object.keys(S.cfg.profiles), act = S.cfg.activeProfile;
  const html = `<div style="overflow-x:auto"><table class="tn-table"><thead><tr><th>Profile</th><th>Overclock</th><th>Saved on driver</th><th>Saved</th><th></th></tr></thead><tbody>${names.map(p => {
    const o = profileOc(p), isAct = p === act;
    return `<tr data-ocrow="${esc(p)}" class="${isAct ? 'act' : ''}"><td><b>${esc(p)}</b>${aiBadge(p)}${isAct ? ' <span class="oc-tag active">active</span>' : ''}</td><td>${ocBadge(o)}</td>
      <td class="muted">${o.kind === 'stock' ? '—' : esc(o.driver ?? '—')}</td><td class="muted">${o.kind === 'stock' || !o.when ? '—' : esc(new Date(o.when).toLocaleDateString())}</td>
      <td class="pm-act"><button class="btn small" data-pswitch="${esc(p)}" ${isAct ? 'disabled' : ''}>Switch to it</button><button class="btn small" data-pclear="${esc(p)}" ${o.kind === 'stock' ? 'disabled' : ''}>Clear overclock</button></td></tr>`;
  }).join('')}</tbody></table></div>`;
  if (el._html !== html) { el._html = html; el.innerHTML = html; }     // the 3 s refresh must not rebuild (and swallow clicks on) an unchanged table
}
document.addEventListener('click', e => {
  if (e.target.closest('#tn-restore')) tuningAsk('profileRestore');
  const sw = e.target.closest('#tn-profiles [data-pswitch]'), cl = e.target.closest('#tn-profiles [data-pclear]');
  if (sw && !sw.disabled) switchProfile(sw.dataset.pswitch);
  if (cl && !cl.disabled) {
    const p = cl.dataset.pclear;
    if (own(S.cfg.profiles, p) && confirm(`Clear the overclock saved for "${p}"? It will run at stock clocks${p === S.cfg.activeProfile ? ' now' : ''}.`)) tuningAsk('clearProfile', { profile: p });
  }
});

/* Manual overclocking (GPU card). The sliders live outside the polled markup so a refresh never moves them. */
const MAN_UNITS = { core: 'MHz', mem: '' };
const manState = () => (S.tnMan ??= { core: 0, mem: 0, prof: null });
const manClamp = (v, max) => clamp(Math.round(num(v, 0)), 0, Math.max(0, num(max, 0)));
function manLimits() { const L = S.tuning?.status?.limits ?? {}; return { core: num(L.maxCore, 0), mem: num(L.maxMemAb, 0) }; }
/* Proven = a passing full (not light) test at or above these offsets on this driver (this page's recent history and the
   profiles' saved tested levels). The host decides for real: this only decides whether to ask first. */
function manProven(core, mem) {
  if (core <= 0 && mem <= 0) return true;
  const st = S.tuning?.status ?? {}, drv = st.gpu?.driver;
  if (!drv) return false;
  if ((st.history ?? []).some(h => h.ok && !h.light && h.driver === drv && num(h.core) >= core && num(h.memAb) >= mem)) return true;
  return Object.values(st.profiles ?? {}).some(o => o.driver === drv && num(o.testedCore) >= core && num(o.testedMemAb) >= mem && (num(o.testedCore) > 0 || num(o.testedMemAb) > 0));
}
function manMarkup() {
  const m = manState();
  return `<div class="tn-man" id="tn-man"><div class="label">Manual overclock</div>
    <div class="man-row"><label for="tn-m-core">Core</label><input type="range" id="tn-m-core" min="0" max="500" step="5" value="${m.core}"><input type="number" class="input tn-num" id="tn-m-core-n" min="0" max="500" step="5" value="${m.core}" aria-label="Core offset in MHz"><span class="muted">MHz</span></div>
    <div class="man-row"><label for="tn-m-mem">Memory</label><input type="range" id="tn-m-mem" min="0" max="3000" step="50" value="${m.mem}"><input type="number" class="input tn-num" id="tn-m-mem-n" min="0" max="3000" step="50" value="${m.mem}" aria-label="Memory offset (Afterburner units)"><span class="muted">AB</span></div>
    <div class="muted" style="font-size:12.5px;margin-top:6px" id="tn-m-info"></div>
    <div class="row" style="gap:8px;margin-top:10px;flex-wrap:wrap"><button class="btn" id="tn-m-test" title="A 60 second stress test at these offsets; the card goes back to stock after it">Test 60 s</button><button class="btn" id="tn-m-apply">Apply</button>
      <span class="muted" style="font-size:12.5px">Save to</span><select id="tn-m-prof" aria-label="Profile to save to"></select><button class="btn" id="tn-m-save">Save to profile</button></div>
    <div class="muted" style="font-size:12.5px;margin-top:6px" id="tn-m-power"></div></div>`;
}
function manBind() {
  const m = manState();
  const set = (k, v) => {
    const max = manLimits()[k]; v = manClamp(v, max); m[k] = v;
    $(`#tn-m-${k === 'core' ? 'core' : 'mem'}`).value = v; $(`#tn-m-${k === 'core' ? 'core' : 'mem'}-n`).value = v; liveMan();
  };
  $('#tn-m-core').oninput = e => set('core', e.target.value); $('#tn-m-mem').oninput = e => set('mem', e.target.value);
  $('#tn-m-core-n').onchange = e => set('core', e.target.value); $('#tn-m-mem-n').onchange = e => set('mem', e.target.value);
  $('#tn-m-prof').onchange = e => { m.prof = e.target.value; liveMan(); };
  $('#tn-m-test').onclick = () => tuningAsk('test', { core: m.core, memAb: m.mem, seconds: 60 });
  const ask = (what, extra) => {
    const proven = manProven(m.core, m.mem), args = { core: m.core, memAb: m.mem, ...extra };
    if (!proven) {
      if (!confirm(`Untested overclock\n\ncore ${pmv(m.core)} MHz / memory ${pmv(m.mem)} has not passed a full stress test on this driver. An untested overclock can crash games, reset the GPU driver or blue-screen the PC. Glint's crash watch will back it off, but only after a crash.\n\nRun "Test 60 s" first unless you accept that risk.\n\n${what} anyway?`)) return;
      args.force = true;                                                // the only place the dashboard ever sends force
    }
    return args;
  };
  $('#tn-m-apply').onclick = () => { const a = ask('Apply'); if (a) tuningAsk('manualApply', a); };
  $('#tn-m-save').onclick = () => {
    const p = $('#tn-m-prof').value; if (!own(S.cfg.profiles, p)) return;
    const a = ask(`Save for "${p}"`, { profile: p }); if (a) tuningAsk('manualSave', a);
  };
}
function liveMan() {
  const box = $('#tn-man'); if (!box) return;
  const m = manState(), st = S.tuning?.status ?? {}, g = st.gpu, L = manLimits();
  for (const [k, id] of [['core', 'core'], ['mem', 'mem']]) {
    const max = L[k] || (k === 'core' ? 500 : 3000);
    for (const sfx of ['', '-n']) $(`#tn-m-${id}${sfx}`).max = max;
    m[k] = manClamp(m[k], max);
    $(`#tn-m-${id}`).value = m[k]; if (document.activeElement !== $(`#tn-m-${id}-n`)) $(`#tn-m-${id}-n`).value = m[k];
    $(`#tn-m-${id}`).style.setProperty('--p', (max ? m[k] / max * 100 : 0) + '%');
  }
  const names = Object.keys(S.cfg.profiles);
  if (!names.includes(m.prof)) m.prof = S.cfg.activeProfile;
  const sel = $('#tn-m-prof'), want = names.join('\u0001');
  if (sel.dataset.sig !== want) { sel.dataset.sig = want; sel.innerHTML = names.map(p => `<option value="${esc(p)}">${esc(p)}</option>`).join(''); }
  sel.value = m.prof;
  const proven = manProven(m.core, m.mem), busy = !!st.busy, ok = !!g && g.tunable !== false && !g.blocked;
  const o = g?.offsets ?? {};
  $('#tn-m-info').innerHTML = `Applied now: <b>core ${esc(pmv(o.core))} MHz · memory ${esc(pmv(o.memAb))}</b> · this setting: ${proven ? '<span class="oc-tag tested">proven by a full test</span>' : '<span class="oc-tag untested">untested</span> (applying or saving asks first)'} · limits core ≤ ${esc(pmv(L.core))}, memory ≤ ${esc(pmv(L.mem))}`;
  const pl = g?.powerLimitW ?? {}, unlocked = !!st.limits?.unlockPower;
  $('#tn-m-power').textContent = g ? (unlocked ? `Power limit unlocked: raised to the card's maximum (${num(pl.max)} W) while an overclock is applied` : `Power limit locked at the default (${num(pl.default)} W): turn on "Unlock power limit" below to raise it to ${num(pl.max)} W`) : '';
  for (const id of ['#tn-m-test', '#tn-m-apply', '#tn-m-save', '#tn-m-core', '#tn-m-mem', '#tn-m-core-n', '#tn-m-mem-n', '#tn-m-prof']) $(id).disabled = busy || !ok;
}

/* Game overlay page */
function renderOverlay() {
  const I = S.init;
  $('#page-overlay').innerHTML = `
    <div class="grid">
      <div class="card card-pad ov-card" id="ov-card">
        <div class="row between card-head"><div><div class="card-title">Game overlay</div><div class="card-sub">FPS and sensors on top of your game · no injection, anti-cheat safe</div></div><div class="toggle ${OV_CFG().enabled ? 'on' : ''}" id="ov-enabled" title="Show the overlay"></div></div>
        <div class="ov-grid">
          <div class="ov-col">
            <div class="ov-row2">
              <div class="field"><label for="ov-hotkey">Toggle hotkey</label><input type="text" id="ov-hotkey" value="${esc(OV_CFG().hotkey ?? '')}" placeholder="Ctrl+Shift+O" spellcheck="false" autocomplete="off" aria-describedby="ov-hotkey-err"><div class="ov-err" id="ov-hotkey-err" role="alert"></div></div>
              <div class="field"><label>Position</label>
                <div class="ov-pos" id="ov-pos" role="radiogroup" aria-label="Overlay corner">${['top-left', 'top-right', 'bottom-left', 'bottom-right'].map(p => `<button class="${p} ${OV_CFG().position === p ? 'on' : ''}" data-pos="${p}" role="radio" aria-checked="${OV_CFG().position === p}" title="${p.replace('-', ' ')}"></button>`).join('')}</div></div>
            </div>
            <div class="ov-row2">
              <div class="field"><label for="ov-ox">Offset X (px)</label><input type="number" id="ov-ox" value="${num(OV_CFG().offsetX)}" min="-200" max="4000" step="1"></div>
              <div class="field"><label for="ov-oy">Offset Y (px)</label><input type="number" id="ov-oy" value="${num(OV_CFG().offsetY)}" min="-200" max="4000" step="1"></div>
            </div>
            <div class="field"><label>Size</label>${slider('ov-scale', OV_CFG().scale, 0.5, 4, 0.05, v => Math.round(v * 100) + '%')}</div>
            <div class="field"><label>Overlay opacity</label>${slider('ov-opacity', OV_CFG().opacity, 0.1, 1, 0.05, v => Math.round(v * 100) + '%')}</div>
            <div class="field"><label>Background opacity</label>${slider('ov-bg', OV_CFG().backgroundOpacity, 0, 1, 0.05, v => Math.round(v * 100) + '%')}</div>
            <div class="ov-row2">
              <div class="field"><label>Layout</label><div class="seg" id="ov-layout">${['compact', 'detailed'].map(l => `<button data-l="${l}" class="${OV_CFG().layout === l ? 'on' : ''}">${l[0].toUpperCase() + l.slice(1)}</button>`).join('')}</div></div>
              <div class="field"><label>Accent colour</label><div class="row" style="gap:8px"><input type="color" class="ov-color ${OV_CFG().accent ? '' : 'unset'}" id="ov-accent" value="${color(OV_CFG().accent, '#8b6cff')}" aria-label="Accent colour"><button class="btn small ghost" id="ov-accent-reset" title="Use the Glint gradient">${OV_CFG().accent ? 'Glint gradient' : 'Glint gradient (default)'}</button></div></div>
            </div>
            <div class="row" style="gap:10px"><div class="toggle ${OV_CFG().onlyWhenGame ? 'on' : ''}" id="ov-game" title="Only show while a game is in the foreground"></div><label class="ov-lbl">Only while a game is in the foreground</label></div>
          </div>
          <div class="ov-col">
            <div class="row between"><div class="label">Items <span class="muted" id="ov-count"></span></div><button class="btn small" id="ov-add" aria-expanded="false">${'+'} Add item</button></div>
            <div class="ov-picker hidden" id="ov-picker">
              <div class="search small" style="max-width:none"><svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round"><circle cx="11" cy="11" r="7"/><path d="m20 20-3.5-3.5"/></svg><input type="text" id="ov-q" placeholder="Search sensors, fans, FPS…" spellcheck="false"></div>
              <div class="ov-plist" id="ov-plist"></div>
            </div>
            <div class="ov-items" id="ov-items"></div>
            <div class="label" style="margin-top:6px">Preview</div>
            <div class="ov-stage"><div class="ov-preview" id="ov-preview"></div></div>
            <div class="muted" style="font-size:12px">Drag items or use the arrows to reorder. Sensors that are not reading are left out in the game. Exclusive-fullscreen games may hide the overlay; borderless and windowed show it.</div>
          </div>
        </div>
      </div>
    </div>`;
  bindOverlay();
}

/* Home Assistant (MQTT) card */
function HA() { return (S.cfg.homeAssistant ??= { enabled: false, host: '', port: 1883, tls: false, discoveryPrefix: 'homeassistant', baseTopic: 'glint' }); }
function bindHa() {
  const ha = HA();
  const text = (id, k, trim = true) => $(id).addEventListener('change', e => { ha[k] = trim ? e.target.value.trim() : e.target.value; commit(); });
  text('#ha-host', 'host'); text('#ha-user', 'username'); text('#ha-prefix', 'discoveryPrefix'); text('#ha-topic', 'baseTopic');
  $('#ha-port').addEventListener('change', e => { ha.port = clamp(Math.round(+e.target.value) || 1883, 1, 65535); e.target.value = ha.port; commit(); });
  $('#ha-enabled').onclick = e => { ha.enabled = !ha.enabled; e.currentTarget.classList.toggle('on', ha.enabled); commit(); liveSettings(); };
  $('#ha-tls').onclick = e => {
    ha.tls = !ha.tls; e.currentTarget.classList.toggle('on', ha.tls);
    if (ha.tls && ha.port === 1883) ha.port = 8883; else if (!ha.tls && ha.port === 8883) ha.port = 1883;
    $('#ha-port').value = ha.port; commit();
  };
  // write-only: only a newly typed password is sent (once); "Clear" sends "" to remove the saved one
  $('#ha-pass').addEventListener('change', e => { if (e.target.value === '') return; ha.password = e.target.value; e.target.value = ''; commit(); });
  $('#ha-pass-clear').onclick = () => { ha.password = ''; $('#ha-pass').value = ''; commit(); };
  haPassUi(); liveHa();
}
function haPassUi() {
  const el = $('#ha-pass'); if (!el) return;
  el.placeholder = S.init.haHasPassword ? 'Saved · type to replace' : 'Not set';
  $('#ha-pass-clear').style.visibility = S.init.haHasPassword ? 'visible' : 'hidden';
}
function liveHa() {
  const el = $('#ha-status'); if (!el) return;
  const ha = HA(), st = S.state?.ha;
  // TODO(host): status comes from HomeAssistantBridge.Status() in the state message; without it only enabled/disabled is known.
  let cls = 'off', txt = 'Off';
  if (ha.enabled && !st) { cls = 'ok'; txt = 'Enabled'; }
  else if (ha.enabled && st?.connected) { cls = 'ok'; txt = `Connected to ${st.broker}${st.entities ? ` · ${st.entities} entities` : ''}`; }
  else if (ha.enabled) { cls = st?.lastError ? 'bad' : 'warn'; txt = st?.lastError ? `Not connected: ${st.lastError}` : `Connecting to ${st?.broker || ha.host || 'broker'}…`; }
  $('.pdot', el).className = 'pdot ' + cls;
  $('span:last-child', el).textContent = txt;
}

/* Game overlay card */
const OV = { open: false, q: '', sig: '', drag: null };
const OV_DEFAULT_ITEMS = () => ['fps', 'low1', 'cpu', 'gpu', 'gpu.hotspot', 'liquid'].map(source => ({ source }));
function OV_CFG() {
  return (S.cfg.overlay ??= { enabled: false, hotkey: 'Ctrl+Shift+O', position: 'top-left', offsetX: 16, offsetY: 16, scale: 1, opacity: 1, backgroundOpacity: 0.72, accent: null, layout: 'detailed', onlyWhenGame: false, items: OV_DEFAULT_ITEMS() });
}
const OV_ITEMS_MAX = 24, OV_LABEL_MAX = 32;
const OV_BUILTINS = [['fps', 'FPS', 'Frames per second'], ['frametime', 'Frame time', 'Milliseconds per frame'], ['low1', '1% low', '99th-percentile frame rate over ~10 s'], ['time', 'Clock', 'Time of day'], ['glint.cpu', 'Glint CPU', "Glint's own CPU use"]];
const OV_FRIENDLY = [['cpu', 'CPU'], ['gpu', 'GPU'], ['gpu.hotspot', 'Hotspot'], ['liquid', 'Liquid'], ['storage', 'SSD'], ['ram', 'RAM']];
const OV_BUILTIN_LABEL = { fps: 'FPS', frametime: 'Frame', low1: '1% low', time: 'Time', 'glint.cpu': 'Glint', cpu: 'CPU', gpu: 'GPU', 'gpu.hotspot': 'Hotspot', liquid: 'Liquid', storage: 'SSD', ram: 'RAM' };

// "Ctrl+Shift+O", "Alt+F10", "" (none). Light check only: the host validates fully.
function hotkeyError(s) {
  s = s.trim(); if (!s) return null;
  const parts = s.split('+').map(x => x.trim().toLowerCase());
  const mods = new Set(['ctrl', 'control', 'shift', 'alt', 'win', 'windows']);
  const key = parts.pop();
  if (!key || parts.some(x => !mods.has(x))) return 'Use modifiers plus one key, like Ctrl+Shift+O';
  if (mods.has(key)) return 'Add a key after the modifiers, like Ctrl+Shift+O';
  if (!/^([a-z0-9]|f([1-9]|1\d|2[0-4])|[a-z]{3,12})$/.test(key)) return `"${key}" is not a key name`;
  if (!parts.length && !/^f([1-9]|1\d|2[0-4])$/.test(key)) return 'Needs Ctrl, Shift, Alt or Win (function keys can stand alone)';
  return null;
}
// flat list of catalog sensors with the id the overlay should use for each
function ovCatalogList() {
  const out = [], fanIds = new Set((S.init.fans ?? []).map(f => f.id));
  const walk = (h, path) => {
    const name = h.name;
    for (const x of h.sensors) {
      const fr = (x.aliases ?? []).find(a => !a.startsWith('/') && !fanIds.has(a));
      out.push({ src: fr ?? x.id, id: x.id, name: x.name, hw: path ? path + ' › ' + name : name, type: x.type, unit: x.unit, value: x.value });
    }
    h.sub?.forEach(s => walk(s, path ? path + ' › ' + name : name));
  };
  S.sens?.hardware.forEach(h => walk(h, ''));
  return out;
}
function ovIndex() {
  const m = new Map();
  for (const x of ovCatalogList()) { m.set(x.src, x); m.set(x.id, x); }
  return m;
}
function ovDefaultLabel(src) {
  if (own(OV_BUILTIN_LABEL, src)) return OV_BUILTIN_LABEL[src];
  const m = /^(fan|duty):(.+)$/i.exec(src);
  if (m) return fanLabel(m[2]);
  if (S.cfg.sensorNames?.[src]) return S.cfg.sensorNames[src];
  const c = ovIndex().get(src); if (c) return c.name;
  const leaf = src.replace(/\/+$/, ''); return leaf.slice(leaf.lastIndexOf('/') + 1);
}
function ovFmt(v, unit) {
  const n = unit === '°C' || unit === 'rpm' || unit === '%' || unit === 'MHz' || unit === 'W' || unit === 'fps' || Math.abs(v) >= 100 ? Math.round(v).toString() : v.toFixed(1);
  return n;
}
const OV_SAMPLE = { fps: [144, 'fps'], frametime: [6.9, 'ms'], low1: [118, ''] };
// -> { v, unit } or null (not reading)
function ovValue(src, idx) {
  const st = S.state ?? {};
  if (OV_SAMPLE[src]) return { v: OV_SAMPLE[src][0], unit: OV_SAMPLE[src][1], hero: src === 'fps' };
  if (src === 'time') { const d = new Date(); return { text: d.toTimeString().slice(0, 5), unit: '' }; }
  if (src === 'glint.cpu') return S.selfCpu == null ? null : { v: S.selfCpu, unit: '%' };
  let m = /^fan:(.+)$/i.exec(src); if (m) return st.rpm?.[m[1]] == null ? null : { v: st.rpm[m[1]], unit: 'rpm' };
  m = /^duty:(.+)$/i.exec(src); if (m) return st.duty?.[m[1]] == null ? null : { v: st.duty[m[1]], unit: '%' };
  if (st.sensors?.[src] != null) return { v: st.sensors[src], unit: idx.get(src)?.unit ?? '°C' };
  const c = idx.get(src);
  if (c && c.value != null && Date.now() - (OV.catAt ?? 0) < 4000) return { v: c.value, unit: c.unit ?? '' };
  return null;
}
function ovPreview() {
  const el = $('#ov-preview'); if (!el) return;
  const o = OV_CFG(), idx = ovIndex();
  const acc = o.accent ? color(o.accent, null) : null;     // an invalid accent from config.json shows as the default gradient
  const accent = acc ? `background:${acc}` : '';
  const rows = o.items.map(it => {
    const val = ovValue(it.source, idx);
    const label = (it.label && it.label.trim()) || ovDefaultLabel(it.source);
    const col = color(it.color, null) ? ` style="color:${color(it.color)}"` : '';
    const txt = val ? (val.text ?? ovFmt(val.v, val.unit)) : '–';
    const hero = val?.hero && o.layout === 'detailed';
    return { label, txt, unit: val?.unit ?? '', col, hero, off: !val, fps: it.source === 'fps' };
  });
  el.className = 'ov-preview ' + o.layout + (acc ? ' own' : '');
  el.style.setProperty('--ov-bg', `rgba(14,15,22,${num(o.backgroundOpacity, 0.72)})`);
  el.style.opacity = num(o.opacity, 1);
  el.style.setProperty('--ov-scale', Math.min(1.4, Math.max(0.7, num(o.scale, 1))));
  el.style.setProperty('--ov-accent', acc ?? '');
  el.classList.toggle('flat', num(o.backgroundOpacity, 0.72) < 0.02);
  el.dataset.pos = o.position;
  el.innerHTML = rows.length
    ? `<span class="ov-bar" style="${accent}"></span><div class="ov-lines">${rows.map(r => r.hero
      ? `<div class="ov-line hero"><span class="ov-big ${acc ? '' : 'grad'}" ${acc ? `style="color:${acc}"` : ''}>${esc(r.txt)}</span><span class="ov-l">${esc(r.label)}</span></div>`
      : `<div class="ov-line ${r.off ? 'off' : ''}"><span class="ov-l">${esc(r.label)}</span><span class="ov-v ${r.fps && !acc ? 'grad' : ''}"${r.fps && acc ? ` style="color:${acc}"` : r.col}>${esc(r.txt)}</span>${r.unit ? `<span class="ov-u">${esc(r.unit)}</span>` : ''}</div>`).join('')}</div>`
    : '<div class="muted" style="padding:6px 10px;font-size:12px">No items: the overlay shows nothing</div>';
}
function liveOverlay() { ovPreview(); }
function ovItems() {
  const o = OV_CFG(), box = $('#ov-items'); if (!box) return;
  $('#ov-count').textContent = `${o.items.length} / ${OV_ITEMS_MAX}`;
  $('#ov-add').disabled = o.items.length >= OV_ITEMS_MAX;
  box.innerHTML = o.items.length ? o.items.map((it, i) => `
    <div class="ov-item" draggable="true" data-i="${i}">
      <span class="ov-grip" title="Drag to reorder" aria-hidden="true">⋮⋮</span>
      <span class="ov-src"><span class="ov-sname">${esc(ovDefaultLabel(it.source))}</span><span class="ov-sid" title="${esc(it.source)}">${esc(it.source)}</span></span>
      <input type="text" class="ov-label" data-i="${i}" value="${esc(it.label ?? '')}" placeholder="${esc(ovDefaultLabel(it.source))}" maxlength="${OV_LABEL_MAX}" spellcheck="false" aria-label="Label for ${esc(it.source)}">
      <input type="color" class="ov-color ${it.color ? '' : 'unset'}" data-i="${i}" value="${color(it.color, '#ffffff')}" aria-label="Colour for ${esc(it.source)}" title="Value colour">
      <button class="icon-btn ov-b" data-act="clr" data-i="${i}" title="Default colour" ${it.color ? '' : 'disabled'}>↺</button>
      <button class="icon-btn ov-b" data-act="up" data-i="${i}" title="Move up" ${i === 0 ? 'disabled' : ''}>↑</button>
      <button class="icon-btn ov-b" data-act="down" data-i="${i}" title="Move down" ${i === o.items.length - 1 ? 'disabled' : ''}>↓</button>
      <button class="icon-btn ov-b" data-act="rm" data-i="${i}" title="Remove">×</button>
    </div>`).join('') : '<div class="empty" style="padding:14px">No items yet. Add FPS, a temperature or a fan.</div>';
  ovPreview();
}
function ovPickerList() {
  const list = $('#ov-plist'); if (!list) return;
  const q = OV.q.trim().toLowerCase(), words = q.split(/\s+/).filter(Boolean);
  const match = (...f) => { const s = f.join(' ').toLowerCase(); return words.every(w => s.includes(w)); };
  const groups = [];
  const g1 = OV_BUILTINS.filter(([s, n, d]) => match(n, s, d)).map(([s, n, d]) => ({ src: s, name: n, sub: d }));
  if (g1.length) groups.push(['Built-in', g1]);
  const st = S.state?.sensors ?? {};
  const g2 = OV_FRIENDLY.filter(([s, n]) => st[s] != null && match(n, s, 'temperature')).map(([s, n]) => ({ src: s, name: n, sub: s, v: st[s] + ' °C' }));
  if (g2.length) groups.push(['Common temperatures', g2]);
  const fans = (S.init.fans ?? []);
  const g3 = fans.flatMap(f => [['fan', 'speed', 'rpm'], ['duty', 'duty', '%']].map(([k, w, u]) => ({ src: `${k}:${f.id}`, name: `${fanLabel(f.id)} ${w}`, sub: `${k}:${f.id}` }))).filter(x => match(x.name, x.src, 'fan'));
  if (g3.length) groups.push(['Fans', g3]);
  const by = new Map();
  for (const x of ovCatalogList()) {
    if (!match(x.name, x.hw, x.type, x.src, x.id)) continue;
    if (!by.has(x.hw)) by.set(x.hw, []);
    by.get(x.hw).push({ src: x.src, name: x.name, sub: x.type + (x.src !== x.id ? ' · ' + x.src : ''), v: x.value == null ? '' : fmtSensor(x.value, x.type, x.unit) });
  }
  for (const [hw, items] of by) groups.push([hw, items]);
  list.innerHTML = groups.length ? groups.map(([name, items]) => `<div class="ov-pg"><div class="ov-ph">${esc(name)}<span class="sg-n">${items.length}</span></div>${items.map(x =>
    `<button class="ov-pick" data-src="${esc(x.src)}" title="${esc(x.src)}"><span class="ov-pn">${esc(x.name)}</span><span class="ov-ps">${esc(x.sub ?? '')}</span><span class="ov-pv num">${esc(x.v ?? '')}</span></button>`).join('')}</div>`).join('')
    : `<div class="empty" style="padding:14px">${S.sens ? `Nothing matches “${esc(OV.q)}”.` : 'Reading every sensor…'}</div>`;
}
function ovCatalog() {
  OV.catAt = Date.now();
  if (!OV.open || !$('#ov-plist')) return ovPreview();
  const sig = sensSig(S.sens) + '|' + (S.init.fans?.length ?? 0);
  if (sig !== OV.sig) { OV.sig = sig; ovPickerList(); }
  else { const idx = ovIndex(); for (const b of $$('#ov-plist .ov-pick')) { const c = idx.get(b.dataset.src); if (c) b.querySelector('.ov-pv').textContent = c.value == null ? '' : fmtSensor(c.value, c.type, c.unit); } }
  ovPreview();
}
function bindOverlay() {
  const o = OV_CFG(); OV.open = false; OV.q = ''; OV.sig = '';
  const pic = $('#ov-picker');
  $('#ov-enabled').onclick = e => { o.enabled = !o.enabled; e.currentTarget.classList.toggle('on', o.enabled); commit(); };
  $('#ov-game').onclick = e => { o.onlyWhenGame = !o.onlyWhenGame; e.currentTarget.classList.toggle('on', o.onlyWhenGame); commit(); };
  const hk = $('#ov-hotkey');
  const hkCheck = () => { const err = hotkeyError(hk.value); hk.classList.toggle('invalid', !!err); hk.setAttribute('aria-invalid', !!err); $('#ov-hotkey-err').textContent = err ?? ''; return err; };
  hk.addEventListener('input', hkCheck);
  hk.addEventListener('change', () => { if (hkCheck()) return; o.hotkey = hk.value.trim(); commit(); });
  $('#ov-pos').onclick = e => {
    const b = e.target.closest('[data-pos]'); if (!b) return;
    o.position = b.dataset.pos;
    $$('#ov-pos button').forEach(x => { x.classList.toggle('on', x === b); x.setAttribute('aria-checked', x === b); });
    commit(); ovPreview();
  };
  const numField = (id, k) => $(id).addEventListener('change', e => { o[k] = clamp(Math.round(+e.target.value) || 0, -200, 4000); e.target.value = o[k]; commit(); });
  numField('#ov-ox', 'offsetX'); numField('#ov-oy', 'offsetY');
  const pct = v => Math.round(v * 100) + '%';
  bindSlider('ov-scale', pct, v => { o.scale = +v.toFixed(2); commit(); ovPreview(); });
  bindSlider('ov-opacity', pct, v => { o.opacity = +v.toFixed(2); commit(); ovPreview(); });
  bindSlider('ov-bg', pct, v => { o.backgroundOpacity = +v.toFixed(2); commit(); ovPreview(); });
  $('#ov-layout').onclick = e => {
    const b = e.target.closest('[data-l]'); if (!b) return;
    o.layout = b.dataset.l; $$('#ov-layout button').forEach(x => x.classList.toggle('on', x === b)); commit(); ovPreview();
  };
  $('#ov-accent').addEventListener('input', e => { o.accent = e.target.value; e.target.classList.remove('unset'); $('#ov-accent-reset').textContent = 'Glint gradient'; commit(); ovPreview(); });
  $('#ov-accent-reset').onclick = () => { o.accent = null; $('#ov-accent').classList.add('unset'); $('#ov-accent-reset').textContent = 'Glint gradient (default)'; commit(); ovPreview(); };

  // items
  const box = $('#ov-items');
  box.addEventListener('input', e => {
    const i = +e.target.dataset.i;
    if (e.target.classList.contains('ov-label')) { o.items[i].label = e.target.value.trim() === '' ? null : e.target.value.slice(0, OV_LABEL_MAX); if (o.items[i].label == null) delete o.items[i].label; commit(); ovPreview(); }
    else if (e.target.classList.contains('ov-color')) { o.items[i].color = e.target.value; e.target.classList.remove('unset'); e.target.nextElementSibling.disabled = false; commit(); ovPreview(); }
  });
  box.addEventListener('click', e => {
    const b = e.target.closest('[data-act]'); if (!b || b.disabled) return;
    const i = +b.dataset.i, a = b.dataset.act, it = o.items;
    if (a === 'rm') it.splice(i, 1);
    else if (a === 'up' && i > 0) [it[i - 1], it[i]] = [it[i], it[i - 1]];
    else if (a === 'down' && i < it.length - 1) [it[i + 1], it[i]] = [it[i], it[i + 1]];
    else if (a === 'clr') delete it[i].color;
    commit(); ovItems();
    if (a === 'up' || a === 'down') $(`.ov-b[data-act="${a}"][data-i="${a === 'up' ? i - 1 : i + 1}"]`)?.focus();
  });
  box.addEventListener('dragstart', e => { const r = e.target.closest?.('.ov-item'); if (!r) return; OV.drag = +r.dataset.i; r.classList.add('drag'); e.dataTransfer.effectAllowed = 'move'; e.dataTransfer.setData('text/plain', String(OV.drag)); });
  box.addEventListener('dragend', () => { OV.drag = null; $$('.ov-item', box).forEach(r => r.classList.remove('drag', 'over')); });
  box.addEventListener('dragover', e => { if (OV.drag == null) return; e.preventDefault(); $$('.ov-item', box).forEach(r => r.classList.toggle('over', r === e.target.closest('.ov-item'))); });
  box.addEventListener('drop', e => {
    const r = e.target.closest('.ov-item'); if (OV.drag == null || !r) return; e.preventDefault();
    const to = +r.dataset.i, from = OV.drag; OV.drag = null;
    if (to !== from) { const [m] = o.items.splice(from, 1); o.items.splice(to, 0, m); commit(); }
    ovItems();
  });

  // picker
  $('#ov-add').onclick = () => {
    OV.open = !OV.open; pic.classList.toggle('hidden', !OV.open); $('#ov-add').setAttribute('aria-expanded', OV.open);
    if (OV.open) { send({ type: 'sensorsWanted' }); OV.sig = ''; ovPickerList(); $('#ov-q').focus(); }
  };
  $('#ov-q').oninput = e => { OV.q = e.target.value; ovPickerList(); };
  $('#ov-plist').onclick = e => {
    const b = e.target.closest('.ov-pick'); if (!b || o.items.length >= OV_ITEMS_MAX) return;
    o.items.push({ source: b.dataset.src }); commit(); ovItems();
    b.classList.add('added'); setTimeout(() => b.classList.remove('added'), 700);
    toast(`Added ${ovDefaultLabel(b.dataset.src)}`);
  };
  ovItems();
}
function liveSettings() {
  const st = S.state; if (!st) return;
  $('#p-cpu').textContent = S.selfCpu.toFixed(2) + '%';
  $('#p-mem').textContent = st.perf.privateMB + ' MB';
  $('#p-fps').textContent = st.perf.fps + ' fps';
  $('#p-devs').innerHTML = Object.entries(st.perf.devices).map(([k, v]) => `<tr><td>${esc(hubName(k))}</td><td class="num">${esc(v)} fps</td></tr>`).join('');
  liveHa();
  liveUpdate();
  liveOpenRgb();
  ovPreview();
}

/* Overrides (set from Home Assistant / the API) arrive with the live state so the page never shows or saves a stale one. */
function syncOverrides(ov) {
  if (!ov || Date.now() < (S.ovrLocalUntil ?? 0)) return;
  let changed = false;
  for (const [p, list] of Object.entries(ov)) (S.cfg.profiles[p] ?? []).forEach((r, i) => {
    const v = list[i] ?? null;
    if ((r.override ?? null) !== v) { if (v == null) delete r.override; else r.override = v; changed = true; }
  });
  if (changed && (S.page === 'cooling' || S.page === 'overview')) renderPage(S.page);
}

/* ------------------------------------------------------------------ accessibility */
// Custom controls (toggles, chips built from divs) get keyboard focus, a role and Enter/Space activation.
const FOCUSABLE = '.toggle, .fan-chip, .rule-item, .rule-card, .lg-zone, .lg-name';
function a11y(root = document) {
  for (const el of root.querySelectorAll(FOCUSABLE)) {
    if (!el.hasAttribute('tabindex')) el.tabIndex = 0;
    if (el.classList.contains('toggle')) { el.setAttribute('role', 'switch'); el.setAttribute('aria-checked', el.classList.contains('on')); }
    else if (el.classList.contains('fan-chip')) { el.setAttribute('role', 'checkbox'); el.setAttribute('aria-checked', el.classList.contains('on')); }
    else el.setAttribute('role', 'button');
  }
  for (const el of root.querySelectorAll('button[title]:not([aria-label]), .icon-btn[title]:not([aria-label])')) el.setAttribute('aria-label', el.title);
}
new MutationObserver(muts => {
  for (const m of muts) {
    if (m.type === 'attributes') { const el = m.target; if (el.matches?.('.toggle, .fan-chip')) el.setAttribute('aria-checked', el.classList.contains('on')); }
    else if (m.addedNodes.length) { a11y(document); return; }
  }
}).observe(document.body, { childList: true, subtree: true, attributes: true, attributeFilter: ['class'] });
document.addEventListener('keydown', e => {
  if ((e.key === 'Enter' || e.key === ' ') && e.target.matches?.(FOCUSABLE)) { e.preventDefault(); e.target.click(); }
});

/* ------------------------------------------------------------------ boot */
send({ type: 'ready' });
