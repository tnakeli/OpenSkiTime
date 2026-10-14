// One-time signing code handshake between the maintainer and the `windows-sign.yml` job.
//
// The job creates an RSA key pair and a random request ID, and publishes the public key as a workflow
// artifact of its own run, together with the identity of the run and of the tested unsigned build it
// will sign (build run, artifact ID and SHA-256 digest). The maintainer verifies all of that against the
// GitHub API, encrypts the current SimplySign code for that one request and posts it as a commit comment
// on the release commit. The job accepts only the maintainer's (numeric user ID) comment for its own
// request, decrypts it and logs in once. No long-lived signing secret is stored in GitHub.
//
// Usage:
//   node scripts/release-signing.mjs submit vX.Y.Z [--run <id>]   maintainer: start signing and enter the code
//   node scripts/release-signing.mjs request <output-directory>    job: create the key pair and request file
//   node scripts/release-signing.mjs receive                       job: wait for the code and print it to stdout
import { constants, createHash, generateKeyPairSync, privateDecrypt, publicEncrypt, randomBytes } from 'node:crypto';
import { execFile } from 'node:child_process';
import { mkdir, readFile, rm, writeFile } from 'node:fs/promises';
import path from 'node:path';
import { createInterface } from 'node:readline/promises';
import { fileURLToPath } from 'node:url';
import { promisify } from 'node:util';
import { inflateRawSync } from 'node:zlib';

export const repository = 'tnakeli/OpenSkiTime';
// The only account allowed to submit a signing code. The numeric ID survives a login rename and cannot be reused.
export const operator = { login: 'tnakeli', id: 259367049 };
export const signingWorkflow = 'windows-sign.yml';
export const signingWorkflowPath = `.github/workflows/${signingWorkflow}`;
export const buildWorkflow = 'windows-release.yml';
export const unsignedArtifactName = 'windows-unsigned';
export const signStepName = 'Receive signing code and sign application files';
const tagPattern = /^v\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?$/;
const headerPattern = /^ost-signing v2 run=(\d+) attempt=(\d+) request=([0-9a-f]{32})\n([A-Za-z0-9+/=]+)\s*$/;
const digestPattern = /^sha256:[0-9a-f]{64}$/;
const oaep = key => ({ key, padding: constants.RSA_PKCS1_OAEP_PADDING, oaepHash: 'sha256' });

export const requestArtifactName = attempt => `signing-request-${attempt}`;
export const sha256Digest = bytes => `sha256:${createHash('sha256').update(bytes).digest('hex')}`;

/** Creates the job's key pair and the public request the maintainer verifies before sending a code. */
export function createRequest({ repository: repo, runId, runAttempt, tag, sha, buildRunId, artifactId, artifactDigest }) {
  if (!tagPattern.test(tag ?? '')) throw new Error('Invalid release tag.');
  if (!/^[0-9a-f]{40}$/.test(sha ?? '')) throw new Error('Invalid release commit.');
  for (const value of [runId, runAttempt, buildRunId, artifactId]) {
    if (!/^\d+$/.test(String(value))) throw new Error('Invalid run or artifact identity.');
  }
  if (!digestPattern.test(artifactDigest ?? '')) throw new Error('Invalid unsigned artifact digest.');
  const { publicKey, privateKey } = generateKeyPairSync('rsa', { modulusLength: 3072 });
  return {
    request: {
      version: 2, repository: repo, workflow: signingWorkflowPath, runId: String(runId), runAttempt: String(runAttempt),
      requestId: randomBytes(16).toString('hex'), tag, sha, operatorId: operator.id,
      build: { runId: String(buildRunId), artifactId: String(artifactId), artifactDigest },
      publicKey: publicKey.export({ type: 'spki', format: 'pem' }),
    },
    privateKeyPem: privateKey.export({ type: 'pkcs8', format: 'pem' }),
  };
}

/** Checks that a downloaded request belongs to the expected signing run, release commit and tested build. */
export function verifyRequest(request, expected) {
  if (request?.version !== 2 || typeof request.publicKey !== 'string' || !/^[0-9a-f]{32}$/.test(request.requestId ?? '')) {
    throw new Error('Unsupported signing request.');
  }
  for (const field of ['repository', 'runId', 'runAttempt', 'tag', 'sha']) {
    if (String(request[field]) !== String(expected[field])) throw new Error(`Signing request ${field} does not match the release run.`);
  }
  if (request.workflow !== signingWorkflowPath || request.operatorId !== operator.id) throw new Error('Signing request is not for this workflow and operator.');
  for (const field of ['runId', 'artifactId', 'artifactDigest']) {
    if (String(request.build?.[field]) !== String(expected.build[field])) throw new Error(`Signing request build ${field} does not match the tested build.`);
  }
}

export function validateCode(code) {
  const trimmed = String(code ?? '').trim();
  if (!/^\d{6}$/.test(trimmed)) throw new Error('A SimplySign code has exactly six digits.');
  return trimmed;
}

/** Encrypts the code for one request and returns the commit comment body. */
export function encryptCode(request, code) {
  const payload = JSON.stringify({ requestId: request.requestId, runId: request.runId, runAttempt: request.runAttempt, code: validateCode(code) });
  const cipher = publicEncrypt(oaep(request.publicKey), Buffer.from(payload, 'utf8')).toString('base64');
  return `ost-signing v2 run=${request.runId} attempt=${request.runAttempt} request=${request.requestId}\n${cipher}`;
}

/**
 * Returns the code from the first comment that the operator (by numeric user ID) wrote for this request and
 * that decrypts with this request's key. Comments by anyone else, for other requests or with invalid content
 * are ignored.
 */
export function findCode(comments, { operatorId, runId, runAttempt, requestId, privateKeyPem }) {
  for (const comment of comments) {
    if (comment?.user?.id !== operatorId) continue;
    const match = headerPattern.exec(String(comment.body ?? '').replace(/\r\n/g, '\n'));
    if (!match || match[1] !== String(runId) || match[2] !== String(runAttempt) || match[3] !== requestId) continue;
    try {
      const payload = JSON.parse(privateDecrypt(oaep(privateKeyPem), Buffer.from(match[4], 'base64')).toString('utf8'));
      if (payload.requestId !== requestId || payload.runId !== String(runId) || payload.runAttempt !== String(runAttempt)) continue;
      return validateCode(payload.code);
    } catch {
      continue;
    }
  }
  return null;
}

/** Reads one file from a workflow artifact ZIP (stored or deflated entries, no ZIP64). */
export function readZipEntry(zip, name) {
  const end = zip.lastIndexOf(Buffer.from([0x50, 0x4b, 0x05, 0x06]));
  if (end < 0) throw new Error('Artifact is not a ZIP archive.');
  let entry = zip.readUInt32LE(end + 16);
  for (let i = zip.readUInt16LE(end + 10); i > 0; i--) {
    if (zip.readUInt32LE(entry) !== 0x02014b50) throw new Error('Corrupt ZIP directory.');
    const method = zip.readUInt16LE(entry + 10);
    const size = zip.readUInt32LE(entry + 20);
    const nameLength = zip.readUInt16LE(entry + 28);
    const local = zip.readUInt32LE(entry + 42);
    if (zip.toString('utf8', entry + 46, entry + 46 + nameLength) === name) {
      if (size === 0xffffffff || zip.readUInt32LE(local) !== 0x04034b50) throw new Error('Unsupported ZIP entry.');
      const start = local + 30 + zip.readUInt16LE(local + 26) + zip.readUInt16LE(local + 28);
      const data = zip.subarray(start, start + size);
      if (method === 0) return data;
      if (method === 8) return inflateRawSync(data);
      throw new Error('Unsupported ZIP compression.');
    }
    entry += 46 + nameLength + zip.readUInt16LE(entry + 30) + zip.readUInt16LE(entry + 32);
  }
  throw new Error(`Artifact has no ${name}.`);
}

const run = promisify(execFile);
async function gh(args, { json = true, binary = false } = {}) {
  const { stdout } = await run('gh', args, { maxBuffer: 64 * 1024 * 1024, encoding: binary ? 'buffer' : 'utf8' });
  return binary ? stdout : json ? JSON.parse(stdout) : stdout;
}
const sleep = ms => new Promise(resolve => setTimeout(resolve, ms));
const required = name => {
  const value = process.env[name];
  if (!value) throw new Error(`${name} is not set.`);
  return value;
};
const statePath = () => path.join(required('RUNNER_TEMP'), 'signing-request.key.json');

async function request(outputDirectory) {
  const { request, privateKeyPem } = createRequest({
    repository: required('GITHUB_REPOSITORY'), runId: required('GITHUB_RUN_ID'), runAttempt: required('GITHUB_RUN_ATTEMPT'),
    tag: required('SIGNING_TAG'), sha: required('SIGNING_SHA'), buildRunId: required('SIGNING_BUILD_RUN'),
    artifactId: required('SIGNING_ARTIFACT_ID'), artifactDigest: required('SIGNING_ARTIFACT_DIGEST'),
  });
  await writeFile(statePath(), JSON.stringify({ requestId: request.requestId, privateKeyPem }), { mode: 0o600 });
  await mkdir(outputDirectory, { recursive: true });
  await writeFile(path.join(outputDirectory, 'signing-request.json'), JSON.stringify(request, null, 2));
  console.error(`Signing request ${request.requestId} created for ${request.tag} (run ${request.runId}, attempt ${request.runAttempt}).`);
}

async function receive() {
  const repo = required('GITHUB_REPOSITORY');
  const sha = required('SIGNING_SHA');
  const state = JSON.parse(await readFile(statePath(), 'utf8'));
  const expected = {
    operatorId: operator.id, runId: required('GITHUB_RUN_ID'), runAttempt: required('GITHUB_RUN_ATTEMPT'),
    requestId: state.requestId, privateKeyPem: state.privateKeyPem,
  };
  const timeoutMinutes = Number(process.env.SIGNING_CODE_TIMEOUT_MINUTES ?? 15);
  const deadline = Date.now() + timeoutMinutes * 60_000;
  console.error(`Waiting up to ${timeoutMinutes} minutes for the signing code. Run on the maintainer machine:`);
  console.error(`  node scripts/release-signing.mjs submit ${required('SIGNING_TAG')} --run ${expected.runId}`);
  try {
    while (Date.now() < deadline) {
      const comments = (await gh(['api', '--paginate', '--slurp', `repos/${repo}/commits/${sha}/comments?per_page=100`])).flat();
      const code = findCode(comments, expected);
      if (code) {
        process.stdout.write(code);
        console.error('Signing code received.');
        return;
      }
      await sleep(3000);
    }
    throw new Error('No signing code was received in time. Re-run the failed job and submit a new code.');
  } finally {
    // The key is useless after this attempt; remove it so nothing later in the job can decrypt old comments.
    await rm(statePath(), { force: true });
  }
}

async function tagCommit(tag) {
  const ref = await gh(['api', `repos/${repository}/git/ref/tags/${tag}`]);
  return ref.object.type === 'tag'
    ? (await gh(['api', `repos/${repository}/git/tags/${ref.object.sha}`])).object.sha
    : ref.object.sha;
}

async function runArtifacts(runId) {
  return (await gh(['api', '--paginate', '--slurp', `repos/${repository}/actions/runs/${runId}/artifacts?per_page=100`]))
    .flatMap(page => page.artifacts);
}

async function findSigningRun(tag, since) {
  const deadline = Date.now() + 120_000;
  while (Date.now() < deadline) {
    const { workflow_runs: runs } = await gh(['api', `repos/${repository}/actions/workflows/${signingWorkflow}/runs?event=workflow_dispatch&branch=master&per_page=20`]);
    const found = runs.find(r => r.display_title === `Sign and publish ${tag}` && r.actor?.id === operator.id && Date.parse(r.created_at) >= since);
    if (found) return found;
    await sleep(3000);
  }
  throw new Error('The signing workflow did not start. Check the Actions page.');
}

async function stepState(runId, attempt) {
  const { jobs } = await gh(['api', `repos/${repository}/actions/runs/${runId}/attempts/${attempt}/jobs`]);
  const job = jobs.find(j => j.name === 'sign');
  return job?.steps?.find(s => s.name === signStepName) ?? (job?.status === 'completed' ? { status: 'completed', conclusion: job.conclusion } : null);
}

async function submit(tag, runOption) {
  if (!tagPattern.test(tag ?? '')) throw new Error('Usage: node scripts/release-signing.mjs submit vX.Y.Z [--run <id>]');
  const user = await gh(['api', 'user']);
  if (user.id !== operator.id) throw new Error(`Only ${operator.login} can submit signing codes; gh is signed in as ${user.login}.`);
  const sha = await tagCommit(tag);
  const { workflow_runs: builds } = await gh(['api', `repos/${repository}/actions/workflows/${buildWorkflow}/runs?event=push&head_sha=${sha}&per_page=20`]);
  const build = builds.find(r => r.head_branch === tag);
  if (!build) throw new Error(`No ${buildWorkflow} run exists for ${tag}.`);
  if (build.status !== 'completed') throw new Error(`The ${tag} build is still running. Try again when it has finished: ${build.html_url}`);
  if (build.conclusion !== 'success') throw new Error(`The ${tag} build did not succeed: ${build.html_url}`);
  const unsigned = (await runArtifacts(build.id)).find(a => a.name === unsignedArtifactName);
  if (!unsigned || unsigned.expired) throw new Error(`The ${tag} build has no unexpired ${unsignedArtifactName} artifact; build the tag again.`);
  if (!digestPattern.test(unsigned.digest ?? '')) throw new Error('The unsigned build artifact has no SHA-256 digest.');

  let signing;
  if (runOption) {
    signing = await gh(['api', `repos/${repository}/actions/runs/${runOption}`]);
  } else {
    const since = Date.now() - 5000;
    await gh(['workflow', 'run', signingWorkflow, '--repo', repository, '--ref', 'master', '-f', `tag=${tag}`], { json: false });
    console.log(`Started signing for ${tag}. Waiting for the runner to ask for the code (usually 1-2 minutes)...`);
    signing = await findSigningRun(tag, since);
  }
  if (signing.path !== signingWorkflowPath || signing.event !== 'workflow_dispatch' || signing.head_branch !== 'master'
      || signing.display_title !== `Sign and publish ${tag}` || signing.repository?.full_name !== repository || signing.actor?.id !== operator.id) {
    throw new Error('That run is not a signing run for this release.');
  }
  console.log(signing.html_url);

  const deadline = Date.now() + 15 * 60_000;
  let request;
  let attempt;
  while (!request) {
    if (Date.now() > deadline) throw new Error('The signing job did not publish its request in time.');
    const current = await gh(['api', `repos/${repository}/actions/runs/${signing.id}`]);
    if (current.status === 'completed') throw new Error(`The signing run ended (${current.conclusion}) before asking for a code.`);
    attempt = String(current.run_attempt);
    const artifact = (await runArtifacts(signing.id)).find(a => a.name === requestArtifactName(attempt) && !a.expired);
    if (!artifact) {
      await sleep(3000);
      continue;
    }
    // The ZIP must match the digest GitHub recorded at upload before its content is trusted.
    const zip = await gh(['api', `repos/${repository}/actions/artifacts/${artifact.id}/zip`], { binary: true });
    if (!digestPattern.test(artifact.digest ?? '') || sha256Digest(zip) !== artifact.digest) throw new Error('The signing request artifact does not match its SHA-256 digest.');
    request = JSON.parse(readZipEntry(zip, 'signing-request.json').toString('utf8'));
  }
  verifyRequest(request, {
    repository, runId: signing.id, runAttempt: attempt, tag, sha,
    build: { runId: build.id, artifactId: unsigned.id, artifactDigest: unsigned.digest },
  });
  console.log(`Request ${request.requestId}: signs build ${build.id} artifact ${unsigned.id} (${unsigned.digest}).`);

  const prompt = createInterface({ input: process.stdin, output: process.stdout });
  let code;
  try {
    code = validateCode(await prompt.question(`The runner is ready. Enter the current SimplySign code for ${tag}: `));
  } finally {
    prompt.close();
  }
  const comment = await gh(['api', `repos/${repository}/commits/${sha}/comments`, '-f', `body=${encryptCode(request, code)}`]);
  try {
    console.log('Code sent. Waiting for the runner to sign the application files...');
    const signDeadline = Date.now() + 20 * 60_000;
    while (Date.now() < signDeadline) {
      const step = await stepState(signing.id, attempt);
      if (step?.status === 'completed') {
        if (step.conclusion !== 'success') throw new Error(`Signing failed (${step.conclusion}). Check the run, then re-run the failed jobs and submit a new code with --run ${signing.id}.`);
        console.log('Signed. Installer signing, tests, scanning and publication continue without you:');
        console.log(signing.html_url);
        return;
      }
      await sleep(3000);
    }
    throw new Error(`The signing step did not finish in time. Check the run: ${signing.html_url}`);
  } finally {
    // The ciphertext is useless after the attempt, but the comment is only transport; remove it.
    await gh(['api', '-X', 'DELETE', `repos/${repository}/comments/${comment.id}`], { json: false }).catch(() =>
      console.error(`Could not delete commit comment ${comment.html_url}; delete it manually.`));
  }
}

if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  const [command, argument, option, optionValue] = process.argv.slice(2);
  try {
    if (command === 'request' && argument) await request(argument);
    else if (command === 'receive') await receive();
    else if (command === 'submit') await submit(argument, option === '--run' ? optionValue : undefined);
    else throw new Error('Usage: node scripts/release-signing.mjs <submit vX.Y.Z [--run <id>] | request <dir> | receive>');
  } catch (error) {
    console.error(error.message);
    process.exitCode = 1;
  }
}
