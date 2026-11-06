const puppeteer = require('puppeteer-core');
(async () => {
  const [, , page, out, width, ...clicks] = process.argv;
  const b = await puppeteer.launch({ executablePath: '/usr/bin/google-chrome', headless: 'new', args: ['--no-sandbox'] });
  const p = await b.newPage(); await p.setViewport({ width: +width, height: 860 });
  await p.goto('file:///mnt/c/Projects/Glint/src/Glint/web/index.html'); await new Promise(r => setTimeout(r, 600));
  await p.click(`#nav button[data-page="${page}"]`); await new Promise(r => setTimeout(r, 400));
  for (const c of clicks) { await p.click(c); await new Promise(r => setTimeout(r, 300)); }
  const overflow = await p.evaluate(() => { const c = document.getElementById('content'); return { scrollW: c.scrollWidth, clientW: c.clientWidth, culprits: [...document.querySelectorAll('#content *')].filter(e => e.getBoundingClientRect().right > c.getBoundingClientRect().right + 1).slice(0, 6).map(e => e.className || e.tagName) }; });
  console.log(JSON.stringify(overflow));
  await p.screenshot({ path: out }); await b.close();
})();
