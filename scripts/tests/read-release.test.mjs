import test from 'node:test';
import assert from 'node:assert/strict';
import { selectRelease } from '../read-release.mjs';

const r = (tag_name, extra = {}) => ({ tag_name, draft: false, prerelease: false, ...extra });

test('ignores live server releases and drafts, and prefers stable releases to previews', () => {
  assert.equal(selectRelease([r('live-v0.1.3'), r('v0.1.0-preview.1', { draft: true, prerelease: true })]), null);
  assert.equal(selectRelease([r('v0.2.0', { draft: true }), r('live-v0.2.0'), r('v0.1.0')]).tag_name, 'v0.1.0');
});

test('chooses the highest stable application version regardless of order', () => {
  assert.equal(selectRelease([r('v0.9.0'), r('v0.10.0'), r('v0.10.1'), r('v1.0.0'), r('v0.2.0')]).tag_name, 'v1.0.0');
  assert.equal(selectRelease([r('v0.9.0'), r('v0.10.0')]).tag_name, 'v0.10.0');
});

test('offers the newest application preview only while no stable release exists', () => {
  const preview = (tag, created) => r(tag, { prerelease: true, created_at: created });
  assert.equal(selectRelease([r('live-v0.1.3'), preview('v0.1.0-preview.1', '2026-10-04T11:00:00Z')]).tag_name, 'v0.1.0-preview.1');
  assert.equal(selectRelease([preview('v0.1.0-preview.2', '2026-11-01T00:00:00Z'), preview('v0.1.0-preview.1', '2026-10-04T00:00:00Z'), preview('v0.2.0-preview.1', '2026-10-20T00:00:00Z')]).tag_name, 'v0.2.0-preview.1');
  assert.equal(selectRelease([preview('v0.1.0-preview.2', '2026-11-01T00:00:00Z'), preview('v0.1.0-preview.1', '2026-10-04T00:00:00Z')]).tag_name, 'v0.1.0-preview.2');
  assert.equal(selectRelease([preview('v0.2.0-preview.1', '2026-12-01T00:00:00Z'), r('v0.1.0')]).tag_name, 'v0.1.0');
  assert.equal(selectRelease([preview('live-v0.2.0-rc.1'), r('v0.1.0-preview.1', { prerelease: true, draft: true })]), null);
});
