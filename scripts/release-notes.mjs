// Writes the GitHub release notes for one tagged version from its changelog section.
// Usage: node scripts/release-notes.mjs <app|live> <version> [output-file]
// Without output-file, only validates that the section exists and is non-empty.
import { readFile, writeFile } from 'node:fs/promises';
import { fileURLToPath } from 'node:url';
import path from 'node:path';

const root = fileURLToPath(new URL('..', import.meta.url));
const escapeRegex = value => value.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');

/** Returns the body of `## [version]` up to the next `## ` heading or link reference block. */
export function section(changelog, version) {
  const heading = new RegExp(`^## \\[${escapeRegex(version)}\\] - \\d{4}-\\d{2}-\\d{2}[ \\t]*$`, 'm');
  const match = heading.exec(changelog);
  if (!match) throw new Error(`Changelog has no dated section for ${version}.`);
  const rest = changelog.slice(match.index + match[0].length);
  const end = rest.search(/^(## |\[[^\]]+\]: )/m);
  const body = (end < 0 ? rest : rest.slice(0, end)).trim();
  if (!body) throw new Error(`Changelog section for ${version} is empty.`);
  return body;
}

export function appNotes(changes, live, version) {
  const protocol = /live protocol (\d+)/i.exec(changes)?.[1];
  if (!protocol) throw new Error(`Application section for ${version} must state the live protocol it uses (e.g. "live protocol 1").`);
  const compatible = [...live.matchAll(/^## \[(\d+\.\d+\.\d+)\][^\n]*\n+Live protocol: (\d+)/gm)]
    .filter(m => m[2] === protocol).map(m => `live-v${m[1]}`);
  return `## Application

${changes}

## Live timing

This version publishes with **live protocol ${protocol}**. ${compatible.length ? `Compatible live server releases: ${compatible.join(', ')}, and later releases supporting protocol ${protocol}.` : `Use a live server that supports protocol ${protocol}.`} The hosted server at \`https://live.openskiti.me\` is updated independently; see [live server releases](https://github.com/tnakeli/OpenSkiTime/releases?q=live-v&expanded=true) and [CHANGELOG-LIVE.md](https://github.com/tnakeli/OpenSkiTime/blob/v${version}/CHANGELOG-LIVE.md). Local publishing uses the server included in this installer.

## Installation

Windows x64 installer and portable package. The .NET 10 runtime is included. The installer is per-user and needs no administrator rights.

**Unsigned build:** Windows may show an unknown-publisher warning. Code signing is not enabled yet. Download only from this project's GitHub Releases.

Rehearse before race day and back up event files before updating. Incompatible series files are preserved and rejected; automatic schema upgrades are not available. ALGE native USB needs the vendor driver and SDK separately.

## Verification

The attached CycloneDX SBOM describes detected runtime dependencies. CVE reports include all severities; high and critical findings block publication. Native vendor components may need separate advisories.

Compare downloaded files with \`SHA256SUMS.txt\`. Checksums verify file integrity, not publisher identity.
`;
}

export function liveNotes(changes, version, image) {
  return `## Live timing server

${changes}

## Deployment

Deployed to \`https://live.openskiti.me\` after container security scanning and automated acceptance checks.${image ? `\n\nImage: \`${image}\`` : ''}

This is a hosted service release; it has no downloadable files. Desktop applications are released separately as \`vX.Y.Z\`. Self-hosting instructions: [cloud server deployment](https://github.com/tnakeli/OpenSkiTime/blob/live-v${version}/docs/live-timing-cloud-deployment.md).
`;
}

if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  const [kind, version, output] = process.argv.slice(2);
  if (!['app', 'live'].includes(kind) || !/^\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?$/.test(version ?? '')) {
    throw new Error('Usage: node scripts/release-notes.mjs <app|live> <version> [output-file]');
  }
  const live = await readFile(path.join(root, 'CHANGELOG-LIVE.md'), 'utf8');
  const notes = kind === 'app'
    ? appNotes(section(await readFile(path.join(root, 'CHANGELOG.md'), 'utf8'), version), live, version)
    : liveNotes(section(live, version), version, process.env.LIVE_IMAGE);
  if (output) await writeFile(output, notes);
  console.log(`Release notes for ${kind} ${version}: ${notes.length} characters${output ? ` written to ${output}` : ''}.`);
}
