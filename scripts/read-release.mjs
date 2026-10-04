import { writeFile, appendFile, mkdir } from 'node:fs/promises';
import { fileURLToPath } from 'node:url';
import path from 'node:path';

// The website links to the newest stable application release, or to the newest application preview
// while no stable release exists. GitHub's releases/latest is not used: when no stable application
// release exists it returns a live-v* server release despite make_latest=false.
export function selectRelease(releases) {
  const published = releases.filter(r => !r.draft);
  const newest = list => list.sort((a, b) => compare(b.tag_name, a.tag_name)
    || Date.parse(b.created_at ?? 0) - Date.parse(a.created_at ?? 0))[0] ?? null;
  return newest(published.filter(r => !r.prerelease && /^v\d+\.\d+\.\d+$/.test(r.tag_name ?? '')))
    ?? newest(published.filter(r => r.prerelease && /^v\d+\.\d+\.\d+-[0-9A-Za-z.-]+$/.test(r.tag_name ?? '')));
}
// Compares MAJOR.MINOR.PATCH only; previews of one version are ordered by creation time.
const compare = (a, b) => {
  const [x, y] = [a, b].map(t => t.slice(1).split('-')[0].split('.').map(Number));
  return x[0] - y[0] || x[1] - y[1] || x[2] - y[2];
};

if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  const response = await fetch('https://api.github.com/repos/tnakeli/OpenSkiTime/releases?per_page=100', {
    headers: { Accept: 'application/vnd.github+json', ...(process.env.GH_TOKEN ? { Authorization: `Bearer ${process.env.GH_TOKEN}` } : {}) }
  });
  if (!response.ok) throw new Error(`GitHub release lookup failed: HTTP ${response.status}`);
  const release = selectRelease(await response.json());
  if (!release) {
    console.log('No published application release; showing development instructions.');
  } else {
    await mkdir('artifacts', { recursive: true });
    await writeFile('artifacts/website-release.json', JSON.stringify(release));
    if (process.env.GITHUB_ENV) await appendFile(process.env.GITHUB_ENV, 'OPENSKITIME_RELEASE_FILE=artifacts/website-release.json\n');
    console.log(`Website download: ${release.tag_name}${release.prerelease ? ' (preview)' : ''}.`);
  }
}
