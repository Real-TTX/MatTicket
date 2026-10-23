#!/usr/bin/env node
/*
 * tools/icons.js — renders the PNG icons of MatTicket from the SVG sources.
 *
 * Sources (hand-written SVG, the single source of truth):
 *   src/MatTicket/wwwroot/favicon.svg              favicon, minimal (32 px legible)
 *   src/MatTicket/wwwroot/logo.svg                 detailed tile (sign-in, setup, error pages)
 *   src/MatTicket/wwwroot/logo-wordmark.svg        tile + "MatTicket" (README, docs, og-image)
 *   src/MatTicket/wwwroot/img/icon-maskable.svg    full-bleed PWA icon (glyph inside the 80 % safe zone)
 *
 * Output (src/MatTicket/wwwroot/img/):
 *   favicon-32.png, apple-touch-icon.png (180), icon-192.png, icon-512.png,
 *   icon-maskable-192.png, icon-maskable-512.png, og-image.png (1200x630)
 *
 * How to run (from the repository root; Playwright lives in tests/e2e):
 *   cd tests/e2e && npm install && cd ../..          # once
 *   Git Bash:    NODE_PATH=tests/e2e/node_modules node tools/icons.js
 *   PowerShell:  $env:NODE_PATH="tests/e2e/node_modules"; node tools/icons.js
 *
 * Uses the installed Microsoft Edge (channel "msedge"). Without Edge it falls back
 * to Playwright's bundled Chromium (install it once with: npx playwright install chromium).
 * Re-run after every change to one of the SVG sources and commit the PNGs.
 */
'use strict';

const fs = require('fs');
const path = require('path');
const { chromium } = require('playwright');

const root = path.resolve(__dirname, '..');
const web = path.join(root, 'src', 'MatTicket', 'wwwroot');
const img = path.join(web, 'img');

const read = (file) => fs.readFileSync(path.join(web, file), 'utf8');
const dataUrl = (svg) => 'data:image/svg+xml;base64,' + Buffer.from(svg).toString('base64');

// One page per image: the SVG as <img> at exactly the target size, nothing else.
const svgPage = (file, w, h, background) => `<!doctype html><html><head><style>
  html,body{margin:0;padding:0;background:${background || 'transparent'};}
  img{display:block;width:${w}px;height:${h}px;}
</style></head><body><img src="${dataUrl(read(file))}" alt=""></body></html>`;

const ogPage = () => `<!doctype html><html lang="de"><head><style>
  html,body{margin:0;padding:0;}
  body{width:1200px;height:630px;background:#f5f4f1;display:flex;flex-direction:column;
       align-items:center;justify-content:center;gap:34px;
       font-family:system-ui,"Segoe UI",Roboto,Helvetica,Arial,sans-serif;}
  img{display:block;width:720px;height:auto;}
  p{margin:0;font-size:40px;font-weight:500;color:#57534e;letter-spacing:.005em;}
</style></head><body>
  <img src="${dataUrl(read('logo-wordmark.svg'))}" alt="MatTicket">
  <p>Tickets, Formulare und KI-Assistent</p>
</body></html>`;

const jobs = [
  { out: 'favicon-32.png', w: 32, h: 32, html: () => svgPage('favicon.svg', 32, 32), transparent: true },
  { out: 'icon-192.png', w: 192, h: 192, html: () => svgPage('logo.svg', 192, 192), transparent: true },
  { out: 'icon-512.png', w: 512, h: 512, html: () => svgPage('logo.svg', 512, 512), transparent: true },
  { out: 'icon-maskable-192.png', w: 192, h: 192, html: () => svgPage('img/icon-maskable.svg', 192, 192) },
  { out: 'icon-maskable-512.png', w: 512, h: 512, html: () => svgPage('img/icon-maskable.svg', 512, 512) },
  // iOS rounds the corners itself and wants no transparency: the full-bleed maskable design.
  { out: 'apple-touch-icon.png', w: 180, h: 180, html: () => svgPage('img/icon-maskable.svg', 180, 180, '#a15c07') },
  { out: 'og-image.png', w: 1200, h: 630, html: ogPage },
];

async function launch() {
  try {
    return await chromium.launch({ channel: 'msedge' });
  } catch (err) {
    console.warn('Microsoft Edge not usable (' + err.message.split('\n')[0] + '), falling back to bundled Chromium.');
    return chromium.launch();
  }
}

(async () => {
  fs.mkdirSync(img, { recursive: true });
  const browser = await launch();
  try {
    for (const job of jobs) {
      const page = await browser.newPage({ viewport: { width: job.w, height: job.h }, deviceScaleFactor: 1, colorScheme: 'light' });
      await page.setContent(job.html(), { waitUntil: 'load' });
      await page.evaluate(() => document.fonts.ready);
      const file = path.join(img, job.out);
      await page.screenshot({ path: file, omitBackground: !!job.transparent, clip: { x: 0, y: 0, width: job.w, height: job.h } });
      await page.close();
      console.log('wrote ' + path.relative(root, file) + ' (' + job.w + 'x' + job.h + ')');
    }
  } finally {
    await browser.close();
  }
})().catch((err) => {
  console.error(err);
  process.exit(1);
});
