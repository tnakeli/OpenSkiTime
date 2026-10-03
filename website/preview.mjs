import http from 'node:http';
import { readFile, stat } from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
const root = path.resolve(fileURLToPath(new URL('./dist/', import.meta.url)));
const types = { '.html':'text/html; charset=utf-8', '.css':'text/css', '.png':'image/png', '.svg':'image/svg+xml', '.xml':'application/xml', '.txt':'text/plain' };
http.createServer(async (request, response) => {
  try {
    const pathname = decodeURIComponent(new URL(request.url, 'http://localhost').pathname);
    let target = path.resolve(root, '.' + pathname);
    if (target !== root && !target.startsWith(root + path.sep)) { response.writeHead(403); response.end(); return; }
    if ((await stat(target)).isDirectory()) target = path.join(target, 'index.html');
    const data = await readFile(target);
    response.writeHead(200, { 'Content-Type': types[path.extname(target)] ?? 'application/octet-stream' });
    response.end(data);
  } catch { response.writeHead(404, {'Content-Type':'text/html'}); response.end(await readFile(path.join(root, '404.html'))); }
}).listen(4173, '127.0.0.1', () => console.log('OpenSkiTime preview: http://127.0.0.1:4173'));
