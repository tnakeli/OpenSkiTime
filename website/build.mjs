import { mkdir, readFile, writeFile, copyFile } from 'node:fs/promises';
import { fileURLToPath } from 'node:url';
import path from 'node:path';

const root = fileURLToPath(new URL('.', import.meta.url));
const output = path.join(root, 'dist');
const repo = 'https://github.com/tnakeli/OpenSkiTime';
const pages = [
  ['index', '/', 'Alpine timing with less busywork', 'Automation and integrations that help alpine timekeepers prepare races, capture timing and share live results faster. Open source, Windows-first and offline-ready.'],
  ['download', '/download/', 'Download OpenSkiTime', 'Windows downloads, release notes and installation requirements for OpenSkiTime.'],
  ['guide', '/guide/', 'Your first race', 'Get started with event series, competitors, start lists, timing and optional live publishing.'],
  ['privacy', '/privacy/', 'Privacy & your race data', 'How local race data and optional online integrations work in OpenSkiTime.'],
  ['404', '/404.html', 'Page not found', 'Find your way back to OpenSkiTime.']
];
const header = `<a class="skip" href="#main">Skip to content</a>
<header class="site-header"><div class="shell header-inner"><a class="brand" href="/" aria-label="OpenSkiTime home"><img src="/favicon.svg" width="34" height="34" alt="">OpenSkiTime<span class="brand-dot">.</span></a><nav aria-label="Main navigation"><a href="/#workflow">Features</a><a href="/guide/">Get started</a><a href="${repo}">GitHub <span aria-hidden="true">↗</span></a><a class="nav-download" href="/download/">Download <span aria-hidden="true">↓</span></a></nav></div></header>`;
const footer = `<footer class="site-footer"><div class="shell footer-top"><a class="brand" href="/">OpenSkiTime<span class="brand-dot">.</span></a><p>Made for the people behind the race.</p></div><div class="shell footer-bottom"><span>Open source · MIT license</span><nav aria-label="Footer"><a href="/privacy/">Privacy</a><a href="${repo}/blob/master/LICENSE">License</a><a href="${repo}/issues">Report an issue ↗</a><a href="${repo}">Source code ↗</a></nav></div></footer>`;

let release = null;
if (process.env.OPENSKITIME_RELEASE_FILE) {
  release = JSON.parse(await readFile(process.env.OPENSKITIME_RELEASE_FILE, 'utf8'));
  if (release.draft || release.prerelease || !/^v\d+\.\d+\.\d+$/.test(release.tag_name ?? '')) {
    throw new Error('The website only links to a published stable release.');
  }
}
const asset = release?.assets?.find(a => /^OpenSkiTime-\d+\.\d+\.\d+-win-x64-setup\.exe$/.test(a.name));
if (release && !asset) throw new Error('Stable release has no Windows installer.');
if (asset && !asset.browser_download_url.startsWith(`${repo}/releases/download/${release.tag_name}/`)) {
  throw new Error('Unexpected release asset origin.');
}
const escape = value => String(value).replace(/[&<>"']/g, c => ({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c]));
const download = asset
  ? `<p class="eyebrow">Windows x64 · ${escape(release.tag_name)}</p><h2>Ready for your next rehearsal.</h2><p>The installer includes the .NET runtime and the local live timing tools.</p><a class="button" href="${escape(asset.browser_download_url)}">Download for Windows <span aria-hidden="true">↓</span></a><p class="small"><a href="${repo}/releases/tag/${escape(release.tag_name)}">Release notes and SHA-256 checksums ↗</a></p><p class="notice">This installer is currently unsigned. Windows may display an unknown-publisher warning. Download only from this project’s GitHub Releases.</p>`
  : `<p class="eyebrow">Pre-release development</p><h2>The first installer is on its way.</h2><p>There is no published Windows installer yet. You can run the current development version from source and follow progress on GitHub.</p><a class="button" href="${repo}/releases">View releases <span aria-hidden="true">↗</span></a><p class="small"><a href="/guide/#from-source">Run from source →</a></p>`;

await mkdir(path.join(output, 'assets'), { recursive: true });
for (const name of ['site.css', 'favicon.svg', 'robots.txt', 'staticwebapp.config.json']) {
  await copyFile(path.join(root, name), path.join(output, name));
}
for (const name of ['overview.png', '03-competitors.png', '04-start-lists.png', '07-race-information.png']) {
  await copyFile(path.join(root, '..', 'docs', 'screenshots', name), path.join(output, 'assets', name));
}
for (const [name, route, title, description] of pages) {
  const content = (await readFile(path.join(root, 'pages', `${name}.html`), 'utf8')).replace('{{download}}', download);
  const html = `<!doctype html>\n<html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"><title>${escape(title)} · OpenSkiTime</title><meta name="description" content="${escape(description)}"><link rel="canonical" href="https://openskiti.me${route}"><meta name="theme-color" content="#12323c"><meta property="og:title" content="${escape(title)} · OpenSkiTime"><meta property="og:description" content="${escape(description)}"><meta property="og:type" content="website"><meta property="og:url" content="https://openskiti.me${route}"><link rel="icon" href="/favicon.svg" type="image/svg+xml"><link rel="stylesheet" href="/site.css"></head><body>${header}<main id="main">${content}</main>${footer}</body></html>\n`;
  const target = name === 'index' ? output : name === '404' ? output : path.join(output, name);
  await mkdir(target, { recursive: true });
  await writeFile(path.join(target, name === '404' ? '404.html' : 'index.html'), html);
}
await writeFile(path.join(output, 'sitemap.xml'), `<?xml version="1.0" encoding="UTF-8"?><urlset xmlns="http://www.sitemaps.org/schemas/sitemap/0.9">${pages.filter(p => p[0] !== '404').map(p => `<url><loc>https://openskiti.me${p[1]}</loc></url>`).join('')}</urlset>\n`);
console.log(`Built ${pages.length} pages in ${output}; download: ${asset ? release.tag_name : 'pre-release'}.`);
