"""Live Timing browser E2E for the synthetic 100-athlete slalom.

Publishes the snapshots exported by LiveFullRaceSnapshots (the desktop's LiveSnapshotMapper at checkpoints during
and after both runs) to a real local LiveTiming.Server and verifies the spectator view in Chromium after each update:
rows, statuses, intermediate times, run and combined times, ranks, ties and differences. The completed runs are also
checked against independent expectations (expected.json). Offline; loopback only; synthetic data only.

Run:
  OPENSKITIME_LIVE_FULL_RACE=<dir> dotnet test tests/OpenSkiTime.Desktop.Tests -c Release --filter "FullyQualifiedName~ExportLiveSnapshotsOfTheFullRace"
  python tests/live-timing-full-race-e2e.py --snapshots <dir> --screenshots <dir>
Requires: pip install playwright; python -m playwright install chromium; a Release build of OpenSkiTime.LiveTiming.Server.
"""
import argparse
import base64
import hashlib
import json
import os
from pathlib import Path
import secrets
import socket
import subprocess
import sys
import time
import urllib.request

from playwright.sync_api import sync_playwright, expect


def fmt(hundredths):
    if hundredths is None:
        return None
    return f"{hundredths // 6000}:{hundredths // 100 % 60:02d}.{hundredths % 100:02d}"


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--dotnet', default='dotnet')
    parser.add_argument('--configuration', default='Release')
    parser.add_argument('--snapshots', required=True)
    parser.add_argument('--screenshots', default=None)
    parser.add_argument('--hold', type=float, default=0, help='seconds to keep each checkpoint on screen (recordings)')
    parser.add_argument('--headed', action='store_true')
    args = parser.parse_args()
    root = Path(__file__).resolve().parents[1]
    snapshots = sorted(Path(args.snapshots).glob('snapshot-*.json'))
    expected = json.loads((Path(args.snapshots) / 'expected.json').read_text(encoding='utf-8'))
    if len(snapshots) < 8:
        raise SystemExit(f'Expected at least 8 snapshots, found {len(snapshots)}')
    shots = Path(args.screenshots) if args.screenshots else None
    if shots:
        shots.mkdir(parents=True, exist_ok=True)
    server = root / 'src/OpenSkiTime.LiveTiming.Server'
    assembly = server / 'bin' / args.configuration / 'net10.0/OpenSkiTime.LiveTiming.Server.dll'
    with socket.socket() as reserve:
        reserve.bind(('127.0.0.1', 0))
        port = reserve.getsockname()[1]
    base = f'http://127.0.0.1:{port}'
    publisher_key = 'ost_pk_' + secrets.token_urlsafe(32)
    env = dict(os.environ, LiveTiming__SigningKey=base64.b64encode(secrets.token_bytes(32)).decode(), Logging__LogLevel__Default='Warning',
               LiveTiming__PublisherKeys='fullrace:' + hashlib.sha256(publisher_key.encode()).hexdigest())
    process = subprocess.Popen([args.dotnet, str(assembly), '--urls', base, '--contentRoot', str(server)], env=env,
                               stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, creationflags=getattr(subprocess, 'CREATE_NO_WINDOW', 0))

    def request(method, path, value=None, token=None):
        headers = {'Content-Type': 'application/json'}
        if token:
            headers['Authorization'] = 'Bearer ' + token
        data = json.dumps(value).encode() if value is not None else (b'' if method == 'POST' else None)
        with urllib.request.urlopen(urllib.request.Request(base + path, data=data, headers=headers, method=method), timeout=10) as response:
            raw = response.read()
            return json.loads(raw) if raw else None

    log = []
    try:
        for _ in range(150):
            try:
                request('GET', '/health')
                break
            except OSError:
                time.sleep(0.1)
        session = request('POST', '/api/sessions', token=publisher_key)
        sid, token = session['sessionId'], session['publisherToken']
        first = json.loads(snapshots[0].read_text(encoding='utf-8'))
        request('PUT', f'/api/sessions/{sid}/state', first, token)
        with sync_playwright() as playwright:
            browser = playwright.chromium.launch(headless=not args.headed)
            context = browser.new_context(viewport={'width': 1440, 'height': 900}, locale='en-GB', timezone_id='UTC')
            context.route('**/*', lambda route: route.continue_() if route.request.url.startswith(base) else route.abort())
            page = context.new_page()
            errors = []
            page.on('pageerror', lambda error: errors.append(str(error)))
            page.goto(session['publicUrl'])
            expect(page.locator('#connection')).to_have_text('Live')
            expect(page.locator('#name')).to_have_text(first['competition']['name'])
            for path in snapshots:
                state = json.loads(path.read_text(encoding='utf-8'))
                request('PUT', f'/api/sessions/{sid}/state', state, token)
                run = next(r for r in state['runs'] if r['number'] == state['currentRun'])
                expect(page.locator('#runs button[aria-pressed="true"]')).to_have_text(f"Run {state['currentRun']}")
                expect(page.locator('#results tr')).to_have_count(len(run['startOrder']))
                previous = {r['bib']: r for r in state['runs'][0]['results']} if state['currentRun'] == 2 else {}
                head = page.locator('#head th').all_inner_texts()
                col = {name: index for index, name in enumerate(head)}
                checked = 0
                for result in run['results']:
                    row = page.locator(f'tr[data-bib="{result["bib"]}"] td')
                    expect(row.nth(4)).to_have_text(result['status'])
                    if result['status'] != 'Finished':
                        continue
                    rank = result.get('totalRank') if state['currentRun'] == 2 else result.get('rank')
                    expect(row.nth(0)).to_have_text(str(rank) if rank else '—')
                    splits = result.get('intermediates') or []
                    if splits:
                        assert 'Intermediate 1' in col, f'{path.stem}: intermediate column missing ({head})'
                        expect(row.nth(col['Intermediate 1'])).to_contain_text(fmt(splits[0]['hundredths']))
                    if state['currentRun'] == 1:
                        expect(row.nth(col['Finish'])).to_have_text(fmt(result['hundredths']))
                    else:
                        expect(row.nth(col['Run 1'])).to_have_text(fmt(previous[result['bib']]['hundredths']))
                        expect(row.nth(col['Run 2'])).to_have_text(fmt(result['hundredths']))
                        expect(row.nth(col['Total'])).to_have_text(fmt(result['totalHundredths']))
                    checked += 1
                label = path.stem
                if label.endswith('complete'):
                    expect(page.locator('#course')).to_have_text(f"Run {state['currentRun']} complete")
                log.append(f'{label}: run {state["currentRun"]}, {len(run["startOrder"])} rows, '
                           f'{sum(1 for r in run["results"] if r["status"] == "Finished")} finished, {checked} finished rows verified')
                if shots:
                    page.screenshot(path=str(shots / f'{label}.png'))
                if args.hold:
                    time.sleep(args.hold)
                # Completed runs against the independent expectations.
                if label.endswith('run1-complete'):
                    for item in expected['run1']:
                        row = page.locator(f'tr[data-bib="{item["bib"]}"] td')
                        expect(row.nth(4)).to_have_text(item['status'])
                        if item['status'] == 'Finished':
                            expect(row.nth(0)).to_have_text(str(item['rank']))
                            expect(row.nth(col['Finish'])).to_have_text(item['time'])
                            if item.get('split'):
                                expect(row.nth(col['Intermediate 1'])).to_contain_text(item['split'])
                    for a, b in expected['run1Ties']:
                        ra = page.locator(f'tr[data-bib="{a}"] td').nth(0).inner_text()
                        rb = page.locator(f'tr[data-bib="{b}"] td').nth(0).inner_text()
                        assert ra == rb, f'Run 1 tie {a}/{b} shown as {ra}/{rb}'
                    log.append(f'  run 1 verified against {len(expected["run1"])} independent expectations; ties {expected["run1Ties"]} share ranks')
                if label.endswith('run2-complete'):
                    for item in expected['final']:
                        row = page.locator(f'tr[data-bib="{item["bib"]}"] td')
                        if item['total']:
                            expect(row.nth(0)).to_have_text(str(item['rank']))
                            expect(row.nth(col['Total'])).to_have_text(item['total'])
                    for a, b in expected['combinedTies']:
                        ra = page.locator(f'tr[data-bib="{a}"] td').nth(0).inner_text()
                        rb = page.locator(f'tr[data-bib="{b}"] td').nth(0).inner_text()
                        assert ra == rb, f'Combined tie {a}/{b} shown as {ra}/{rb}'
                    log.append(f'  final standings verified against {sum(1 for x in expected["final"] if x["total"])} independent totals; '
                               f'combined ties {expected["combinedTies"]} share ranks')
            # A spectator on a phone sees the same final standings.
            mobile = browser.new_context(viewport={'width': 390, 'height': 844}, locale='en-GB', timezone_id='UTC')
            mobile.route('**/*', lambda route: route.continue_() if route.request.url.startswith(base) else route.abort())
            phone = mobile.new_page()
            phone.goto(session['publicUrl'])
            expect(phone.locator('#connection')).to_have_text('Live')
            expect(phone.locator('#results tr')).to_have_count(len(run['startOrder']))
            if shots:
                phone.screenshot(path=str(shots / 'final-mobile.png'))
            if errors:
                raise AssertionError('Browser errors: ' + '; '.join(errors))
            browser.close()
        log.append('PASS: all live checkpoints verified in Chromium')
    finally:
        process.terminate()
        process.wait(10)
        print('\n'.join(log))


if __name__ == '__main__':
    sys.exit(main())
