import { writeFile, appendFile, mkdir } from 'node:fs/promises';
const response = await fetch('https://api.github.com/repos/tnakeli/OpenSkiTime/releases/latest', {
  headers: { Accept: 'application/vnd.github+json', ...(process.env.GH_TOKEN ? { Authorization: `Bearer ${process.env.GH_TOKEN}` } : {}) }
});
if (response.status === 404) {
  console.log('No published stable release; showing development instructions.');
} else {
  if (!response.ok) throw new Error(`GitHub release lookup failed: HTTP ${response.status}`);
  await mkdir('artifacts', {recursive:true});
  await writeFile('artifacts/website-release.json', JSON.stringify(await response.json()));
  if (process.env.GITHUB_ENV) await appendFile(process.env.GITHUB_ENV, 'OPENSKITIME_RELEASE_FILE=artifacts/website-release.json\n');
}
