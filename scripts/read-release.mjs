import { writeFile, appendFile, mkdir } from 'node:fs/promises';
import { fileURLToPath } from 'node:url';
import path from 'node:path';

// The website links only to the newest stable application release. GitHub's releases/latest is not used:
// when no stable application release exists it returns a live-v* server release despite make_latest=false.
export function selectRelease(releases) {
  return releases
    .filter(r => !r.draft && !r.prerelease && /^v\d+\.\d+\.\d+$/.test(r.tag_name ?? ''))
    .sort((a, b) => compare(b.tag_name, a.tag_name))[0] ?? null;
}
const compare = (a, b) => {
  const [x, y] = [a, b].map(t => t.slice(1).split('.').map(Number));
  return x[0] - y[0] || x[1] - y[1] || x[2] - y[2];
};

if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  const response = await fetch('https://api.github.com/repos/tnakeli/OpenSkiTime/releases?per_page=100', {
    headers: { Accept: 'application/vnd.github+json', ...(process.env.GH_TOKEN ? { Authorization: `Bearer ${process.env.GH_TOKEN}` } : {}) }
  });
  if (!response.ok) throw new Error(`GitHub release lookup failed: HTTP ${response.status}`);
  const release = selectRelease(await response.json());
  if (!release) {
    console.log('No published stable application release; showing development instructions.');
  } else {
    await mkdir('artifacts', { recursive: true });
    await writeFile('artifacts/website-release.json', JSON.stringify(release));
    if (process.env.GITHUB_ENV) await appendFile(process.env.GITHUB_ENV, 'OPENSKITIME_RELEASE_FILE=artifacts/website-release.json\n');
    console.log(`Website download: ${release.tag_name}.`);
  }
}
