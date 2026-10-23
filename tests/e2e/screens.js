// Takes the screenshots of the README from the running local stack (light mode), signed in with the local test account.
//   cd tests/e2e && npm install
//   node screens.js http://localhost:2740 ../../docs/images
const { chromium } = require('playwright');
const path = require('path');
const account = require('./test-account.json');

(async () => {
  const base = process.argv[2] || 'http://localhost:2740';
  const out = path.resolve(process.argv[3] || path.join(__dirname, '..', '..', 'docs', 'images'));
  require('fs').mkdirSync(out, { recursive: true });
  let browser;
  try { browser = await chromium.launch({ channel: 'msedge' }); } catch { browser = await chromium.launch(); }

  const desktop = await browser.newContext({ viewport: { width: 1440, height: 900 }, colorScheme: 'light', locale: 'de-DE' });
  const page = await desktop.newPage();
  await page.goto(base + '/Account/Login');
  await page.fill('#Input_Email', account.email);
  await page.fill('#Input_Password', account.password);
  await Promise.all([page.waitForNavigation(), page.click('button[type=submit]')]);

  // The shell scrolls inside .mat-main: a full screenshot needs a viewport as tall as the content.
  async function shot(url, file, prepare, height) {
    await page.setViewportSize({ width: 1440, height: height || 900 });
    await page.goto(base + url, { waitUntil: 'networkidle' });
    if (prepare) { await prepare(page); }
    await page.waitForTimeout(300);
    await page.screenshot({ path: path.join(out, file) });
    console.log('wrote', file);
  }

  await shot('/', 'dashboard.png');
  await shot('/Tickets', 'tickets.png');
  await shot('/Tickets/Details?id=2', 'ticket.png', null, 1000);
  await shot('/Admin/Forms/Designer?id=1', 'designer.png', null, 1000);
  await shot('/Admin/Forms/Designer?id=1', 'designer-flow.png', async p => { await p.click('[data-view=flow]'); await p.click('.mt-node[data-node="assistent"]'); }, 1000);
  await shot('/Crm/Companies/Edit?id=1', 'company.png');
  await shot('/Admin/Ai', 'ai.png', null, 1000);

  const phone = await browser.newContext({ viewport: { width: 390, height: 844 }, isMobile: true, hasTouch: true, colorScheme: 'light', locale: 'de-DE', deviceScaleFactor: 2 });
  const p2 = await phone.newPage();
  await p2.goto(base + '/it-support', { waitUntil: 'networkidle' });
  await p2.screenshot({ path: path.join(out, 'phone-form.png') });
  console.log('wrote phone-form.png');
  await p2.goto(base + '/hilfe', { waitUntil: 'networkidle' });
  await p2.screenshot({ path: path.join(out, 'phone-portal.png') });
  console.log('wrote phone-portal.png');

  await browser.close();
})();
