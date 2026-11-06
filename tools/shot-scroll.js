const puppeteer = require('puppeteer-core');
(async () => {
  const [, , page, out, sel] = process.argv;
  const b = await puppeteer.launch({ executablePath: '/usr/bin/google-chrome', headless: 'new', args: ['--no-sandbox'] });
  const p = await b.newPage(); await p.setViewport({ width: 1320, height: 860 });
  await p.goto('file:///mnt/c/Projects/Glint/src/Glint/web/index.html'); await new Promise(r => setTimeout(r, 600));
  await p.click(`#nav button[data-page="${page}"]`); await new Promise(r => setTimeout(r, 400));
  if (sel) { await p.click(sel); await new Promise(r => setTimeout(r, 300)); await p.$eval(sel, e => e.scrollIntoView({ block: 'center' })); }
  await new Promise(r => setTimeout(r, 800));
  await p.screenshot({ path: out }); await b.close();
})();
