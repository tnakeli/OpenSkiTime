"""Real Chromium + WebSocket E2E. No Azure, device or personal data required.
Run: python tests/live-timing-browser-e2e.py --dotnet <dotnet.exe>
Requires: pip install playwright; python -m playwright install chromium.
"""
import argparse
import copy
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
import datetime
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
            # A desktop viewer first; the phone layout is checked further down.
            context = browser.new_context(viewport={'width': 1100, 'height': 844})
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
            # No status column: a run's time column says DNS/DNF/DSQ and shows the running time on course.
            # A race of one nation shows no nation anywhere: no column, nothing in the banner, no nation statistics.
            expect(page.locator('#head th:not(.fav)')).to_have_text(['Rank', 'Bib', 'Competitor', 'Club', 'Finish', 'Difference'])
            expect(page.locator('#track li.next')).to_have_count(2)
            expect(page.locator('#track .nation .code')).to_have_count(0)
            expect(page.locator('#byNation')).to_be_hidden()
            expect(page.locator('#totals')).to_have_text('6 racers · 1 club')
            # A second nation brings them back: flag and code, the country name on hover.
            state['version'] += 1
            state['competitors'][5]['nation'] = 'SWE'
            request('PUT', f'/api/sessions/{sid}/state', state, token)
            expect(page.locator('#head th:not(.fav)')).to_have_text(['Rank', 'Bib', 'Competitor', 'Nation', 'Club', 'Finish', 'Difference'])
            expect(page.locator('tr[data-bib="1"] td:not(.fav)')).to_have_count(7)
            expect(page.locator('tr[data-bib="1"] td.nation')).to_have_text(re.compile(r'FIN$'))
            expect(page.locator('tr[data-bib="1"] td.nation')).to_have_attribute('title', 'Finland')
            expect(page.locator('tr[data-bib="6"] td.nation .flag')).to_have_text('\U0001F1F8\U0001F1EA')
            # Where the platform draws flags as letters (Windows), the bundled flag font draws them.
            if page.evaluate("document.documentElement.classList.contains('flag-font')"):
                assert page.evaluate("document.fonts.ready.then(() => [...document.fonts].some(f => f.family.includes('Twemoji Country Flags') && f.status === 'loaded'))"), 'flag font not loaded'
            # The banner follows the course from the top down: the racer after next, then the next racer just above the course.
            expect(page.locator('#track li.next .bib')).to_have_text(['2', '1'])
            expect(page.locator('#track li.next .who')).to_have_text(['TEST2 Synthetic', 'TEST1 Synthetic'])
            expect(page.locator('#track li.next .nation .code')).to_have_text(['FIN', 'FIN'])
            expect(page.locator('#course')).to_be_hidden()
            # Race statistics below the results; racers per nation and club open on demand.
            expect(page.locator('#totals')).to_have_text('6 racers · 2 nations · 1 club')
            expect(page.locator('#runCounts li')).to_have_text(['Run 1 6 starters · 0 finished · 0 DNS · 0 DNF · 0 DSQ · 6 to start'])
            expect(page.locator('#nations')).to_be_hidden()
            page.locator('#byNation summary').click()
            expect(page.locator('#nations li')).to_have_text([re.compile(r'FIN Finland\s*5$'), re.compile(r'SWE Sweden\s*1$')])
            # The server counts this race's viewers; the footer shows it.
            expect(page.locator('#viewers')).to_have_text('1 watching live')
            # Selecting a nation in the statistics shows only its racers, as a removable chip; ranks are not recalculated.
            page.locator('#nations button', has_text='Sweden').click()
            expect(page.locator('#results tr')).to_have_count(1)
            expect(page.locator('#results tr[data-bib="6"]')).to_have_count(1)
            expect(page.locator('#chips .chip')).to_have_text('Nation: Sweden ✕')
            expect(page.locator('#chips .chip')).to_be_focused()
            expect(page.locator('#matches')).to_have_text('1 of 6 racers')
            expect(page.locator('#nations button', has_text='Sweden')).to_have_attribute('aria-pressed', 'true')
            # Several nations are alternatives; removing a chip moves focus to the next one, or to the search.
            page.locator('#nations button', has_text='Finland').click()
            expect(page.locator('#results tr')).to_have_count(6)
            expect(page.locator('#chips .chip')).to_have_text(['Nation: Sweden ✕', 'Nation: Finland ✕', 'Clear all'])
            page.locator('#chips .chip', has_text='Sweden').click()
            expect(page.locator('#results tr')).to_have_count(5)
            expect(page.locator('#chips .chip', has_text='Finland')).to_be_focused()
            page.locator('#chips .chip', has_text='Finland').click()
            expect(page.locator('#search')).to_be_focused()
            expect(page.locator('#results tr')).to_have_count(6)
            expect(page.locator('#matches')).to_have_text('')
            # A club too; selecting it again removes the filter.
            page.locator('#byClub summary').click()
            page.locator('#clubs button', has_text='Test Club').click()
            expect(page.locator('#chips .chip')).to_have_text('Club: Test Club ✕')
            expect(page.locator('#matches')).to_have_text('6 of 6 racers')
            page.locator('#clubs button', has_text='Test Club').click()
            expect(page.locator('#chips .chip')).to_have_count(0)
            # Search by name, club, nation or bib, ignoring case; nothing found says so.
            page.locator('#search').fill('test3 synth')
            expect(page.locator('#results tr')).to_have_count(1)
            expect(page.locator('#results tr[data-bib="3"]')).to_have_count(1)
            page.locator('#search').fill('sweden')
            expect(page.locator('#results tr[data-bib="6"]')).to_have_count(1)
            expect(page.locator('#results tr')).to_have_count(1)
            page.locator('#search').fill('4')
            expect(page.locator('#results tr[data-bib="4"]')).to_have_count(1)
            page.locator('#search').fill('nobody')
            expect(page.locator('#results td.none')).to_have_text('No racers match the filter.')
            # Enter keeps a search as a filter chip, and searches are alternatives too: two names show both racers.
            page.locator('#search').fill('test2')
            page.locator('#search').press('Enter')
            expect(page.locator('#search')).to_have_value('')
            page.locator('#search').fill('test5')
            expect(page.locator('#addFilter')).to_be_visible()
            page.locator('#addFilter').click()
            expect(page.locator('#chips .chip')).to_have_text(['“test2” ✕', '“test5” ✕', 'Clear all'])
            expect(page.locator('#results tr')).to_have_count(2)
            # The typed text must match as well.
            page.locator('#search').fill('test5')
            expect(page.locator('#results tr')).to_have_count(1)
            page.locator('#search').fill('')
            page.locator('#chips .chip', has_text='Clear all').click()
            expect(page.locator('#chips .chip')).to_have_count(0)
            expect(page.locator('#results tr')).to_have_count(6)
            # The search keeps its text and focus through the live update below.
            page.locator('#search').fill('SYNTHETIC')
            # Forerunners show where they are (on course, at start) but never a time and never a results row.
            expect(page.locator('#forerunners')).to_be_hidden()
            state['version'] += 1
            state['runs'][0]['forerunners'] = [{'letter': 'A', 'lastName': 'FORERUNNER', 'firstName': 'Synthetic', 'nation': 'FIN', 'status': 'OnCourse'},
                                               {'letter': 'B', 'lastName': 'SECOND', 'firstName': 'Synthetic', 'nation': 'FIN', 'status': 'Ready'}]
            request('PUT', f'/api/sessions/{sid}/state', state, token)
            expect(page.locator('#forerunners')).to_have_text('Forerunners · A FORERUNNER Synthetic: on course · B SECOND Synthetic: at start')
            expect(page.locator('#search')).to_be_focused()
            expect(page.locator('#search')).to_have_value('SYNTHETIC')
            expect(page.locator('#results tr')).to_have_count(6)
            page.locator('#search').fill('')
            # A star makes a racer a favorite in this browser only (local storage, never sent to the server); the star keeps
            # focus, and Favorites shows only them.
            page.locator('tr[data-bib="2"] button.star').click()
            expect(page.locator('tr[data-bib="2"] button.star')).to_have_attribute('aria-pressed', 'true')
            expect(page.locator('tr[data-bib="2"] button.star')).to_be_focused()
            assert page.evaluate("JSON.parse(localStorage.getItem('openskitime.live.favorites'))") == [
                {'code': '', 'lastName': 'TEST2', 'firstName': 'Synthetic', 'nation': 'FIN', 'club': 'Test Club'}]
            page.locator('#favoritesOnly').click()
            expect(page.locator('#results tr')).to_have_count(1)
            expect(page.locator('#results tr[data-bib="2"]')).to_have_class(re.compile('favorite'))
            page.locator('#favoritesOnly').click()
            expect(page.locator('#results tr')).to_have_count(6)
            # Everyone on course has a banner row; a forerunner on it shows no running time.
            expect(page.locator('#track li.oncourse .bib')).to_have_text(['F A'])
            expect(page.locator('#track li.oncourse .who')).to_have_text(['FORERUNNER Synthetic'])
            expect(page.locator('#track li.oncourse .time')).to_have_text(['forerunner'])
            expect(page.locator('#results tr')).to_have_count(6)
            state['version'] += 1
            state['runs'][0]['forerunners'][0]['status'] = 'Finished'
            state['runs'][0]['forerunners'][1]['status'] = 'Finished'
            request('PUT', f'/api/sessions/{sid}/state', state, token)
            expect(page.locator('#forerunners')).to_have_text('Forerunners · A FORERUNNER Synthetic: finished · B SECOND Synthetic: finished')
            expect(page.locator('#track li.oncourse')).to_have_count(0)
            # Racers on course are listed in course order from the top: the one who started last is highest.
            waiting = copy.deepcopy(state['runs'][0]['results'])
            state['version'] += 1
            for bib, ago in [(5, 40), (6, 15)]:
                started = (datetime.datetime.now(datetime.timezone.utc) - datetime.timedelta(seconds=ago)).isoformat().replace('+00:00', 'Z')
                state['runs'][0]['results'][bib - 1].update(status='OnCourse', at=started, startedAt=started)
            request('PUT', f'/api/sessions/{sid}/state', state, token)
            expect(page.locator('#track li.oncourse .bib')).to_have_text(['6', '5'])
            state['version'] += 1
            state['runs'][0]['results'] = waiting
            request('PUT', f'/api/sessions/{sid}/state', state, token)
            expect(page.locator('#track li.oncourse')).to_have_count(0)
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
                # Each change is published later than the previous one; a started racer carries its start instant.
                now = datetime.datetime.now(datetime.timezone.utc)
                result['at'] = now.isoformat().replace('+00:00', 'Z')
                if kind == 'CompetitorStarted':
                    # Started 70 s ago, so the 1:00.12 finish lies before the later DNF, as on a real course.
                    result['startedAt'] = (now - datetime.timedelta(seconds=70)).isoformat().replace('+00:00', 'Z')
                request('POST', f'/api/sessions/{sid}/events', {'version': state['version'], 'run': 1, 'kind': kind, 'result': result, 'at': state['updatedAt']}, token)
                expect(page.locator(f'tr[data-bib="{bib}"]')).to_have_class(re.compile(rf'(^|\s){status.lower()}(\s|$)'))
                if status in ('DNS', 'DNF', 'DSQ'):
                    expect(page.locator(f'tr[data-bib="{bib}"] td:not(.fav)').nth(-2)).to_have_text(status)
                if kind == 'CompetitorStarted':
                    # The racer is listed on course with a running time that ticks between updates, in the results too.
                    expect(page.locator('#track li.oncourse .who')).to_have_text(['TEST1 Synthetic'])
                    expect(page.locator('#track li')).to_have_class([re.compile('next'), re.compile('next'), re.compile('oncourse')])
                    expect(page.locator('tr[data-bib="1"] td:not(.fav)').nth(-2)).to_have_text(re.compile(r'^1:1\d\.\d$'))
                    expect(page.locator('#track li.next .bib')).to_have_text(['3', '2'])
                    clock = page.locator('#track li.oncourse .clock')
                    expect(clock).to_have_text(re.compile(r'^1:1\d\.\d$'))
                    first = clock.inner_text(); page.wait_for_timeout(400)
                    assert clock.inner_text() != first, 'running time does not tick'
                if kind == 'CompetitorFinished':
                    # The new latest finisher is highlighted for ten seconds, in the banner and in the results.
                    expect(page.locator('#latest .bib')).to_have_text('1')
                    expect(page.locator('#latest .who')).to_have_text('TEST1 Synthetic')
                    expect(page.locator('#latest .time')).to_have_text('1:00.12')
                    expect(page.locator('#latest .rank')).to_have_text('#1')
                    expect(page.locator('#latest')).to_have_class(re.compile('flash'))
                    expect(page.locator('tr[data-bib="1"]')).to_have_class(re.compile('flash'))
                    page.wait_for_timeout(8000)
                    expect(page.locator('tr[data-bib="1"]')).to_have_class(re.compile('flash'), timeout=100)
                    expect(page.locator('#latest')).not_to_have_class(re.compile('flash'), timeout=5000)
                    expect(page.locator('#track li.oncourse')).to_have_count(0)
                if kind == 'DNS':
                    expect(page.locator('#latest .time')).to_have_text('1:00.12')
                if kind == 'DNF':
                    # A DNF is the latest outcome too, shown with its status and highlighted.
                    expect(page.locator('#latest .bib')).to_have_text('3')
                    expect(page.locator('#latest .time')).to_have_text('DNF')
                    expect(page.locator('#latest .rank')).to_have_text('')
                    expect(page.locator('#latest')).to_have_class(re.compile('out.*flash|flash.*out'))
            expect(page.locator('tr[data-bib="1"] td:not(.fav)').nth(5)).to_contain_text('0:21.34')
            expect(page.locator('tr[data-bib="1"] td:not(.fav)').nth(6)).to_have_text('1:00.12')
            # A DNS marked ahead of time is not a start: the next racers are those after the last one who started.
            expect(page.locator('#track li.next .bib')).to_have_text(['6', '5'])
            expect(page.locator('#runCounts li')).to_have_text(['Run 1 6 starters · 1 finished · 1 DNS · 1 DNF · 1 DSQ · 2 to start'])
            # The viewer switches between cumulative intermediate times and sector times, each in its own column; the last
            # sector ends at the finish. The choice is remembered by this browser across a reload.
            expect(page.locator('#head th:not(.fav)')).to_have_text(['Rank', 'Bib', 'Competitor', 'Nation', 'Club', 'Intermediate 1', 'Finish', 'Difference'])
            expect(page.locator('#splitMode button[data-mode="intermediate"]')).to_have_attribute('aria-pressed', 'true')
            page.locator('#splitMode button[data-mode="sector"]').click()
            expect(page.locator('#head th:not(.fav)')).to_have_text(['Rank', 'Bib', 'Competitor', 'Nation', 'Club', 'Sector 1', 'Sector 2', 'Finish', 'Difference'])
            expect(page.locator('tr[data-bib="1"] td:not(.fav)')).to_have_text(['1', '1', 'TEST1 Synthetic', re.compile(r'FIN$'), 'Test Club', '0:21.34', '0:38.78', '1:00.12', '+0:00.00'])
            page.reload()
            expect(page.locator('#connection')).to_have_text('Live')
            expect(page.locator('#splitMode button[data-mode="sector"]')).to_have_attribute('aria-pressed', 'true')
            expect(page.locator('tr[data-bib="1"] td:not(.fav)').nth(6)).to_have_text('0:38.78')
            expect(page.locator('tr[data-bib="2"] button.star')).to_have_attribute('aria-pressed', 'true')
            page.locator('#splitMode button[data-mode="intermediate"]').click()
            expect(page.locator('tr[data-bib="1"] td:not(.fav)').nth(6)).to_have_text('1:00.12')
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
            expect(page.locator('#head th:not(.fav)')).to_have_text(['Rank', 'Bib', 'Competitor', 'Nation', 'Club', 'Run 1', 'Run 2', 'Total', 'Difference'])
            expect(page.locator('#results tr')).to_have_count(2)
            expect(page.locator('tr[data-bib="5"] td:not(.fav)')).to_have_text(['1', '5', 'TEST5 Synthetic', re.compile(r'FIN$'), 'Test Club', '1:01.00', '0:59.00', '2:00.00', '+0:00.00'])
            # On course without a published start instant: the running time still ticks, from when this viewer saw the start.
            expect(page.locator('tr[data-bib="1"] td:not(.fav)')).to_have_text(['—', '1', 'TEST1 Synthetic', re.compile(r'FIN$'), 'Test Club', '1:00.12', re.compile(r'^0:0\d\.\d$'), '—', '—'])
            expect(page.locator('#track li.oncourse .time')).to_have_text([re.compile(r'^0:0\d\.\d$')])
            page.locator('#runs button', has_text='Run 1').click()
            expect(page.locator('#head th:not(.fav)').nth(5)).to_have_text('Intermediate 1')
            # A run the viewer chose stays selected through updates while the current run is unchanged.
            second['runs'][1]['results'][1]['intermediates'] = [{'number': 1, 'hundredths': 2050, 'at': '2026-10-02T11:02:20.500Z'}]
            request('PUT', f'/api/sessions/{sid}/state', second, token)
            expect(page.locator('#updated')).to_contain_text('Run 1')
            expect(page.locator('#runs button[aria-pressed="true"]')).to_have_text('Run 1')
            page.locator('#runs button', has_text='Run 2').click()
            expect(page.locator('#head th:not(.fav)')).to_have_text(['Rank', 'Bib', 'Competitor', 'Nation', 'Club', 'Run 1', 'Intermediate 1', 'Run 2', 'Total', 'Difference'])
            expect(page.locator('tr[data-bib="1"] td:not(.fav)').nth(6)).to_have_text('0:20.50')
            expect(page.locator('#runCounts li')).to_have_text(['Run 1 6 starters · 2 finished · 1 DNS · 1 DNF · 1 DSQ · 1 to start',
                                                               'Run 2 2 starters · 1 finished · 0 DNS · 0 DNF · 0 DSQ · 1 on course'])
            # A phone gets two-line cells instead of the wide table: rank over bib, last name over first name and club,
            # Run 1 over Run 2, total over difference. Neither the page nor the table scrolls sideways.
            page.set_viewport_size({'width': 390, 'height': 844})
            expect(page.locator('.table')).to_have_class(re.compile('compact'))
            expect(page.locator('tr[data-bib="5"] td:not(.fav)').first.locator('.line')).to_have_text(['1', '5'])
            expect(page.locator('tr[data-bib="5"] td.name .line')).to_have_text(['TEST5', 'Synthetic', 'Test Club'])
            expect(page.locator('tr[data-bib="5"] td.nation .code')).to_have_text('FIN')
            expect(page.locator('tr[data-bib="5"] td.runs .line')).to_have_text(['1:01.00', '0:59.00'])
            expect(page.locator('tr[data-bib="5"] td.result .line')).to_have_text(['2:00.00', '+0:00.00'])
            expect(page.locator('tr[data-bib="1"] td.splits .line')).to_have_text(['0:20.50'])
            assert page.evaluate('document.documentElement.scrollWidth') <= 390
            assert page.evaluate("(() => { const t = document.querySelector('.table'); return t.scrollWidth <= t.clientWidth; })()"), 'phone table scrolls sideways'
            page.set_viewport_size({'width': 1100, 'height': 844})
            expect(page.locator('.table')).not_to_have_class(re.compile('compact'))
            page.locator('#runs button', has_text='Run 1').click()
            request('PUT', f'/api/sessions/{sid}/state', state, token)
            expect(page.locator('#runs button')).to_have_count(1)
            expect(page.locator('tr[data-bib="1"] td:not(.fav)').nth(6)).to_have_text('1:00.12')
            # Missing keep-alive pings reveal the silent socket; the viewer replaces it without a page reload.
            silent['on'] = True
            # 'Reconnecting' shows for under a second before 'Connecting'; leaving 'Live' is the detection itself.
            expect(page.locator('#connection')).not_to_have_text('Live', timeout=50000)
            silent['on'] = False
            expect(page.locator('#connection')).to_have_text('Live', timeout=20000)
            expect(page.locator('tr[data-bib="1"] td:not(.fav)').nth(6)).to_have_text('1:00.12')
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
            expect(page.locator('tr[data-bib="1"] td:not(.fav)').nth(6)).to_have_text('1:00.12')
            request('PUT', f'/api/sessions/{sid}/state', state, token)
            expect(page.locator('#course')).not_to_contain_text('Waiting for publisher')
            expect(page.locator('tr[data-bib="1"] td:not(.fav)').nth(6)).to_have_text('1:00.12')
            # The front page lists the favorites with their results in every published race. Removing one there removes
            # it in the race page open in another tab too.
            front = context.new_page()
            front.on('pageerror', lambda error: failures.append(str(error)))
            front.goto(base + '/')
            expect(front.locator('#favorites')).to_be_visible()
            expect(front.locator('#favoriteList > li .who strong')).to_have_text(['TEST2 Synthetic'])
            expect(front.locator('#favoriteList .entries a')).to_have_text(['Synthetic browser race'])
            expect(front.locator('#favoriteList .entries a')).to_have_attribute('href', '/r/' + sid)
            expect(front.locator('#favoriteList .entries .runs')).to_have_text(re.compile(r'^Run 1 '))
            front.locator('#favoriteList button.star').click()
            expect(front.locator('#favorites')).to_be_hidden()
            expect(page.locator('tr[data-bib="2"] button.star')).to_have_attribute('aria-pressed', 'false')
            front.close()
            request('DELETE', f'/api/sessions/{sid}/data', token=token)
            expect(page.locator('#results tr')).to_have_count(0)
            expect(page.locator('#name')).to_have_text('Live timing unavailable')
            assert not navigations, f'Updates caused page navigation: {navigations}'
            assert not failures, f'Browser errors: {failures}'
            browser.close()
        print('PASS: offline responsive Chromium (desktop columns and two-line phone cells), nation flags/codes (hidden for one nation, flag font where needed), start gate/on course/finish banner, race statistics with several nation and club filters, name/club/nation/bib search kept as filter chips and keeping focus through updates, favorite stars in local storage with a favorites-only filter and the front-page favorites list synced across tabs, live viewer count, forerunner progress without times, live start/intermediate/finish/DNS/DNF/DSQ in the time column with a ten-second highlight, intermediate/sector toggle remembered across reload, Run 2 with Run 1 and total columns and current-run following, stop/resume, silent-socket and process restart/self-reconnect/retained state/full resync, deletion; zero refreshes or browser errors.')
    finally:
        if process and process.poll() is None:
            process.kill()
            process.wait(timeout=10)


if __name__ == '__main__':
    main()
