import test from 'node:test';
import assert from 'node:assert/strict';
import { generateKeyPairSync } from 'node:crypto';
import { deflateRawSync } from 'node:zlib';
import { createRequest, encryptCode, findCode, operator, readZipEntry, sha256Digest, validateCode, verifyRequest } from '../release-signing.mjs';

const build = { buildRunId: '777', artifactId: '888', artifactDigest: `sha256:${'c'.repeat(64)}` };
const identity = { repository: 'tnakeli/OpenSkiTime', runId: '123456', runAttempt: '2', tag: 'v1.2.0', sha: 'a'.repeat(40), ...build };
const { request, privateKeyPem } = createRequest(identity);
const expected = { operatorId: operator.id, runId: '123456', runAttempt: '2', requestId: request.requestId, privateKeyPem };
const comment = (id, body, login = 'tnakeli') => ({ user: { id, login }, body });
const verified = { ...identity, build: { runId: '777', artifactId: '888', artifactDigest: build.artifactDigest } };

test('the maintainer code round-trips only through this request\'s key', () => {
  const body = encryptCode(request, '042117');
  assert.doesNotMatch(body, /042117/);
  assert.match(body, new RegExp(`request=${request.requestId}`));
  assert.equal(findCode([comment(operator.id, body)], expected), '042117');
});

test('comments are matched by numeric user ID, not by login', () => {
  const body = encryptCode(request, '111111');
  assert.equal(findCode([comment(1, body, 'tnakeli')], expected), null);
  assert.equal(findCode([comment(1, body), comment(operator.id, encryptCode(request, '222222'), 'renamed')], expected), '222222');
});

test('a comment for another request, run attempt or key is ignored', () => {
  const previous = createRequest({ ...identity, runAttempt: '1' });
  assert.equal(findCode([comment(operator.id, encryptCode(previous.request, '333333'))], expected), null);
  const sameRunOtherRequest = createRequest(identity);
  assert.equal(findCode([comment(operator.id, encryptCode(sameRunOtherRequest.request, '333334'))], expected), null);
  const otherKey = generateKeyPairSync('rsa', { modulusLength: 3072 }).publicKey.export({ type: 'spki', format: 'pem' });
  assert.equal(findCode([comment(operator.id, encryptCode({ ...request, publicKey: otherKey }, '444444'))], expected), null);
});

test('a header naming this request cannot redirect a payload made for another one', () => {
  const stale = encryptCode({ ...request, requestId: 'f'.repeat(32) }, '555555').split('\n')[1];
  assert.equal(findCode([comment(operator.id, `ost-signing v2 run=123456 attempt=2 request=${request.requestId}\n${stale}`)], expected), null);
});

test('malformed comments are skipped and Windows line endings are accepted', () => {
  const body = encryptCode(request, '666666').replace('\n', '\r\n');
  const broken = `ost-signing v2 run=123456 attempt=2 request=${request.requestId}\nnot-base64!`;
  assert.equal(findCode([comment(operator.id, broken), comment(operator.id, body)], expected), '666666');
});

test('codes must be exactly six digits', () => {
  assert.equal(validateCode(' 012345 '), '012345');
  for (const bad of ['12345', '1234567', 'abcdef', '', undefined]) assert.throws(() => validateCode(bad));
  assert.throws(() => encryptCode(request, '12 345'));
});

test('a downloaded request must match the signing run, commit, operator and tested build', () => {
  verifyRequest(request, verified);
  assert.throws(() => verifyRequest(request, { ...verified, runAttempt: '3' }), /runAttempt/);
  assert.throws(() => verifyRequest(request, { ...verified, sha: 'b'.repeat(40) }), /sha/);
  assert.throws(() => verifyRequest(request, { ...verified, build: { ...verified.build, artifactId: '999' } }), /artifactId/);
  assert.throws(() => verifyRequest(request, { ...verified, build: { ...verified.build, artifactDigest: `sha256:${'d'.repeat(64)}` } }), /artifactDigest/);
  assert.throws(() => verifyRequest({ ...request, workflow: '.github/workflows/ci.yml' }, verified), /workflow/);
  assert.throws(() => verifyRequest({ ...request, operatorId: 1 }, verified), /operator/);
  assert.throws(() => verifyRequest({ ...request, version: 1 }, verified), /Unsupported/);
});

test('each request has its own random ID', () => {
  assert.match(request.requestId, /^[0-9a-f]{32}$/);
  assert.notEqual(createRequest(identity).request.requestId, request.requestId);
});

test('requests reject invalid tags, commits and build identities', () => {
  assert.throws(() => createRequest({ ...identity, tag: 'live-v1.0.0' }), /tag/);
  assert.throws(() => createRequest({ ...identity, sha: 'HEAD' }), /commit/);
  assert.throws(() => createRequest({ ...identity, artifactId: 'x' }), /identity/);
  assert.throws(() => createRequest({ ...identity, artifactDigest: 'sha1:abc' }), /digest/);
});

// Minimal ZIP writer for the reader tests: one local header and central directory entry per file.
function zip(entries) {
  const locals = [];
  const centrals = [];
  let offset = 0;
  for (const { name, data, method } of entries) {
    const body = method === 8 ? deflateRawSync(data) : data;
    const nameBytes = Buffer.from(name);
    const local = Buffer.alloc(30);
    local.writeUInt32LE(0x04034b50, 0); local.writeUInt16LE(method, 8);
    local.writeUInt32LE(body.length, 18); local.writeUInt32LE(data.length, 22); local.writeUInt16LE(nameBytes.length, 26);
    const central = Buffer.alloc(46);
    central.writeUInt32LE(0x02014b50, 0); central.writeUInt16LE(method, 10);
    central.writeUInt32LE(body.length, 20); central.writeUInt32LE(data.length, 24); central.writeUInt16LE(nameBytes.length, 28);
    central.writeUInt32LE(offset, 42);
    locals.push(local, nameBytes, body);
    centrals.push(central, nameBytes);
    offset += local.length + nameBytes.length + body.length;
  }
  const directory = Buffer.concat(centrals);
  const end = Buffer.alloc(22);
  end.writeUInt32LE(0x06054b50, 0); end.writeUInt16LE(entries.length, 8); end.writeUInt16LE(entries.length, 10);
  end.writeUInt32LE(directory.length, 12); end.writeUInt32LE(offset, 16);
  return Buffer.concat([...locals, directory, end]);
}

test('artifact ZIP entries are read from stored and deflated archives', () => {
  const json = Buffer.from(JSON.stringify(request));
  const archive = zip([{ name: 'other.txt', data: Buffer.from('x'), method: 0 }, { name: 'signing-request.json', data: json, method: 8 }]);
  assert.deepEqual(readZipEntry(archive, 'signing-request.json'), json);
  assert.deepEqual(readZipEntry(zip([{ name: 'signing-request.json', data: json, method: 0 }]), 'signing-request.json'), json);
  assert.throws(() => readZipEntry(archive, 'missing.json'), /no missing\.json/);
  assert.throws(() => readZipEntry(Buffer.from('not a zip'), 'signing-request.json'), /not a ZIP/);
});

test('artifact digests use GitHub\'s sha256: format', () => {
  assert.equal(sha256Digest(Buffer.from('abc')), 'sha256:ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad');
});
