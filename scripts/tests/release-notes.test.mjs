import test from 'node:test';
import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import { section, appNotes, liveNotes } from '../release-notes.mjs';

const changelog = `# Changelog

## [Unreleased]

## [1.2.0-preview.1] - 2026-11-01

### Added

- New thing, live protocol 2.

## [1.1.0] - 2026-10-01

### Fixed

- Old thing.

[1.2.0-preview.1]: https://example.invalid/a
[1.1.0]: https://example.invalid/b
`;
const live = `## [Unreleased]

## [0.3.0] - 2026-11-01

Live protocol: 2

## [0.2.0] - 2026-10-01

Live protocol: 1
`;

test('extracts exactly one dated section, excluding link references and later sections', () => {
  assert.equal(section(changelog, '1.2.0-preview.1'), '### Added\n\n- New thing, live protocol 2.');
  assert.equal(section(changelog, '1.1.0'), '### Fixed\n\n- Old thing.');
});

test('refuses missing, undated and empty sections so a tag cannot publish without notes', () => {
  assert.throws(() => section(changelog, '1.2.0'), /no dated section/);
  assert.throws(() => section(changelog, 'Unreleased'), /no dated section/);
  assert.throws(() => section('## [2.0.0] - 2026-01-01\n\n## [1.0.0] - 2025-01-01\n- x', '2.0.0'), /empty/);
});

test('version text is matched literally, not as a pattern', () => {
  assert.throws(() => section('## [1x2x0] - 2026-01-01\n- x', '1.2.0'), /no dated section/);
});

test('application notes have separate application and live sections with compatible servers', () => {
  const notes = appNotes(section(changelog, '1.2.0-preview.1'), live, '1.2.0-preview.1');
  assert.match(notes, /^## Application\n/);
  assert.match(notes, /## Live timing\n\nThis version publishes with \*\*live protocol 2\*\*\. Compatible live server releases: live-v0\.3\.0,/);
  assert.doesNotMatch(notes, /live-v0\.2\.0/);
  assert.match(notes, /Unsigned build/);
  assert.match(notes, /SHA256SUMS\.txt/);
  assert.match(notes, /Get-FileHash -LiteralPath \$name -Algorithm SHA256/);
  assert.match(notes, /sha256sum -c --ignore-missing SHA256SUMS\.txt/);
  assert.throws(() => appNotes('- No protocol here.', live, '1.0.0'), /must state the live protocol/);
});

test('live notes include the deployed image only when known', () => {
  assert.match(liveNotes('- Fix.', '0.3.0', 'ghcr.io/x@sha256:abc'), /Image: `ghcr\.io\/x@sha256:abc`/);
  assert.doesNotMatch(liveNotes('- Fix.', '0.3.0', undefined), /Image:/);
});

test('repository changelogs produce notes for their newest released versions', async () => {
  const root = new URL('../../', import.meta.url);
  const app = await readFile(new URL('CHANGELOG.md', root), 'utf8');
  const server = await readFile(new URL('CHANGELOG-LIVE.md', root), 'utf8');
  const newest = text => /^## \[(\d[^\]]*)\]/m.exec(text)[1];
  assert.match(appNotes(section(app, newest(app)), server, newest(app)), /## Live timing/);
  assert.match(liveNotes(section(server, newest(server)), newest(server)), /## Live timing server/);
});
