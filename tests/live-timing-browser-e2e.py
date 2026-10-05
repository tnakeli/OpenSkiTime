"""Real Chromium + WebSocket E2E. No Azure, device or personal data required.
Run: python tests/live-timing-browser-e2e.py --dotnet <dotnet.exe>
Requires: pip install playwright; python -m playwright install chromium.
"""
import argparse
import base64
import hashlib
import json
import os
import re
from pathlib import Path
import secrets
import socket
import subprocess
import time
import urllib.request
from playwright.sync_api import sync_playwright, expect


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--dotnet', default='dotnet')
    parser.add_argument('--configuration', default='Debug')
    args = parser.parse_args()
    root = Path(__file__).resolve().parents[1]
    assembly = root / 'src/OpenSkiTime.LiveTiming.Server/bin' / args.configuration / 'net10.0/OpenSkiTime.LiveTiming.Server.dll'
    with socket.socket() as reserve:
        reserve.bind(('127.0.0.1', 0))
        port = reserve.getsockname()[1]
    base = f'http://127.0.0.1:{port}'
    publisher_key = 'ost_pk_' + secrets.token_urlsafe(32)
    env = dict(os.environ, LiveTiming__SigningKey=base64.b64encode(secrets.token_bytes(32)).decode(), Logging__LogLevel__Default='Warning',
               LiveTiming__PublisherKeys='e2e:' + hashlib.sha256(publisher_key.encode()).hexdigest())
    process = None

    def start():
        nonlocal process
        process = subprocess.Popen([args.dotnet, str(assembly), '--urls', base], cwd=assembly.parent, env=env,
                                   stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, creationflags=getattr(subprocess, 'CREATE_NO_WINDOW', 0))
        deadline = time.monotonic() + 15
        while time.monotonic() < deadline:
            try:
                request('GET', '/health')
                return
            except OSError:
                time.sleep(.1)
        raise RuntimeError('Server did not become healthy')

    def request(method, path, value=None, token=None):
        headers = {'Content-Type': 'application/json'}
        if token:
            headers['Authorization'] = 'Bearer ' + token
        data = json.dumps(value).encode() if value is not None else (b'' if method == 'POST' else None)
        with urllib.request.urlopen(urllib.request.Request(base + path, data=data, headers=headers, method=method), timeout=5) as response:
            raw = response.read()
            return json.loads(raw) if raw else None

    try:
        start()
        session = request('POST', '/api/sessions', token=publisher_key)
        sid, token = session['sessionId'], session['publisherToken']
        state = {'version': 1, 'competition': {'name': 'Synthetic browser race', 'place': 'Test slope', 'discipline': 'SL', 'date': '2026-10-02',
                 'isFis': False, 'codex': '', 'gender': 'M', 'category': 'Club', 'intermediateCount': 2, 'slope': 'Offline slope'},
                 'competitors': [{'bib': i, 'lastName': f'TEST{i}', 'firstName': 'Synthetic', 'nation': 'FIN', 'club': 'Test Club', 'fisCode': ''} for i in range(1, 7)],
                 'currentRun': 1, 'runs': [{'number': 1, 'listCreatedAt': '2026-10-02T09:00:00Z', 'startOrder': list(range(1, 7)), 'results': [
                 {'bib': i, 'status': 'Ready', 'hundredths': None, 'rank': None, 'difference': None, 'at': '2026-10-02T09:00:00Z'} for i in range(1, 7)]}],
                 'updatedAt': '2026-10-02T09:00:00Z', 'paused': False}
        request('PUT', f'/api/sessions/{sid}/state', state, token)
        with sync_playwright() as playwright:
            browser = playwright.chromium.launch()
            context = browser.new_context(viewport={'width': 390, 'height': 844})
            # The browser may contact only this offline service.
            context.route('**/*', lambda route: route.continue_() if route.request.url.startswith(base) else route.abort())
            # Viewer WebSockets pass through a proxy that can silence the server like a half-open connection after a revision swap.
            silent = {'on': False}

            def proxy(ws):
                upstream = ws.connect_to_server()
                upstream.on_message(lambda message: None if silent['on'] else ws.send(message))
            context.route_web_socket(re.compile(r'/live\?'), proxy)
            page = context.new_page()
            failures = []
            page.on('pageerror', lambda error: failures.append(str(error)))
            # The landing page lists every active published race and links to it.
            page.goto(base + '/')
            expect(page.locator('#races a')).to_have_text('Synthetic browser race')
            expect(page.locator('footer a', has_text='Privacy')).to_have_attribute('href', 'https://openskiti.me/privacy/')
            page.locator('#races a').click()
            expect(page).to_have_url(base + '/r/' + sid)
            expect(page.locator('#connection')).to_have_text('Live')
            expect(page.locator('#name')).to_have_text('Synthetic browser race')
            expect(page.locator('#results tr')).to_have_count(6)
            # No intermediate times yet: the column is removed rather than filled with dashes.
            expect(page.locator('#splitMode')).to_be_hidden()
            expect(page.locator('#head th')).to_have_text(['Rank', 'Bib', 'Competitor', 'Nation / club', 'Status', 'Finish', 'Difference'])
            expect(page.locator('tr[data-bib="1"] td')).to_have_count(7)
            navigations = []
            page.on('framenavigated', lambda frame: navigations.append(frame.url))
            for bib, status, kind in [(1, 'OnCourse', 'CompetitorStarted'), (1, 'OnCourse', 'IntermediateTime'), (1, 'Finished', 'CompetitorFinished'),
                                      (2, 'DNS', 'DNS'), (3, 'DNF', 'DNF'), (4, 'DSQ', 'DSQ')]:
                state['version'] += 1
                result = state['runs'][0]['results'][bib - 1]
                result['status'] = status
                if kind == 'IntermediateTime':
                    result['intermediates'] = [{'number': 1, 'hundredths': 2134, 'at': '2026-10-02T09:00:21.340Z'}]
                if status == 'Finished':
                    result.update(hundredths=6012, rank=1, difference=0)
                request('POST', f'/api/sessions/{sid}/events', {'version': state['version'], 'run': 1, 'kind': kind, 'result': result, 'at': state['updatedAt']}, token)
                expect(page.locator(f'tr[data-bib="{bib}"] td').nth(4)).to_have_text(status)
            expect(page.locator('tr[data-bib="1"] td').nth(5)).to_contain_text('0:21.34')
            expect(page.locator('tr[data-bib="1"] td').nth(6)).to_have_text('1:00.12')
            # The viewer switches between cumulative intermediate times and sector times, each in its own column; the last
            # sector ends at the finish. The choice is remembered by this browser across a reload.
            expect(page.locator('#head th')).to_have_text(['Rank', 'Bib', 'Competitor', 'Nation / club', 'Status', 'Intermediate 1', 'Finish', 'Difference'])
            expect(page.locator('#splitMode button[data-mode="intermediate"]')).to_have_attribute('aria-pressed', 'true')
            page.locator('#splitMode button[data-mode="sector"]').click()
            expect(page.locator('#head th')).to_have_text(['Rank', 'Bib', 'Competitor', 'Nation / club', 'Status', 'Sector 1', 'Sector 2', 'Finish', 'Difference'])
            expect(page.locator('tr[data-bib="1"] td')).to_have_text(['1', '1', 'TEST1 Synthetic', 'FIN / Test Club', 'Finished', '0:21.34', '0:38.78', '1:00.12', '+0:00.00'])
            page.reload()
            expect(page.locator('#connection')).to_have_text('Live')
            expect(page.locator('#splitMode button[data-mode="sector"]')).to_have_attribute('aria-pressed', 'true')
            expect(page.locator('tr[data-bib="1"] td').nth(6)).to_have_text('0:38.78')
            page.locator('#splitMode button[data-mode="intermediate"]').click()
            expect(page.locator('tr[data-bib="1"] td').nth(6)).to_have_text('1:00.12')
            navigations.clear()
            # In Run 2 every racer shows the Run 1 result and, once finished, the combined time that orders the standings.
            second = json.loads(json.dumps(state))
            first_run = second['runs'][0]
            first_run['results'][4].update(status='Finished', hundredths=6100, rank=2, difference=88)
            second['currentRun'] = 2
            second['runs'].append({'number': 2, 'listCreatedAt': '2026-10-02T11:00:00Z', 'startOrder': [5, 1], 'results': [
                {'bib': 5, 'status': 'Finished', 'hundredths': 5900, 'rank': 1, 'difference': 0, 'at': '2026-10-02T11:01:00Z',
                 'totalHundredths': 12000, 'totalRank': 1, 'totalDifference': 0},
                {'bib': 1, 'status': 'OnCourse', 'hundredths': None, 'rank': None, 'difference': None, 'at': '2026-10-02T11:02:00Z'}]})
            request('PUT', f'/api/sessions/{sid}/state', second, token)
            expect(page.locator('#runs button[aria-pressed="true"]')).to_have_text('Run 2')
            expect(page.locator('#head th')).to_have_text(['Rank', 'Bib', 'Competitor', 'Nation / club', 'Status', 'Run 1', 'Run 2', 'Total', 'Difference'])
            expect(page.locator('#results tr')).to_have_count(2)
            expect(page.locator('tr[data-bib="5"] td')).to_have_text(['1', '5', 'TEST5 Synthetic', 'FIN / Test Club', 'Finished', '1:01.00', '0:59.00', '2:00.00', '+0:00.00'])
            expect(page.locator('tr[data-bib="1"] td')).to_have_text(['—', '1', 'TEST1 Synthetic', 'FIN / Test Club', 'OnCourse', '1:00.12', '—', '—', '—'])
            page.locator('#runs button', has_text='Run 1').click()
            expect(page.locator('#head th').nth(5)).to_have_text('Intermediate 1')
            # A run the viewer chose stays selected through updates while the current run is unchanged.
            second['runs'][1]['results'][1]['intermediates'] = [{'number': 1, 'hundredths': 2050, 'at': '2026-10-02T11:02:20.500Z'}]
            request('PUT', f'/api/sessions/{sid}/state', second, token)
            expect(page.locator('#updated')).to_contain_text('Run 1')
            expect(page.locator('#runs button[aria-pressed="true"]')).to_have_text('Run 1')
            page.locator('#runs button', has_text='Run 2').click()
            expect(page.locator('#head th')).to_have_text(['Rank', 'Bib', 'Competitor', 'Nation / club', 'Status', 'Run 1', 'Intermediate 1', 'Run 2', 'Total', 'Difference'])
            expect(page.locator('tr[data-bib="1"] td').nth(6)).to_have_text('0:20.50')
            # The wider table scrolls inside its own container; the phone-width page never scrolls sideways.
            assert page.evaluate('document.documentElement.scrollWidth') <= 390
            page.locator('#runs button', has_text='Run 1').click()
            request('PUT', f'/api/sessions/{sid}/state', state, token)
            expect(page.locator('#runs button')).to_have_count(1)
            expect(page.locator('tr[data-bib="1"] td').nth(6)).to_have_text('1:00.12')
            # Missing keep-alive pings reveal the silent socket; the viewer replaces it without a page reload.
            silent['on'] = True
            # 'Reconnecting' shows for under a second before 'Connecting'; leaving 'Live' is the detection itself.
            expect(page.locator('#connection')).not_to_have_text('Live', timeout=50000)
            silent['on'] = False
            expect(page.locator('#connection')).to_have_text('Live', timeout=20000)
            expect(page.locator('tr[data-bib="1"] td').nth(6)).to_have_text('1:00.12')
            request('POST', f'/api/sessions/{sid}/pause', token=token)
            expect(page.locator('#course')).to_contain_text('Publishing stopped')
            request('PUT', f'/api/sessions/{sid}/state', state, token)
            expect(page.locator('#course')).not_to_contain_text('Publishing stopped')
            # Same signing key validates the race token after a real server process restart.
            process.kill()
            process.wait(timeout=10)
            expect(page.locator('#connection')).to_have_text('Reconnecting', timeout=10000)
            start()
            # The restarted server has empty RAM: the viewer reconnects by itself and keeps the last results until the publisher restores them.
            expect(page.locator('#connection')).to_have_text('Live', timeout=20000)
            expect(page.locator('#course')).to_contain_text('Waiting for publisher')
            expect(page.locator('#results tr')).to_have_count(6)
            expect(page.locator('tr[data-bib="1"] td').nth(6)).to_have_text('1:00.12')
            request('PUT', f'/api/sessions/{sid}/state', state, token)
            expect(page.locator('#course')).not_to_contain_text('Waiting for publisher')
            expect(page.locator('tr[data-bib="1"] td').nth(6)).to_have_text('1:00.12')
            request('DELETE', f'/api/sessions/{sid}/data', token=token)
            expect(page.locator('#results tr')).to_have_count(0)
            expect(page.locator('#name')).to_have_text('Live timing unavailable')
            assert not navigations, f'Updates caused page navigation: {navigations}'
            assert not failures, f'Browser errors: {failures}'
            browser.close()
        print('PASS: offline responsive Chromium, live start/intermediate/finish/DNS/DNF/DSQ, intermediate/sector toggle remembered across reload, Run 2 with Run 1 and total columns and current-run following, stop/resume, silent-socket and process restart/self-reconnect/retained state/full resync, deletion; zero refreshes or browser errors.')
    finally:
        if process and process.poll() is None:
            process.kill()
            process.wait(timeout=10)


if __name__ == '__main__':
    main()
