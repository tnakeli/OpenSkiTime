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
            expect(page.locator('#intermediates')).to_be_hidden()
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
            # Missing keep-alive pings reveal the silent socket; the viewer replaces it without a page reload.
            silent['on'] = True
            expect(page.locator('#connection')).to_have_text('Reconnecting', timeout=50000)
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
        print('PASS: offline responsive Chromium, live start/intermediate/finish/DNS/DNF/DSQ, stop/resume, silent-socket and process restart/self-reconnect/retained state/full resync, deletion; zero refreshes or browser errors.')
    finally:
        if process and process.poll() is None:
            process.kill()
            process.wait(timeout=10)


if __name__ == '__main__':
    main()
