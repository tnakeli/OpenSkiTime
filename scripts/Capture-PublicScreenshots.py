"""Render the real live viewer and website in an isolated CI browser using fictional data."""
import argparse
import base64
import json
import os
from pathlib import Path
import secrets
import socket
import subprocess
import time
import urllib.request

from playwright.sync_api import sync_playwright, expect


def wait_ready(url, process):
    for _ in range(100):
        if process.poll() is not None:
            raise RuntimeError("Preview process exited before becoming ready")
        try:
            with urllib.request.urlopen(url, timeout=1):
                return
        except OSError:
            time.sleep(0.1)
    raise RuntimeError("Preview process did not become ready")


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--dotnet', default='dotnet')
    parser.add_argument('--website', action='store_true')
    parser.add_argument('--output', default='artifacts/visual-previews')
    args = parser.parse_args()
    root = Path(__file__).resolve().parents[1]
    output = root / args.output
    output.mkdir(parents=True, exist_ok=True)
    server = root / 'rewrite/src/OpenSkiTime.LiveTiming.Server'
    assembly = server / 'bin/Release/net10.0/OpenSkiTime.LiveTiming.Server.dll'
    with socket.socket() as reserve:
        reserve.bind(('127.0.0.1', 0))
        port = reserve.getsockname()[1]
    base = f'http://127.0.0.1:{port}'
    env = dict(os.environ, LiveTiming__SigningKey=base64.b64encode(secrets.token_bytes(32)).decode(),
               Logging__LogLevel__Default='Warning')
    process = subprocess.Popen([args.dotnet, str(assembly), '--urls', base, '--contentRoot', str(server)],
                               env=env, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    website = None
    try:
        wait_ready(base + '/health', process)
        request = urllib.request.Request(base + '/api/sessions', data=b'', method='POST')
        with urllib.request.urlopen(request) as response:
            session = json.load(response)
        snapshot = (root / 'docs/screenshots/live-demo.json').read_bytes()
        request = urllib.request.Request(base + f"/api/sessions/{session['sessionId']}/state", data=snapshot, method='PUT',
                                         headers={'Content-Type': 'application/json', 'Authorization': 'Bearer ' + session['publisherToken']})
        with urllib.request.urlopen(request):
            pass
        with sync_playwright() as playwright:
            browser = playwright.chromium.launch()
            context = browser.new_context(viewport={'width': 1440, 'height': 900}, locale='en-GB', timezone_id='Europe/Helsinki')
            # Only the loopback demo can be reached; no real race service or external resource is used.
            context.route('**/*', lambda route: route.continue_() if route.request.url.startswith(base + '/') else route.abort())
            page = context.new_page()
            errors = []
            page.on('pageerror', lambda error: errors.append(str(error)))
            page.goto(session['publicUrl'])
            expect(page.locator('#connection')).to_have_text('Live')
            expect(page.locator('#name')).to_have_text('Northern Slalom Women')
            expect(page.locator('#results tr')).to_have_count(12)
            expect(page.locator('#course')).to_contain_text('4 TUNTURI Helmi')
            expect(page.locator('#results tr').first.locator('td').nth(6)).to_have_text('0:53.67')
            page.get_by_role('button', name='Run 2', exact=True).click()
            expect(page.locator('#updated')).to_contain_text('Run 2')
            page.get_by_role('button', name='Run 1', exact=True).click()
            expect(page.locator('#updated')).to_contain_text('Run 1')
            page.screenshot(path=str(root / 'docs/screenshots/10-live-timing.png'))
            page.screenshot(path=str(output / '10-live-timing.png'))
            page.set_viewport_size({'width': 390, 'height': 844})
            page.screenshot(path=str(output / 'live-timing-mobile.png'))
            assert not errors, errors
            context.close()
            if args.website:
                subprocess.run(['node', 'website/build.mjs'], cwd=root, check=True)
                subprocess.run(['node', 'website/check.mjs'], cwd=root, check=True)
                website = subprocess.Popen(['node', 'website/preview.mjs'], cwd=root, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
                wait_ready('http://127.0.0.1:4173/', website)
                context = browser.new_context(viewport={'width': 1440, 'height': 1000}, locale='en-GB')
                context.route('**/*', lambda route: route.continue_() if route.request.url.startswith('http://127.0.0.1:4173/') else route.abort())
                page = context.new_page()
                page.on('pageerror', lambda error: errors.append(str(error)))
                for path, name in [('/', 'website-home'), ('/guide/', 'website-guide')]:
                    page.goto('http://127.0.0.1:4173' + path)
                    # Trigger lazy images before the full-page capture and verify every image has decoded.
                    for image in page.locator('img').all():
                        image.scroll_into_view_if_needed()
                        expect(image).to_have_js_property('complete', True)
                        assert image.evaluate('(image) => image.naturalWidth > 0')
                    page.evaluate('window.scrollTo(0, 0)')
                    page.screenshot(path=str(output / f'{name}-desktop.png'), full_page=True)
                    page.set_viewport_size({'width': 390, 'height': 844})
                    page.screenshot(path=str(output / f'{name}-mobile.png'), full_page=True)
                    overflow = page.evaluate('''() => Array.from(document.querySelectorAll('body *')).filter(element =>
                        element.getBoundingClientRect().right > window.innerWidth + 1).map(element => element.tagName + '.' + element.className)''')
                    assert page.evaluate('document.documentElement.scrollWidth <= window.innerWidth'), f'Website overflows on mobile: {overflow}'
                    page.set_viewport_size({'width': 1440, 'height': 1000})
                assert not errors, errors
                context.close()
            browser.close()
    finally:
        for child in [website, process]:
            if child is not None:
                child.terminate()
                try:
                    child.wait(timeout=10)
                except subprocess.TimeoutExpired:
                    child.kill()
                    child.wait()


if __name__ == '__main__':
    main()
