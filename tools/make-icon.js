// Renders docs/brand/logo.svg to src/Glint/Resources/glint.ico (PNG-compressed ICO, 16..256). usage: node tools/make-icon.js
const puppeteer = require('puppeteer-core');
const fs = require('fs'), path = require('path');
const root = path.resolve(__dirname, '..');
const sizes = [16, 24, 32, 48, 64, 128, 256];
(async () => {
  const svg = fs.readFileSync(path.join(root, 'docs/brand/logo.svg'), 'utf8');
  const b = await puppeteer.launch({ executablePath: process.env.CHROME_PATH || '/usr/bin/google-chrome', headless: 'new', args: ['--no-sandbox'] });
  const p = await b.newPage();
  const pngs = [];
  for (const s of sizes) {
    await p.setViewport({ width: s, height: s });
    await p.setContent(`<body style="margin:0;background:transparent"><div style="width:${s}px;height:${s}px">${svg.replace(/width="128" height="128"/, `width="${s}" height="${s}"`)}</div></body>`);
    pngs.push(await p.screenshot({ omitBackground: true, clip: { x: 0, y: 0, width: s, height: s } }));
  }
  await b.close();
  const head = Buffer.alloc(6); head.writeUInt16LE(1, 2); head.writeUInt16LE(sizes.length, 4);
  let off = 6 + 16 * sizes.length;
  const dir = sizes.map((s, i) => {
    const e = Buffer.alloc(16);
    e[0] = s >= 256 ? 0 : s; e[1] = s >= 256 ? 0 : s; e.writeUInt16LE(1, 4); e.writeUInt16LE(32, 6);
    e.writeUInt32LE(pngs[i].length, 8); e.writeUInt32LE(off, 12); off += pngs[i].length; return e;
  });
  const out = path.join(root, 'src/Glint/Resources/glint.ico');
  fs.writeFileSync(out, Buffer.concat([head, ...dir, ...pngs]));
  console.log('wrote', out, off, 'bytes');
})();
