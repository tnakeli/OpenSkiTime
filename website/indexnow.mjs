import { readFile } from 'node:fs/promises';
import { fileURLToPath } from 'node:url';
import path from 'node:path';

// After a deployment, tells IndexNow search engines (Bing, Yandex and others) which pages to recrawl.
// The key is public by design: search engines verify it at keyLocation on this site.
const root = fileURLToPath(new URL('./dist/', import.meta.url));
const site = 'https://openskiti.me';
const key = (await readFile(path.join(root, 'indexnow-key.txt'), 'utf8')).trim();
if (!/^[0-9a-f]{32}$/.test(key)) throw new Error('Unexpected IndexNow key.');
const sitemap = await readFile(path.join(root, 'sitemap.xml'), 'utf8');
const urlList = [...sitemap.matchAll(/<loc>([^<]+)<\/loc>/g)].map(m => m[1]);
// The key file must already be live, or the search engine rejects the submission. A fresh upload can take a moment to serve.
const keyIsLive = async () => { const r = await fetch(`${site}/indexnow-key.txt`, { cache: 'no-store' }); return r.ok && (await r.text()).trim() === key; };
let attempt = 0;
while (!(await keyIsLive())) {
  if (++attempt === 6) throw new Error('The deployed IndexNow key file does not match.');
  await new Promise(resolve => setTimeout(resolve, 10000));
}
const response = await fetch('https://api.indexnow.org/indexnow', {
  method: 'POST',
  headers: { 'Content-Type': 'application/json; charset=utf-8' },
  body: JSON.stringify({ host: new URL(site).host, key, keyLocation: `${site}/indexnow-key.txt`, urlList })
});
// 200 and 202 both mean the submission was received.
if (response.status !== 200 && response.status !== 202) throw new Error(`IndexNow submission failed: HTTP ${response.status}`);
console.log(`IndexNow: submitted ${urlList.length} URLs (HTTP ${response.status}).`);
