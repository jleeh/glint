// Headless screenshot of a dashboard page against mock.js. usage: node shot.js <page> <out.png> [clickSelector...]
const puppeteer = require('puppeteer-core');
(async () => {
  const [, , page, out, ...clicks] = process.argv;
  const b = await puppeteer.launch({ executablePath: '/usr/bin/google-chrome', headless: 'new', args: ['--no-sandbox'] });
  const p = await b.newPage();
  await p.setViewport({ width: 1320, height: 860 });
  const errs = [];
  p.on('pageerror', e => errs.push(e.message));
  await p.goto('file:///mnt/c/Projects/Glint/src/Glint/web/index.html');
  await new Promise(r => setTimeout(r, 600));
  if (page !== 'overview') await p.click(`#nav button[data-page="${page}"]`);
  for (const c of clicks) { await new Promise(r => setTimeout(r, 300)); await p.click(c); }
  await new Promise(r => setTimeout(r, 1500));
  await p.screenshot({ path: out });
  console.log(errs.length ? errs.join('\n') : 'no errors');
  await b.close();
})();
