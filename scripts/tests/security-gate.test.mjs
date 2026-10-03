import { test } from 'node:test';
import assert from 'node:assert/strict';
import { mkdtempSync, writeFileSync, chmodSync, readFileSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join, resolve } from 'node:path';
import { spawnSync } from 'node:child_process';

// Exercise the shell boundary on the same OS as the production scanning jobs.
for (const scenario of ['clean', 'vulnerable', 'empty', 'database-error']) {
  test(`security gate: ${scenario}`, { skip: process.platform === 'win32' }, () => {
    const directory = mkdtempSync(join(tmpdir(), 'openskitime-security-'));
    try {
      const scanner = join(directory, 'trivy');
      writeFileSync(scanner, `#!/usr/bin/env node
const fs = require('node:fs');
const args = process.argv.slice(2);
fs.appendFileSync(process.env.CALL_LOG, JSON.stringify(args)+'\\n');
const output = args[args.indexOf('--output')+1];
if (args.includes('--version')) { console.log('scanner fixture'); process.exit(0); }
if (args.includes('cyclonedx')) {
  fs.writeFileSync(output, JSON.stringify({components:process.env.SCENARIO==='empty'?[]:[{name:'runtime'}]}));
} else if (args.includes('--exit-code')) {
  fs.writeFileSync(output, 'gate report');
  process.exit(process.env.SCENARIO==='vulnerable'?1:0);
} else {
  if (process.env.SCENARIO==='database-error') process.exit(9);
  fs.writeFileSync(output, '{}');
}
`);
      chmodSync(scanner, 0o755);
      const output = join(directory, 'reports');
      const log = join(directory, 'calls.jsonl');
      const result = spawnSync('bash', [resolve('scripts/scan-security.sh'), 'fs', directory, output], {
        env: { ...process.env, PATH: `${directory}:${process.env.PATH}`, SCENARIO: scenario, CALL_LOG: log },
        encoding: 'utf8',
      });
      assert.equal(result.status, scenario === 'clean' ? 0 : scenario === 'database-error' ? 9 : 1, result.stderr);
      const calls = readFileSync(log, 'utf8').trim().split('\n').map(line => JSON.parse(line));
      const gate = calls.find(args => args.includes('--exit-code'));
      if (scenario === 'empty' || scenario === 'database-error') assert.equal(gate, undefined);
      else {
        assert.equal(gate[gate.indexOf('--severity')+1], 'HIGH,CRITICAL');
        assert.ok(!gate.includes('--ignore-unfixed'));
        assert.equal(readFileSync(join(output, 'cve-gate.txt'), 'utf8'), 'gate report');
      }
    } finally { rmSync(directory, { recursive: true, force: true }); }
  });
}
