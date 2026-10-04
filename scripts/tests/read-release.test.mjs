import test from 'node:test';
import assert from 'node:assert/strict';
import { selectRelease } from '../read-release.mjs';

const r = (tag_name, extra = {}) => ({ tag_name, draft: false, prerelease: false, ...extra });

test('ignores live server releases, prereleases and drafts', () => {
  assert.equal(selectRelease([r('live-v0.1.3'), r('v0.1.0-preview.1', { prerelease: true })]), null);
  assert.equal(selectRelease([r('v0.2.0', { draft: true }), r('live-v0.2.0'), r('v0.1.0')]).tag_name, 'v0.1.0');
});

test('chooses the highest stable application version regardless of order', () => {
  assert.equal(selectRelease([r('v0.9.0'), r('v0.10.0'), r('v0.10.1'), r('v1.0.0'), r('v0.2.0')]).tag_name, 'v1.0.0');
  assert.equal(selectRelease([r('v0.9.0'), r('v0.10.0')]).tag_name, 'v0.10.0');
});
