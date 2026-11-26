import assert from 'node:assert/strict';
import { readFile, readdir, stat } from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
const root = fileURLToPath(new URL('./dist/', import.meta.url));
for (const page of ['index.html', 'fi/index.html', 'tutorials/index.html', 'fi/opetusvideot/index.html', 'download/index.html', 'guide/index.html', 'live-api/index.html', 'privacy/index.html', 'code-signing/index.html', '404.html']) {
  const html = await readFile(path.join(root, page), 'utf8');
  assert.match(html, page.startsWith('fi/') ? /<html lang="fi">/ : /<html lang="en">/);
  assert.match(html, /<main id="main">/);
  assert(!html.includes('{{'), `Unresolved template in ${page}`);
  assert.match(html, /<meta name="description" content="[^"]{30,}">/, `Missing description in ${page}`);
  // Search results cut longer descriptions short.
  assert(html.match(/<meta name="description" content="([^"]*)">/)[1].replace(/&\w+;|&#\d+;/g, '_').length <= 160, `Description too long in ${page}`);
  assert.match(html, /<meta property="og:image" content="https:\/\/openskiti\.me\/assets\/og-image\.png">/);
  // Structured data blocks are allowed; executable visitor-side scripts are not.
  const scripts = [...html.matchAll(/<script\b([^>]*)>([\s\S]*?)<\/script>/gi)];
  assert.equal(scripts.length, (html.match(/<script\b/gi) ?? []).length);
  for (const [, attrs, body] of scripts) {
    assert.equal(attrs.trim(), 'type="application/ld+json"', 'The static website should not need visitor-side scripts.');
    const types = JSON.parse(body)['@graph'].map(node => node['@type']);
    const landing = page === 'index.html' || page === 'fi/index.html';
    assert(types.includes(landing ? 'SoftwareApplication' : 'BreadcrumbList'), `Missing structured data type in ${page}`);
    if (page === 'guide/index.html') assert(types.includes('TechArticle'), 'Missing TechArticle in the guide.');
  }
  if (page !== '404.html') assert.equal(scripts.length, 1, `Missing structured data in ${page}`);
  if (page === 'index.html' || page === 'fi/index.html') {
    assert.match(html, /<link rel="alternate" hreflang="fi" href="https:\/\/openskiti\.me\/fi\/">/);
    assert.match(html, /<link rel="alternate" hreflang="x-default" href="https:\/\/openskiti\.me\/">/);
    // The demo and live timing videos link out to YouTube; the page itself loads no third-party frame.
    assert.equal([...html.matchAll(/<a class="video-link" href="https:\/\/www\.youtube\.com\/watch\?v=[\w-]{11}">/g)].length, 2, `Missing video links in ${page}`);
    assert(!/<iframe\b/i.test(html), `Unexpected iframe in ${page}`);
    assert.equal(JSON.parse(scripts[0][2])['@graph'].filter(node => node['@type'] === 'VideoObject').length, 2, `Missing VideoObject in ${page}`);
    assert.match(html, /href="https:\/\/wapice\.com\/insights\/ai-radio-hosts-live-event-commentary\/"/, `Missing race radio credit in ${page}`);
  }
  if (page.endsWith('tutorials/index.html') || page.startsWith('fi/opetusvideot/')) {
    // A table of contents: grouped links to YouTube, no images and no third-party frame.
    const links = [...html.matchAll(/<li><a href="https:\/\/www\.youtube\.com\/watch\?v=[\w-]{11}">/g)].length;
    assert(links >= 4, `Missing tutorial links in ${page}`);
    assert(!/<iframe\b|<img\b/i.test(html.slice(html.indexOf('<main'))), `Unexpected media in ${page}`);
    const graph = JSON.parse(scripts[0][2])['@graph'];
    assert.equal(graph.find(node => node['@type'] === 'ItemList').itemListElement.length, links, `Missing VideoObject list in ${page}`);
    assert.match(html, /<link rel="alternate" hreflang="fi" href="https:\/\/openskiti\.me\/fi\/opetusvideot\/">/);
    assert.match(html, /<link rel="alternate" hreflang="en" href="https:\/\/openskiti\.me\/tutorials\/">/);
  }
  const ids = new Set([...html.matchAll(/\bid="([^"]+)"/g)].map(m => m[1]));
  for (const [, attr, value] of html.matchAll(/\b(href|src)="([^"]+)"/g)) {
    if (value.startsWith('#')) { assert(ids.has(value.slice(1)), `Missing anchor ${value} in ${page}`); continue; }
    if (!value.startsWith('/')) continue;
    const [pathname, fragment] = value.split('#');
    const target = path.join(root, pathname.endsWith('/') ? pathname + 'index.html' : pathname);
    assert((await stat(target)).isFile(), `Missing ${attr} target ${value}`);
    if (fragment) assert((await readFile(target, 'utf8')).includes(`id="${fragment}"`), `Missing cross-page anchor ${value}`);
  }
}
// The version on offer is visible in the download card and the landing-page download buttons.
if (process.env.OPENSKITIME_RELEASE_FILE) {
  const version = JSON.parse(await readFile(process.env.OPENSKITIME_RELEASE_FILE, 'utf8')).tag_name.slice(1);
  const read = page => readFile(path.join(root, page), 'utf8');
  assert((await read('download/index.html')).includes(`<h2>OpenSkiTime ${version}</h2>`), 'The download page must show the version.');
  assert((await read('download/index.html')).includes(`Download ${version} for Windows`), 'The download button must show the version.');
  assert((await read('index.html')).includes(`Get OpenSkiTime ${version} <`), 'The landing page button must show the version.');
  assert((await read('fi/index.html')).includes(`Lataa OpenSkiTime ${version} <`), 'The Finnish landing page button must show the version.');
}
const guide = await readFile(path.join(root, 'guide', 'index.html'), 'utf8');
const guideSteps = [...guide.match(/<nav class="guide-nav"[^>]*>([\s\S]*?)<\/nav>/)[1].matchAll(/<a href="#[^"]+">(\d\d) (\S+) /g)];
assert(guideSteps.length > 0, 'Missing numbered guide navigation steps.');
for (const [, step, separator] of guideSteps) assert.equal(separator, '\u00B7', `Guide step ${step} must use the middle dot separator.`);
const security = await readFile(path.join(root, '.well-known', 'security.txt'), 'utf8');
assert.match(security, /^Contact: https:\/\/github\.com\/tnakeli\/OpenSkiTime\/security\/advisories\/new$/m);
assert.match(security, /^Contact: mailto:tniemi@gmail\.com$/m);
assert.match(security, /^Canonical: https:\/\/openskiti\.me\/\.well-known\/security\.txt$/m);
const expires = new Date(security.match(/^Expires: (.+)$/m)[1]);
assert(expires - Date.now() > 30 * 86400000 && expires - Date.now() <= 366 * 86400000, 'security.txt Expires must be within the next year.');
const llms = await readFile(path.join(root, 'llms.txt'), 'utf8');
assert.match(llms, /^# OpenSkiTime\n\n> /);
assert(!llms.includes('{{'), 'Unresolved template in llms.txt');
assert.match(llms, /^- Current release: /m);
const llmsFull = await readFile(path.join(root, 'llms-full.txt'), 'utf8');
assert(llmsFull.startsWith(llms.trimEnd()), 'llms-full.txt must start with llms.txt.');
for (const heading of ['## Frequently asked questions', '### Is OpenSkiTime ready for official races?', '## Getting started guide', '### Run from source.']) assert(llmsFull.includes(heading), `llms-full.txt is missing ${heading}`);
assert(!/<[a-z]/i.test(llmsFull), 'llms-full.txt must not contain HTML.');
assert.match(await readFile(path.join(root, 'indexnow-key.txt'), 'utf8'), /^[0-9a-f]{32}$/);
for (const [, url] of llms.matchAll(/\]\((https:\/\/openskiti\.me\/[^)]*)\)/g)) {
  const pathname = new URL(url).pathname;
  assert((await stat(path.join(root, pathname.endsWith('/') ? pathname + 'index.html' : pathname))).isFile(), `llms.txt links to missing ${url}`);
}
const sitemap = await readFile(path.join(root, 'sitemap.xml'), 'utf8');
assert(sitemap.includes('<loc>https://openskiti.me/fi/</loc>'));
assert(sitemap.includes('<loc>https://openskiti.me/fi/opetusvideot/</loc>'));
for (const [, date] of sitemap.matchAll(/<lastmod>([^<]*)<\/lastmod>/g)) assert.match(date, /^\d{4}-\d{2}-\d{2}$/);
// The Live API: the OpenAPI document is complete, the page shows the published examples, and the text file has everything.
const openApi = JSON.parse(await readFile(path.join(root, 'live-api', 'openapi.json'), 'utf8'));
assert.equal(openApi.openapi, '3.1.0');
for (const [, ref] of JSON.stringify(openApi).matchAll(/"\$ref":"([^"]+)"/g)) {
  assert(ref.startsWith('#/') && ref.slice(2).split('/').reduce((node, key) => node?.[key], openApi), `Unresolved ${ref} in openapi.json`);
}
const apiPage = await readFile(path.join(root, 'live-api', 'index.html'), 'utf8');
const apiText = await readFile(path.join(root, 'live-api', 'live-api.txt'), 'utf8');
for (const heading of ['## Synchronization rules', '## From changes to events', '### Java']) assert(apiText.includes(heading), `live-api.txt is missing ${heading}`);
// Azure Static Web Apps answers 404 for source files such as .cs and .java.
for (const file of await readdir(path.join(root, 'live-api'))) assert.match(file, /\.(html|json|txt)$/, `${file} would not be served.`);
for (const file of ['live-api.schema.json', 'live-reader.mjs', 'live-reader.cs', 'LiveReader.java']) {
  const source = (await readFile(new URL(`./live-api/${file}`, import.meta.url), 'utf8')).replaceAll('\r\n', '\n');
  assert(apiText.includes(source.trimEnd()), `live-api.txt is missing ${file}`);
  const link = file.endsWith('.json') ? `/live-api/${file}` : `https://github.com/tnakeli/OpenSkiTime/blob/master/website/live-api/${file}`;
  assert(apiPage.includes(`href="${link}"`), `The Live API page does not link to ${file}`);
}
assert(!/<\/?[a-z]+>/.test(apiText.slice(0, apiText.indexOf('## File: '))), 'The text of live-api.txt must not contain HTML.');
const config = JSON.parse(await readFile(path.join(root, 'staticwebapp.config.json'), 'utf8'));
assert(config.globalHeaders['Content-Security-Policy'].includes("frame-ancestors 'none'"));
console.log('Website checked: pages, assets, anchors, metadata and security headers.');
