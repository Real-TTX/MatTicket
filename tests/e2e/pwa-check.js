// Checks the installable app in a real browser (Edge, headless): the service worker registers, a page that was open is shown
// without a connection (marked as a copy), and a page that was never open shows the offline page with "Erneut verbinden".
//   cd tests/e2e && npm install
//   node pwa-check.js http://localhost:2740
const { chromium } = require('playwright');

(async () => {
  const base = process.argv[2] || 'http://localhost:2740';
  let browser;
  try {
    browser = await chromium.launch({ channel: 'msedge' });
  } catch {
    browser = await chromium.launch();
  }
  const context = await browser.newContext({ viewport: { width: 390, height: 844 }, isMobile: true, hasTouch: true });
  const page = await context.newPage();
  const results = [];
  const check = (name, ok, detail) => { results.push({ name, ok, detail }); };

  await page.goto(base + '/hilfe', { waitUntil: 'networkidle' });
  const scope = await page.evaluate(async () => {
    const reg = await navigator.serviceWorker.ready;
    return reg.scope;
  });
  check('service worker active', !!scope, scope);
  await page.reload({ waitUntil: 'networkidle' });

  const manifest = await page.evaluate(async () => (await fetch('/manifest.webmanifest')).json());
  check('manifest', manifest.display === 'standalone' && manifest.icons.length >= 4, `${manifest.name}, ${manifest.icons.length} icons`);
  const viewport = await page.getAttribute('meta[name=viewport]', 'content');
  check('viewport (zoom allowed, safe areas)', viewport.includes('viewport-fit=cover') && !viewport.includes('user-scalable=no'), viewport);
  check('apple status bar meta', !!(await page.$('meta[name=apple-mobile-web-app-status-bar-style]')), '');

  await context.setOffline(true);
  await page.goto(base + '/hilfe', { waitUntil: 'domcontentloaded' });
  const copy = await page.getAttribute('html', 'data-offline-copy');
  check('kept page offline (marked as copy)', copy !== null, copy);
  const banner = await page.isVisible('[data-offline-banner]');
  check('offline banner shown', banner, '');

  await page.goto(base + '/service', { waitUntil: 'domcontentloaded' }).catch(() => {});
  const offlinePage = await page.$('[data-offline-page], [data-offline-banner]');
  const text = await page.textContent('body');
  check('offline page or banner for an unknown page', !!offlinePage, text.includes('Keine Verbindung') ? 'Keine Verbindung' : text.slice(0, 80));

  await context.setOffline(false);
  await browser.close();
  for (const r of results) { console.log(`${r.ok ? 'OK  ' : 'FAIL'} ${r.name}${r.detail ? ' – ' + r.detail : ''}`); }
  process.exit(results.every(r => r.ok) ? 0 : 1);
})();
