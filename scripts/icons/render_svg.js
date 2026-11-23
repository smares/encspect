// Renders SVG files to PNG with Playwright's Chromium: node render_svg.js <svg> <size> <png> [<svg> <size> <png> ...]
const { chromium } = require('playwright');
const fs = require('fs');
const path = require('path');
const os = require('os');

(async () => {
  const args = process.argv.slice(2);
  const browser = await chromium.launch();
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'encspect-icons-'));
  for (let i = 0; i + 2 < args.length + 0; i += 3) {
    const [svg, size, out] = args.slice(i, i + 3);
    fs.copyFileSync(svg, path.join(dir, 'icon.svg'));
    const html = path.join(dir, 'render.html');
    fs.writeFileSync(html, `<!doctype html><style>html,body{margin:0;background:transparent}img{display:block}</style><img src="icon.svg" width=${size} height=${size}>`);
    const page = await browser.newPage({ viewport: { width: +size, height: +size }, deviceScaleFactor: 1 });
    await page.goto('file://' + html);
    await page.waitForLoadState('load');
    await page.screenshot({ path: out, omitBackground: true });
    await page.close();
  }
  await browser.close();
  fs.rmSync(dir, { recursive: true, force: true });
})();
