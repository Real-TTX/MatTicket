// Checks who sees what in the portal, against the local stack with the sample data (the form /service set to "Nur angemeldete Benutzer",
// the category "Wartung und Reparatur" limited to a group the test requester is not in):
//   guest      – /service asks to sign in, robots.txt does not name it
//   requester  – lands on /hilfe, cannot open the work of the service, the form does not ask for name and e-mail nor shows the customer,
//                the limited category is missing, the ticket is in "Meine Anfragen"
//   staff      – sees the customer of the CRM and enters the contact of the customer
//   cd tests/e2e && node portal-access.js http://localhost:2740
const { chromium } = require('playwright');
const account = require('./test-account.json');

(async () => {
  const base = process.argv[2] || 'http://localhost:2740';
  let browser;
  try { browser = await chromium.launch({ channel: 'msedge' }); } catch { browser = await chromium.launch(); }
  const results = [];
  const check = (name, ok, detail) => results.push({ name, ok: !!ok, detail });

  async function signIn(page, who) {
    await page.goto(base + '/Account/Login');
    const open = await page.$('details.mt-auth-local:not([open]) summary');
    if (open) { await open.click(); }
    await page.fill('#Input_Email', who.email);
    await page.fill('#Input_Password', who.password);
    await Promise.all([page.waitForNavigation(), page.click('form[action="/Account/Login"] button[type=submit]')]);
  }

  // ---- guest
  const guest = await (await browser.newContext()).newPage();
  await guest.goto(base + '/service');
  check('guest: /service asks to sign in', (await guest.textContent('main')).includes('Bitte melden Sie sich an'));
  const robots = await (await guest.request.get(base + '/robots.txt')).text();
  check('guest: robots.txt does not name /service', !robots.includes('/service'), robots.replace(/\n/g, ' | '));

  // ---- requester
  const req = await (await browser.newContext()).newPage();
  await signIn(req, account.requester);
  check('requester: lands on the portal', req.url().endsWith('/hilfe'), req.url());
  await req.goto(base + '/Tickets');
  check('requester: the work of the service leads back to the portal', req.url().endsWith('/hilfe'), req.url());
  await req.goto(base + '/service');
  const inputs = await req.$$eval('[data-public-form] [name^="a_"]', els => els.map(e => e.name));
  check('requester: no customer, no name or e-mail asked', !inputs.includes('a_kunde') && !inputs.includes('a_name') && !inputs.includes('a_email'), inputs.join(', '));
  check('requester: told that the account is used', (await req.textContent('main')).includes('Sie sind angemeldet als'));
  const categories = await req.$$eval('[name="a_kategorie"] option', els => els.map(e => e.textContent.trim()));
  check('requester: the limited category is missing', !categories.some(c => c.includes('Wartung')) && categories.some(c => c.includes('Hardware')), categories.join(' | '));
  await req.selectOption('[name="a_kategorie"]', { label: 'Hardware' });
  await req.fill('[name="a_geraet"]', 'Monitor flackert');
  await req.fill('[name="a_beschreibung"]', 'Der Monitor am Platz 12 flackert seit dem Update.');
  await Promise.all([req.waitForNavigation(), req.click('button[value=next]')]);
  const step2 = await req.$$eval('[data-public-form] [name^="a_"]', els => els.map(e => e.name));
  check('requester: the contact step asks only what the account does not know', !step2.includes('a_name') && !step2.includes('a_email'), step2.join(', '));
  if (await req.$('[name="a_telefon"]')) { await req.fill('[name="a_telefon"]', '+49 40 555 12'); }
  if (await req.$('[name="a_firma"]')) { await req.fill('[name="a_firma"]', 'Muster GmbH'); }
  await Promise.all([req.waitForNavigation(), req.click('button[value=next]')]);
  const done = await req.textContent('main');
  const number = (done.match(/T\d\d-\d{5}/) || [])[0];
  check('requester: ticket created', !!number, number);
  await req.goto(base + '/hilfe');
  check('requester: the ticket is in "Meine Anfragen"', number && (await req.textContent('main')).includes('Monitor flackert'));

  // ---- staff
  const staff = await (await browser.newContext()).newPage();
  await signIn(staff, account);
  await staff.goto(base + '/service');
  const staffInputs = await staff.$$eval('[data-public-form] [name^="a_"]', els => els.map(e => e.name));
  check('staff: the customer of the CRM can be chosen', staffInputs.includes('a_kunde'), staffInputs.join(', '));
  const staffCategories = await staff.$$eval('[name="a_kategorie"] option', els => els.map(e => e.textContent.trim()));
  check('staff: every category', staffCategories.some(c => c.includes('Wartung')));

  await browser.close();
  for (const r of results) { console.log(`${r.ok ? 'OK  ' : 'FAIL'} ${r.name}${r.detail ? ' – ' + r.detail : ''}`); }
  process.exit(results.every(r => r.ok) ? 0 : 1);
})();
