import test from 'node:test';
import assert from 'node:assert/strict';
import http from 'node:http';
import { smoke, syntheticSnapshot } from '../live-smoke.mjs';

test('smoke deletes its synthetic session even when publication fails', async () => {
  const calls = [];
  const server = http.createServer((request, response) => {
    calls.push(`${request.method} ${request.url}`);
    response.setHeader('Content-Type','application/json');
    if (request.url === '/health') response.end('{}');
    else if (request.url === '/api/sessions') response.end(JSON.stringify({sessionId:'00000000-0000-0000-0000-000000000001',publisherToken:'test-credential'}));
    else if (request.method === 'DELETE') { response.statusCode = 204; response.end(); }
    else if (request.method === 'GET') { response.statusCode = 404; response.end('{}'); }
    else { response.statusCode = request.headers.authorization === 'Bearer invalid' ? 401 : 503; response.end('{}'); }
  });
  await new Promise(resolve => server.listen(0,'127.0.0.1',resolve));
  try {
    await assert.rejects(smoke(`http://127.0.0.1:${server.address().port}`,{websocket:false}), /Full state publication/);
    assert(calls.includes('DELETE /api/sessions/00000000-0000-0000-0000-000000000001/data'));
  } finally { await new Promise(resolve => server.close(resolve)); }
});

test('smoke rejects insecure remote origins before sending data', async () => {
  await assert.rejects(smoke('http://example.com'), /requires HTTPS/);
  assert.equal(syntheticSnapshot().competitors[0].lastName, 'TEST');
});
