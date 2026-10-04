import assert from 'node:assert/strict';
import { readFile, stat } from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
const root = fileURLToPath(new URL('./dist/', import.meta.url));
for (const page of ['index.html', 'download/index.html', 'guide/index.html', 'privacy/index.html', 'code-signing/index.html', '404.html']) {
  const html = await readFile(path.join(root, page), 'utf8');
  assert.match(html, /<html lang="en">/);
  assert.match(html, /<main id="main">/);
  assert(!html.includes('{{'), `Unresolved template in ${page}`);
  assert(!/<script\b/i.test(html), 'The static website should not need visitor-side scripts.');
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
const security = await readFile(path.join(root, '.well-known', 'security.txt'), 'utf8');
assert.match(security, /^Contact: https:\/\/github\.com\/tnakeli\/OpenSkiTime\/security\/advisories\/new$/m);
assert.match(security, /^Canonical: https:\/\/openskiti\.me\/\.well-known\/security\.txt$/m);
const expires = new Date(security.match(/^Expires: (.+)$/m)[1]);
assert(expires - Date.now() > 30 * 86400000 && expires - Date.now() <= 366 * 86400000, 'security.txt Expires must be within the next year.');
const config = JSON.parse(await readFile(path.join(root, 'staticwebapp.config.json'), 'utf8'));
assert(config.globalHeaders['Content-Security-Policy'].includes("frame-ancestors 'none'"));
console.log('Website checked: pages, assets, anchors, metadata and security headers.');
