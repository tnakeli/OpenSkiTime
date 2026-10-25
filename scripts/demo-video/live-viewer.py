"""The live page with the race radio for the Live Timing demonstration (tests/OpenSkiTime.Desktop.E2E/LiveDemoVideoTests.cs).

Opens the public live page in a visible Chromium window that covers the recorded screen area, presses Radio and keeps
it playing. At the same time it captures:
- audio.wav: what this computer plays (WASAPI loopback), with the Unix time of its first sample in viewer.json;
- phone/*.jpg: a headless phone-sized view of the same page (1080 × 1680 screenshots), each with its Unix time;
- radio-events.json: when each radio clip and music track started and ended in the visible page.
Usage: python -I live-viewer.py --url <public live URL> --output <directory> --seconds <duration>
Requires: playwright==1.58.0 (python -m playwright install chromium) and PyAudioWPatch (Windows).
"""
import argparse
import json
import tempfile
import threading
import time
import wave
from pathlib import Path

import pyaudiowpatch as pyaudio
from playwright.sync_api import sync_playwright

PHONE = {'width': 360, 'height': 560}  # CSS pixels; at scale 3 a 1080 × 1680 frame under a caption band in 1080 × 1920
PHONE_SCALE = 3
TRACE = """
(() => {
  const play = HTMLMediaElement.prototype.play;
  const log = (event, el) => window.ostRadioEvent && window.ostRadioEvent(event, el.currentSrc || el.src || '', Date.now() / 1000);
  HTMLMediaElement.prototype.play = function () {
    if (!this.__ostTraced) {
      this.__ostTraced = true;
      this.addEventListener('playing', () => log('playing', this));
      this.addEventListener('pause', () => log('pause', this));
      this.addEventListener('ended', () => log('ended', this));
    }
    return play.apply(this, arguments);
  };
})();
"""


class Loopback:
    """Records the default output device. WASAPI loopback delivers nothing while the device is silent, so gaps are
    filled with silence from the wall clock to keep the file on the real timeline."""

    def __init__(self, path):
        self.audio = pyaudio.PyAudio()
        device = self.audio.get_default_wasapi_loopback()
        self.rate, self.channels = int(device['defaultSampleRate']), device['maxInputChannels']
        self.file = wave.open(str(path), 'wb')
        self.file.setnchannels(self.channels)
        self.file.setsampwidth(2)
        self.file.setframerate(self.rate)
        self.start = None
        self.frames = 0
        self.lock = threading.Lock()
        self.stream = self.audio.open(format=pyaudio.paInt16, channels=self.channels, rate=self.rate, input=True,
                                      input_device_index=device['index'], frames_per_buffer=1024, stream_callback=self.callback)

    def callback(self, data, count, info, status):
        now = time.time()
        with self.lock:
            if self.start is None:
                self.start = now - count / self.rate
            expected = int((now - self.start) * self.rate) - count
            if expected - self.frames > self.rate // 10:
                self.file.writeframes(b'\0' * (expected - self.frames) * self.channels * 2)
                self.frames = expected
            self.file.writeframes(data)
            self.frames += count
        return None, pyaudio.paContinue

    def close(self):
        self.stream.stop_stream()
        self.stream.close()
        with self.lock:
            self.file.close()
        self.audio.terminate()


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--url', required=True)
    parser.add_argument('--output', required=True)
    parser.add_argument('--seconds', type=int, required=True)
    args = parser.parse_args()
    output = Path(args.output)
    phone_dir = output / 'phone'
    phone_dir.mkdir(parents=True, exist_ok=False)
    events, frames = [], []
    meta = {'url': args.url}
    loopback = Loopback(output / 'audio.wav')
    try:
        with sync_playwright() as p, tempfile.TemporaryDirectory(prefix='ost-live-demo-') as profile:
            # The window's title bar and invisible resize borders lie outside the recorded 1920 × 1032 area.
            screen = p.chromium.launch_persistent_context(
                profile, headless=False, no_viewport=True, ignore_default_args=['--enable-automation'],
                args=[f'--app={args.url}', '--window-position=-8,-32', '--window-size=1936,1072',
                      '--force-device-scale-factor=1.25', '--autoplay-policy=no-user-gesture-required'])
            screen.expose_function('ostRadioEvent', lambda event, src, at: events.append({'event': event, 'src': src, 'at': at}))
            screen.add_init_script(TRACE)
            page = screen.pages[0] if screen.pages else screen.new_page()
            if page.url != args.url:
                page.goto(args.url)
            page.reload()  # the trace applies from the next document on

            phone_browser = p.chromium.launch(headless=True)
            phone = phone_browser.new_context(viewport=PHONE, device_scale_factor=PHONE_SCALE, is_mobile=True, has_touch=True)
            phone_page = phone.new_page()
            phone_page.goto(args.url)
            radio = page.locator('#radio')
            radio.wait_for(state='visible', timeout=60000)
            page.wait_for_timeout(1500)
            radio.click()
            meta['radioOn'] = time.time()
            print('radio-on', flush=True)
            # Full-resolution screenshots of the phone page (a screencast delivers only CSS-pixel frames), about 5-8 a
            # second; each is stamped with the time it was requested.
            end = time.time() + args.seconds
            while time.time() < end:
                at = time.time()
                name = f'{len(frames):06d}.jpg'
                (phone_dir / name).write_bytes(phone_page.screenshot(type='jpeg', quality=90))
                frames.append({'file': name, 'at': at})
                page.wait_for_timeout(max(10, int((at + 0.12 - time.time()) * 1000)))
            phone_browser.close()
            screen.close()
    finally:
        loopback.close()
        meta['audioStart'] = loopback.start
        meta['audioRate'] = loopback.rate
        (output / 'viewer.json').write_text(json.dumps(meta, indent=2), encoding='utf-8')
        (output / 'radio-events.json').write_text(json.dumps(events, indent=2), encoding='utf-8')
        (output / 'phone.json').write_text(json.dumps(frames), encoding='utf-8')
    print('done', flush=True)


if __name__ == '__main__':
    main()
