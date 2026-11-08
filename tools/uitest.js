// Headless UI test for the Glint dashboard (web/), driven against mock.js.
// usage: node uitest.js <path-to-web/index.html> <report.json>   (browser: $CHROME_PATH, default /usr/bin/google-chrome)
// Every check asserts on the config the page sends back to the host, not on pixels.
const puppeteer = require('puppeteer-core');
const fs = require('fs');

(async () => {
  try {
  const [, , page, out] = process.argv;
  const b = await puppeteer.launch({ executablePath: process.env.CHROME_PATH || '/usr/bin/google-chrome', headless: 'new', args: ['--no-sandbox'] });
  const p = await b.newPage();
  await p.setViewport({ width: 1320, height: 860 });
  p.setDefaultTimeout(4000);
  const errors = [], checks = [];
  p.on('pageerror', e => errors.push(e.message));
  p.on('console', m => { if (m.type() === 'error') errors.push(m.text()); });
  const wait = ms => new Promise(r => setTimeout(r, ms));
  const cfg = async () => { await wait(350); return p.evaluate(() => window.__lastConfig); };
  const check = (name, pass, detail = '') => { checks.push({ name, pass: !!pass, detail: String(detail) }); console.log((pass ? 'ok   ' : 'FAIL ') + name + (detail !== '' ? '  ' + detail : '')); };
  const nav = async pg => { await p.click(`#nav button[data-page="${pg}"]`); await wait(250); };

  await p.goto('file://' + page);
  await wait(700);

  // --- every page renders
  for (const pg of ['overview', 'cooling', 'sensors', 'lighting', 'devices', 'overlay', 'tuning', 'setup', 'settings']) {
    await nav(pg);
    const n = await p.$eval(`#page-${pg}`, el => el.children.length);
    check(`page ${pg} renders`, n > 0, `${n} children`);
  }

  // --- overview
  await nav('overview');
  check('overview shows 3 hero temps', (await p.$$('[data-tile]')).length === 3);
  check('overview lists other sensors', (await p.$$('[data-small]')).length >= 3, (await p.$$('[data-small]')).length);
  await p.click('#profile-seg button:nth-child(1)');
  let c = await cfg();
  check('profile switch sends activeProfile', c?.activeProfile === (await p.$eval('#profile-seg button:nth-child(1)', e => e.dataset.p)), c?.activeProfile);
  // global fan-control switch: overview toggle off → config + notice on overview and cooling; cooling button turns it back on
  check('fan control toggle starts on, no notice', !!(await p.$('[data-fancontrol].on')) && !(await p.$('[data-fanoff]')));
  await p.click('[data-fancontrol]');
  c = await cfg();
  await wait(100);
  check('fan control toggle off → fanControl:false sent, notice shown', c?.fanControl === false && !!(await p.$('[data-fanoff]')) && !(await p.$('[data-fancontrol].on')), String(c?.fanControl));
  await nav('cooling');
  check('cooling page shows the fan-control-off notice', !!(await p.$('#page-cooling [data-fanoff]')));
  check('cooling page has its own fan-control toggle, showing off', !!(await p.$('#c-fancontrol')) && !(await p.$('#c-fancontrol.on')));
  await p.click('#c-fancontrol');
  c = await cfg();
  await wait(100);
  check('cooling toggle on → fanControl:true, notice gone, toggle on', c?.fanControl === true && !(await p.$('#page-cooling [data-fanoff]')) && !!(await p.$('#c-fancontrol.on')), String(c?.fanControl));
  await p.click('#c-fancontrol');
  c = await cfg();
  await wait(100);
  check('cooling toggle off → fanControl:false, notice shown', c?.fanControl === false && !!(await p.$('#page-cooling [data-fanoff]')) && !(await p.$('#c-fancontrol.on')), String(c?.fanControl));
  await p.click('#c-fancontrol');
  c = await cfg();
  await wait(100);
  check('cooling toggle back on', c?.fanControl === true && !!(await p.$('#c-fancontrol.on')), String(c?.fanControl));
  await nav('overview');

  // --- cooling
  await nav('cooling');
  const prof = await p.$eval('#c-profile', e => e.value);
  const rules = await p.$$('[data-ri]');
  await p.click(`[data-ri="${rules.length - 1}"]`); await wait(200);
  const idx = rules.length - 1;
  await p.click('#c-combine button[data-c="average"]');
  c = await cfg();
  check('combine → average', c.profiles[prof][idx].combine === 'average', c.profiles[prof][idx].combine);
  const nIn = c.profiles[prof][idx].inputs.length;
  await p.click('#c-addin'); c = await cfg();
  check('add sensor input', c.profiles[prof][idx].inputs.length === nIn + 1, `${nIn} → ${c.profiles[prof][idx].inputs.length}`);
  await p.click(`[data-rm="${nIn}"]`); c = await cfg();
  check('remove sensor input', c.profiles[prof][idx].inputs.length === nIn);
  // drag first handle up
  const curveName = await p.$eval('#c-curve', e => e.value);
  const before = JSON.stringify(c.curves[curveName][1]);
  await p.$eval('#curve', e => e.scrollIntoView({ block: 'center' })); await wait(150);
  const h = await p.$('[data-h="1"] circle:last-child'); const bb = await h.boundingBox();
  await p.mouse.move(bb.x + bb.width / 2, bb.y + bb.height / 2); await p.mouse.down();
  await p.mouse.move(bb.x + bb.width / 2 + 10, bb.y - 60, { steps: 6 }); await p.mouse.up();
  c = await cfg();
  check('drag curve point changes curve', JSON.stringify(c.curves[curveName][1]) !== before, `${before} → ${JSON.stringify(c.curves[curveName][1])}`);
  const nPts = c.curves[curveName].length;
  await p.$eval('#curve', e => e.scrollIntoView({ block: 'center' })); await wait(150);
  const svg = await (await p.$('#curve')).boundingBox();
  // aim at the midpoint between the first two curve points, at 50% duty (never on a handle)
  const aim = await p.evaluate(() => {
    const pts = S.cfg.curves[S.curve], t = Math.round((pts[0][0] + pts[1][0]) / 2);
    const el = document.getElementById('curve'), W = el.clientWidth, H = el.clientHeight;
    const pw = W - CX.padL - CX.padR, ph = H - CX.padT - CX.padB;
    return { x: (CX.padL + (t - CX.t0) / (CX.t1 - CX.t0) * pw) / W, y: (CX.padT + 0.5 * ph) / H };
  });
  await p.mouse.click(svg.x + svg.width * aim.x, svg.y + svg.height * aim.y, { count: 2 });
  c = await cfg();
  check('double-click adds curve point', c.curves[curveName].length === nPts + 1, `${nPts} → ${c.curves[curveName].length}`);
  // fan assignment moves a fan between rules
  const mbFan = await p.$eval('.fan-chip[data-fan^="mb:"]', e => e.dataset.fan);
  await p.click(`.fan-chip[data-fan="${mbFan}"]`); c = await cfg();
  {
    const gpu = await p.$$eval('.group-label', els => els.filter(e => e.textContent.startsWith('Graphics card · ')).map(e => [...e.nextElementSibling.querySelectorAll('.fan-chip')].map(c => c.textContent)));
    check('cooling: GPU fans listed under the card\'s name as "GPU fan N", on driver control while unassigned',
      gpu.length === 1 && gpu[0].length === 2 && gpu[0].every((t, i) => t.startsWith(`GPU fan ${i + 1}`) && t.includes('Driver control')), JSON.stringify(gpu));
  }
  {
    await nav('devices');
    const card = await p.$eval('[data-gpucard="gpu"]', e => e.textContent).catch(() => '');
    check('devices: graphics card listed with its model, fan names and driver/rule status', card.includes('Graphics card') && card.includes('Demo Graphics 9000') && card.includes('2 fans') && (await p.$$('[data-gpucard="gpu"] [data-fanname]')).length === 2, card.slice(0, 160));
    await nav('plugins');
    const row = await p.$$eval('.plug-row', rs => rs.filter(r => r.textContent.includes('Graphics card fans')).map(r => ({ t: r.textContent, open: !!r.querySelector('[data-open]'), del: !!r.querySelector('[data-rm]'), en: !!r.querySelector('[data-en="builtin-gpu-fans"]') })));
    check('plugins: built-in "Graphics card fans" entry, switchable, no edit/delete', row.length === 1 && row[0].t.includes('Built in') && !row[0].t.includes('USB device') && row[0].en && !row[0].open && !row[0].del, JSON.stringify(row));
    await nav('cooling');
  }
  check('assign motherboard header to rule', c.profiles[prof][idx].fans.includes(mbFan), mbFan);
  const other = c.profiles[prof].findIndex((r, i) => i !== idx && r.fans.length);
  const stolen = c.profiles[prof][other].fans[0];
  await p.click(`.fan-chip[data-fan="${stolen}"]`); c = await cfg();
  check('fan belongs to exactly one rule', c.profiles[prof][idx].fans.includes(stolen) && !c.profiles[prof][other].fans.includes(stolen), stolen);
  await p.click('#c-fixed'); c = await cfg();
  check('fixed speed toggle', c.profiles[prof][idx].fixed === 50, c.profiles[prof][idx].fixed);
  await p.click('#c-add'); c = await cfg();
  check('new rule', c.profiles[prof].length === rules.length + 1);
  await p.click('#c-del'); c = await cfg();
  check('delete rule', c.profiles[prof].length === rules.length);

  // --- lighting: every effect card applies
  await nav('lighting');
  const fxs = await p.$$eval('[data-fx]', els => els.map(e => e.dataset.fx));
  let okFx = 0;
  for (const f of fxs) {
    await p.click(`[data-fx="${f}"]`); c = await cfg();
    if (c.lighting.effect.type.toLowerCase() === f.toLowerCase()) okFx++;
    else check(`effect ${f}`, false, c.lighting.effect.type);
  }
  check(`all ${fxs.length} effect cards apply`, okFx === fxs.length, `${okFx}/${fxs.length}`);
  // a one-colour palette (e.g. after Static) must not turn multi-colour effects into a single colour
  await p.click('[data-fx="Static"]'); await p.evaluate(() => { S.cfg.lighting.effect.colors = ['#004000']; });
  for (const f of ['ColorWave', 'Comet', 'Spin', 'Ripple']) {
    await p.click('[data-fx="Static"]'); await p.evaluate(() => { S.cfg.lighting.effect.colors = ['#004000']; });
    await p.click(`[data-fx="${f}"]`); c = await cfg();
    const own = await p.evaluate(t => Object.values(S.cfg.lighting.presets).find(x => x.type === t)?.colors ?? null, f.charAt(0).toLowerCase() + f.slice(1));
    check(`${f} from a one-colour palette gets its own colours`, own ? JSON.stringify(c.lighting.effect.colors) === JSON.stringify(own) : c.lighting.effect.colors.length >= 2, JSON.stringify(c.lighting.effect.colors));
  }
  // an effect card brings its own colours, not the previous preset's
  {
    const pr = await p.evaluate(() => { const ps = S.cfg.lighting.presets; const a = Object.keys(ps).find(k => ps[k].type === 'temperature'); const b = Object.keys(ps).find(k => ps[k].type === 'twinkle'); return { a, b, bc: b && ps[b].colors }; });
    if (pr.a && pr.b) {
      await p.click(`#page-lighting [data-preset="${pr.a}"]`); await wait(150);
      await p.click('[data-fx="Twinkle"]'); c = await cfg();
      check('effect card uses its own colours, not the last preset\'s', JSON.stringify(c.lighting.effect.colors) === JSON.stringify(pr.bc), JSON.stringify(c.lighting.effect.colors));
    } else check('mock has temperature + twinkle presets', false, JSON.stringify(pr));
  }
  await p.click('[data-fx="Static"]'); c = await cfg();
  check('Static keeps the palette (no forced change)', c.lighting.effect.colors.length >= 1);
  // click the first fan ring on the stage → zone override
  await p.click('[data-fx="Static"]'); await wait(200);
  await p.$eval('#stage', e => e.scrollIntoView({ block: 'center' })); await wait(150);
  const st = await (await p.$('#stage-c')).boundingBox();
  const ring = await p.evaluate(() => { const z = S.init.zones.find(z => z.kind === 'ring' && z.count <= 16); const pl = placement(z.id); return { x: (pl.x + pl.w / 2) * stageGeo.W, y: (pl.y + pl.h / 2) * stageGeo.H, id: z.id }; });
  await p.mouse.click(st.x + ring.x, st.y + ring.y); await wait(250);
  await p.click('[data-fx="Comet"]'); c = await cfg();
  check('click fan on stage → per-fan override', c.lighting.overrides[ring.id]?.type === 'comet', ring.id + ' ' + JSON.stringify(c.lighting.overrides[ring.id]?.type));
  check('override leaves global effect alone', c.lighting.effect.type === 'static', c.lighting.effect.type);
  await p.click('#l-reset'); c = await cfg();
  check('reset override', !c.lighting.overrides[ring.id]);
  await p.click('[data-t="all"]'); await wait(200);
  // selection scoping: a device selection must be obvious and easy to leave
  {
    const dev = await p.$eval('[data-t]:not([data-t="all"])', e => e.dataset.t);
    await p.click(`[data-t="${dev}"]`); await wait(150);
    check('selecting a device shows the scoped bar', await p.$eval('.target-bar', e => e.classList.contains('scoped')));
    check('scoped bar has a back-to-all button', !!(await p.$('#l-all')));
    await p.click(`[data-t="${dev}"]`); await wait(150);
    check('clicking the selected device again goes back to all lights', await p.evaluate(() => S.target === 'all'));
    await p.click(`[data-t="${dev}"]`); await wait(150);
    await p.keyboard.press('Escape'); await wait(150);
    check('Esc goes back to all lights', await p.evaluate(() => S.target === 'all'));
    await p.click(`[data-t="${dev}"]`); await wait(150);
    await p.click('#l-all'); await wait(150);
    check('back-to-all button works', await p.evaluate(() => S.target === 'all') && !(await p.$('.target-bar.scoped')));
    const preset = await p.$eval('#page-lighting [data-preset]', e => e.dataset.preset);
    await p.click(`#page-lighting [data-preset="${preset}"]`); c = await cfg();
    check('preset with all lights selected changes the global effect', JSON.stringify(c.lighting.effect) === JSON.stringify(await p.evaluate(n => S.cfg.lighting.presets[n], preset)));
  }
  const nCol = c.lighting.effect.colors.length;
  await p.click('#fx-addcol'); c = await cfg();
  check('add colour', c.lighting.effect.colors.length === nCol + 1, `${nCol} → ${c.lighting.effect.colors.length}`);
  const preset = await p.$eval('#page-lighting [data-preset]', e => e.dataset.preset);
  await p.click(`#page-lighting [data-preset="${preset}"]`); c = await cfg();
  check('apply preset', !!c.lighting.effect.type, preset);
  await p.click('#l-enabled'); c = await cfg();
  check('lights toggle', c.lighting.enabled === false);
  await p.click('#l-enabled');

  // --- plugin effects
  await p.click('[data-t="all"]'); await wait(200);
  check('plugin effects listed in gallery', (await p.$$('[data-pfx]')).length === 12, (await p.$$('[data-pfx]')).length);
  await p.click('[data-pfx="Aurora"]'); c = await cfg();
  check('plugin effect applies', c.lighting.effect.type === 'plugin' && c.lighting.effect.plugin === 'Aurora', JSON.stringify([c.lighting.effect.type, c.lighting.effect.plugin]));
  check('plugin params rendered from schema', (await p.$$('#pp-speed, #pp-depth')).length === 2);
  await p.$eval('#pp-speed', e => { e.value = 1.25; e.dispatchEvent(new Event('input')); }); c = await cfg();
  check('plugin number param saved', c.lighting.effect.params?.speed === 1.25, JSON.stringify(c.lighting.effect.params));
  await p.click('[data-ppadd="colors"]'); c = await cfg();
  check('plugin colours param add', c.lighting.effect.params?.colors?.length === 4, JSON.stringify(c.lighting.effect.params?.colors));
  await p.click('[data-pfx="Heartbeat"]'); await wait(200);
  check('sensor param renders a sensor picker', (await p.$$('select[data-pps="sensor"] option')).length > 3);

  // --- layout editor
  await p.click('[data-t="all"]'); await wait(200);
  await p.$eval('#stage', e => e.scrollIntoView({ block: 'center' })); await wait(150);
  check('layout: every zone listed in the legend', (await p.$$('.lg-zone')).length === 14, (await p.$$('.lg-zone')).length);
  const fieldDrawn = await p.evaluate(() => { const c = document.getElementById('stage-c'), g = c.getContext('2d'); const d = g.getImageData(c.width * 0.05, c.height * 0.5, 1, 1).data; return d[0] + d[1] + d[2]; });
  check('layout: effect field painted behind the devices', fieldDrawn > 30, fieldDrawn);
  await p.click('#lay-arrange'); await wait(200);
  const geo = await p.evaluate(() => { const c = document.getElementById('stage-c').getBoundingClientRect(); const pl = placement('cc:fan2'); return { left: c.left, top: c.top, W: c.width, H: c.height, x: pl.x, y: pl.y, w: pl.w, h: pl.h }; });
  const sx = geo.left + (geo.x + geo.w / 2) * geo.W, sy = geo.top + (geo.y + geo.h / 2) * geo.H;
  await p.mouse.move(sx, sy); await p.mouse.down(); await p.mouse.move(sx + 120, sy + 30, { steps: 8 }); await p.mouse.up();
  c = await cfg();
  const moved = c.lighting.layout?.['cc:fan2'];
  check('arrange: dragging a fan saves its new position', moved && Math.abs(moved.x - geo.x - 120 / geo.W) < 0.02, JSON.stringify(moved));
  await p.keyboard.press('ArrowRight'); await p.keyboard.press('r'); c = await cfg();
  check('arrange: arrow nudges, R rotates the selected zone', Math.abs(c.lighting.layout['cc:fan2'].x - moved.x - 0.005) < 1e-6 && c.lighting.layout['cc:fan2'].rotation === 90, JSON.stringify(c.lighting.layout['cc:fan2']));
  p.once('dialog', d => d.accept());
  await p.click('#lay-auto'); await wait(400);
  check('auto-arrange asks, then applies the automatic layout', (await p.evaluate(() => window.__msgs.some(m => m.type === 'autoLayout'))) && (await p.evaluate(() => Math.abs(S.cfg.lighting.layout['cc:fan2'].x - S.init.placements['cc:fan2'].x) < 1e-9)));
  await p.click('#lay-arrange'); await wait(200);
  await p.click('[data-identify="lnp"]'); await wait(100);
  check('identify sends the device id', await p.evaluate(() => window.__msgs.some(m => m.type === 'identify' && m.target === 'lnp')));
  await p.click('[data-t="all"]'); await wait(200);
  await p.click('[data-fx="Wave"]'); await wait(200);
  await p.$eval('#fx-angle', e => { e.value = 90; e.dispatchEvent(new Event('input')); }); c = await cfg();
  check('Direction dial saves the angle', c.lighting.effect.angle === 90, c.lighting.effect.angle);

  // --- devices
  await nav('devices');
  await p.click('[data-leds="lnp:ch1"]', { count: 3 }); await p.keyboard.type('34'); c = await cfg();
  check('edit LED count', c.lighting.ledCounts['lnp:ch1'] === 34, c.lighting.ledCounts['lnp:ch1']);
  check('restart banner shown', await p.$eval('#restart-banner', e => !e.classList.contains('hidden')));
  check('devices page summarises plugins', (await p.$eval('#page-devices', e => e.textContent)).includes('43 installed')); // 42 plugin files + the built-in graphics-card fans
  {
    const txt = await p.$eval('#page-devices', e => e.innerText);
    check('devices: no empty "Firmware" label when the host sends none', !/Firmware\s*·/.test(txt));
    const titles = await p.$$eval('#page-devices .card-title', els => els.map(e => e.textContent));
    const raw = await p.evaluate(() => S.init.devices.map(d => d.id));
    check('devices: every card has a readable name, never a raw id', !titles.some(t => raw.includes(t)), titles.join(' | '));
    check('devices: keyboard and mouse get their own icons', await p.evaluate(() => { const h = document.getElementById('page-devices').innerHTML, norm = x => { const t = document.createElement('div'); t.innerHTML = x; return t.innerHTML; }; return h.includes(norm(IC.keyboard)) && h.includes(norm(IC.mouse)); }));
    check('devices: user-set LED counts editable on MSI ARGB headers', !!(await p.$('[data-leds="mystic:jrainbow1"]')));
  }
  // --- devices: ARGB LED-count test pattern
  {
    const tpMsgs = () => p.evaluate(() => window.__msgs.filter(m => m.type === 'testPattern'));
    check('test pattern toggle on every user-set LED count (and only there)', !!(await p.$('[data-tp="lnp:ch1"]')) && !!(await p.$('[data-tp="mystic:jrainbow1"]')) && !(await p.$('[data-tp="cc:fan1"]')) && !(await p.$('[data-tp="razer25a:keys"]')));
    await p.click('[data-tp="mystic:jrainbow1"]'); await wait(100);
    let tm = (await tpMsgs()).at(-1);
    check('test pattern on: sends zone + the configured count', tm?.on === true && tm.zone === 'mystic:jrainbow1' && tm.count === 30 && await p.$eval('[data-tp="mystic:jrainbow1"]', e => e.classList.contains('on')), JSON.stringify(tm));
    // the host paints it: LED 1 white, LED <count> red, a new colour every 8 LEDs, nothing past the count
    await p.evaluate(() => window.mockBridge.postMessage({ type: 'preview', on: true })); await wait(200);
    await p.$eval('[data-leds="mystic:jrainbow1"]', e => { e.value = 20; e.dispatchEvent(new Event('input', { bubbles: true })); }); await wait(150);
    tm = (await tpMsgs()).at(-1);
    check('test pattern: typing a new count updates it live', tm?.on === true && tm.count === 20 && tm.zone === 'mystic:jrainbow1', JSON.stringify(tm));
    await wait(250);
    const px = await p.evaluate(() => { const z = S.init.zones.find(x => x.id === 'mystic:jrainbow1'), at = i => [...S.rgb.slice((z.start + i) * 3, (z.start + i) * 3 + 3)]; return { first: at(0), b1: at(1), b2: at(8), last: at(19), past: at(20), end: at(29) }; });
    check('test pattern frame: white first, red at the count, blocks of 8, dark past the count', String(px.first) === '255,255,255' && String(px.last) === '255,0,0' && String(px.past) === '0,0,0' && String(px.end) === '0,0,0' && String(px.b1) !== String(px.b2), JSON.stringify(px));
    await p.evaluate(() => window.mockBridge.postMessage({ type: 'preview', on: false }));
    await p.$eval('[data-leds="mystic:jrainbow1"]', e => { e.value = 40; e.dispatchEvent(new Event('input', { bubbles: true })); }); await wait(150);
    check('test pattern: a count above the current length says a restart is needed', await p.$eval('[data-tp-note="mystic:jrainbow1"]', e => !e.hidden && e.textContent.includes('30')));
    await p.$eval('[data-leds="mystic:jrainbow1"]', e => { e.value = 30; e.dispatchEvent(new Event('input', { bubbles: true })); }); await wait(150);
    await p.click('[data-tp="lnp:ch1"]'); await wait(100);
    tm = (await tpMsgs()).slice(-2);
    check('test pattern: one zone at a time', tm[1]?.zone === 'lnp:ch1' && tm[1].on && (await p.$$('#page-devices [data-tp].on')).length === 1, JSON.stringify(tm));
    await nav('overview');
    tm = (await tpMsgs()).at(-1);
    check('test pattern: leaving the Devices page switches it off', tm?.on === false && tm.zone === 'lnp:ch1', JSON.stringify(tm));
    await nav('devices');
    check('test pattern: off after coming back', (await p.$$('#page-devices [data-tp].on')).length === 0);
    check('test pattern: page times out after 60 s like the host', await p.evaluate(() => typeof TP_SECONDS === 'number' && TP_SECONDS === 60));
  }
  await p.click('[data-fanname="cc:fan1"]', { count: 3 }); await p.keyboard.type('Front top'); c = await cfg();
  check('rename fan', c.fanNames['cc:fan1'] === 'Front top', c.fanNames['cc:fan1']);
  await nav('overview');
  check('renamed fan shows on overview', (await p.content()).includes('Front top'));

  // --- plugins manager
  await nav('plugins');
  const rows0 = (await p.$$('.plug-row')).length;
  check('plugins page lists every plugin', rows0 === 43, rows0); // incl. the built-in graphics-card fans
  await p.type('#pl-q', 'razer'); await wait(150);
  check('search filters the list', (await p.$$('.plug-row')).length === 1, (await p.$$('.plug-row')).length);
  await p.$eval('#pl-q', e => { e.value = ''; e.dispatchEvent(new Event('input')); });
  await p.click('[data-pf="problems"]'); await wait(150);
  check('Problems filter shows errors + approvals', (await p.$$('.plug-row')).length === 2 && (await p.$$('[data-approve]')).length === 1, (await p.$$('.plug-row')).length);
  await p.click('[data-pf="all"]'); await wait(150);
  check('sidebar badge flags plugins needing attention', await p.$eval('#plugins-badge', e => e.textContent === '2' && e.classList.contains('warn')));
  await p.click('[data-en="lava.js"]'); await wait(100);
  let msgs = await p.evaluate(() => window.__msgs.filter(m => m.type === 'setPluginEnabled'));
  check('toggle sends setPluginEnabled', msgs.some(m => m.file === 'lava.js' && m.enabled === false), JSON.stringify(msgs.at(-1)));
  p.once('dialog', d => d.accept());
  await p.click('[data-rm="sunset.js"]'); await wait(100);
  msgs = await p.evaluate(() => window.__msgs.filter(m => m.type === 'removePlugin'));
  check('delete asks, then sends removePlugin', msgs.some(m => m.file === 'sunset.js'));
  check('bundled plugins have no delete button', !(await p.$('[data-rm="aurora.js"]')));
  p.once('dialog', d => d.accept());
  await p.click('[data-pf="problems"]'); await wait(100); await p.click('[data-approve]'); await wait(100);
  msgs = await p.evaluate(() => window.__msgs.filter(m => m.type === 'approvePlugin'));
  check('approve asks, then sends approvePlugin', msgs.some(m => m.file === 'patriot-viper.js'));
  await p.click('[data-pf="all"]'); await wait(100);
  check('MCP command shown for Windows and WSL', (await p.$eval('#pl-mcp', e => e.textContent)).includes('Glint.exe" mcp') && (await p.$eval('#pl-mcp-wsl', e => e.textContent)).includes('/mnt/c/Program Files/Glint/Glint.exe'),
    await p.$eval('#pl-mcp-wsl', e => e.textContent));
  await p.click('#pl-new-fx'); await wait(100);
  check('New effect sends newPlugin', (await p.evaluate(() => window.__msgs.some(m => m.type === 'newPlugin' && m.kind === 'effect'))));
  // gallery search + cap
  await nav('lighting');
  const visible = async () => p.$$eval('#pfx-grid .fx[data-pfx]', els => els.filter(e => getComputedStyle(e).display !== 'none').length);
  check('plugin gallery capped when long', (await visible()) === 11, await visible());
  await p.type('#fx-q', 'storm'); await wait(150);
  check('effect search finds plugin effects', (await visible()) === 1, await visible());
  await p.$eval('#fx-q', e => { e.value = ''; e.dispatchEvent(new Event('input')); });

  // --- settings
  await nav('settings');
  await p.$eval('#s-fail', e => { e.value = 80; e.dispatchEvent(new Event('input')); }); c = await cfg();
  check('fail-safe slider', c.failsafeDuty === 80, c.failsafeDuty);
  // updates card
  check('settings: update status shows the new version', (await p.$eval('#upd-status', e => e.textContent)).includes('v0.3.0') && await p.$eval('#upd-open', e => e.style.display !== 'none'));
  await p.click('#upd-open'); await wait(100);
  check('settings: "Open release page" sends only the release URL', await p.evaluate(() => window.__msgs.some(m => m.type === 'openUrl' && m.url.startsWith('https://github.com/jleeh/glint/releases'))));
  // one-click install
  const updMsgs = () => p.evaluate(() => window.__msgs.filter(m => m.type === 'installUpdate').length);
  check('settings: "Install update" shown next to an available update', await p.$eval('#upd-install', e => e.style.display !== 'none' && !e.disabled && e.textContent === 'Install update'));
  let n0 = await updMsgs();
  p.once('dialog', d => d.dismiss()); await p.click('#upd-install'); await wait(150);
  check('settings: cancelling the confirmation installs nothing', (await updMsgs()) === n0, `${n0} → ${await updMsgs()}`);
  await p.evaluate(() => { window.__mockUpdateFail = 'no valid build attestation from Glint\'s release workflow'; });
  p.once('dialog', d => d.accept()); await p.click('#upd-install'); await wait(100);
  check('settings: confirming sends installUpdate once', (await updMsgs()) === n0 + 1);
  check('settings: install button disabled while it runs', await p.$eval('#upd-install', e => e.disabled && e.textContent.includes('Installing')));
  check('settings: download progress shown in MB', /Downloading.*of 60\.0 MB/.test(await p.$eval('#upd-progress', e => e.textContent)), await p.$eval('#upd-progress', e => e.textContent));
  await wait(500);
  const failTxt = await p.$eval('#upd-progress', e => e.textContent);
  check('settings: a failed verification is shown as an error with the reason', failTxt.startsWith('Failed') && failTxt.includes('attestation') && await p.$eval('#upd-progress', e => e.classList.contains('bad'))
    && await p.$eval('#upd-install', e => !e.disabled && e.textContent === 'Try again'), failTxt);
  await p.evaluate(() => { window.__mockUpdateFail = null; });
  p.once('dialog', d => d.accept()); await p.click('#upd-install'); await wait(550);
  check('settings: verified update goes downloading → verifying → installing', (await p.$eval('#upd-progress', e => e.textContent)).startsWith('Installing') && await p.$eval('#upd-install', e => e.disabled));
  await p.evaluate(() => { S.init.cannotInstall = 'this is a portable copy: download the new version from the release page'; liveUpdate(); });
  check('settings: a portable copy offers only the release page, and says why', await p.$eval('#upd-install', e => e.style.display === 'none') && await p.$eval('#upd-open', e => e.style.display !== 'none')
    && (await p.$eval('#upd-progress', e => e.textContent)).includes('portable'));
  await p.evaluate(() => { S.init.cannotInstall = null; liveUpdate(); });
  // automatic install toggle
  await p.click('#upd-auto'); c = await cfg();
  check('settings: automatic install toggle saves updates.autoInstall', c.updates?.autoInstall === true && c.updates.check === true, JSON.stringify(c.updates));
  check('settings: "Check now" hidden while an update is on offer', await p.$eval('#upd-recheck', e => e.style.display === 'none'));
  await p.evaluate(() => { window.__mockInstallIdle(); window.__mockNoUpdate = true; window.__mockChecks = 0; document.querySelector('#upd-recheck').style.display = ''; });
  await p.click('#upd-recheck'); await wait(30);
  check('settings: "Check now" shows it is checking and disables itself', (await p.$eval('#upd-status', e => e.textContent)).includes('Checking') && await p.$eval('#upd-recheck', e => e.disabled));
  await wait(250);
  const upTxt = await p.$eval('#upd-status', e => e.textContent);
  check('settings: "Check now" sends one checkUpdate and shows the fresh answer with its age', await p.evaluate(() => window.__mockChecks) === 1 && upTxt.includes('is the latest version') && upTxt.includes('checked just now')
    && await p.$eval('#upd-recheck', e => !e.disabled && e.style.display !== 'none'), upTxt);
  await p.click('#upd-check'); c = await cfg();
  check('settings: update check toggle saves updates.check (and switches automatic install off)', c.updates?.check === false && c.updates.autoInstall === false, JSON.stringify(c.updates));
  await p.click('#upd-auto'); c = await cfg();
  check('settings: automatic install can\'t be switched on without the check', c.updates.autoInstall === false && await p.$eval('#upd-auto', e => e.classList.contains('disabled') && !e.classList.contains('on')), JSON.stringify(c.updates));
  await wait(1100);
  check('settings: update check off hides the release and install buttons', await p.$eval('#upd-open', e => e.style.display === 'none') && await p.$eval('#upd-install', e => e.style.display === 'none') && (await p.$eval('#upd-status', e => e.textContent)).includes('off'));
  await p.click('#upd-check'); c = await cfg();
  check('settings: update check back on', c.updates?.check === true && c.updates.autoInstall === false);
  // OpenRGB bridge card
  await p.click('#orgb-enabled'); c = await cfg();
  check('settings: OpenRGB bridge toggle saves openRgb.enabled', c.openRgb?.enabled === true && c.openRgb.host === '127.0.0.1' && c.openRgb.port === 6742, JSON.stringify(c.openRgb));
  check('settings: OpenRGB status says a restart is needed', (await p.$eval('#orgb-status', e => e.textContent)).includes('restart'));
  await p.$eval('#orgb-port', e => { e.value = '99999'; e.dispatchEvent(new Event('change')); }); c = await cfg();
  check('settings: OpenRGB port clamped to 65535', c.openRgb.port === 65535, c.openRgb.port);
  await p.$eval('#orgb-exclude', e => { e.value = 'Razer,  Corsair Vengeance , '; e.dispatchEvent(new Event('change')); }); c = await cfg();
  check('settings: OpenRGB exclude list split and trimmed', JSON.stringify(c.openRgb.exclude) === '["Razer","Corsair Vengeance"]', JSON.stringify(c.openRgb.exclude));
  await p.$eval('#orgb-port', e => { e.value = '6742'; e.dispatchEvent(new Event('change')); });
  await p.click('#orgb-enabled'); c = await cfg();
  check('settings: OpenRGB bridge off again', c.openRgb.enabled === false);

  // --- setup check page
  {
    const before = await p.evaluate(() => window.__msgs.filter(m => m.type === 'setupCheck').length);
    await nav('setup'); await wait(300);
    const after = await p.evaluate(() => window.__msgs.filter(m => m.type === 'setupCheck').length);
    check('setup: opening the page asks the host to check (on demand, not a timer)', after === before + 1, `${before} -> ${after}`);
    const rows = await p.$$eval('#setup-list .setup-row', els => els.map(e => [e.dataset.item, e.className.split(' ')[1]]));
    check('setup: every check listed with a status', rows.length >= 8 && rows.every(r => ['ok', 'warn', 'fail'].includes(r[1])), JSON.stringify(rows));
    check('setup: problems first, then warnings, then ok', rows.map(r => ({ fail: 0, warn: 1, ok: 2 })[r[1]]).every((v, i, a) => i === 0 || a[i - 1] <= v));
    check('setup: plain-language detail on each row', await p.$$eval('#setup-list .sd', els => els.every(e => e.textContent.length > 20)));
    check('setup: sidebar badge shows the problem count in red', await p.$eval('#setup-badge', e => e.textContent === '1' && e.classList.contains('bad')));
    await p.click('[data-fix="plugins:failed"]'); await wait(250);
    check('setup: "Open Plugins page" fix goes to the plugins Problems filter', await p.evaluate(() => S.page === 'plugins' && PF.f === 'problems'));
    await p.click('[data-pf="all"]');
    await nav('setup'); await wait(300);
    await p.click('[data-fix="conflict:signalrgb"]'); await wait(100);
    check('setup: startup-apps fix sends setupFix', await p.evaluate(() => window.__msgs.some(m => m.type === 'setupFix' && m.fix === 'startupApps')));
    const nInst = await p.evaluate(() => window.__msgs.filter(m => m.type === 'installUpdate').length);
    p.once('dialog', d => d.accept()); await p.click('[data-fix="version"]'); await wait(100);
    check('setup: update row installs the update (after confirming)', await p.evaluate(n => window.__msgs.filter(m => m.type === 'installUpdate').length === n + 1, nInst)
      && await p.$eval('[data-fix="version"]', e => e.textContent === 'Install update'));
    const n0 = await p.evaluate(() => window.__msgs.filter(m => m.type === 'setupCheck').length);
    await p.click('#setup-recheck'); await wait(300);
    check('setup: Re-check asks again', await p.evaluate(n => window.__msgs.filter(m => m.type === 'setupCheck').length === n + 1, n0));
    await nav('overview');
    check('overview: no setup banner without an important failure', !(await p.$('#setup-alert')));
    await p.evaluate(() => { window.__setupExtra = [{ id: 'cpuTemp2', title: 'CPU temperature not readable', status: 'fail', detail: 'Rules that use the CPU temperature run at the fail-safe speed.', important: true }]; window.mockBridge.postMessage({ type: 'setupCheck' }); });
    await wait(400);
    check('overview: one-line banner when something important fails', await p.$eval('#setup-alert', e => e.textContent.includes('CPU temperature not readable')));
    check('setup: badge counts the new problem', await p.$eval('#setup-badge', e => e.textContent === '2'));
    await p.click('#setup-alert [data-go="setup"]'); await wait(300);
    check('overview banner links to the Setup check page', await p.evaluate(() => S.page === 'setup'));
    await p.evaluate(() => { window.__setupExtra = []; window.mockBridge.postMessage({ type: 'setupCheck' }); }); await wait(300);
  }


  // --- sensors page
  await nav('sensors'); await wait(1300);
  {
    const msgs = await p.evaluate(() => window.__msgs.filter(m => m.type === 'sensorsWanted'));
    check('sensors: opening the page asks for the catalog (and resets min/max)', msgs.length >= 1 && msgs[0].reset === true, JSON.stringify(msgs.slice(0, 2)));
    const rows = (await p.$$('#sens-tree .sr')).length;
    check('sensors: tree lists every sensor of the catalog', rows > 80 && rows === await p.evaluate(() => { let n = 0; const w = h => { n += h.sensors.length; h.sub?.forEach(w); }; S.sens.hardware.forEach(w); return n; }), rows);
    const hw = await p.$$eval('#sens-tree .sg.hw > .sg-h .hw-name', els => els.map(e => e.textContent));
    check('sensors: grouped by hardware (CPU, GPU, board + Super I/O chip, drives, memory, network, hub)', ['AMD Ryzen 7 7800X3D', 'NVIDIA GeForce RTX 4070 SUPER', 'Nuvoton NCT6687D', 'Samsung SSD 990 PRO 2TB', 'Total Memory', 'Ethernet', 'Corsair Commander Core'].every(n => hw.includes(n)), hw.join(', '));
    const heads = await p.$$eval('[data-g="/amdcpu/0"] > .sg-body > .sg > .sg-h', els => els.map(e => e.textContent.replace(/\d+$/, '').trim()));
    check('sensors: then by sensor type', JSON.stringify(heads) === JSON.stringify(['Temperatures', 'Voltages', 'Powers', 'Clocks', 'Utilization']), heads.join(', '));
    const txt = await p.$eval('#sens-tree', e => e.innerText);
    const units = { '°C': /(^|\s)\d+\.\d °C/, V: /(^|\s)\d+\.\d{3} V/, MHz: /(^|\s)[\d,]+ MHz/, '%': /(^|\s)\d+\.\d %/, W: /(^|\s)\d+\.\d W/, RPM: /(^|\s)[\d,]+ RPM/, GB: /(^|\s)\d+(\.\d+)? (GB|TB)/, MB: /(^|\s)[\d,]+ MB\b/, 'MB/s': /(^|\s)\d+(\.\d+)? (KB|MB)\/s/ };
    const miss = Object.entries(units).filter(([, re]) => !re.test(txt)).map(([u]) => u);
    check('sensors: units formatted (°C, V, MHz, %, W, RPM, GB, MB, MB/s)', miss.length === 0, 'missing ' + miss.join(' '));
    check('sensors: Value / Min / Max columns', (await p.$$eval('.sens-head span', e => e.map(x => x.textContent))).join() === 'Sensor,Value,Min,Max');
    const tags = await p.$$eval('#sens-tree .stag', els => els.map(e => e.textContent));
    check('sensors: friendly aliases tagged (cpu, gpu, liquid)', ['cpu', 'gpu', 'liquid'].every(t => tags.includes(t)), [...new Set(tags)].slice(0, 12).join(', '));
    const drive = await p.$$eval('#sens-tree .stag.drive', els => els.map(e => e.closest('.sr').querySelector('.sname').textContent + '@' + e.closest('.sr').dataset.sid));
    check('sensors: sensors used by fan curves get a "drives fans" tag', drive.length >= 2 && drive.some(d => d.startsWith('Core (Tctl/Tdie)')) && drive.some(d => d.startsWith('liquid')), drive.join(', '));
    check('sensors: board fan headers show the fan\'s name', tags.includes('Front intake 1'), '');
    // live values + min/max
    await wait(2200);
    const spread = await p.evaluate(() => [...document.querySelectorAll('#sens-tree .sr')].filter(r => r.children[2].textContent !== r.children[3].textContent).length);
    check('sensors: min/max track changing values', spread > 5, spread);
    const vals0 = await p.$eval('[data-sid="/amdcpu/0/clock/0"] [data-v]', e => e.textContent).catch(() => null);
    await wait(1100);
    const vals1 = await p.$eval('[data-sid="/amdcpu/0/clock/0"] [data-v]', e => e.textContent).catch(() => null);
    check('sensors: values update live (~1 Hz)', vals0 && vals1 && vals0 !== vals1, `${vals0} → ${vals1}`);
    await p.click('#sens-reset');
    const after = await p.evaluate(() => [...document.querySelectorAll('#sens-tree .sr')].filter(r => r.children[2].textContent !== r.children[1].textContent || r.children[3].textContent !== r.children[1].textContent).length);
    check('sensors: Reset min/max sets min = max = value', after === 0, after + ' rows differ');
    check('sensors: Reset sends sensorsReset to the host', await p.evaluate(() => window.__msgs.some(m => m.type === 'sensorsReset')));
    await wait(1100);
    const mmAfter = await p.evaluate(() => { const x = S.sens.hardware[0].sensors.find(s => s.type === 'Clock'); return [x.min, x.max]; });
    check('sensors: host min/max restart after reset', await p.evaluate(() => { let ok = 0, n = 0; const w = h => { h.sensors.forEach(x => { if (x.value == null) return; n++; if (x.min <= x.value && x.max >= x.value) ok++; }); h.sub?.forEach(w); }; S.sens.hardware.forEach(w); return ok === n; }), JSON.stringify(mmAfter));
    // keep-alive while open
    const kept = await p.evaluate(() => window.__msgs.filter(m => m.type === 'sensorsWanted').length);
    check('sensors: page keeps the catalog alive while open', kept >= 4, kept);
    // search
    await p.type('#sens-q', 'tctl'); await wait(150);
    let vis = await p.$$eval('#sens-tree .sr', els => els.filter(e => e.offsetParent).map(e => e.querySelector('.sname').textContent));
    check('sensors: search filters rows', vis.length === 1 && vis[0] === 'Core (Tctl/Tdie)', vis.join(', '));
    const hwVis = await p.$$eval('#sens-tree .sg.hw', els => els.filter(e => e.offsetParent).length);
    check('sensors: search hides hardware with no matches', hwVis === 1, hwVis);
    await p.$eval('#sens-q', e => { e.value = ''; e.dispatchEvent(new Event('input')); });
    await p.type('#sens-q', 'gpu fan'); await wait(150);
    vis = await p.$$eval('#sens-tree .sr', els => els.filter(e => e.offsetParent).length);
    check('sensors: multi-word search', vis === 4, vis);
    await p.$eval('#sens-q', e => { e.value = 'zzznothing'; e.dispatchEvent(new Event('input')); }); await wait(100);
    check('sensors: empty search result says so', await p.$eval('#sens-none', e => !!e.offsetParent && e.textContent.includes('zzznothing')));
    await p.$eval('#sens-q', e => { e.value = ''; e.dispatchEvent(new Event('input')); }); await wait(100);
    // collapse
    const gpuRows = () => p.$$eval('[data-g="/gpu-nvidia/0"] .sr', els => els.filter(e => e.offsetParent).length);
    const n0 = await gpuRows();
    await p.click('[data-tg="/gpu-nvidia/0"]'); await wait(100);
    check('sensors: clicking a hardware header collapses it', n0 > 10 && (await gpuRows()) === 0 && await p.$eval('[data-tg="/gpu-nvidia/0"]', e => e.getAttribute('aria-expanded') === 'false'), `${n0} → ${await gpuRows()}`);
    await wait(1100);
    check('sensors: collapsed state survives live updates', (await gpuRows()) === 0);
    await p.click('[data-tg="/gpu-nvidia/0"]'); await wait(100);
    check('sensors: and expands again', (await gpuRows()) === n0);
    await p.click('[data-tg="/gpu-nvidia/0|Clock"]'); await wait(100);
    check('sensors: type groups collapse on their own', (await gpuRows()) === n0 - 3, await gpuRows());
    await p.click('#sens-collapse'); await wait(100);
    check('sensors: Collapse all', (await p.$$eval('#sens-tree .sr', els => els.filter(e => e.offsetParent).length)) === 0);
    await p.click('#sens-expand'); await wait(100);
    check('sensors: Expand all', (await p.$$eval('#sens-tree .sr', els => els.filter(e => e.offsetParent).length)) === rows);
  }
  // leaving the page stops the keep-alive
  await nav('overview');
  {
    const k0 = await p.evaluate(() => window.__msgs.filter(m => m.type === 'sensorsWanted').length);
    await wait(2100);
    const k1 = await p.evaluate(() => window.__msgs.filter(m => m.type === 'sensorsWanted').length);
    check('sensors: closing the page stops asking for the catalog', k0 === k1, `${k0} → ${k1}`);
  }

  // --- cooling: manual override badge + back to auto
  await nav('cooling');
  {
    const prof2 = await p.$eval('#c-profile', e => e.value);
    await p.click('[data-ri="1"]'); await wait(150);
    // the host (Home Assistant / API) sets an override: it arrives with the live state
    await p.evaluate(pr => { const c = window.__lastConfig ?? S.cfg; window.__lastConfig = JSON.parse(JSON.stringify(c)); window.__lastConfig.profiles[pr][1].override = 65; }, prof2);
    await wait(2600);
    check('override: badge on the rule list', (await p.$eval('[data-ri="1"]', e => e.textContent)).includes('Held 65%'));
    check('override: "Held at 65% by override" bar in the editor', (await p.$eval('#c-ovr', e => e.textContent).catch(() => '')).includes('Held at 65% by override'));
    check('override: overview explains it', await p.evaluate(pr => winner(S.cfg.profiles[pr][1], {}).label === 'Held at 65% by override', prof2));
    await p.click('#c-auto'); c = await cfg();
    check('override: Back to auto sends override = null', c.profiles[prof2][1].override == null && 'override' in c.profiles[prof2][1] && !(await p.$('#c-ovr')), JSON.stringify(c.profiles[prof2][1].override));
    await wait(1200);
    check('override: badge stays gone after the next state update', !(await p.$('#c-ovr')) && !(await p.$eval('[data-ri="1"]', e => e.textContent)).includes('Held'));
  }

  // --- settings: Home Assistant card
  await nav('settings');
  {
    check('HA: card rendered', !!(await p.$('#ha-card')) && (await p.$eval('#ha-card', e => e.textContent)).includes('Mosquitto'));
    const retype = async (sel, v) => { await p.focus(sel); await p.keyboard.down('Control'); await p.keyboard.press('a'); await p.keyboard.up('Control'); await p.keyboard.type(v); await p.keyboard.press('Tab'); };
    check('HA: status says off while disabled', (await p.$eval('#ha-status', e => e.textContent)).trim() === 'Off');
    await p.click('#ha-enabled'); c = await cfg();
    check('HA: enable toggle saves', c.homeAssistant.enabled === true);
    await retype('#ha-host', 'broker.example');
    await retype('#ha-port', '1884');
    await retype('#ha-user', 'glint');
    await retype('#ha-prefix', 'ha');
    await retype('#ha-topic', 'desk-pc');
    await p.click('#ha-tls'); c = await cfg();
    const h = c.homeAssistant;
    check('HA: fields save (host, port, tls, username, prefix, topic)', h.host === 'broker.example' && h.port === 1884 && h.tls === true && h.username === 'glint' && h.discoveryPrefix === 'ha' && h.baseTopic === 'desk-pc', JSON.stringify(h));
    check('HA: no password sent until one is typed', !('password' in h));
    const n0 = await p.evaluate(() => window.__msgs.length);
    await p.click('#ha-pass'); await p.keyboard.type('s3cret-pw'); await p.keyboard.press('Tab'); c = await cfg();
    const sent = await p.evaluate(n => window.__msgs.slice(n).filter(m => m.type === 'config').map(m => m.config.homeAssistant.password ?? null), n0);
    check('HA: typed password is sent once', sent.filter(x => x === 's3cret-pw').length === 1, JSON.stringify(sent));
    check('HA: password field is empty after saving and says it is saved', await p.$eval('#ha-pass', e => e.value === '' && /Saved/.test(e.placeholder)));
    await p.click('#ha-enabled'); await p.click('#ha-enabled'); c = await cfg();
    check('HA: later saves do not resend the password', !('password' in c.homeAssistant) && c.homeAssistant.enabled === true, JSON.stringify(c.homeAssistant));
    await nav('overview'); await nav('settings');
    check('HA: password never echoed into the page', await p.evaluate(() => !document.documentElement.outerHTML.includes('s3cret-pw') && !JSON.stringify(S.cfg).includes('s3cret-pw') && document.getElementById('ha-pass').value === ''));
    check('HA: re-rendered card keeps the saved fields', await p.$eval('#ha-host', e => e.value === 'broker.example') && await p.$eval('#ha-port', e => e.value === '1884'));
    await wait(1100);
    check('HA: status line shows the connection', /Connected to mqtts:\/\/broker\.example:1884 · 27 entities/.test(await p.$eval('#ha-status', e => e.textContent)), await p.$eval('#ha-status', e => e.textContent));
    await p.click('#ha-pass-clear'); c = await cfg();
    check('HA: Clear sends an empty password (removes the saved one)', await p.evaluate(() => window.__msgs.filter(m => m.type === 'config').at(-1).config.homeAssistant.password === '') && await p.$eval('#ha-pass', e => e.placeholder === 'Not set'));
    await p.click('#ha-enabled'); c = await cfg();
    check('HA: disable saves', c.homeAssistant.enabled === false);
  }

  // --- game overlay page
  await nav('overlay');
  {
    const retype = async (sel, v) => { await p.focus(sel); await p.keyboard.down('Control'); await p.keyboard.press('a'); await p.keyboard.up('Control'); if (v) await p.keyboard.type(v); else await p.keyboard.press('Backspace'); await p.keyboard.press('Tab'); };
    const ov = async () => (await cfg()).overlay;
    check('overlay: card renders with the items and a preview', !!(await p.$('#ov-card')) && (await p.$$('#ov-items .ov-item')).length === 7 && (await p.$eval('#ov-preview', e => e.textContent)).includes('FPS'));
    await p.click('#ov-enabled'); let o = await ov();
    check('overlay: enable toggle saves', o.enabled === false, o.enabled);
    await p.click('#ov-game'); o = await ov();
    check('overlay: only-while-gaming toggle saves', o.onlyWhenGame === true);
    await p.click('#ov-pos [data-pos="bottom-right"]'); o = await ov();
    check('overlay: position picker saves', o.position === 'bottom-right' && await p.$eval('#ov-pos [data-pos="bottom-right"]', e => e.classList.contains('on')), o.position);
    await p.click('#ov-layout [data-l="compact"]'); o = await ov();
    check('overlay: layout saves', o.layout === 'compact');
    await p.click('#ov-layout [data-l="detailed"]');
    await retype('#ov-ox', '40'); await retype('#ov-oy', '-20'); o = await ov();
    check('overlay: offsets save', o.offsetX === 40 && o.offsetY === -20, `${o.offsetX},${o.offsetY}`);
    await p.$eval('#ov-scale', e => { e.value = 1.5; e.dispatchEvent(new Event('input')); });
    await p.$eval('#ov-opacity', e => { e.value = 0.8; e.dispatchEvent(new Event('input')); });
    await p.$eval('#ov-bg', e => { e.value = 0.3; e.dispatchEvent(new Event('input')); }); o = await ov();
    check('overlay: scale / opacity / background sliders save', o.scale === 1.5 && o.opacity === 0.8 && o.backgroundOpacity === 0.3, `${o.scale} ${o.opacity} ${o.backgroundOpacity}`);
    await p.$eval('#ov-accent', e => { e.value = '#00e5ff'; e.dispatchEvent(new Event('input', { bubbles: true })); }); o = await ov();
    check('overlay: accent colour saves', o.accent === '#00e5ff', o.accent);
    await p.click('#ov-accent-reset'); o = await ov();
    check('overlay: accent reset goes back to the Glint gradient (null)', o.accent === null);
    // hotkey
    await retype('#ov-hotkey', 'Ctrl+Alt+F9'); o = await ov();
    check('overlay: valid hotkey saves', o.hotkey === 'Ctrl+Alt+F9', o.hotkey);
    await retype('#ov-hotkey', 'banana'); o = await ov();
    check('overlay: invalid hotkey shows an error state and is not saved', o.hotkey === 'Ctrl+Alt+F9' && await p.$eval('#ov-hotkey', e => e.classList.contains('invalid') && e.getAttribute('aria-invalid') === 'true') && (await p.$eval('#ov-hotkey-err', e => e.textContent)).length > 5, o.hotkey);
    await retype('#ov-hotkey', 'O'); check('overlay: bare letter key is rejected', await p.$eval('#ov-hotkey', e => e.classList.contains('invalid')));
    await retype('#ov-hotkey', ''); o = await ov();
    check('overlay: empty hotkey (none) is valid', o.hotkey === '' && await p.$eval('#ov-hotkey', e => !e.classList.contains('invalid')), o.hotkey);
    // items: add catalog sensors
    const n0 = o.items.length;
    await p.click('#ov-add'); await wait(900);
    check('overlay: opening the picker asks for the sensor catalog', await p.evaluate(() => window.__msgs.some(m => m.type === 'sensorsWanted')));
    const groups = await p.$$eval('#ov-plist .ov-ph', els => els.map(e => e.textContent.replace(/\d+$/, '')));
    check('overlay: picker lists built-ins, fans and hardware groups', ['Built-in', 'Fans'].every(g => groups.includes(g)) && groups.some(g => /Ryzen/.test(g)) && groups.some(g => /GeForce/.test(g)), groups.join(' | '));
    await p.type('#ov-q', 'tctl'); await wait(150);
    const picks = await p.$$eval('#ov-plist .ov-pick', els => els.map(e => e.dataset.src));
    check('overlay: search narrows the picker', picks.length >= 1 && picks.length < 5, picks.join(', '));
    await p.click('#ov-plist .ov-pick'); o = await ov();
    check('overlay: adding a catalog sensor appends an item', o.items.length === n0 + 1 && o.items.at(-1).source === picks[0], JSON.stringify(o.items.at(-1)));
    await p.$eval('#ov-q', e => { e.value = 'dimm'; e.dispatchEvent(new Event('input')); }); await wait(100);
    await p.click('#ov-plist .ov-pick'); o = await ov();
    check('overlay: a memory sensor can be added too', o.items.length === n0 + 2, o.items.at(-1).source);
    await p.$eval('#ov-q', e => { e.value = 'fan'; e.dispatchEvent(new Event('input')); }); await wait(100);
    check('overlay: fans offer fan:<id> and duty:<id>', await p.$$eval('#ov-plist .ov-pick', els => els.some(e => /^fan:/.test(e.dataset.src)) && els.some(e => /^duty:/.test(e.dataset.src))));
    await p.click('#ov-add');
    // label + colour
    const last = o.items.length - 1;
    await p.$eval(`.ov-label[data-i="${last}"]`, e => { e.value = 'Package'; e.dispatchEvent(new Event('input', { bubbles: true })); });
    await p.$eval(`.ov-color[data-i="${last}"]`, e => { e.value = '#ff6a3d'; e.dispatchEvent(new Event('input', { bubbles: true })); }); o = await ov();
    check('overlay: item label and colour save', o.items[last].label === 'Package' && o.items[last].color === '#ff6a3d', JSON.stringify(o.items[last]));
    check('overlay: preview shows the custom label', (await p.$eval('#ov-preview', e => e.textContent)).toUpperCase().includes('PACKAGE'));
    // reorder
    const before = o.items.map(i => i.source);
    await p.click(`[data-act="up"][data-i="${last}"]`); o = await ov();
    check('overlay: move up reorders', o.items[last - 1].source === before[last] && o.items[last].source === before[last - 1], o.items.map(i => i.source).join(','));
    await p.click('[data-act="down"][data-i="0"]'); o = await ov();
    check('overlay: move down reorders', o.items[0].source === before[1] && o.items[1].source === before[0]);
    const cur = (await ov()).items.map(i => i.source);
    await p.evaluate(() => { const rows = document.querySelectorAll('#ov-items .ov-item'), dt = new DataTransfer(); rows[2].dispatchEvent(new DragEvent('dragstart', { bubbles: true, dataTransfer: dt })); rows[0].dispatchEvent(new DragEvent('dragover', { bubbles: true, cancelable: true, dataTransfer: dt })); rows[0].dispatchEvent(new DragEvent('drop', { bubbles: true, cancelable: true, dataTransfer: dt })); });
    const ids2 = (await ov()).items.map(i => i.source);
    check('overlay: drag and drop reorders', ids2[0] === cur[2] && ids2[1] === cur[0], ids2.join(','));
    // remove
    const nr = ids2.length;
    await p.click('[data-act="rm"][data-i="0"]'); o = await ov();
    check('overlay: remove drops the item', o.items.length === nr - 1 && o.items[0].source === ids2[1], `${nr} -> ${o.items.length}`);
    check('overlay: saved overlay uses the host field names', ['enabled', 'hotkey', 'position', 'offsetX', 'offsetY', 'scale', 'opacity', 'backgroundOpacity', 'accent', 'layout', 'onlyWhenGame', 'items'].every(k => k in o));
    await nav('overview'); await nav('overlay');
    check('overlay: has its own sidebar item and is not on Settings', !!(await p.$('#nav button[data-page="overlay"]')) && await p.evaluate(() => { go('settings'); const none = !document.querySelector('#page-settings #ov-card'); go('overlay'); return none; }));
    check('overlay: card survives a page switch', (await p.$$('#ov-items .ov-item')).length === o.items.length && await p.$eval('#ov-hotkey', e => e.value === ''));
  }

  // --- nothing spills out horizontally, on any page, at narrow / medium / wide windows
  for (const width of [1000, 1150, 1320, 1600]) {
    await p.setViewport({ width, height: 860 }); await wait(200);
    const bad = [];
    for (const pg of ['overview', 'cooling', 'sensors', 'lighting', 'devices', 'plugins', 'overlay', 'setup', 'settings']) {
      await nav(pg);
      if (pg === 'cooling') { await p.click('[data-ri="1"]'); await wait(200); }
      const o = await p.evaluate(() => { const c = document.getElementById('content'); return c.scrollWidth - c.clientWidth; });
      if (o > 1) bad.push(`${pg} +${o}px`);
    }
    check(`no horizontal overflow at ${width}px`, bad.length === 0, bad.join(', '));
  }
  await p.setViewport({ width: 1320, height: 860 });

  // --- brand, naming, privacy
  await nav('overview');
  check('brand: inline SVG spark with the accent gradient', await p.evaluate(() => { const s = document.querySelector('.brand svg.brand-mark'); return !!s && !!s.querySelector('linearGradient stop[stop-color="#ff6a3d"]') && !!s.querySelector('path[fill^="url(#"]'); }));
  {
    const small = await p.$$eval('[data-small] .k', els => els.map(e => e.textContent));
    check('overview: friendly sensor names (Storage, Memory, renamed board sensors)', ['Storage', 'Memory', 'Motherboard'].every(n => small.includes(n)), small.join(', '));
    check('overview: per-hub copy of the liquid temperature is not repeated', !small.some(n => /liquid/i.test(n)), small.join(', '));
  }
  check('privacy: no user paths or serials in the page or the mock data', await p.evaluate(() => {
    const blob = document.documentElement.outerHTML + JSON.stringify(S.init);
    return !/[A-Z]:\\{1,2}Users\\{1,2}/i.test(blob) && !/"serial"/i.test(blob) && !/AppData\\{1,2}Roaming/i.test(blob);
  }));
  // --- lighting: labels, honest preview, empty state
  await nav('lighting');
  {
    const lg = await p.$$eval('.lg-zone span:first-child', els => els.map(e => e.textContent));
    check('legend: MSI headers and fan rings get friendly names', lg.includes('ARGB header 1') && lg.includes('Radiator 2') && !lg.some(n => n.includes(':')), lg.join(', '));
    const chips = await p.$$eval('.target-bar [data-t]', els => els.map(e => e.textContent.trim()));
    check('device chips use product names', chips.includes('Razer BlackWidow V3 Pro') && !chips.some(t => /^[a-z]+[0-9a-f]*$/.test(t)), chips.join(', '));
    await p.click('[data-t="all"]'); await p.click('[data-fx="Static"]'); await wait(100);
    await p.evaluate(() => { S.cfg.lighting.effect.colors = ['#ff0000']; commit(); });
    await wait(400);
    const px = await p.evaluate(() => [...S.rgb.slice(0, 3)]);
    check('mock preview follows the configured effect', px[0] > 200 && px[1] < 30 && px[2] < 30, JSON.stringify(px));
    const empty = await p.evaluate(() => { const z = S.init.zones; S.init.zones = []; renderLighting(); const ok = !!document.querySelector('#page-lighting .empty-state [data-go-plugins]'); S.init.zones = z; renderLighting(); return ok; });
    check('lighting: empty state when no RGB devices are found', empty);
  }
  // --- keyboard access
  {
    const t = await p.$eval('#l-enabled', e => [e.tabIndex, e.getAttribute('role'), e.getAttribute('aria-checked')]);
    check('toggles are focusable switches', t[0] === 0 && t[1] === 'switch' && t[2] === 'true', JSON.stringify(t));
    const before = await p.evaluate(() => S.cfg.lighting.enabled);
    await p.focus('#l-enabled'); await p.keyboard.press(' '); c = await cfg();
    check('Space on a focused toggle flips it', c.lighting.enabled === !before, c.lighting.enabled);
    await p.focus('#l-enabled'); await p.keyboard.press('Enter'); c = await cfg();
    check('Enter on a focused toggle flips it back', c.lighting.enabled === before);
    check('icon-only buttons have accessible names', await p.$$eval('#page-lighting .icon-btn', els => els.every(e => e.getAttribute('aria-label'))));
    check('current page marked for screen readers', await p.$eval('#nav button[data-page="lighting"]', e => e.hasAttribute('aria-current')));
  }

  check('no JavaScript errors', errors.length === 0, errors.join(' | '));

  // --- security: hostile strings from plugins, config.json, hardware and the host never become markup (the page can
  // approve SMBus plugins in an elevated process, so script injection = privilege escalation). A fresh tab rewrites every
  // host message through mock.js's __mockTamper hook; a MutationObserver records any injected element or handler.
  {
    const xp = await b.newPage();
    await xp.setViewport({ width: 1320, height: 860 });
    xp.setDefaultTimeout(4000);
    const xerr = [];
    xp.on('pageerror', e => xerr.push(e.message));
    await xp.evaluateOnNewDocument(() => {
      const P = `"'><img src=x onerror=window.__xss=1 data-xss>`;                               // text + attribute breakout
      const C = `red;background:url(x)"><img src=x onerror=window.__xss=1 data-xss>`;           // colour: CSS + markup injection
      window.__inj = [];
      const BAD = 'img,script,iframe,object,embed,[data-xss],[onerror],[onload],[onclick],[onmouseover]';
      const own = n => n.matches('script[src="mock.js"], script[src="app.js"], script[src="tuning-format.js"], script[src="monitor.js"]');                   // index.html's own scripts
      const look = n => { if (n.nodeType !== 1) return; [n, ...n.querySelectorAll(BAD)].forEach(x => { if (x.matches(BAD) && !own(x)) window.__inj.push(x.outerHTML.slice(0, 120)); }); };
      new MutationObserver(ms => ms.forEach(m => m.addedNodes.forEach(look))).observe(document, { childList: true, subtree: true });
      const zone = { id: P, dev: P, start: 0, count: 8, kind: 'ring', cols: 0, category: P, name: P };
      window.__mockTamper = m => {
        if (m.type === 'init') {
          const c = m.config, L = c.lighting, prof = c.profiles[c.activeProfile];
          c.profiles[P] = structuredClone(prof); c.curves[P] = [[30, 20], [80, 100]];
          prof[0].name = P; prof[0].override = P; prof[0].minDuty = P; prof[0].inputs[0].weight = P; prof[0].inputs[0].hysteresis = P; prof[0].inputs[0].smoothingSec = P;
          prof[1].inputs.push({ sensor: P, curve: P, weight: 1, hysteresis: 2, smoothingSec: 0 }); prof[2].fixed = P;
          c.fanNames['cc:fan1'] = P; c.sensorNames[P] = P; c.sensorNames.cpu = P; c.failsafeDuty = P; c.pollMs = P;
          L.brightness = P; L.fps = P; L.presets[P] = { type: 'twinkle', colors: [C], background: C };
          L.ledCounts['lnp:ch1'] = P; L.overrides[P] = { type: 'static', colors: [C] };
          L.effect = { type: 'plugin', plugin: P, colors: [C], params: { [P]: P, col: C, cols: [C, C] } };
          c.homeAssistant = { enabled: true, host: P, port: P, tls: false, username: P, discoveryPrefix: P, baseTopic: P };
          c.openRgb = { enabled: true, host: P, port: P, exclude: [P] };
          Object.assign(c.overlay, { offsetX: P, offsetY: P, scale: P, opacity: P, backgroundOpacity: P, accent: C, items: [{ source: P, label: P, color: C }, { source: 'fps', color: C }, ...c.overlay.items] });
          m.deviceNames[P] = P; m.zones.push(zone); m.devices.push({ id: P, name: P, zones: [{ id: P, name: P, leds: P }] });
          m.hubs[0].firmware = P; m.hubs[0].name = P; m.fans.push({ id: P, name: P, device: P });
          m.version = P; m.plugins.exe = P;
          m.plugins.list.push({ file: P, path: P, source: 'user', kind: P, name: P, enabled: true, ok: false, message: P, needsApproval: true });
          m.plugins.effects.push({ name: P, description: P, preview: [C, C], params: {
            [P]: { type: 'number', label: P, min: P, max: P, step: P, default: P }, col: { type: 'color', label: P, default: C },
            cols: { type: 'colors', label: P, default: [C] }, sel: { type: 'select', label: P, options: [P] }, sen: { type: 'sensor', label: P, default: P }, on: { type: 'bool', label: P } } });
        } else if (m.type === 'state') {
          m.sensors[P] = 50; m.sensors['cc:' + P] = 40; m.rpm[P] = 900; m.perf.devices[P] = P;
        } else if (m.type === 'sensors') {
          m.catalog.hardware.push({ id: P, name: P, type: P, sub: [], sensors: [{ id: P, name: P, type: P, unit: P, value: 1, min: 1, max: 2, aliases: [P, '/' + P] }] });
          m.catalog.error = P;
        } else if (m.type === 'tuning' && m.status) {
          const st = m.status, IMG = '<img src=x onerror=window.__xss=1 data-xss>';
          st.gpu.name = P; st.gpu.driver = P; st.reverted = P; st.history.forEach(r => { r.why = P; r.driver = P; r.loadMhz = P; r.core = P; r.thermalLimited = true; });
          st.compare.tuned.loadMhz = P; st.compare.stock.rate = P; st.compare.tuned.core = P;
          if (st.testing) st.testing.core = P;
          if (st.live) st.live.throttle = [P, 'thermal'];
          m.ai.transcript.push(P, '**' + P + '**', '[x](javascript:window.__xss=1) ' + IMG, '# ' + P, '- ' + P, '| a | ' + P + ' |\n|---|---|\n| ' + IMG + ' | `' + IMG + '` |',
            '→ tuning.gpu.test {"core":"' + IMG + '","memAb":0}', '→ ' + IMG, '! ' + P);
          m.ai.summary = P + '\n\n## ' + P + '\n* ' + IMG; m.ai.goal = P; m.ai.running = false;
        } else if (m.type === 'tuningHistory') {
          m.history.items.forEach(r => { r.why = P; r.core = P; r.memAb = P; r.loadMhz = P; r.thermalLimited = true; r.powerLimited = true; r.time = P; });
        } else if (m.type === 'setup') {
          m.report.items.push({ id: P, title: P, status: 'fail', detail: P, fixLabel: P, fix: P, fixArg: P, important: true });
          m.report.items.push({ id: P + 2, title: P, status: P, detail: P, important: false });
        }
        return m;
      };
    });
    await xp.goto('file://' + page);
    await wait(900);
    const xnav = async pg => { await xp.evaluate(g => go(g), pg); await wait(300); };
    for (const pg of ['overview', 'cooling', 'sensors', 'lighting', 'devices', 'plugins', 'overlay', 'setup', 'settings']) await xnav(pg);
    // every cooling rule (fixed, override, hostile sensor input), the lighting page with hostile built-in colours, plugin params and a zone
    for (let i = 0; i < 3; i++) { await xp.evaluate(i => { S.profile = S.cfg.activeProfile; S.rule = i; S.curve = null; go('cooling'); }, i); await wait(200); }
    await xp.evaluate(() => { const P = Object.keys(S.cfg.profiles).find(k => k.includes('<img')); S.profile = P; S.rule = 0; renderCooling(); });
    await xp.evaluate(() => { go('lighting'); }); await wait(200);
    await xp.evaluate(() => { const P = S.init.zones.at(-1).id; setTarget(P); }); await wait(150);
    await xp.evaluate(() => { setTarget('all'); S.cfg.lighting.effect = { type: 'twinkle', colors: [S.cfg.overlay.accent], background: S.cfg.overlay.accent, speed: '<b>', angle: '<b>', range: ['<b>', '<i>'] }; renderLighting(); });
    await xp.evaluate(() => { S.cfg.lighting.effect = { type: 'temperature', colors: [S.cfg.overlay.accent], sensor: 'cpu', range: ['"><img src=x data-xss>', 1] }; renderLighting(); });
    await xp.evaluate(() => { go('overlay'); document.getElementById('ov-add').click(); }); await wait(1300);
    await xp.evaluate(() => { go('sensors'); }); await wait(1300);
    await xp.evaluate(() => { go('devices'); toast('"><img src=x onerror=window.__xss=1 data-xss>'); }); await wait(200);
    await xp.evaluate(() => { go('tuning'); }); await wait(500);
    await xp.evaluate(() => document.getElementById('tn-ai-start').click()); await wait(700);
    await xp.evaluate(() => { document.querySelector('[data-hp="1"]')?.click(); }); await wait(500);
    const tnx = await xp.evaluate(() => ({ log: document.getElementById('tn-log').textContent.length, anchors: document.querySelectorAll('#page-tuning a').length, rows: document.querySelectorAll('#tn-history tbody tr').length }));
    check('xss: tuning page (transcript Markdown, results, history, compare) is inert and still renders', tnx.log > 50 && tnx.anchors === 0 && tnx.rows > 0, JSON.stringify(tnx));
    const r = await xp.evaluate(() => ({
      xss: window.__xss, inj: window.__inj, now: document.querySelectorAll('img, [data-xss]').length,
      css: [...document.querySelectorAll('[style]')].filter(e => /url\(|;\s*background:\s*url/i.test(e.getAttribute('style'))).map(e => e.getAttribute('style').slice(0, 80)),
      shown: document.body.innerHTML.includes('&lt;img src=x onerror'),
    }));
    check('xss: hostile plugin / config / sensor / setup strings inject no elements on any page', r.inj.length === 0 && r.now === 0, r.inj.slice(0, 3).join(' | '));
    check('xss: no injected handler ran (window.__xss unset)', r.xss === undefined, r.xss);
    check('xss: hostile colours never reach a style attribute (no url())', r.css.length === 0, r.css.slice(0, 3).join(' | '));
    check('xss: hostile strings are shown as text', r.shown);
    // CSP: even if markup got in, inline handlers / inline script must not run
    const csp = await xp.evaluate(async () => {
      const meta = document.querySelector('meta[http-equiv="Content-Security-Policy"]')?.content ?? '';
      const d = document.createElement('div'); document.body.append(d);
      d.innerHTML = '<img src="data:image/gif;base64,R0lGODlhAQABAAAAACw=" onload="window.__cspInline=1" onerror="window.__cspInline=1">';
      const s = document.createElement('script'); s.textContent = 'window.__cspScript=1'; document.body.append(s);
      try { setTimeout('window.__cspEval=1', 0); } catch { }                // string timers are eval (DevTools' own evaluate is exempt from CSP)
      await new Promise(r => setTimeout(r, 300)); d.remove(); s.remove();
      return { meta, inline: window.__cspInline, script: window.__cspScript, evalRan: window.__cspEval, app: typeof go === 'function' && !!S.cfg };
    });
    check('csp: meta forbids inline/remote script', /script-src 'self'(;|$)/.test(csp.meta) && /default-src 'none'/.test(csp.meta) && /object-src 'none'/.test(csp.meta) && /base-uri 'none'/.test(csp.meta), csp.meta);
    check('csp: inline handlers, inline <script> and eval are blocked; app.js + mock.js still load', csp.inline === undefined && csp.script === undefined && csp.evalRan === undefined && csp.app, JSON.stringify(csp));
    check('xss: no JavaScript errors with hostile data', xerr.length === 0, xerr.slice(0, 3).join(' | '));
    await xp.close();
  }
  // --- tuning page (AI overclocking; on in the demo config, off by default in Glint)
  await nav('tuning');
  await wait(300);
  check('tuning: page shows the AI card and the GPU', await p.$eval('#page-tuning', el => el.textContent.includes('Tune with AI') && el.textContent.includes('Demo GeForce RTX 4080')));
  check('tuning: history table lists the first page of tests (pass and fail)', await p.$$eval('#tn-history tbody tr', r => r.length) === 15 && await p.$eval('#tn-history', el => el.textContent.includes('fail') && el.textContent.includes('pass')));
  check('tuning: saved result shown', await p.$eval('#tn-gpu', el => el.textContent.includes('+120') && el.textContent.includes('+750')));
  check('tuning: CPU card shows per-core values and the BIOS sheet', await p.$eval('#tn-cpu', el => el.textContent.includes('Negative 19') && el.querySelectorAll('.tn-cores span').length === 8));
  await p.click('#tn-ai-start'); await wait(400);
  check('tuning: Tune with AI shows the live transcript', await p.$eval('#tn-log', el => el.textContent.includes('Test core +150') && el.textContent.includes('Starting Claude Code')));
  check('tuning: start disabled while a run is going', await p.$eval('#tn-ai-start', el => el.disabled));
  await p.click('#tn-ai-stop'); await wait(300);

  // --- tuning page: formatting, stock vs tuned, unlock toggle, paginated history, monitor button
  await nav('tuning');
  await wait(500);
  const mdt = await p.evaluate(() => ({
    img: TF.md('<img src=x onerror=window.__t=1> **b**'), link: TF.md('[click](javascript:window.__t=1)'), code: TF.md('use `**not bold**` and **bold** and *it*'),
    tbl: TF.md('| a | b |\n|---|---|\n| 1 | **2** |'), list: TF.md('1. one\n2. two\n\n- x\n- y'), head: TF.md('## Title\ntext'), fence: TF.md('```\n<b>x</b>\n```'),
  }));
  check('markdown: raw HTML is escaped, links drop their target', !/<img/i.test(mdt.img) && mdt.img.includes('&lt;img') && !/<a[\s>]/.test(mdt.link) && !mdt.link.includes('javascript'), mdt.img + ' | ' + mdt.link);
  check('markdown: bold/italic/code/table/lists/headings become elements', mdt.code.includes('<code>**not bold**</code>') && mdt.code.includes('<strong>bold</strong>') && mdt.code.includes('<em>it</em>')
    && mdt.tbl.includes('<table') && mdt.tbl.includes('<th>a</th>') && mdt.tbl.includes('<strong>2</strong>') && mdt.list.includes('<ol') && mdt.list.includes('<ul') && mdt.head.includes('<h4') && mdt.fence.includes('&lt;b&gt;x&lt;/b&gt;'), JSON.stringify(mdt).slice(0, 200));
  const steps = await p.evaluate(() => ['→ tuning.gpu.test {"core":250,"memAb":0,"seconds":45,"light":true}', '→ tuning.status {}', '→ tuning.gpu.save {"core":200,"memAb":750}', '→ tuning.gpu.stock {}', '→ tuning.wait {"seconds":25}', '→ tuning.gpu.test {"core":100,"memAb":500,"seconds":4', 'plain text'].map(l => TF.stepText(l)));
  check('steps: tool calls become friendly text, tuning.wait is hidden', JSON.stringify(steps) === JSON.stringify(['Test core +250 / memory +0 · 45 s (light)', 'Read status', 'Save core +200 / memory +750', 'Back to stock', '', 'Test core +100 / memory +500 · 4 s', null]), JSON.stringify(steps));
  // the AI log on the Tuning page
  await p.click('#tn-ai-start'); await wait(500);
  const lg = await p.$eval('#tn-log', el => ({ t: el.textContent, steps: [...el.querySelectorAll('.tf-step')].map(x => x.textContent), tbl: el.querySelectorAll('table.md-table').length, strong: el.querySelectorAll('strong').length, li: el.querySelectorAll('ol li').length, res: el.querySelectorAll('.tf-res').length, run: el.querySelectorAll('.tf-res.run').length }));
  check('tuning log: tool calls shown as friendly steps, no raw JSON or tuning.wait', lg.steps.some(x => x.includes('Test core +150 / memory +500 · 45 s')) && lg.steps.some(x => x.includes('Read status')) && !lg.t.includes('{"core"') && !lg.t.includes('tuning.wait') && !lg.t.includes('seconds":25'), lg.steps.join(' | '));
  check('tuning log: Markdown rendered (table, bold, numbered list), no raw asterisks or pipes', lg.tbl === 1 && lg.strong >= 2 && lg.li === 3 && !lg.t.includes('**') && !lg.t.includes('|---'), JSON.stringify({ tbl: lg.tbl, strong: lg.strong, li: lg.li }));
  check('tuning log: finished tests are result cards, the running one is marked', lg.res === 3 && lg.run === 1, JSON.stringify({ res: lg.res, run: lg.run }));
  const mq = await p.evaluate(() => (window.__msgs ?? []).filter(m => m.type === 'tuning').length);
  await p.click('#tn-monitor'); await wait(150);
  check('tuning: "Open monitor" asks the host for the monitor window', await p.evaluate(() => (window.__msgs ?? []).some(m => m.type === 'tuning' && m.op === 'monitor')), mq);
  await p.click('#tn-ai-stop'); await wait(300);
  // power / limits wording and the unlock toggle
  const gp = await p.$eval('#page-tuning', el => ({ head: el.querySelector('.tn-head').textContent, sub: el.querySelector('#tn-gpu-sub').textContent, gpu: el.querySelector('#tn-gpu').textContent }));
  check('tuning: header explains the new limits model (no "never ... power limits")', gp.head.includes("card's own maximum") && gp.head.includes('emergency') && !/never voltage or power limits/.test(gp.head) && !/stop at your temperature limits/.test(gp.head), gp.head.slice(0, 160));
  check('tuning: GPU card shows power limit now/default/max and slowdown + emergency temperatures', gp.sub.includes('320 W now') && gp.sub.includes('340 W card maximum') && gp.gpu.includes('slows down at 83 °C') && gp.gpu.includes('86 °C') && gp.gpu.includes('100 °C'), gp.sub + ' | ' + gp.gpu.slice(0, 200));
  check('tuning: unlock-power toggle starts on', !!(await p.$('#tn-unlock.on')) && /Unlock power limit \(up to this card's maximum/.test(await p.$eval('#tn-unlock + label', e => e.textContent)));
  await p.click('#tn-unlock');
  check('tuning: unlock-power toggle goes through the config (off)', (await cfg())?.tuning?.unlockPower === false);
  await p.click('#tn-unlock');
  check('tuning: unlock-power toggle goes through the config (on again)', (await cfg())?.tuning?.unlockPower === true);
  // stock vs tuned
  await wait(3300);
  const cm = await p.evaluate(() => ({
    rows: [...document.querySelectorAll('#tn-cmp .cmp-row')].map(r => ({ k: r.dataset.cmp, d: r.querySelector('.cmp-delta').textContent, c: r.querySelector('.cmp-delta').className, s: r.querySelector('.cmp-stock i').style.width, t: r.querySelector('.cmp-tuned i').style.width })),
    legend: document.querySelector('#tn-cmp .tn-cmp-legend')?.textContent ?? '',
  }));
  const by = k => cm.rows.find(r => r.k === k);
  check('compare: bars for clock, compute, VRAM, power and temperature', cm.rows.map(r => r.k).join() === 'loadMhz,rate,gBps,powerW,peakCoreC', cm.rows.map(r => r.k).join());
  check('compare: labels Stock and Tuned (core +120 / memory +750)', cm.legend.includes('Stock') && cm.legend.includes('Tuned (core +120 / memory +750)'), cm.legend);
  check('compare: % deltas (clock +7.0 % green, power +10.0 % amber), tuned bar longer than stock', by('loadMhz')?.d === '+7.0%' && by('loadMhz').c.includes('gain') && by('powerW')?.d === '+10.0%' && by('powerW').c.includes('cost') && by('loadMhz').t === '100%' && parseFloat(by('loadMhz').s) < 100, JSON.stringify(cm.rows[0]) + JSON.stringify(by('powerW')));
  await p.evaluate(() => { window.__mockNoStock = true; tuningAsk(); }); await wait(400);
  check('compare: no baseline → clear empty state, no bars', (await p.$$('#tn-cmp .cmp-row')).length === 0 && (await p.$eval('#tn-cmp-empty', e => e.textContent)).includes('measures stock first'));
  await p.evaluate(() => { window.__mockNoStock = false; tuningAsk(); }); await wait(300);
  // paginated history: 38 saved tests, 15 per page
  const hs = async () => p.evaluate(() => ({ rows: document.querySelectorAll('#tn-history tbody tr').length, info: document.getElementById('tn-hinfo')?.textContent ?? '', first: document.querySelector('#tn-history tbody tr')?.textContent ?? '', cur: document.querySelector('#tn-history .cur')?.textContent ?? '', nums: [...document.querySelectorAll('#tn-history [data-hp]')].map(b => b.dataset.hp).join() }));
  await p.evaluate(() => { try { sessionStorage.clear(); } catch { } tnHist.page = 0; renderTuning(); }); await wait(500);
  let h1 = await hs();
  check('history: page 1 has 15 rows, total 38, pages 1..3', h1.rows === 15 && h1.info.includes('1–15 of 38 tests') && h1.info.includes('page 1 of 3') && h1.nums === 'prev,0,1,2,next', JSON.stringify(h1));
  check('history: rows show clock, compute, VRAM, power, peak and limited chips', await p.$eval('#tn-history', el => el.querySelectorAll('.tf-chip.warn').length > 0 && /\d+ G\/s/.test(el.textContent) && /\d+ GB\/s/.test(el.textContent) && /\d+ W/.test(el.textContent) && /\d+ °C/.test(el.textContent)));
  await p.click('#tn-history [data-hp="1"]'); await wait(500);
  const h2 = await hs();
  check('history: page 2 shows the next 15 rows', h2.rows === 15 && h2.info.includes('16–30 of 38') && h2.cur === '2' && h2.first !== h1.first, h2.info);
  await p.click('#tn-history [data-hp="next"]'); await wait(500);
  const h3 = await hs();
  check('history: last page shows the remaining 8, Older is disabled', h3.rows === 8 && h3.info.includes('31–38 of 38') && await p.$eval('#tn-history [data-hp="next"]', b => b.disabled), h3.info);
  await p.click('#tn-history [data-hp="1"]'); await wait(400);
  await wait(3300);                                                       // the 3 s refresh must keep the page
  check('history: the page survives the 3 s refresh', (await hs()).info.includes('page 2 of 3'), (await hs()).info);
  await nav('overview'); await nav('tuning'); await wait(600);
  check('history: the page survives leaving and re-rendering the Tuning page', (await hs()).info.includes('page 2 of 3') && (await hs()).cur === '2', (await hs()).info);
  await p.evaluate(() => window.__mockAddTest()); await wait(3500);
  const h4 = await hs();
  check('history: a new test does not yank the reader off page 2; total goes to 39', h4.info.includes('page 2 of 3') && h4.info.includes('of 39 tests'), h4.info);
  await p.click('#tn-history [data-hp="0"]'); await wait(500);
  await p.evaluate(() => window.__mockAddTest()); await wait(3500);
  const h5 = await hs();
  check('history: on page 1 the newest test appears at the top', h5.info.includes('page 1 of 3') && h5.info.includes('of 40 tests') && /\+135/.test(h5.first), h5.info + ' | ' + h5.first.slice(0, 60));
  await p.select('#tn-psize', '30'); await wait(600);
  const h6 = await hs();
  check('history: page size 30 → 30 rows, 2 pages; the choice survives a re-render', h6.rows === 30 && h6.info.includes('page 1 of 2'), h6.info);
  await nav('overview'); await nav('tuning'); await wait(600);
  check('history: page size is remembered', (await hs()).rows === 30);
  await p.select('#tn-psize', '15'); await wait(500);
  check('history: back to 15 per page', (await hs()).rows === 15);

  // --- tuning monitor window (monitor.html): rendered against mock.js's demo run
  {
    const mp = await b.newPage();
    await mp.setViewport({ width: 520, height: 720 });
    mp.setDefaultTimeout(4000);
    const merr = [];
    mp.on('pageerror', e => merr.push(e.message)); mp.on('console', m => { if (m.type() === 'error') merr.push(m.text()); });
    await mp.evaluateOnNewDocument(() => {
      window.__inj = [];
      const BAD = 'img,script,iframe,object,embed,[data-xss],[onerror],[onload]';
      const own = n => n.matches('script[src="mock.js"], script[src="tuning-format.js"], script[src="monitor.js"]');
      new MutationObserver(ms => ms.forEach(m => m.addedNodes.forEach(n => { if (n.nodeType === 1) [n, ...n.querySelectorAll(BAD)].forEach(x => { if (x.matches(BAD) && !own(x)) window.__inj.push(x.outerHTML.slice(0, 100)); }); }))).observe(document, { childList: true, subtree: true });
    });
    await mp.goto('file://' + page.replace(/index\.html$/, 'monitor.html'));
    await wait(1300);
    const T = async f => mp.evaluate(f);
    const hd = await T(() => ({ title: document.getElementById('m-title').textContent, sub: document.getElementById('m-sub').textContent, stop: document.getElementById('m-stop').disabled, prog: !document.getElementById('m-prog').hidden }));
    check('monitor: header says what is being tested, with a progress bar and an enabled Stop', hd.title === 'Testing core +150 / memory +500 · 45 s' && hd.sub.includes('AI tuning GPU') && !hd.stop && hd.prog, JSON.stringify(hd));
    check('monitor: the header shows the run\'s goal and the user\'s context', hd.sub.includes('Goal: Balanced') && hd.sub.includes('I game at 4K'), hd.sub);
    const lg = await T(() => { const el = document.getElementById('m-log'); return { t: el.textContent, steps: [...el.querySelectorAll('.tf-step')].map(x => x.textContent), tbl: el.querySelectorAll('table.md-table th').length, strong: el.querySelectorAll('strong').length, ol: el.querySelectorAll('ol li').length, code: el.querySelectorAll('code').length, pass: el.querySelectorAll('.tf-res.pass').length, run: el.querySelectorAll('.tf-res.run').length, chip: el.querySelectorAll('.tf-res .tf-chip.warn').length }; });
    check('monitor: Markdown rendered as elements (table, bold, list, code), not raw asterisks', lg.tbl === 3 && lg.strong >= 2 && lg.ol === 3 && lg.code >= 1 && !lg.t.includes('**') && !lg.t.includes('|---'), JSON.stringify({ tbl: lg.tbl, strong: lg.strong, ol: lg.ol, code: lg.code }));
    check('monitor: tool calls are friendly steps; tuning.wait and raw JSON are hidden', lg.steps.length === 4 && lg.steps[0] === '▸Read status' && lg.steps.includes('▸Test core +100 / memory +0 · 45 s') && lg.steps.includes('▸Test core +0 / memory +0 · 30 s (light)') && !lg.t.includes('tuning.') && !lg.t.includes('{"'), lg.steps.join(' | '));
    check('monitor: finished tests are result rows (pass, power-limited chip), the current one is RUNNING', lg.pass === 2 && lg.run === 1 && lg.chip >= 1 && lg.t.includes('434 G ops/s') && lg.t.includes('903 GB/s'), JSON.stringify({ pass: lg.pass, run: lg.run, chip: lg.chip }));
    const gg = await T(() => ({ n: document.querySelectorAll('.mg').length, txt: document.getElementById('m-gauges').textContent, warn: document.querySelectorAll('.mg .mk.warn').length, bad: document.querySelectorAll('.mg .mk.bad').length, chips: document.getElementById('m-chips').textContent }));
    check('monitor: gauges for core/memory temperature, clock, power, fan and load, with slowdown + emergency marks', gg.n === 6 && /GPU core/i.test(gg.txt) && /Memory temp/i.test(gg.txt) && /Power/i.test(gg.txt) && gg.warn >= 2 && gg.bad >= 2 && gg.txt.includes('340 W'), JSON.stringify({ n: gg.n, warn: gg.warn, bad: gg.bad }));
    check('monitor: power-limited chip while the card is power-capped', gg.chips.includes('power-limited'), gg.chips);
    const pw = await T(() => {
      const base = structuredClone(M.msg), w = () => document.getElementById('m-prog-fill').style.width;
      window.__now = () => 1e12; const at = s => { const m = structuredClone(base); m.status.testing.seconds = 60; m.status.testing.startedAt = new Date(1e12 - s * 1000).toISOString(); monitorApply(m); return w(); };
      const r = [at(0), at(15), at(30), at(60), at(90)];
      const m = structuredClone(base); m.status.testing = null; monitorApply(m); r.push(document.getElementById('m-prog').hidden);
      const t = structuredClone(base); t.status.live.throttle = ['thermal slowdown']; monitorApply(t); r.push(document.getElementById('m-chips').textContent);
      delete window.__now; return r;
    });
    check('monitor: progress bar width follows elapsed time (0 / 25 / 50 / 100 / clamped 100 %), hidden when no test runs', pw[0] === '0%' && pw[1] === '25%' && pw[2] === '50%' && pw[3] === '100%' && pw[4] === '100%' && pw[5] === true, pw.slice(0, 6).join(' '));
    check('monitor: thermal-limited chip when the card throttles on temperature', String(pw[6]).includes('thermal-limited'), pw[6]);
    const fin = await T(() => {
      const m = structuredClone(M.msg); m.ai.running = false; m.status.busy = false; m.status.testing = null; m.status.live = null; m.ai.summary = '## Result\n**Saved** core `+120` / memory `+750`\n\n| Metric | Stock | Tuned |\n|---|---|---|\n| Clock | 2790 | **2985** |\n\n- stable for 180 s';
      monitorApply(m);
      const s = document.querySelector('#m-log .tf-summary');
      return { h: !!s?.querySelector('h4'), tbl: s?.querySelectorAll('td').length, strong: s?.querySelectorAll('strong').length, li: s?.querySelectorAll('li').length, title: document.getElementById('m-title').textContent, stop: document.getElementById('m-stop').disabled, once: document.querySelectorAll('#m-log .tf-say').length };
    });
    check('monitor: the final summary is rendered as Markdown; header goes back to Idle', fin.h && fin.tbl === 3 && fin.strong >= 2 && fin.li === 1 && fin.title === 'Idle' && fin.stop, JSON.stringify(fin));
    const er = await T(() => { monitorApply({ type: 'tuning', error: 'GPU busy' }); return document.getElementById('m-title').textContent; });
    check('monitor: a host error is shown, not a blank window', er === 'Status unavailable', er);
    await mp.evaluate(() => { window.__mockIdleMonitor = false; });
    await wait(1300);                                                       // the mock's 1 s push brings the demo run back
    await mp.click('#m-top');
    check('monitor: "Keep on top" toggle tells the host', await T(() => document.getElementById('m-top').classList.contains('on') && window.__msgs.some(m => m.type === 'top' && m.on === true)));
    await mp.click('#m-stop'); await wait(1400);
    const sp = await T(() => ({ sent: window.__msgs.some(m => m.type === 'action' && m.op === 'stop'), title: document.getElementById('m-title').textContent, stop: document.getElementById('m-stop').disabled, bar: document.getElementById('m-prog').hidden }));
    check('monitor: Stop sends the stop action and the window shows Idle afterwards', sp.sent && sp.title === 'Idle' && sp.stop && sp.bar, JSON.stringify(sp));
    check('monitor: asks the host for status on load', await T(() => window.__msgs.some(m => m.type === 'ready' && m.page === 'monitor')));
    // hostile transcript / status text stays inert
    const hx = await T(() => {
      const P = `"'><img src=x onerror=window.__xss=1 data-xss>`, m = structuredClone(M.msg);
      m.ai.running = true; m.ai.goal = P; m.status.gpu.name = P; m.status.testing = { core: P, memAb: P, seconds: P, light: true, startedAt: P };
      m.status.live = { coreC: P, memC: P, mhz: P, powerW: P, fanPct: P, utilPct: P, throttle: [P] }; m.status.history.forEach(r => { r.why = P; r.core = P; });
      m.ai.transcript = [P, '**' + P + '**', '[x](javascript:window.__xss=1)', '# ' + P, '| a | b |\n|-|-|\n| ' + P + ' | ' + P + ' |', '→ tuning.gpu.test {"core":"' + P + '"}', '→ ' + P, '! ' + P];
      m.ai.summary = P; monitorApply(m);
      return { xss: window.__xss, inj: window.__inj, a: document.querySelectorAll('a').length, shown: document.getElementById('m-log').innerHTML.includes('&lt;img src=x onerror') };
    });
    check('monitor xss: hostile transcript / status text injects nothing and runs nothing', hx.xss === undefined && hx.inj.length === 0 && hx.a === 0 && hx.shown, JSON.stringify(hx).slice(0, 200));
    check('monitor: no JavaScript errors', merr.length === 0, merr.slice(0, 3).join(' | '));
    await mp.close();
  }
  // ===== profiles: header switcher, manager, per-profile overclocks, manual overclocking, AI goal, external config changes =====
  const dlg = { log: [], accept: true };
  const dlgFn = d => { dlg.log.push(d.message()); dlg.accept ? d.accept() : d.dismiss(); };
  p.on('dialog', dlgFn);
  const sent = (type, pred = () => true) => p.evaluate((t, f) => (window.__msgs ?? []).filter(m => m.type === t).map(m => JSON.parse(JSON.stringify(m))), type).then(a => a.filter(pred));
  const clearMsgs = () => p.evaluate(() => { window.__msgs.length = 0; });
  const hdr = () => p.evaluate(() => ({ seg: !document.getElementById('profile-seg').hidden, sel: !document.getElementById('profile-sel').hidden,
    names: [...document.querySelectorAll('#profile-seg button, #profile-sel option')].map(x => x.textContent), active: document.querySelector('#profile-seg button.on, #profile-sel option:checked')?.textContent }));
  const pmCreate = async (name, from) => { await p.evaluate((n, f) => { document.getElementById('pm-new').value = n; if (f) document.getElementById('pm-from').value = f; }, name, from); await p.click('#pm [data-pm="create"]'); await wait(200); };
  const pmErr = () => p.$eval('#pm-err', e => e.textContent);
  await nav('overview'); await wait(2700);
  await p.evaluate(() => window.__mockConfigChanged(c => { for (const n of Object.keys(c.profiles)) if (n.startsWith('AI ')) delete c.profiles[n]; c.activeProfile = 'Balanced'; }));   // earlier checks started an AI run: start this section from the three demo profiles
  await wait(400);
  let h0 = await hdr(); c = await cfg();
  check('header: with three profiles it shows segmented buttons, not the dropdown', h0.seg && !h0.sel && h0.names.length === 3, JSON.stringify(h0));
  await p.click('#profile-manage'); await wait(150);
  check('manager: the header button opens a dialog listing every profile', !!(await p.$('#pm [role="dialog"]')) && (await p.$$('#pm tr[data-prow]')).length === 3);
  await p.keyboard.press('Escape'); await wait(100);
  check('manager: Escape closes it', !(await p.$('#pm')));
  await p.click('#profile-manage'); await wait(100);
  await pmCreate('Gaming', 'Quiet'); c = await cfg();
  check('manager: create makes a copy of the chosen profile (same rules, not the same object)', !!c.profiles.Gaming && JSON.stringify(c.profiles.Gaming) === JSON.stringify(c.profiles.Quiet) && Object.keys(c.profiles).length === 4, Object.keys(c.profiles).join());
  const nRules = c.profiles.Gaming.length;
  const nBefore = Object.keys(c.profiles).length;
  const bad = [['', 'Give the profile a name'], ['   ', 'Give the profile a name'], ['stock', 'reserved'], ['STOCK', 'reserved'], ['balanced', 'already exists'], [' QUIET ', 'already exists'], ['x'.repeat(41), '40 characters'], ['bad\u0007name', 'control']];
  const badRes = [];
  for (const [n, want] of bad) { await pmCreate(n); badRes.push([JSON.stringify(n.slice(0, 8)), (await pmErr()).includes(want)]); }
  c = await cfg();
  check('manager: invalid names (empty, Stock, duplicate ignoring case, > 40 chars, control character) are refused with a message', badRes.every(x => x[1]) && Object.keys(c.profiles).length === nBefore, JSON.stringify(badRes.filter(x => !x[1])));
  const XN = '<img src=x onerror=window.__xss=1>';
  await pmCreate(XN); c = await cfg();
  const xi = await p.evaluate(() => ({ xss: window.__xss, imgs: document.querySelectorAll('#pm img, #profile-seg img, #profile-sel img, #page-overview img, #page-cooling img').length, shown: document.querySelector('#pm tr[data-prow] b') ? [...document.querySelectorAll('#pm tr[data-prow] b')].some(b => b.textContent.includes('<img src=x')) : false }));
  check('manager: an XSS-looking name is stored as plain text and renders inert (manager, header)', !!c.profiles[XN] && xi.xss === undefined && xi.imgs === 0 && xi.shown, JSON.stringify(xi));
  for (const n of ['Night', 'Travel', 'Stream']) await pmCreate(n, 'Balanced');
  c = await cfg(); h0 = await hdr();
  check('header: with many profiles (8) the segmented buttons give way to a dropdown listing all of them', !h0.seg && h0.sel && h0.names.length === 8 && h0.names.includes(XN) && !(await p.$('#profile-seg button')), JSON.stringify(h0).slice(0, 200));
  await p.keyboard.press('Escape'); await wait(100);
  await p.select('#profile-sel', 'Night'); c = await cfg();
  check('header dropdown: choosing a profile sends activeProfile', c.activeProfile === 'Night', c.activeProfile);
  check('header dropdown reflects the active profile', (await hdr()).active === 'Night');
  await wait(2700); await clearMsgs();
  await p.click('#profile-manage'); await wait(100);
  await p.click('#pm tr[data-prow="Travel"] [data-pm="rename"]'); await wait(100);
  await p.evaluate(() => { document.getElementById('pm-ren').value = 'Stock'; }); await p.click('#pm [data-pm="rename-ok"]');
  check('manager: renaming to a bad name shows an error and sends nothing', (await pmErr()).includes('reserved') && (await sent('profileRename')).length === 0);
  await p.evaluate(() => { document.getElementById('pm-ren').value = 'Weekend'; }); await p.click('#pm [data-pm="rename-ok"]'); await wait(300);
  const rm = await sent('profileRename');
  c = await p.evaluate(() => window.__lastConfig); h0 = await hdr();
  check('manager: rename sends one profileRename {from,to}; the page shows the host\'s result (renamed, same position)', rm.length === 1 && rm[0].from === 'Travel' && rm[0].to === 'Weekend' && h0.names.includes('Weekend') && !h0.names.includes('Travel') && Object.keys(c.profiles).indexOf('Weekend') === 6, JSON.stringify(rm) + ' ' + h0.names.join());
  await p.click('#pm tr[data-prow="Night"] [data-pm="rename"]'); await wait(100);
  await p.evaluate(() => { document.getElementById('pm-ren').value = 'Evening'; }); await p.click('#pm [data-pm="rename-ok"]'); await wait(300);
  h0 = await hdr();
  check('manager: renaming the active profile keeps it active', h0.active === 'Evening' && (await p.evaluate(() => S.cfg.activeProfile)) === 'Evening', h0.active);
  check('manager: the active profile\'s Delete is disabled', await p.$eval('#pm tr[data-prow="Evening"] [data-pm="delete"]', b => b.disabled));
  await clearMsgs();
  await p.$eval('#pm tr[data-prow="Evening"] [data-pm="delete"]', b => { b.disabled = false; b.click(); }); await wait(200);
  c = await cfg();
  check('manager: deleting the active profile is refused even if the button is forced', (await pmErr()).includes('active profile') && !!c.profiles.Evening && (await sent('config')).length === 0, await pmErr());
  dlg.accept = false; dlg.log.length = 0;
  await p.click('#pm tr[data-prow="Stream"] [data-pm="delete"]'); await wait(200);
  check('manager: delete asks first; cancelling keeps the profile', dlg.log.length === 1 && !!(await p.evaluate(() => S.cfg.profiles.Stream)));
  dlg.accept = true;
  await p.click('#pm tr[data-prow="Stream"] [data-pm="delete"]'); c = await cfg();
  check('manager: confirmed delete removes the profile from the config', !c.profiles.Stream && Object.keys(c.profiles).length === 7, Object.keys(c.profiles).join());
  const ord0 = Object.keys(c.profiles);
  await p.click(`#pm tr[data-prow="${ord0[1]}"] [data-pm="up"]`); c = await cfg();
  check('manager: move up reorders the profiles in the config', Object.keys(c.profiles)[0] === ord0[1] && Object.keys(c.profiles)[1] === ord0[0], Object.keys(c.profiles).join());
  await p.click('#pm [data-pm="switch"]:not([disabled])'); c = await cfg();
  check('manager: Switch makes a profile active', c.activeProfile !== 'Evening');
  await p.click('#pm [data-pm="close"]'); await wait(100);
  check('manager: closes with its button', !(await p.$('#pm')));
  check('manager: the profile with an XSS-looking name did not run anything (after all operations)', (await p.evaluate(() => window.__xss)) === undefined);

  // --- per-profile overclock table on the Tuning page
  await p.evaluate(() => { document.getElementById('profile-sel').value = 'Balanced'; document.getElementById('profile-sel').dispatchEvent(new Event('change')); });
  await nav('tuning'); await wait(700);
  const rows = await p.$$eval('#tn-profiles tr[data-ocrow]', r => r.map(x => ({ n: x.dataset.ocrow, t: x.textContent.replace(/\s+/g, ' ') })));
  const nProf = Object.keys(await cfg().then(x => x.profiles)).length;
  check('overclocks table: one row per profile', rows.length === nProf && nProf === 7, rows.length);
  const rb = rows.find(r => r.n === 'Balanced'), rq = rows.find(r => r.n === 'Quiet'), rg = rows.find(r => r.n === 'Gaming');
  check('overclocks table: tested overclock shows offsets, tested tag, driver', /core \+120/.test(rb.t) && /memory \+750/.test(rb.t) && /tested/.test(rb.t) && !/untested/.test(rb.t) && rb.t.includes('999.99') && rb.t.includes('active'), rb.t);
  check('overclocks table: an untested saved overclock is marked untested', /core \+200/.test(rq.t) && /untested/.test(rq.t), rq.t);
  check('overclocks table: a profile with none says stock', /stock/.test(rg.t) && !/core/.test(rg.t), rg.t);
  check('overclocks table: Clear is disabled for stock, Switch for the active profile', await p.$eval('[data-ocrow="Gaming"] [data-pclear]', b => b.disabled) && await p.$eval('[data-ocrow="Balanced"] [data-pswitch]', b => b.disabled));
  await p.click('[data-ocrow="Quiet"] [data-pswitch]'); c = await cfg();
  check('overclocks table: Switch to it makes that profile active (config)', c.activeProfile === 'Quiet', c.activeProfile);
  await wait(2700); await nav('overview'); await nav('tuning'); await wait(600); await clearMsgs();
  dlg.accept = false; await p.click('[data-ocrow="Quiet"] [data-pclear]'); await wait(200);
  check('overclocks table: Clear asks first; cancel sends nothing', dlg.log.at(-1).includes('Clear the overclock') && (await sent('tuning', m => m.op === 'clearProfile')).length === 0);
  dlg.accept = true; await p.click('[data-ocrow="Quiet"] [data-pclear]'); await wait(600);
  const cl = await sent('tuning', m => m.op === 'clearProfile');
  check('overclocks table: confirmed Clear sends clearProfile for that profile and the row becomes stock', cl.length === 1 && cl[0].args.profile === 'Quiet' && /stock/.test(await p.$eval('[data-ocrow="Quiet"]', r => r.textContent)));

  // --- manual overclocking
  await p.evaluate(() => { document.getElementById('profile-sel').value = 'Balanced'; document.getElementById('profile-sel').dispatchEvent(new Event('change')); }); await wait(500);
  await p.evaluate(() => { S.tnMan = { core: 0, mem: 0, prof: null }; renderTuning(); }); await wait(700);
  const mx = await p.evaluate(() => ({ cm: +document.getElementById('tn-m-core').max, mm: +document.getElementById('tn-m-mem').max, info: document.getElementById('tn-m-info').textContent, pw: document.getElementById('tn-m-power').textContent, prof: document.getElementById('tn-m-prof').value }));
  check('manual: sliders span 0..the card\'s limits (core 500, memory 3000)', mx.cm === 500 && mx.mm === 3000, JSON.stringify(mx));
  check('manual: shows the applied offsets and the power-unlock state', mx.info.includes('Applied now') && /core \+0 MHz/.test(mx.info) && /unlocked/.test(mx.pw) && mx.pw.includes('340 W'), mx.info + ' | ' + mx.pw);
  check('manual: "Save to" defaults to the active profile', mx.prof === 'Balanced', mx.prof);
  const setN = async (id, v) => { await p.evaluate((i, x) => { const e = document.getElementById(i); e.value = x; e.dispatchEvent(new Event('change', { bubbles: true })); }, id, v); await wait(60); };
  await setN('tn-m-core-n', 9999); await setN('tn-m-mem-n', -40);
  let mv = await p.evaluate(() => ({ c: document.getElementById('tn-m-core').value, cn: document.getElementById('tn-m-core-n').value, m: document.getElementById('tn-m-mem').value }));
  check('manual: values clamp to 0..limit (9999 → 500, −40 → 0), slider and number agree', mv.c === '500' && mv.cn === '500' && mv.m === '0', JSON.stringify(mv));
  await p.evaluate(() => { const e = document.getElementById('tn-m-core'); e.value = 5000; e.dispatchEvent(new Event('input', { bubbles: true })); });
  check('manual: the slider itself cannot exceed its limit', (await p.$eval('#tn-m-core-n', e => e.value)) === '500');
  await setN('tn-m-core-n', 100); await setN('tn-m-mem-n', 500); await clearMsgs(); dlg.log.length = 0;
  check('manual: a setting under a passing full test is shown as proven', /proven by a full test/.test(await p.$eval('#tn-m-info', e => e.textContent)));
  await p.click('#tn-m-test'); await wait(150);
  const ts = await sent('tuning', m => m.op === 'test');
  check('manual: "Test 60 s" sends the test op for these offsets (60 s)', ts.length === 1 && ts[0].args.core === 100 && ts[0].args.memAb === 500 && ts[0].args.seconds === 60, JSON.stringify(ts));
  await p.click('#tn-m-apply'); await wait(500);
  const ap = await sent('tuning', m => m.op === 'manualApply');
  check('manual: applying a proven setting asks nothing and sends no force', ap.length === 1 && dlg.log.length === 0 && ap[0].args.core === 100 && ap[0].args.memAb === 500 && !('force' in ap[0].args), JSON.stringify(ap));
  check('manual: the applied offsets update after Apply', /core \+100 MHz · memory \+500/.test(await p.$eval('#tn-m-info', e => e.textContent)), await p.$eval('#tn-m-info', e => e.textContent));
  await setN('tn-m-core-n', 300); await clearMsgs(); dlg.log.length = 0; dlg.accept = false;
  check('manual: a setting above every passing test is flagged untested', /untested/.test(await p.$eval('#tn-m-info', e => e.textContent)));
  await p.click('#tn-m-apply'); await wait(300);
  check('manual: untested Apply asks first (explains the risk); cancelling sends nothing', dlg.log.length === 1 && /untested/i.test(dlg.log[0]) && /crash/i.test(dlg.log[0]) && (await sent('tuning', m => m.op === 'manualApply')).length === 0, dlg.log[0]?.slice(0, 80));
  dlg.accept = true; await p.click('#tn-m-apply'); await wait(400);
  const apf = await sent('tuning', m => m.op === 'manualApply');
  check('manual: after confirming, the untested Apply is sent with force:true', apf.length === 1 && apf[0].args.force === true && apf[0].args.core === 300, JSON.stringify(apf));
  await clearMsgs(); dlg.log.length = 0; dlg.accept = false;
  await p.select('#tn-m-prof', 'Gaming'); await p.click('#tn-m-save'); await wait(300);
  check('manual: untested Save to profile asks first; cancelling sends nothing', dlg.log.length === 1 && /Gaming/.test(dlg.log[0]) && (await sent('tuning', m => m.op === 'manualSave')).length === 0);
  dlg.accept = true; await p.click('#tn-m-save'); await wait(600);
  const sv = await sent('tuning', m => m.op === 'manualSave');
  check('manual: confirmed untested Save sends the chosen profile with force:true, and the table shows it untested', sv.length === 1 && sv[0].args.profile === 'Gaming' && sv[0].args.force === true && /untested/.test(await p.$eval('[data-ocrow="Gaming"]', r => r.textContent)), JSON.stringify(sv));
  await setN('tn-m-core-n', 100); await clearMsgs(); dlg.log.length = 0; await p.select('#tn-m-prof', 'Balanced'); await p.click('#tn-m-save'); await wait(300);
  const sv2 = await sent('tuning', m => m.op === 'manualSave');
  check('manual: a proven Save needs no confirmation and sends no force', dlg.log.length === 0 && sv2.length === 1 && sv2[0].args.profile === 'Balanced' && !('force' in sv2[0].args), JSON.stringify(sv2));
  const allTn = await sent('tuning', () => true);
  check('security: no tuning op other than manualApply / manualSave / cpuFinish ever carries force', (await p.evaluate(() => (window.__msgs ?? []).filter(m => m.type === 'tuning' && m.args && 'force' in m.args && !['manualApply', 'manualSave', 'cpuFinish'].includes(m.op)).length)) === 0);
  await setN('tn-m-core-n', 0); await setN('tn-m-mem-n', 0);

  // --- AI goal picker + "what should the end result be like?"
  const st0 = await p.evaluate(() => ({ seg: [...document.querySelectorAll('#tn-style button')].map(b => b.textContent), on: document.querySelector('#tn-style button.on')?.textContent, ph: document.getElementById('tn-context').placeholder, max: document.getElementById('tn-context').maxLength }));
  check('ai: goal picker has Quiet / Balanced / Performance / Custom (Balanced selected) and a textarea with example placeholder', JSON.stringify(st0.seg) === '["Quiet","Balanced","Performance","Custom"]' && st0.on === 'Balanced' && /fans never above 50%/.test(st0.ph) && st0.max === 1000, JSON.stringify(st0));
  await p.evaluate(() => { const t = document.getElementById('tn-context'); t.value = 'x'.repeat(1100); t.dispatchEvent(new Event('input', { bubbles: true })); });
  const cap = await p.evaluate(() => ({ len: document.getElementById('tn-context').value.length, n: document.getElementById('tn-ctx-n').textContent, saved: localStorage.getItem('glint.tn.context')?.length }));
  check('ai: the context is capped at 1000 characters (value, counter, saved draft)', cap.len === 1000 && cap.n.includes('1000 / 1000') && cap.saved === 1000, JSON.stringify(cap));
  await p.click('#tn-style [data-s="quiet"]');
  const XC = 'Quiet please <img src=x onerror=window.__xss=1> "q" & more';
  await p.evaluate(x => { const t = document.getElementById('tn-context'); t.value = x; t.dispatchEvent(new Event('input', { bubbles: true })); }, XC);
  await p.evaluate(() => renderTuning()); await wait(300);
  check('ai: the draft (style + text) survives a re-render and is kept in localStorage', (await p.$eval('#tn-context', t => t.value)) === XC && (await p.$eval('#tn-style button.on', b => b.textContent)) === 'Quiet' && (await p.evaluate(() => localStorage.getItem('glint.tn.style'))) === 'quiet');
  check('ai: the goal-sentence box is only shown for Custom', await p.$eval('#tn-gt-row', e => e.hidden));
  await wait(2700); await clearMsgs(); await p.click('#tn-ai-start'); await wait(900);
  const as = await sent('tuning', m => m.op === 'aiStart');
  check('ai: Tune with AI sends aiStart {goal, target, budgetMinutes, context} (no goalText unless Custom)', as.length === 1 && as[0].args.goal === 'quiet' && as[0].args.context === XC && as[0].args.budgetMinutes === 45 && as[0].args.target === 'gpu' && !('goalText' in as[0].args), JSON.stringify(as));
  const ap0 = await p.evaluate(() => ({ badge: [...document.querySelectorAll('#tn-profiles tr')].find(r => r.dataset.ocrow === 'AI · Quiet')?.querySelector('.oc-tag.ai')?.textContent, rs: document.getElementById('tn-restore')?.textContent, act: S.cfg.activeProfile }));
  check('ai: the AI works in its own profile (AI badge in the per-profile table), Glint switched to it, and offers to go back', ap0.badge === 'AI' && ap0.act === 'AI · Quiet' && /^Go back to /.test(ap0.rs ?? ''), JSON.stringify(ap0));
  await p.click('#tn-restore'); await wait(700);
  check('ai: "Go back to <profile>" sends profileRestore and the page follows the host back', (await sent('tuning', m => m.op === 'profileRestore')).length === 1 && (await p.evaluate(() => S.cfg.activeProfile)) !== 'AI · Quiet' && !(await p.$('#tn-restore')), await p.evaluate(() => S.cfg.activeProfile));
  await p.click('#profile-manage'); await wait(150);
  check('manager: AI-made profiles carry an AI badge and a note that re-tuning recreates them', await p.$eval('#pm tr[data-prow="AI · Quiet"]', r => !!r.querySelector('.oc-tag.ai')) && (await p.$eval('#pm .card-sub', e => e.textContent)).includes('recreates'));
  await p.keyboard.press('Escape'); await wait(100);
  const ran = await p.evaluate(() => ({ t: document.getElementById('tn-ai-ran').textContent, imgs: document.querySelectorAll('#page-tuning img').length, xss: window.__xss }));
  check('ai: the card shows the running goal and context, and the hostile text is inert (esc / textContent)', /Goal: Quiet/.test(ran.t) && ran.t.includes('<img src=x') && ran.imgs === 0 && ran.xss === undefined, JSON.stringify(ran).slice(0, 220));
  await p.click('#tn-ai-stop'); await wait(400);
  await p.click('#tn-style [data-s="custom"]'); await clearMsgs();
  check('ai: Custom shows the goal-sentence box', !(await p.$eval('#tn-gt-row', e => e.hidden)));
  await p.click('#tn-ai-start'); await wait(300);
  check('ai: a Custom goal with no sentence does not start', (await sent('tuning', m => m.op === 'aiStart')).length === 0);
  await p.evaluate(() => { const t = document.getElementById('tn-goaltext'); t.value = 'y'.repeat(400); t.dispatchEvent(new Event('input', { bubbles: true })); });
  check('ai: the goal sentence is capped at 300 characters', (await p.$eval('#tn-goaltext', e => e.value.length)) === 300);
  await p.evaluate(() => { const t = document.getElementById('tn-goaltext'); t.value = 'Silent gaming <img src=x onerror=window.__xss=1>'; t.dispatchEvent(new Event('input', { bubbles: true })); });
  await p.click('#tn-ai-start'); await wait(900);
  const cs = await sent('tuning', m => m.op === 'aiStart');
  const cr = await p.evaluate(() => ({ t: document.getElementById('tn-ai-ran').textContent, imgs: document.querySelectorAll('#page-tuning img').length, xss: window.__xss }));
  check('ai: a Custom run sends goalText; it is shown back as plain text', cs.length === 1 && cs[0].args.goal === 'custom' && cs[0].args.goalText.startsWith('Silent gaming <img') && /Goal: Custom \(Silent gaming <img/.test(cr.t) && cr.imgs === 0 && cr.xss === undefined, JSON.stringify(cr).slice(0, 200));
  check('history: a Fans column shows the average / peak fan duty of tests that measured it', (await p.$$eval('#tn-history .fan-chip', e => e.length)) > 0 && (await p.$eval('#tn-history thead', e => e.textContent)).includes('Fans'));
  check('cpu: the card shows the goal plan and the undervolt note', await p.$eval('#tn-cpu-plan', e => e.textContent.includes('Power limits (PBO)') && e.textContent.includes('Undervolt') && e.textContent.includes('quiet')) && (await p.$eval('#tn-co-note', e => e.textContent)).includes('undervolts'));
  await p.click('#tn-ai-stop'); await wait(300);
  await p.click('#tn-style [data-s="balanced"]');
  p.off('dialog', dlgFn);

  // --- the dashboard refreshes after changes made elsewhere (configChanged push); never under the user's typing
  await nav('cooling'); await wait(2700);
  await p.evaluate(() => { document.querySelector('#page-cooling').firstElementChild.dataset.sentinel = '1'; });
  const sentinel = () => p.evaluate(() => !!document.querySelector('#page-cooling [data-sentinel]'));
  await p.evaluate(() => window.__mockConfigChanged(() => { }));
  await wait(300);
  check('configChanged: an identical config does not re-render the page', await sentinel());
  const curve0 = await p.evaluate(() => Object.keys(S.cfg.curves)[0]);
  const v0 = await p.evaluate(c0 => S.cfg.curves[c0][1][1], curve0);
  await p.evaluate(c0 => window.__mockConfigChanged(c => { c.curves[c0][1][1] += 3; c.profiles['Remote'] = JSON.parse(JSON.stringify(c.profiles.Balanced)); }), curve0); await wait(300);
  const v1 = await p.evaluate(c0 => S.cfg.curves[c0][1][1], curve0);
  h0 = await hdr();
  check('configChanged: a change made elsewhere re-renders (new profile in the header, the curve value changed, page rebuilt)', v1 === v0 + 3 && h0.names.includes('Remote') && !(await sentinel()) && (await p.$$eval('#c-profile option', o => o.map(x => x.textContent))).includes('Remote'), `${v0} → ${v1} ${h0.names.join()}`);
  await p.evaluate(() => { document.querySelector('#page-cooling').firstElementChild.dataset.sentinel = '1'; });
  await p.click('#c-name'); await p.keyboard.type('x'); await wait(2700);
  await p.evaluate(() => window.__mockConfigChanged(c => { c.profiles['Remote2'] = JSON.parse(JSON.stringify(c.profiles.Balanced)); }));
  await wait(300);
  h0 = await hdr();
  check('configChanged: while the user is typing in an input nothing re-renders (header and page untouched)', !h0.names.includes('Remote2') && await sentinel() && (await p.evaluate(() => document.activeElement?.id)) === 'c-name', h0.names.join());
  await p.$eval('#c-name', e => e.blur()); await wait(300);
  h0 = await hdr();
  check('configChanged: the change applies once the field loses focus', h0.names.includes('Remote2') && !(await sentinel()), h0.names.join());
  await nav('tuning'); await wait(500);

  await p.click('#tn-startup');
  check('tuning: apply-at-startup toggle goes through the config', (await cfg())?.tuning?.applyAtStartup === true);
  p.once('dialog', d => d.accept());
  await p.click('#tn-enabled'); await wait(200);
  const tc = await cfg();
  check('tuning: turning it off goes through the config and hides the controls', tc?.tuning?.enabled === false && !(await p.$('#tn-log')));
  check('tuning: off state explains nothing is touched', await p.$eval('#page-tuning', el => el.textContent.includes('never touches your clocks')));
  const report = { passed: checks.filter(x => x.pass).length, failed: checks.filter(x => !x.pass).length, checks, errors };
  fs.writeFileSync(out, JSON.stringify(report, null, 2));
  console.log(`passed ${report.passed} failed ${report.failed}`);
  await b.close();
  process.exit(report.failed);
  } catch (e) { console.log('ABORT', e.message); process.exit(99); }
})();
