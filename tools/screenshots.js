// README screenshots of the dashboard, rendered headless against mock.js (a fictional demo rig, no real hardware).
// usage: node tools/screenshots.js [outDir]   (default: docs/screenshots next to this folder)
// Uses Segoe UI / Cascadia from C:\Windows\Fonts when running under WSL so the shots match the real app.
const puppeteer = require('puppeteer-core');
const fs = require('fs'), os = require('os'), path = require('path');

const ROOT = path.resolve(__dirname, '..');
const OUT = path.resolve(process.argv[2] ?? path.join(ROOT, 'docs', 'screenshots'));
const PAGE = 'file://' + path.join(ROOT, 'src', 'Glint', 'web', 'index.html');
const CHROME = [process.env.CHROME_PATH, '/usr/bin/google-chrome', '/usr/bin/chromium', '/usr/bin/microsoft-edge'].find(f => f && fs.existsSync(f));
const wait = ms => new Promise(r => setTimeout(r, ms));

// fontconfig pointing at the Windows UI fonts (WSL); harmless elsewhere
function fontEnv() {
  const win = '/mnt/c/Windows/Fonts';
  if (!fs.existsSync(win)) return {};
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'glint-fonts-'));
  fs.mkdirSync(path.join(dir, 'f'));
  for (const f of ['SegUIVar.ttf', 'segoeui.ttf', 'segoeuib.ttf', 'segoeuisl.ttf', 'segoeuil.ttf', 'seguisb.ttf', 'CascadiaMono.ttf'])
    if (fs.existsSync(path.join(win, f))) fs.symlinkSync(path.join(win, f), path.join(dir, 'f', f));
  fs.writeFileSync(path.join(dir, 'fonts.conf'), `<?xml version="1.0"?><!DOCTYPE fontconfig SYSTEM "fonts.dtd"><fontconfig>
<include ignore_missing="yes">/etc/fonts/fonts.conf</include><dir>${dir}/f</dir><cachedir>${dir}/cache</cachedir></fontconfig>`);
  return { FONTCONFIG_FILE: path.join(dir, 'fonts.conf') };
}

(async () => {
  fs.mkdirSync(OUT, { recursive: true });
  const b = await puppeteer.launch({ executablePath: CHROME, headless: 'new', args: ['--no-sandbox', '--hide-scrollbars'], env: { ...process.env, ...fontEnv() } });
  const p = await b.newPage();
  await p.setViewport({ width: 1440, height: 900, deviceScaleFactor: 2 });
  const errors = [];
  p.on('pageerror', e => errors.push(e.message));
  await p.goto(PAGE);
  await wait(1200);
  await p.evaluate(() => document.fonts.ready);

  const nav = async pg => { await p.click(`#nav button[data-page="${pg}"]`); await wait(300); };
  const shot = async name => {
    await p.mouse.move(1, 1);                                   // no hover states
    await wait(1600);                                           // let transitions, sparklines and the LED preview settle
    await p.evaluate(() => { document.querySelectorAll('.toast').forEach(t => t.remove()); document.activeElement?.blur(); });
    const file = path.join(OUT, name + '.png');
    await p.screenshot({ path: file });
    console.log('wrote', file);
  };

  await nav('overview');
  // a lively effect for the overview strip and the lighting page
  await p.evaluate(() => { applyPreset('Rainbow wave', 'all'); });
  await wait(300);
  await nav('overview'); await shot('overview');

  await nav('cooling');
  await p.click('[data-ri="1"]');                               // the rule with two sensor inputs
  await shot('cooling');

  await nav('lighting');
  await shot('lighting');

  // select one fan on the layout and give it its own effect: the amber scoped bar shows what is being edited
  const st = await (await p.$('#stage-c')).boundingBox();
  const fan = await p.evaluate(() => { const pl = placement('cc:fan2'); return { x: (pl.x + pl.w / 2) * stageGeo.W, y: (pl.y + pl.h / 2) * stageGeo.H }; });
  await p.mouse.click(st.x + fan.x, st.y + fan.y); await wait(300);
  await p.click('[data-fx="Spin"]'); await wait(300);
  await p.evaluate(() => { const bar = document.querySelector('.target-bar'); document.getElementById('content').scrollTop = Math.max(0, bar.offsetTop + bar.offsetHeight - 860); });
  await shot('lighting-scoped');
  await p.keyboard.press('Escape');

  await nav('sensors'); await wait(2500);
  for (const g of ['/amdcpu/0|Clock', '/amdcpu/0|Load', '/amdcpu/0|Power']) await p.click(`[data-tg="${g}"]`); // show more of the tree
  await shot('sensors');

  await nav('devices'); await shot('devices');
  await nav('plugins'); await shot('plugins');

  await nav('overlay');
  await shot('settings-overlay');

  await b.close();
  if (errors.length) { console.log('page errors:\n' + errors.join('\n')); process.exit(1); }
})().catch(e => { console.error(e); process.exit(2); });
