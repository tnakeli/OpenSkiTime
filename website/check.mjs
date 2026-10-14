import assert from 'node:assert/strict';
import { readFile, stat } from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
const root = fileURLToPath(new URL('./dist/', import.meta.url));
for (const page of ['index.html', 'fi/index.html', 'download/index.html', 'guide/index.html', 'privacy/index.html', 'code-signing/index.html', '404.html']) {
  const html = await readFile(path.join(root, page), 'utf8');
  assert.match(html, page.startsWith('fi/') ? /<html lang="fi">/ : /<html lang="en">/);
  assert.match(html, /<main id="main">/);
  assert(!html.includes('{{'), `Unresolved template in ${page}`);
  assert.match(html, /<meta name="description" content="[^"]{30,}">/, `Missing description in ${page}`);
  assert.match(html, /<meta property="og:image" content="https:\/\/openskiti\.me\/assets\/[^"]+\.png">/);
  // Structured data blocks are allowed; executable visitor-side scripts are not.
  const scripts = [...html.matchAll(/<script\b([^>]*)>([\s\S]*?)<\/script>/gi)];
  assert.equal(scripts.length, (html.match(/<script\b/gi) ?? []).length);
  for (const [, attrs, body] of scripts) {
    assert.equal(attrs.trim(), 'type="application/ld+json"', 'The static website should not need visitor-side scripts.');
    const data = JSON.parse(body);
    assert(data['@graph'].some(node => node['@type'] === 'SoftwareApplication'), `Missing SoftwareApplication in ${page}`);
  }
  if (page === 'index.html' || page === 'fi/index.html') {
    assert.equal(scripts.length, 1, `Missing structured data in ${page}`);
    assert.match(html, /<link rel="alternate" hreflang="fi" href="https:\/\/openskiti\.me\/fi\/">/);
    assert.match(html, /<link rel="alternate" hreflang="x-default" href="https:\/\/openskiti\.me\/">/);
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
for (const [, url] of llms.matchAll(/\]\((https:\/\/openskiti\.me\/[^)]*)\)/g)) {
  const pathname = new URL(url).pathname;
  assert((await stat(path.join(root, pathname.endsWith('/') ? pathname + 'index.html' : pathname))).isFile(), `llms.txt links to missing ${url}`);
}
const sitemap = await readFile(path.join(root, 'sitemap.xml'), 'utf8');
assert(sitemap.includes('<loc>https://openskiti.me/fi/</loc>'));
const config = JSON.parse(await readFile(path.join(root, 'staticwebapp.config.json'), 'utf8'));
assert(config.globalHeaders['Content-Security-Policy'].includes("frame-ancestors 'none'"));
console.log('Website checked: pages, assets, anchors, metadata and security headers.');
